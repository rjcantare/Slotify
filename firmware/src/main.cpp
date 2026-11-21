// Rental coin acceptor firmware
// S10 done: WiFi + MQTT + LWT + heartbeat + LED handler + exponential backoff.
// S11 Round 1: coin pulse detection via GPIO 18 ISR + burst grouping
//              + denomination decoding + MQTT pulse publish. In-memory
//              seqNo only — NVS persistence in Round 3.
// S11 Round 2: warm-up guard + tuned pulse thresholds
//              from bench measurements on the Allan acceptor.
//              Ghost echo at 91-93 ms filtered by 110 ms ISR debounce.
//              First inter-pulse gap (198-235 ms in NC, ~170 ms uniform in
//              NO mode — locked in NO for production) handled by tight
//              split/finalize thresholds. Boot phantoms are suppressed by
//              warm-up; live-claim phantoms are mainly blocked by inhibit
//              gating plus debounce.
// S11 Round 3 (this): NVS-persistent seqNo + 64-slot unACK'd pulse buffer
//                     in flash + replay on boot/reconnect + 5 s app-level
//                     retry. Closes the "MQTT down between pulse detect
//                     and broker ack" failure mode that would silently
//                     lose customer credit.

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <ArduinoOTA.h>
#include <ESPmDNS.h>
#include <WiFiManager.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include "config.h"

// S53: optional MQTT broker credentials. When config.h does not define
// these (e.g. a pre-S53 config file), default to empty strings which
// resolve to nullptr → anonymous dev broker. The config.example.h
// template includes them; copy the new template at your next flash.
#ifndef MQTT_USER
#define MQTT_USER ""
#endif

#ifndef MQTT_PASS
#define MQTT_PASS ""
#endif

// Empty OTA_PASSWORD opts out of OTA entirely (USB uploads only).
#ifndef OTA_PASSWORD
#define OTA_PASSWORD ""
#endif

// Captive-portal AP password (WiFiManager). Pre-existing config files won't
// define it; default to a WPA2-valid (>= 8 char) value so the build never
// breaks. Override per shop in config.h.
#ifndef WIFI_PORTAL_PASSWORD
#define WIFI_PORTAL_PASSWORD "slotify-setup"
#endif

// --- GPIO pin map (matches ProjectContext/esp32-wiring.md) ---
static const uint8_t LED_PIN          = 19;  // COIN_LED_OUT — drives bench LED + Q1 base
static const uint8_t COIN_PULSE_PIN   = 18;  // COIN_PULSE_IN — falling edge per acceptor pulse
static const uint8_t INHIBIT_PIN      = 23;  // ACCEPTOR_INHIBIT: SET LOW rejects coins; SET HIGH accepts coins. A 10k SET-to-GND pulldown keeps reset/offline fail-safe.

// --- Timing constants ---
static const unsigned long SERIAL_BAUD                = 115200;
static const unsigned long WIFI_CONNECT_TIMEOUT_MS    = 30000;
static const unsigned long WIFI_SEED_TIMEOUT_MS       = 15000;     // Option A: try config.h creds first for this long, then fall to the portal
static const unsigned int  WIFI_PORTAL_TIMEOUT_S      = 180;       // captive portal open window; reboots after to retry saved creds (self-heals a reboot during a WiFi outage instead of sticking in AP mode)
static const unsigned long HEARTBEAT_INTERVAL_MS      = 30000;
static const unsigned long MQTT_BACKOFF_INITIAL_MS    = 1000;
static const unsigned long MQTT_BACKOFF_MAX_MS        = 60000;
static const unsigned int  MQTT_KEEPALIVE_S           = 30;
static const size_t        MQTT_BUFFER_SIZE           = 512;
static const uint16_t      DISCOVERY_UDP_PORT         = 1884;
static const unsigned long DISCOVERY_UDP_TIMEOUT_MS   = 12000;     // server broadcasts every 10 s
static const size_t        DISCOVERY_PACKET_BYTES     = 512;

// --- Pulse detection — tuned in Round 2 from bench measurements ---
static const size_t        PULSE_BUFFER_SIZE          = 32;        // ISR ring buffer slots
static const unsigned long INTER_PULSE_MAX_MS         = 220;       // max normal gap within one production coin's pulse train. NO mode is the locked production setting and measures ~170 ms; NC mode is not supported by this tuning.
static const unsigned long NEXT_COIN_GAP_MS           = 195;       // if we already have a valid denomination and the next edge arrives this late, treat it as the next coin instead of merging fast back-to-back drops.
static const unsigned long COIN_FINALIZE_MS           = 230;       // silence after last pulse => coin complete. Kept above INTER_PULSE_MAX_MS so queued late edges split before the silence check fires.
static const unsigned long MIN_INTER_FALLING_MS       = 110;       // ISR debounce: filters the 91-93 ms ghost echo seen at the start of every real burst on this hardware (real pulse-to-pulse gap is 122-170 ms, comfortable margin)
static const unsigned long PULSE_WARMUP_MS            = 30000;     // Round 2: ignore all pulses for 30 s after boot. Kills GPIO-settle phantoms at attachInterrupt + early WiFi/MQTT noise spikes. Slot can't be claimed for credit during boot anyway (no station has pressed Insert Coin), so this is invisible to users.
static const uint8_t       MIN_VALID_PULSE_COUNT      = 1;         // Peso-count mode accepts single-pulse ₱1. Phantom safety relies on inhibit LOW outside a claim, the RC input filter, and ISR debounce during the short live claim window.
static const uint8_t       MAX_VALID_PULSE_COUNT      = 20;        // Acceptor is programmed ₱1=1, ₱5=5, ₱10=10, ₱20=20 pulses. Fast ₱1 stacks can merge safely because pulse count equals pesos.

// --- Round 3: NVS-backed pulse persistence ---
static const size_t        PULSE_NVS_SLOTS            = 64;        // unACK'd pulses buffered in flash. At ~30 pulses/day this is room for ~2 days of total MQTT outage before the buffer overflows and we start dropping.
static const unsigned long PULSE_REPUB_INTERVAL_MS    = 5000;      // app-level retry: if a slot has been published but no ACK arrived in 5 s, republish. Per the MQTT protocol spec; defends against rare cases where QoS 1's broker-level retry fails (e.g., broker restart with persistent-session expiry).
static const char*         PULSE_NVS_NAMESPACE        = "pulse";
static const char*         PULSE_NVS_SEQNO_KEY        = "seqno";
static const char*         PULSE_NVS_SLOT_KEY_PREFIX  = "s";        // slot keys: "s0", "s1", ..., "s63". Keys must be ≤ 15 chars (NVS limit).

// --- MQTT topic strings (built once in setup() from ACCEPTOR_ID) ---
static String topicStatus;
static String topicLed;
static String topicPulse;
static String topicPulseAck;

