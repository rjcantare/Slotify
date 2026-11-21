#pragma once

// Copy this file to include/config.h and fill in your local values.
// include/config.h is gitignored and never leaves your machine.

// ----- WiFi credentials -----
#define WIFI_SSID       "YourSSID"
#define WIFI_PASSWORD   "YourWiFiPassword"

// ----- MQTT broker -----
// "auto" discovers the server via mDNS (_slotify-mqtt._tcp) — no IP to
// hardcode and it works over Wi-Fi (the server must be running). The port is
// taken from the discovered service. Alternatively set the server's fixed LAN
// IP for a static config, in which case MQTT_PORT below is used as-is.
#define MQTT_HOST       "auto"
#define MQTT_PORT       1883

// ----- MQTT broker credentials -----
// Leave MQTT_USER empty for anonymous dev broker (no auth).
// When set, MQTT_PASS must also be set. The pair is passed to
// PubSubClient.connect(); nullptr means "anonymous".
// In production, the broker should reject anonymous clients — see
// the Mosquitto docs (password_file / allow_anonymous false) for broker setup.
#define MQTT_USER       ""
#define MQTT_PASS       ""

// ----- Device identity -----
#define ACCEPTOR_ID      "coin-01"
#define FIRMWARE_VERSION "1.0.0"

// ----- OTA firmware update -----
// Password gating ArduinoOTA. Anyone on the LAN with this password can
// reflash the device — pick something not-guessable per shop. Empty
// disables OTA entirely (uploads via USB only).
#define OTA_PASSWORD     ""   // Public/shared builds MUST leave this empty. Set a unique value only in your own private build.

// ----- WiFi setup portal (WiFiManager) -----
// If the box can't join Wi-Fi (WIFI_SSID empty or wrong), it opens its own
// hotspot "Slotify-CoinBox-XXXX" so an operator picks the shop Wi-Fi from a
// phone — no recompile. This password gates that portal (WPA2 needs >= 8 chars).
// Leave WIFI_SSID empty above to build a generic .bin that always uses the portal.
#define WIFI_PORTAL_PASSWORD "slotify-setup"