// --- State ---
static WiFiClient    netClient;
static PubSubClient  mqttClient(netClient);
static unsigned long lastHeartbeatMs       = 0;
static unsigned long lastMqttAttemptMs     = 0;
static unsigned long mqttBackoffMs         = MQTT_BACKOFF_INITIAL_MS;
static String        activeClaimId         = "";
static unsigned long ledAutoOffDeadlineMs  = 0;
static bool          ledOn                 = false;
static bool          acceptorInhibited     = false;

// --- Pulse ISR state ---
// ISR writes pulseBufHead, main loop writes pulseBufTail. portMUX protects
// the head/tail update sequence so the ISR and main loop don't race.
static volatile unsigned long pulseTimestamps[PULSE_BUFFER_SIZE];
static volatile uint32_t      pulseBufHead     = 0;
static volatile uint32_t      pulseBufTail     = 0;
static volatile uint32_t      pulseBufDropped  = 0;
static portMUX_TYPE           pulseMux         = portMUX_INITIALIZER_UNLOCKED;
// Last accepted FALLING timestamp, used by ISR to deduplicate oscillation bursts.
static volatile unsigned long lastAcceptedFallMs = 0;
static volatile uint32_t      pulseDedup         = 0;   // count of FALLINGs ignored as oscillation
static unsigned long          lastDedupLogMs     = 0;

// --- Burst state machine (main loop only — no ISR access) ---
enum BurstState { BURST_IDLE, BURST_COLLECTING };
static BurstState    burstState         = BURST_IDLE;
static uint8_t       burstPulseCount    = 0;
static unsigned long burstStartMs       = 0;
static unsigned long burstLastPulseMs   = 0;

// --- Per-device pulse seqNo (persisted to NVS in Round 3) ---
// Monotonic. Loaded from NVS on boot, written to NVS BEFORE each pulse is
// added to the unACK'd buffer. The write-seqNo-first ordering means a crash
// between the seqNo write and the slot write is recoverable: we'd lose the
// pulse record but never reuse a seqNo, which avoids server-side dedup
// collisions.
static uint32_t pulseSeqNo = 0;

// --- Round 3: NVS-backed unACK'd pulse buffer ---
// Each PulseRecord is what the firmware needs to reconstruct the MQTT pulse
// message on replay. millisAtFirstSee is local-only diagnostic — the value
// stored is the millis() at the moment the burst finalized on the boot that
// originally detected this coin, so after a reboot it points back at the
// pre-reboot uptime and is mostly useful for log archaeology.
struct __attribute__((packed)) PulseRecord {
    uint32_t seqNo;
    uint16_t denomination;     // 5, 10, 20 (₱1 later if recalibrated)
    uint8_t  pulseCount;
    uint8_t  _pad;             // explicit padding so sizeof() is stable across compilers
    uint32_t millisAtFirstSee;
};
static_assert(sizeof(PulseRecord) == 12, "PulseRecord layout drift — NVS-stored data on flash assumes 12-byte records");

// RAM mirror of the NVS buffer. RAM is a cache, NVS is the source of truth:
// writes go to NVS first, then update RAM. On boot, RAM is rebuilt from NVS.
static PulseRecord    pulseSlots[PULSE_NVS_SLOTS];
static bool           pulseSlotFilled[PULSE_NVS_SLOTS];      // is this slot holding an unACK'd pulse?
static unsigned long  pulseSlotLastPubMs[PULSE_NVS_SLOTS];   // millis() of last MQTT publish attempt, or 0 = "never published this boot, retry ASAP"

static Preferences    pulsePrefs;

// --- Forward declarations ---
static void   connectWifiManaged();
static void   reconnectWifi();
static bool   tryConnectWith(const char* ssid, const char* pass, unsigned long timeoutMs);
static String chipSuffix();
static bool  connectMqtt();
static bool  hostIsAuto();
static bool  resolveBrokerEndpoint();
static bool  resolveBrokerViaUdpBroadcast(IPAddress* outIp, uint16_t* outPort);
static void  publishOnlineStatus(bool retained);
static void  onMqttMessage(char* topic, byte* payload, unsigned int len);
static void  handleLedCommand(const byte* payload, unsigned int len);
static void  handlePulseAck(const byte* payload, unsigned int len);
static void  setLed(bool on);
static void  setAcceptorInhibit(bool inhibit);
static void  setupOta();
static bool  isOtaActive();
static void  IRAM_ATTR onCoinPulseIsr();
static bool  consumePulseTimestamp(unsigned long* out);
static void  processPulses();
static int   denominationFromCount(uint8_t count);
static bool  shouldSplitBurst(uint8_t currentPulseCount, unsigned long gapMs);
static void  publishPulse(int denomination, uint8_t pulseCount, unsigned long firstPulseMs);
static void  finalizeBurst();

// Round 3 — NVS persistence + replay
static void  pulseNvsInit();
static int   pulseFindFreeSlot();
static int   pulseFindSlotBySeqNo(uint32_t seqNo);
static void  pulseNvsWriteSlot(size_t slot, const PulseRecord& rec);
static void  pulseNvsClearSlot(size_t slot);
static void  pulseDoMqttPublish(size_t slot);
static void  pulseRetryTick();
static void  pulseMarkAllForReplay();
static void  pulseSlotKey(size_t slot, char* out, size_t outSize);

// ============================================================================
// WiFi
// ============================================================================

// Try one SSID/password; block up to timeoutMs. Returns the connected state.
static bool tryConnectWith(const char* ssid, const char* pass, unsigned long timeoutMs) {
    WiFi.begin(ssid, pass);
    const unsigned long deadline = millis() + timeoutMs;
    while (WiFi.status() != WL_CONNECTED && (long)(millis() - deadline) < 0) {
        delay(250);
        Serial.print('.');
    }
    Serial.println();
    return WiFi.status() == WL_CONNECTED;
}

// Short, unique suffix from the eFuse MAC for the setup-portal AP name.
static String chipSuffix() {
    uint16_t tail = (uint16_t)(ESP.getEfuseMac() & 0xFFFF);
    char buf[5];
    snprintf(buf, sizeof(buf), "%04X", tail);
    return String(buf);
}

// Boot-time Wi-Fi bring-up (Option A hybrid). If config.h carries Wi-Fi creds,
// try them first so the dev bench connects with no portal. A generic .bin
// leaves WIFI_SSID empty and goes straight to WiFiManager: it reconnects with
// the creds saved from a prior portal config, or opens the captive portal so an
// operator picks the shop Wi-Fi from a phone. Runs once, at boot only.
static void connectWifiManaged() {
    WiFi.mode(WIFI_STA);
    WiFi.setHostname(ACCEPTOR_ID);
    WiFi.setAutoReconnect(true);

    if (strlen(WIFI_SSID) > 0) {
        Serial.printf("[wifi] trying config.h SSID \"%s\" first...\n", WIFI_SSID);
        if (tryConnectWith(WIFI_SSID, WIFI_PASSWORD, WIFI_SEED_TIMEOUT_MS)) {
            Serial.printf("[wifi] Connected (config seed). IP: %s  RSSI: %d dBm\n",
                          WiFi.localIP().toString().c_str(), WiFi.RSSI());
            return;
        }
        Serial.println("[wifi] config.h creds failed; opening setup portal.");
    }

    // autoConnect first tries WiFiManager's own saved creds, then opens the AP
    // portal. setConfigPortalTimeout makes it return false after the window so
    // we reboot and retry rather than stay stuck in AP mode.
    WiFiManager wm;
    wm.setConfigPortalTimeout(WIFI_PORTAL_TIMEOUT_S);
    const String apName = String("Slotify-CoinBox-") + chipSuffix();
    Serial.printf("[wifi] portal \"%s\" (password-protected, %us timeout)\n",
                  apName.c_str(), WIFI_PORTAL_TIMEOUT_S);

    if (!wm.autoConnect(apName.c_str(), WIFI_PORTAL_PASSWORD)) {
        Serial.println("[wifi] portal timed out with no config; rebooting to retry.");
        delay(1000);
        ESP.restart();
    }

    Serial.printf("[wifi] Connected (portal/saved). IP: %s  RSSI: %d dBm  hostname: %s\n",
                  WiFi.localIP().toString().c_str(), WiFi.RSSI(), WiFi.getHostname());
}

// Runtime reconnect after a link drop. Uses whatever creds the device last
// connected with (saved in NVS — config seed OR portal config); never opens the
// portal on a transient drop. Reboots after a sustained outage so boot-time
// connectWifiManaged() can re-resolve (saved creds or, if those fail, portal).
static void reconnectWifi() {
    Serial.println("[wifi] Link lost. Reconnecting to saved AP...");
    WiFi.reconnect();
    const unsigned long deadline = millis() + WIFI_CONNECT_TIMEOUT_MS;
    while (WiFi.status() != WL_CONNECTED && (long)(millis() - deadline) < 0) {
        delay(500);
        Serial.print('.');
    }
    Serial.println();

    if (WiFi.status() != WL_CONNECTED) {
        Serial.println("[wifi] Reconnect TIMED OUT after 30s. Rebooting in 5s...");
        delay(5000);
        ESP.restart();
    }

    Serial.printf("[wifi] Reconnected. IP: %s  RSSI: %d dBm\n",
                  WiFi.localIP().toString().c_str(), WiFi.RSSI());
}

// ============================================================================
// MQTT
// ============================================================================

// Returns true when MQTT_HOST opts into mDNS discovery instead of a literal
// IP/host. Mirrors the "auto" sentinel used by the Windows/Android clients.
static bool hostIsAuto() {
    return MQTT_HOST[0] == '\0' || strcasecmp(MQTT_HOST, "auto") == 0;
}

// Resolve the broker endpoint into the MQTT client before a connect attempt.
// Literal-host configs use the compiled-in MQTT_HOST/MQTT_PORT unchanged. In
// "auto" mode we query mDNS for the Slotify broker service — multicast that
// Wi-Fi APs forward, unlike the server's wired-only UDP broadcast. Returns
// false only when auto-discovery is on and the service isn't found, so the
// caller skips this attempt and the backoff retries.
// Guardrail: resolves only host:port — the MQTT contract is unchanged.
static bool resolveBrokerEndpoint() {
    if (!hostIsAuto()) {
        mqttClient.setServer(MQTT_HOST, MQTT_PORT);
        return true;
    }

    // MDNS is already started by ArduinoOTA.begin() in setupOta(). Service and
    // protocol labels are passed WITHOUT leading underscores; ESPmDNS adds
    // them. queryService blocks until answers arrive or its timeout elapses.
    int n = MDNS.queryService("slotify-mqtt", "tcp");
    if (n <= 0) {
        Serial.println("[mdns] Slotify broker not found on the LAN; trying UDP discovery");
        IPAddress udpIp;
        uint16_t udpPort = MQTT_PORT;
        if (resolveBrokerViaUdpBroadcast(&udpIp, &udpPort)) {
            Serial.printf("[udp-discovery] broker resolved -> %s:%u\n",
                          udpIp.toString().c_str(), udpPort);
            mqttClient.setServer(udpIp, udpPort);
            return true;
        }

        Serial.println("[udp-discovery] Slotify broker not found on the LAN; will retry");
        return false;
    }

    IPAddress ip   = MDNS.IP(0);
    uint16_t  port = MDNS.port(0);
    Serial.printf("[mdns] broker resolved -> %s:%u via %s\n",
                  ip.toString().c_str(), port, MDNS.hostname(0).c_str());
    mqttClient.setServer(ip, port);
    return true;
}

static bool resolveBrokerViaUdpBroadcast(IPAddress* outIp, uint16_t* outPort) {
    WiFiUDP udp;
    if (!udp.begin(DISCOVERY_UDP_PORT)) {
        Serial.println("[udp-discovery] failed to bind UDP listener");
        return false;
    }

    const unsigned long deadline = millis() + DISCOVERY_UDP_TIMEOUT_MS;
    char buffer[DISCOVERY_PACKET_BYTES + 1];

    while ((long)(millis() - deadline) < 0) {
        ArduinoOTA.handle();

        int packetSize = udp.parsePacket();
        if (packetSize <= 0) {
            delay(25);
            continue;
        }

        int len = udp.read(buffer, DISCOVERY_PACKET_BYTES);
        if (len <= 0) {
            continue;
        }
        buffer[len] = '\0';

        JsonDocument doc;
        DeserializationError err = deserializeJson(doc, buffer);
        if (err) {
            Serial.printf("[udp-discovery] invalid JSON: %s\n", err.c_str());
            continue;
        }

        const char* type = doc["type"] | "";
        if (type[0] == '\0') {
            type = doc["Type"] | "";
        }
        if (strcmp(type, "rental-discovery") != 0) {
            continue;
        }

        const char* host = doc["brokerHost"] | "";
        if (host[0] == '\0') {
            host = doc["BrokerHost"] | "";
        }
        if (host[0] == '\0' || !outIp->fromString(host)) {
            Serial.printf("[udp-discovery] invalid broker host \"%s\"\n", host);
            continue;
        }

        int port = doc["brokerPort"] | 0;
        if (port <= 0) {
            port = doc["BrokerPort"] | MQTT_PORT;
        }
        *outPort = (uint16_t)port;
        udp.stop();
        return true;
    }

    udp.stop();
    return false;
}

static bool connectMqtt() {
    if (!resolveBrokerEndpoint()) {
        return false;  // auto mode + mDNS miss — let the backoff retry
    }

    Serial.printf("[mqtt] Connecting as clientId=%s ...\n", ACCEPTOR_ID);

    JsonDocument lwtDoc;
    lwtDoc["online"]   = false;
    lwtDoc["deviceId"] = ACCEPTOR_ID;
    char lwtBuf[128];
    size_t lwtLen = serializeJson(lwtDoc, lwtBuf, sizeof(lwtBuf));
    (void)lwtLen;

    // MQTT credentials: nullptr when the config macro is empty (anonymous
    // dev broker), otherwise a pointer to the string literal. PubSubClient
    // treats nullptr as "no auth".
    const char* mqttUser = (MQTT_USER[0] != '\0') ? MQTT_USER : nullptr;
    const char* mqttPass = (MQTT_PASS[0] != '\0') ? MQTT_PASS : nullptr;

    bool ok = mqttClient.connect(
        ACCEPTOR_ID,
        mqttUser, mqttPass,
        topicStatus.c_str(),
        1,
        true,
        lwtBuf,
        false  // clean_session = false
    );

    if (!ok) {
        Serial.printf("[mqtt] Connect FAILED state=%d\n", mqttClient.state());
        return false;
    }

    Serial.println("[mqtt] Connected.");
    publishOnlineStatus(true);
    mqttClient.subscribe(topicLed.c_str(), 1);
    mqttClient.subscribe(topicPulseAck.c_str(), 1);
    Serial.printf("[mqtt] Subscribed to %s\n", topicLed.c_str());
    Serial.printf("[mqtt] Subscribed to %s\n", topicPulseAck.c_str());

    lastHeartbeatMs = millis();
    return true;
}

static void publishOnlineStatus(bool retained) {
    JsonDocument doc;
    doc["online"]          = true;
    doc["deviceId"]        = ACCEPTOR_ID;
    doc["firmwareVersion"] = FIRMWARE_VERSION;
    doc["uptimeSec"]       = (uint32_t)(millis() / 1000UL);
    doc["rssi"]            = WiFi.RSSI();

    char buf[256];
    size_t n = serializeJson(doc, buf, sizeof(buf));

    bool ok = mqttClient.publish(topicStatus.c_str(), (const uint8_t*)buf, n, retained);
    Serial.printf("[mqtt] publish status retained=%d ok=%d payload=%.*s\n",
                  retained ? 1 : 0, ok ? 1 : 0, (int)n, buf);
}

static void onMqttMessage(char* topic, byte* payload, unsigned int len) {
    if (strcmp(topic, topicLed.c_str()) == 0) {
        handleLedCommand(payload, len);
    } else if (strcmp(topic, topicPulseAck.c_str()) == 0) {
        handlePulseAck(payload, len);
    } else {
        Serial.printf("[mqtt] unexpected topic: %s\n", topic);
    }
}

// ============================================================================
// LED command handling
// ============================================================================

static void setLed(bool on) {
    ledOn = on;
    digitalWrite(LED_PIN, on ? HIGH : LOW);
}

// Drive the Allan SET pin via INHIBIT_PIN.
// Current failsafe wiring adds a 10k resistor from SET signal to GND, so
// reset/offline/boot defaults to reject. Firmware drives LOW for reject and
// HIGH only during an active server-granted claim window.
static void setAcceptorInhibit(bool inhibit) {
    if (acceptorInhibited == inhibit) {
        return;
    }

    if (inhibit) {
        digitalWrite(INHIBIT_PIN, LOW);
        pinMode(INHIBIT_PIN, OUTPUT);
    } else {
        digitalWrite(INHIBIT_PIN, HIGH);
        pinMode(INHIBIT_PIN, OUTPUT);
    }
    acceptorInhibited = inhibit;
}

// ============================================================================
// OTA firmware update — ArduinoOTA over mDNS hostname (<ACCEPTOR_ID>.local)
// ============================================================================
//
// Once the perfboard is sealed inside the coin box, USB reflashing means
// opening the box. ArduinoOTA lets the dev box push a new firmware over
// WiFi instead. From the operator's side it's a one-liner from the
// platformio CLI:
//
//     pio run -t upload --upload-port coin-01.local
//
// or with the espota.py uploader directly:
//
//     espota.py -i <coin-box-ip> -a "<your OTA password>" -f firmware.bin
//
// Safety: on every OTA start the firmware drops the LED, inhibits the
// acceptor (so a coin drop mid-flash can't trigger the ISR while the
// flash is being rewritten), and detaches the pulse interrupt. On end
// the ESP32 reboots into the new firmware via the standard ArduinoOTA
// flow — no need to re-attach anything.

static volatile bool s_otaActive = false;

static bool isOtaActive() {
    return s_otaActive;
}

static void setupOta() {
    if (strlen(OTA_PASSWORD) == 0) {
        Serial.println("[ota] OTA_PASSWORD is empty — OTA disabled. USB uploads only.");
        return;
    }

    ArduinoOTA.setHostname(ACCEPTOR_ID);
    ArduinoOTA.setPassword(OTA_PASSWORD);

    ArduinoOTA.onStart([]() {
        s_otaActive = true;
        // Safety: muzzle the acceptor + kill the LED + detach the pulse ISR
        // before the flash rewrite starts. A coin drop landing in the
        // middle of an OTA write could trigger the ISR and trash the
        // flash transaction.
        setAcceptorInhibit(true);
        setLed(false);
        detachInterrupt(digitalPinToInterrupt(COIN_PULSE_PIN));

        const String kind = (ArduinoOTA.getCommand() == U_FLASH) ? "firmware" : "filesystem";
        Serial.printf("[ota] start (%s) — acceptor inhibited, ISR detached, LED off\n",
                      kind.c_str());
    });

    ArduinoOTA.onEnd([]() {
        Serial.println("[ota] end — rebooting into new firmware");
        // No need to clear s_otaActive — the next line of execution is the
        // reboot path inside ArduinoOTA.
    });

    ArduinoOTA.onProgress([](unsigned int progress, unsigned int total) {
        // Print every 10 % so the serial monitor (if attached) shows
        // forward motion without flooding.
        static unsigned int lastPct = 999;
        const unsigned int pct = total > 0 ? (progress * 100u) / total : 0u;
        if (pct / 10u != lastPct / 10u) {
            Serial.printf("[ota] progress %u%%\n", pct);
            lastPct = pct;
        }
    });

    ArduinoOTA.onError([](ota_error_t error) {
        s_otaActive = false;
        const char* reason = "unknown";
        switch (error) {
            case OTA_AUTH_ERROR:    reason = "auth failed (wrong password)"; break;
            case OTA_BEGIN_ERROR:   reason = "begin failed (partition not ready?)"; break;
            case OTA_CONNECT_ERROR: reason = "connect failed"; break;
            case OTA_RECEIVE_ERROR: reason = "receive failed (network drop mid-flash)"; break;
            case OTA_END_ERROR:     reason = "end failed (bad checksum?)"; break;
        }
        Serial.printf("[ota] ERROR %u — %s. Re-attaching pulse ISR; OTA can be retried.\n",
                      static_cast<unsigned int>(error), reason);
        // Recover: re-attach the ISR so coin operation resumes if the upload
        // fails mid-flight. Inhibit stays ON until the server next issues a
        // claim grant (LED on); that's the normal idle state anyway.
        attachInterrupt(digitalPinToInterrupt(COIN_PULSE_PIN), onCoinPulseIsr, FALLING);
    });

    ArduinoOTA.begin();
    Serial.printf("[ota] ready — hostname=%s.local port=3232\n", ACCEPTOR_ID);
}

static void handleLedCommand(const byte* payload, unsigned int len) {
    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, payload, len);
    if (err) {
        Serial.printf("[led] JSON parse error: %s\n", err.c_str());
        return;
    }

    const char* state      = doc["state"]      | "";
    const char* claimId    = doc["claimId"]    | "";
    int         autoOffSec = doc["autoOffSec"] | 35;

    if (strcmp(state, "on") == 0) {
        activeClaimId         = String(claimId);
        ledAutoOffDeadlineMs  = millis() + (unsigned long)autoOffSec * 1000UL;
        setLed(true);
        // Release the inhibit so the acceptor can detect the coin the
        // customer is about to drop. The safety auto-off timer will
        // re-engage the inhibit if the server's "off" command never arrives.
        setAcceptorInhibit(false);
        Serial.printf("[led] ON  claimId=%s autoOffSec=%d  inhibit=RELEASED\n", claimId, autoOffSec);
    } else if (strcmp(state, "off") == 0) {
        if (activeClaimId.length() == 0 || activeClaimId == claimId || strcmp(claimId, "force-off") == 0) {
            setLed(false);
            // Re-engage the inhibit immediately. The acceptor is electrically
            // muzzled again until the next claim grant.
            setAcceptorInhibit(true);
            Serial.printf("[led] OFF claimId=%s  inhibit=ENGAGED\n", claimId);
            activeClaimId        = "";
            ledAutoOffDeadlineMs = 0;
        } else {
            Serial.printf("[led] OFF IGNORED (stale): cmd=%s active=%s\n",
                          claimId, activeClaimId.c_str());
        }
    } else {
        Serial.printf("[led] unknown state value: \"%s\"\n", state);
    }
}

// ============================================================================
// Pulse ISR + burst grouping
// ============================================================================

// ISR — FALLING-only. Deduplicates any FALLING within MIN_INTER_FALLING_MS
// of the last accepted FALLING. This collapses two distinct problems into
// one window:
//   1. Sub-millisecond Schmitt oscillation on slow RC-filtered edges
//      (counted in pulseDedup).
//   2. A ~91-93 ms ghost FALLING that fires after the first real pulse
//      of every coin burst on this hardware (root cause likely opto/RC
//      tail dipping below VIL again on the way HIGH). Real pulse-to-pulse
//      gaps measured on this acceptor are 122-170 ms, so a 110 ms window
//      safely kills the ghost without eating real pulses. Subsequent
//      ghosts within a burst either fall inside this window or get
//      shadowed by the next real pulse arriving first.
// Width validation moves to a later round via a burst-integrity check
// rather than an ISR-side LOW-window measurement, which proved
// unreliable with the Schmitt-trigger behaviour on slow edges.
static void IRAM_ATTR onCoinPulseIsr() {
    const unsigned long nowMs = millis();
    portENTER_CRITICAL_ISR(&pulseMux);
    if (nowMs - lastAcceptedFallMs >= MIN_INTER_FALLING_MS) {
        uint32_t next = (pulseBufHead + 1) % PULSE_BUFFER_SIZE;
        if (next != pulseBufTail) {
            pulseTimestamps[pulseBufHead] = nowMs;
            pulseBufHead = next;
            lastAcceptedFallMs = nowMs;
        } else {
            pulseBufDropped++;
        }
    } else {
        pulseDedup++;
    }
    portEXIT_CRITICAL_ISR(&pulseMux);
}

static bool consumePulseTimestamp(unsigned long* out) {
    bool hasOne = false;
    portENTER_CRITICAL(&pulseMux);
    if (pulseBufTail != pulseBufHead) {
        *out = pulseTimestamps[pulseBufTail];
        pulseBufTail = (pulseBufTail + 1) % PULSE_BUFFER_SIZE;
        hasOne = true;
    }
    portEXIT_CRITICAL(&pulseMux);
    return hasOne;
}

static int denominationFromCount(uint8_t count) {
    if (count >= MIN_VALID_PULSE_COUNT && count <= MAX_VALID_PULSE_COUNT) {
        return count;
    }

    return 0;  // unknown — log and don't publish
}

static bool shouldSplitBurst(uint8_t currentPulseCount, unsigned long gapMs) {
    if (gapMs > INTER_PULSE_MAX_MS) {
        return true;
    }

    // Production NO mode has ~170 ms in-coin gaps. A much later edge after
    // any valid peso-count burst is usually a fast next coin, not the same
    // coin continuing.
    return currentPulseCount >= MIN_VALID_PULSE_COUNT &&
           gapMs >= NEXT_COIN_GAP_MS;
}

// Round 3 entry point from finalizeBurst. Mints a new seqNo, persists both
// the seqNo and a PulseRecord to NVS, then asks pulseDoMqttPublish to try
// putting it on the wire. If MQTT is down or the publish fails, the record
// stays in NVS and pulseRetryTick will keep republishing every 5 s until
// the server ACKs it.
static void publishPulse(int denomination, uint8_t pulseCount, unsigned long firstPulseMs) {
    int slot = pulseFindFreeSlot();
    if (slot < 0) {
        // Buffer overflow — every slot is holding an unACK'd pulse. This means
        // MQTT has been down long enough to accumulate 64 pulses (≈ 2 days of
        // typical use). Dropping the new pulse loses one coin's credit, but
        // overwriting an older slot would lose a coin's credit too AND erase
        // a pulse we might still be able to deliver. So drop the new one and
        // log loudly — the operator should see this in serial / admin UI.
        Serial.printf("[pulse] BUFFER FULL — dropping new pulse denom=%d count=%u. MQTT outage too long; check broker connectivity.\n",
                      denomination, pulseCount);
        return;
    }

    // Mint seqNo and persist it to NVS BEFORE writing the slot. This ordering
    // matters for crash recovery: if we crash between the seqNo write and the
    // slot write, on reboot we'll have the new seqNo but no slot record. The
    // pulse is lost — but we never reuse the seqNo, so the server's dedup
    // pointer stays consistent. The opposite ordering (slot first, seqNo
    // second) would let us reuse a seqNo on next pulse, which would either
    // silently dedup at the server or worse, get credited twice depending on
    // exact ACK timing.
    uint32_t seqNo = ++pulseSeqNo;
    pulsePrefs.putUInt(PULSE_NVS_SEQNO_KEY, pulseSeqNo);

    PulseRecord rec;
    rec.seqNo            = seqNo;
    rec.denomination     = (uint16_t)denomination;
    rec.pulseCount       = pulseCount;
    rec._pad             = 0;
    rec.millisAtFirstSee = (uint32_t)firstPulseMs;
    pulseNvsWriteSlot((size_t)slot, rec);

    Serial.printf("[pulse] persisted slot=%d seqNo=%u denom=%d count=%u\n",
                  slot, seqNo, denomination, pulseCount);

    // Attempt the first publish immediately. pulseDoMqttPublish handles the
    // not-connected case by leaving lastPubMs at 0, which tells the retry
    // tick to try again as soon as MQTT is up.
    pulseDoMqttPublish((size_t)slot);
}

// ============================================================================
// Round 3 — NVS-backed pulse buffer (helpers)
// ============================================================================

static void pulseSlotKey(size_t slot, char* out, size_t outSize) {
    snprintf(out, outSize, "%s%u", PULSE_NVS_SLOT_KEY_PREFIX, (unsigned)slot);
}

static void pulseNvsInit() {
    // begin() opens the namespace in read-write mode. It's safe to call once
    // at boot and keep the handle open for the lifetime of the program — the
    // ESP-IDF NVS layer handles concurrency internally.
    if (!pulsePrefs.begin(PULSE_NVS_NAMESPACE, false)) {
        Serial.println("[nvs] FATAL: Preferences.begin failed for 'pulse' namespace");
        // Continue running with a non-persistent seqNo. The system is degraded
        // but not bricked — coins will still be detected, just without crash
        // recovery.
        return;
    }

    pulseSeqNo = pulsePrefs.getUInt(PULSE_NVS_SEQNO_KEY, 0);
    Serial.printf("[nvs] loaded seqNo=%u\n", pulseSeqNo);

    // Walk all slot keys and rebuild the RAM mirror. Check isKey() before
    // getBytes — without that probe the ESP-IDF NVS layer logs an [E] error
    // for every missing key, which spams 60+ red lines into the serial
    // monitor on every boot. isKey() is the silent existence check.
    size_t loaded = 0;
    for (size_t i = 0; i < PULSE_NVS_SLOTS; ++i) {
        char key[8];
        pulseSlotKey(i, key, sizeof(key));

        pulseSlotFilled[i]    = false;
        pulseSlotLastPubMs[i] = 0;

        if (!pulsePrefs.isKey(key)) continue;  // empty slot, no log noise

        PulseRecord rec;
        size_t got = pulsePrefs.getBytes(key, &rec, sizeof(rec));
        if (got == sizeof(rec) && rec.seqNo != 0) {
            pulseSlots[i]      = rec;
            pulseSlotFilled[i] = true;
            loaded++;
            Serial.printf("[nvs] loaded slot %u seqNo=%u denom=%u count=%u\n",
                          (unsigned)i, rec.seqNo, rec.denomination, rec.pulseCount);
        }
    }
    Serial.printf("[nvs] init complete — %u unACK'd pulses recovered from flash\n", (unsigned)loaded);
}

static int pulseFindFreeSlot() {
    for (size_t i = 0; i < PULSE_NVS_SLOTS; ++i) {
        if (!pulseSlotFilled[i]) return (int)i;
    }
    return -1;
}

static int pulseFindSlotBySeqNo(uint32_t seqNo) {
    for (size_t i = 0; i < PULSE_NVS_SLOTS; ++i) {
        if (pulseSlotFilled[i] && pulseSlots[i].seqNo == seqNo) return (int)i;
    }
    return -1;
}

static void pulseNvsWriteSlot(size_t slot, const PulseRecord& rec) {
    char key[8];
    pulseSlotKey(slot, key, sizeof(key));
    pulsePrefs.putBytes(key, &rec, sizeof(rec));
    pulseSlots[slot]         = rec;
    pulseSlotFilled[slot]    = true;
    pulseSlotLastPubMs[slot] = 0;
}

static void pulseNvsClearSlot(size_t slot) {
    char key[8];
    pulseSlotKey(slot, key, sizeof(key));
    pulsePrefs.remove(key);
    pulseSlotFilled[slot]    = false;
    pulseSlotLastPubMs[slot] = 0;
}

// Build the MQTT JSON payload from a slot and try to publish. Updates
// lastPubMs only if the publish actually went out; on disconnect or failure
// the slot stays at its previous lastPubMs (or 0) so the retry loop will
// keep trying.
static void pulseDoMqttPublish(size_t slot) {
    if (!pulseSlotFilled[slot]) return;
    const PulseRecord& rec = pulseSlots[slot];

    if (!mqttClient.connected()) {
        Serial.printf("[pulse] queued (no MQTT) slot=%u seqNo=%u — will retry on reconnect\n",
                      (unsigned)slot, rec.seqNo);
        return;
    }

    JsonDocument doc;
    doc["seqNo"]           = rec.seqNo;
    doc["denomination"]    = (int)rec.denomination;
    doc["pulseCount"]      = rec.pulseCount;
    doc["millisSinceBoot"] = rec.millisAtFirstSee;

    char buf[256];
    size_t n = serializeJson(doc, buf, sizeof(buf));

    bool ok = mqttClient.publish(topicPulse.c_str(), (const uint8_t*)buf, n, false);
    if (ok) {
        pulseSlotLastPubMs[slot] = millis();
        Serial.printf("[pulse] publish ok slot=%u seqNo=%u denom=%u count=%u\n",
                      (unsigned)slot, rec.seqNo, rec.denomination, rec.pulseCount);
    } else {
        // publish() can return false when the TCP send buffer is backed up
        // or the client is in a weird state. Don't mark as published —
        // pulseRetryTick will try again on the next iteration.
        Serial.printf("[pulse] publish FAILED slot=%u seqNo=%u — will retry\n",
                      (unsigned)slot, rec.seqNo);
    }
}

// Called from the main loop on every iteration. Cheap: just scans 64 slots
// looking for ones that need (re)publishing. Two cases trigger a publish:
//   1. lastPubMs == 0 — slot has never been published this boot. Happens
//      after pulseNvsInit (replay-from-flash) and after a reconnect that
//      called pulseMarkAllForReplay.
//   2. now - lastPubMs > PULSE_REPUB_INTERVAL_MS — we've waited 5 s for
//      the ACK and it never came. Republish.
static void pulseRetryTick() {
    if (!mqttClient.connected()) return;
    const unsigned long now = millis();
    for (size_t i = 0; i < PULSE_NVS_SLOTS; ++i) {
        if (!pulseSlotFilled[i]) continue;
        const unsigned long last = pulseSlotLastPubMs[i];
        if (last == 0 || (now - last) > PULSE_REPUB_INTERVAL_MS) {
            if (last != 0) {
                Serial.printf("[pulse] retry slot=%u seqNo=%u (ACK timeout after %lu ms)\n",
                              (unsigned)i, pulseSlots[i].seqNo, now - last);
            }
            pulseDoMqttPublish(i);
        }
    }
}

// Reset all filled slots to "needs publishing" so the next pulseRetryTick
// republishes them immediately. Called after a successful MQTT (re)connect.
static void pulseMarkAllForReplay() {
    size_t marked = 0;
    for (size_t i = 0; i < PULSE_NVS_SLOTS; ++i) {
        if (pulseSlotFilled[i]) {
            pulseSlotLastPubMs[i] = 0;
            marked++;
        }
    }
    if (marked > 0) {
        Serial.printf("[pulse] %u unACK'd pulse(s) queued for replay\n", (unsigned)marked);
    }
}

static void finalizeBurst() {
    if (burstPulseCount == 0) return;

    unsigned long durationMs = burstLastPulseMs - burstStartMs;
    int denomination = denominationFromCount(burstPulseCount);
    bool claimWindowOpen = ledOn;

    // The SET/inhibit wire should prevent this outside a claim. If hardware
    // still emits pulses, keep the protocol safe: no NVS record, no MQTT
    // publish, and no possible credit unless the server opened the slot.
    if (!claimWindowOpen) {
        Serial.printf("[pulse] DROP outside claim window count=%u denom=%d\n",
                      burstPulseCount, denomination);
        goto reset;
    }

    Serial.printf("[pulse] burst complete count=%u duration=%lu ms denom=%d\n",
                  burstPulseCount, durationMs, denomination);

    // Round 2 guard #1 — warm-up window. The 30 s after boot is when the
    // GPIO line settles, WiFi associates, MQTT subscribes, and any opto
    // start-up transient resolves. Any burst formed in this window is
    // overwhelmingly noise. Suppress it silently from MQTT (still logged
    // so we can see it being caught).
    if (millis() < PULSE_WARMUP_MS) {
        Serial.printf("[pulse] DROP warm-up (boot+%lu ms < %lu ms) count=%u\n",
                      millis(), PULSE_WARMUP_MS, burstPulseCount);
        goto reset;
    }

    // Round 2 guard #2 — single-pulse bursts. Idle GPIO phantoms on this
    // hardware are isolated single FALLINGs (count=1, duration=0). Real
    // S21b: filter relaxed to MIN_VALID_PULSE_COUNT=1 so the Allan's ₱1
    // (single-pulse on H1) is accepted. Phantom defense now leans on the
    // hardware inhibit (SET pin) which keeps the acceptor's output transistor
    // muzzled outside claim windows; the ISR debounce + RC filter handle the
    // sub-ms noise floor that could still leak when the LED is on.
    if (burstPulseCount < MIN_VALID_PULSE_COUNT) {
        Serial.printf("[pulse] DROP suspect single pulse (count<%u) — likely idle noise\n",
                      MIN_VALID_PULSE_COUNT);
        goto reset;
    }

    if (denomination > 0) {
        publishPulse(denomination, burstPulseCount, burstStartMs);
    } else {
        Serial.printf("[pulse] REJECT unknown pulse count=%u\n", burstPulseCount);
    }

reset:
    burstState       = BURST_IDLE;
    burstPulseCount  = 0;
    burstStartMs     = 0;
    burstLastPulseMs = 0;
}

static void processPulses() {
    // Drain any ISR-queued timestamps
    unsigned long ts;
    while (consumePulseTimestamp(&ts)) {
        if (burstState == BURST_IDLE) {
            burstState       = BURST_COLLECTING;
            burstPulseCount  = 1;
            burstStartMs     = ts;
            burstLastPulseMs = ts;
            Serial.printf("[pulse] start burst @ t=%lu\n", ts);
        } else {  // BURST_COLLECTING
            unsigned long gap = ts - burstLastPulseMs;
            if (shouldSplitBurst(burstPulseCount, gap)) {
                // gap too long for same coin — finalize previous and start new
                Serial.printf("[pulse] gap=%lu ms split previous count=%u (next-coin threshold=%lu ms, hard max=%lu ms)\n",
                              gap, burstPulseCount, NEXT_COIN_GAP_MS, INTER_PULSE_MAX_MS);
                finalizeBurst();
                burstState       = BURST_COLLECTING;
                burstPulseCount  = 1;
                burstStartMs     = ts;
                burstLastPulseMs = ts;
            } else {
                burstPulseCount++;
                burstLastPulseMs = ts;
                Serial.printf("[pulse] +pulse count=%u gap=%lu ms\n", burstPulseCount, gap);
                if (burstPulseCount >= MAX_VALID_PULSE_COUNT) {
                    Serial.printf("[pulse] max valid count=%u reached, finalize immediately\n",
                                  MAX_VALID_PULSE_COUNT);
                    finalizeBurst();
                }
            }
        }
    }

    // Quiescence check — if we've been silent long enough, the coin is done
    if (burstState == BURST_COLLECTING &&
        (millis() - burstLastPulseMs) > COIN_FINALIZE_MS) {
        finalizeBurst();
    }

    // Log dropped pulses (ISR buffer full) — should never happen at ≤1 coin/sec
    if (pulseBufDropped > 0) {
        portENTER_CRITICAL(&pulseMux);
        uint32_t dropped = pulseBufDropped;
        pulseBufDropped = 0;
        portEXIT_CRITICAL(&pulseMux);
        Serial.printf("[pulse] WARN: ISR buffer dropped %u pulses\n", dropped);
    }

    // Periodic dedup-report (every 5 s if any oscillation bursts happened)
    if (pulseDedup > 0 && (millis() - lastDedupLogMs) > 5000) {
        portENTER_CRITICAL(&pulseMux);
        uint32_t dedup = pulseDedup;
        pulseDedup = 0;
        portEXIT_CRITICAL(&pulseMux);
        Serial.printf("[pulse] dedup'd %u oscillation edges in last 5 s (within %lu ms of an accepted pulse)\n",
                      dedup, MIN_INTER_FALLING_MS);
        lastDedupLogMs = millis();
    }
}

// ============================================================================
// Pulse ACK handling — evicts the acknowledged seqNo from the NVS buffer
// ============================================================================

static void handlePulseAck(const byte* payload, unsigned int len) {
    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, payload, len);
    if (err) {
        Serial.printf("[pulse] ack JSON parse error: %s\n", err.c_str());
        return;
    }
    uint32_t ackedSeqNo = doc["seqNo"] | 0;
    if (ackedSeqNo == 0) {
        Serial.println("[pulse] ack missing seqNo field — ignoring");
        return;
    }

    int slot = pulseFindSlotBySeqNo(ackedSeqNo);
    if (slot < 0) {
        // Spec says the server ACKs duplicates too (so the firmware stops
        // retrying). A duplicate ACK is the normal case for retries: the
        // server processed the pulse, sent the ACK, but the ACK got lost,
        // we republished, server ACKs again. We already cleared the slot on
        // the first ACK, so the second one finds nothing. Not an error.
        Serial.printf("[pulse] ack seqNo=%u — slot already cleared (duplicate ACK, fine)\n",
                      ackedSeqNo);
        return;
    }
    pulseNvsClearSlot((size_t)slot);
    Serial.printf("[pulse] ACK'd seqNo=%u — slot %d cleared from NVS\n", ackedSeqNo, slot);
}

// ============================================================================
// Lifecycle
// ============================================================================

void setup() {
    Serial.begin(SERIAL_BAUD);
    delay(100);
    Serial.println();
    Serial.println();
    Serial.println("=================================================");
    Serial.println("  Rental coin acceptor firmware");
    Serial.printf ("  Acceptor ID: %s\n", ACCEPTOR_ID);
    Serial.printf ("  Firmware version: %s\n", FIRMWARE_VERSION);
    Serial.printf ("  Build: %s %s\n", __DATE__, __TIME__);
    Serial.println("=================================================");

    pinMode(LED_PIN, OUTPUT);
    digitalWrite(LED_PIN, LOW);
    pinMode(COIN_PULSE_PIN, INPUT);

    // Inhibit the acceptor immediately. This runs before WiFi/MQTT come up
    // and before the pulse ISR attaches, so the acceptor is electrically
    // muzzled throughout boot. Any 12 V supply transient or LED-frame noise
    // burst during early boot cannot trigger a phantom pulse because the
    // acceptor's output stage is held in reset by the SET pin.
    setAcceptorInhibit(true);
    Serial.printf("[inhibit] boot state = INHIBITED (GPIO %u OUTPUT LOW)\n", INHIBIT_PIN);

    // Build topic strings once
    topicStatus    = String("rental/coinacceptor/") + ACCEPTOR_ID + "/status";
    topicLed       = String("rental/coinacceptor/") + ACCEPTOR_ID + "/led";
    topicPulse     = String("rental/coinacceptor/") + ACCEPTOR_ID + "/pulse";
    topicPulseAck  = String("rental/coinacceptor/") + ACCEPTOR_ID + "/pulse/ack";

    // Round 3 — open NVS and rebuild the in-RAM buffer mirror. Must run
    // BEFORE the ISR attaches: otherwise a fast post-boot pulse could try
    // to allocate a slot before the slot table is initialized.
    pulseNvsInit();

    connectWifiManaged();

    // OTA needs WiFi. Skip if WiFi failed — connectWifiManaged() reboots on
    // portal timeout, so reaching this line means WL_CONNECTED.
    setupOta();

    // Broker endpoint is resolved inside connectMqtt() (per-attempt, so "auto"
    // mDNS mode re-resolves on each reconnect). Only one-time tunables here.
    mqttClient.setCallback(onMqttMessage);
    mqttClient.setBufferSize(MQTT_BUFFER_SIZE);
    mqttClient.setKeepAlive(MQTT_KEEPALIVE_S);

    if (connectMqtt()) {
        mqttBackoffMs = MQTT_BACKOFF_INITIAL_MS;
        // Replay any unACK'd pulses recovered from flash. pulseRetryTick on
        // the next loop iteration will pick these up and publish them.
        pulseMarkAllForReplay();
    }

    // Attach the pulse ISR. FALLING-only with an in-ISR debounce window —
    // any FALLING within MIN_INTER_FALLING_MS of the last accepted one is
    // counted as oscillation and dropped. Real coin pulses come ~170 ms
    // apart in NO mode, way outside the debounce window.
    attachInterrupt(digitalPinToInterrupt(COIN_PULSE_PIN), onCoinPulseIsr, FALLING);
    Serial.printf("[pulse] ISR attached on GPIO %u (FALLING, ISR debounce=%lu ms)\n",
                  COIN_PULSE_PIN, MIN_INTER_FALLING_MS);
}

void loop() {
    // OTA — has to run every loop iteration. When an upload is in flight,
    // ArduinoOTA.handle() blocks here until the flash write completes (or
    // errors). That's fine because we've already inhibited the acceptor +
    // detached the pulse ISR in the onStart callback, so MQTT activity
    // during the upload is irrelevant — the broker will see us drop and
    // reconnect after the device reboots into the new firmware.
    ArduinoOTA.handle();

    // While OTA is mid-flash, skip the rest of loop(): MQTT publishes
    // during a flash write can corrupt timing, and we don't want to fight
    // the WiFi stack for bandwidth.
    if (isOtaActive()) {
        return;
    }

    // WiFi keepalive
    if (WiFi.status() != WL_CONNECTED) {
        reconnectWifi();
        mqttBackoffMs     = MQTT_BACKOFF_INITIAL_MS;
        lastMqttAttemptMs = 0;
    }

    // MQTT keepalive with exponential backoff
    if (!mqttClient.connected()) {
        const unsigned long now = millis();
        if (now - lastMqttAttemptMs > mqttBackoffMs) {
            lastMqttAttemptMs = now;
            Serial.printf("[mqtt] reconnect attempt (current backoff=%lu ms)\n", mqttBackoffMs);
            if (connectMqtt()) {
                Serial.println("[mqtt] reconnect succeeded; backoff reset to initial");
                mqttBackoffMs = MQTT_BACKOFF_INITIAL_MS;
                // Round 3 — flush the unACK'd buffer to the freshly-connected
                // broker. pulseRetryTick will fire on the next loop iteration.
                pulseMarkAllForReplay();
            } else {
                mqttBackoffMs *= 2;
                if (mqttBackoffMs > MQTT_BACKOFF_MAX_MS) {
                    mqttBackoffMs = MQTT_BACKOFF_MAX_MS;
                }
                Serial.printf("[mqtt] reconnect failed; next attempt in %lu ms\n", mqttBackoffMs);
            }
        }
    } else {
        mqttClient.loop();

        const unsigned long now = millis();
        if (now - lastHeartbeatMs > HEARTBEAT_INTERVAL_MS) {
            publishOnlineStatus(true);
            lastHeartbeatMs = now;
        }
    }

    // Coin pulse detection — runs every loop iteration regardless of MQTT
    // state. processPulses → finalizeBurst → publishPulse → pulseDoMqttPublish.
    // When MQTT is down, the pulse still lands in NVS; pulseRetryTick will
    // ship it to the broker once the link comes back.
    processPulses();

    // Belt-and-suspenders: idle must always mean inhibited. This keeps the
    // SET line pulled low after reconnect churn, stale commands, or any
    // unexpected state transition that left the acceptor released.
    if (!ledOn) {
        setAcceptorInhibit(true);
    }

    // Round 3 — drive the app-level retry. Cheap: bails immediately if MQTT
    // isn't connected or no slots need attention.
    pulseRetryTick();

    // LED safety auto-off. If the server's explicit "off" never arrives
    // within autoOffSec of the "on", we fall back to a safe state: LED off
    // AND acceptor re-inhibited. This defends against a server crash mid-
    // claim leaving the slot accepting coins indefinitely.
    if (ledOn && ledAutoOffDeadlineMs != 0 && (long)(millis() - ledAutoOffDeadlineMs) > 0) {
        Serial.printf("[led] safety auto-off (deadline reached) claimId=%s — re-inhibiting acceptor\n",
                      activeClaimId.c_str());
        setLed(false);
        setAcceptorInhibit(true);
        activeClaimId        = "";
        ledAutoOffDeadlineMs = 0;
    }

    delay(10);
}
