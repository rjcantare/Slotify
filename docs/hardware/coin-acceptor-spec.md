# Coin acceptor spec — Allan 1239A Pro Max + ESP32 firmware

Final tuned thresholds, locked configuration, and known limitations for the coin-acceptor side of the rental system. Bench-measured during S11 (2026-05-15..16) and extended 2026-05-18 with hardware inhibit gating after reverse-engineering the AdoFox firmware. Hardware design lives in `esp32-wiring.md`; this file documents the firmware contract on top of it.

When the firmware in `C:\Projects\RentalCoinAcceptor\src\main.cpp` and this file disagree, the firmware wins — but the change must be reflected here in the same commit.

## 2026-05-20 inhibit failsafe update

The SET inhibit design now includes R8: a 10 kΩ pulldown from Allan SET upper/signal to common GND. GPIO23 shares that SET signal node. Firmware drives GPIO23 LOW while idle and HIGH only during a server-granted claim window. This supersedes older notes in this file that describe GPIO23 `INPUT`/high-Z as the release state or floating SET as acceptable. Floating SET was bench-proven unsafe because it lets the acceptor swallow coins while ESP32 is reset/offline.

## Architectural summary (read this first)

The acceptor is **electrically muzzled by default**. GPIO 23 holds the Allan's SET pin at GND, which puts the acceptor's output transistor in reset — coins inserted while muzzled are physically rejected through the coin-return slot and produce zero electrical activity on the COIN signal line. The acceptor is only released for the duration of a server-issued LED-on (claim grant) window. Re-inhibited on LED-off, on safety auto-off timer expiry, and at boot before the pulse ISR attaches.

This makes phantom pulses **physically impossible** during the ~99 % of the day the system is idle. Software filters (warm-up, ISR debounce, burst splitters, max-count reject, NVS replay) remain in place as defense-in-depth but are no longer load-bearing.

---

## Locked settings (acceptor side)

These are physical DIP / push-button settings on the Allan 1239A Pro Max. Changing any of them invalidates the thresholds below.

| Setting | Value | Why |
|---|---|---|
| Output polarity | **NO** (normally open) | Bench-measured ~100× lower phantom rate than NC. NC keeps the output transistor saturated in idle, biasing it in a noise-sensitive operating point; NO keeps it OFF at rest. NO also produces uniform 170 ms inter-pulse spacing (NC has a 195–235 ms "priming" first gap that complicates filtering). |
| Speed | **Slow** | Slow gives the widest pulse-to-noise discrimination margin. The firmware's debounce, INTER_PULSE_MAX, and warm-up thresholds are sized for Slow timing. Switching to Medium or Fast requires re-tuning every constant in main.cpp. |
| H1 (₱1) | 1 pulse | Peso-count mode. |
| H2 (₱5 new), H3 (₱5 old) | 5 pulses each | Peso-count mode. |
| H4 (₱10 new), H5 (₱10 old) | 10 pulses each | Peso-count mode. |
| H6 (₱20) | 20 pulses | Peso-count mode. |

## Inhibit gating (SET pin)

The Allan exposes its inhibit input as a **2-pin header on the back panel labelled "SET 設定"**, located below the Fast/Medium/Slow speed switch. This is item ⑤ in the datasheet, listed there as "Optional" because not every variant of this acceptor brings the inhibit out to a connector — ours does. 2.54 mm pitch, accepts standard Dupont F/F jumpers directly. The upper pin is the signal (idle voltage +3 V via the acceptor's internal pull-up). The lower pin is GND and is already common with the system GND through the main 4-wire harness — does not need separate wiring.

**Polarity is inverted from the AdoFox universal acceptor** (a finding that matters when adapting the AdoFox approach to this hardware):

| GPIO 23 state | SET pin voltage | Acceptor behavior |
|---|---|---|
| OUTPUT LOW | ~0 V (pulled to GND) | **Inhibited** — coin physically rejected, output transistor in reset, zero MQTT pulse |
| INPUT (high-Z) | ~3 V (floats via internal pull-up) | **Active** — coin accepted, pulse train fires normally |

This polarity is fail-safe in the sense that a firmware crash or unexpected reset leaves GPIO 23 in INPUT mode = acceptor active = coins still work, just with the older S11 software-only defense stack catching phantoms. The opposite polarity would have meant a crashed firmware locked the slot.

**Wiring is one Dupont F/F jumper**: ESP32 GPIO 23 → upper SET pad on the Allan back panel. No resistor (signal idle is 3 V, within the GPIO's 3.3 V tolerance). No additional GND wire (SET-GND already common with system GND).

**Firmware state machine:**

- `setup()` — calls `setAcceptorInhibit(true)` **before** `attachInterrupt` on GPIO 18. Closes the boot-transient window: ESP32 GPIO settling, RC filter charging, and WiFi/MQTT bring-up all happen with the acceptor electrically muzzled.
- `handleLedCommand` on `state:"on"` — calls `setAcceptorInhibit(false)`. Acceptor accepts coins for the duration of the claim window.
- `handleLedCommand` on `state:"off"` (server explicit close, or LED-off after pulse ACK) — calls `setAcceptorInhibit(true)`. Acceptor re-muzzled.
- Safety auto-off timer expiry (`autoOffSec` elapsed without server "off") — same as explicit off: LED off plus re-inhibit. Defends against server crash mid-claim leaving the slot accepting indefinitely.

---

## Measured pulse train characteristics

All numbers from bench captures with the **acceptor side fully assembled** as built in S9 — Allan 1239A Pro Max → R1+R2 → EL817 LED → opto → R3 pull-up → R4+C1 RC filter (τ ≈ 1 ms) → ESP32 GPIO 18 with internal Schmitt input.

| Metric | NO mode (locked in) | NC mode (rejected) |
|---|---|---|
| First inter-pulse gap of a coin's train | 170 ms (uniform) | 195–235 ms |
| Subsequent inter-pulse gaps | 170 ms | 169–171 ms |
| Ghost echo after each real pulse | absent on most pulses; 91–93 ms when present (start of burst only) | same |
| Sub-millisecond Schmitt oscillation per real edge | 50–400 sub-`MIN_INTER_FALLING_MS` events | similar |
| Idle phantom rate (LED frame **connected** to 12 V) | ~25–30/min, plus rare multi-pulse phantom bursts that mimic coin shapes | similar |
| Idle phantom rate (LED frame **disconnected** from 12 V) | ~0.35/min single FALLINGs | ~0.4/min |
| Idle phantom burst shape | single FALLING, duration=0 (historically caught by count<2; now primarily blocked by inhibit outside claim windows) | same |
| Phantom-merge into real coin (LED frame off) | ~0.2% probability per drop | ~25% (mostly during dirty NC startup) |

The LED frame noise dominance is the single most important finding from S11. See "LED frame" below.

---

## Locked firmware thresholds

All defined in `src/main.cpp` of the firmware repo. Don't change without re-running the S11 Phase 1/2/3 validation.

**2026-06-07 peso-count update:** firmware now assumes the locked production
NO output mode and the acceptor must be programmed so pulse count equals pesos
inserted: ₱1=1, ₱5=5, ₱10=10, ₱20=20. Fast ₱1 stacks may merge into one burst,
but the merged pulse count still equals the money inserted.

- `INTER_PULSE_MAX_MS = 220`
- `NEXT_COIN_GAP_MS = 195`
- `COIN_FINALIZE_MS = 230`
- `MAX_VALID_PULSE_COUNT = 20`, which finalizes a ₱20 coin immediately after the twentieth pulse

| Constant | Value | Role |
|---|---|---|
| `MIN_INTER_FALLING_MS` | 110 ms | ISR-side debounce. Collapses sub-millisecond Schmitt oscillation **and** the 91–93 ms ghost echo at the start of every multi-pulse burst. Below the 170 ms real inter-pulse gap, comfortable margin both sides. |
| `INTER_PULSE_MAX_MS` | 220 ms | Hard burst splitter for production NO mode. A FALLING beyond this after the previous one is treated as a new coin, not the next pulse of the current coin. |
| `NEXT_COIN_GAP_MS` | 195 ms | Early splitter for fast back-to-back coins. If the current burst is already a valid peso-count burst and the next edge arrives this late, firmware finalizes the previous burst and starts a new one. |
| `COIN_FINALIZE_MS` | 230 ms | After this many ms of silence post-last-pulse, the burst is final and gets evaluated. |
| `PULSE_WARMUP_MS` | 30000 ms | Suppresses all bursts published to MQTT during the first 30 s after boot. Kills GPIO-settle phantoms at `attachInterrupt`, early WiFi/MQTT start-up transients, and any opto bias settling. Slot can't be claimed until a station has pressed Insert Coin, so this 30 s blackout is invisible to users. |
| `MIN_VALID_PULSE_COUNT` | 1 | `finalizeBurst` accepts the Allan's one-pulse ₱1. Phantom defense relies on inhibit gating plus the ISR debounce/RC filter during live claim windows. |
| `MAX_VALID_PULSE_COUNT` | 20 | The largest supported coin is ₱20 = 20 pulses, so firmware publishes a twenty-pulse burst immediately after the twentieth pulse instead of waiting for the silence timer. |
| `PULSE_NVS_SLOTS` | 64 | Size of the NVS-backed unACK'd pulse buffer. At ~30 pulses/day this is room for ~2 days of total broker outage before overflow drops new pulses. |
| `PULSE_REPUB_INTERVAL_MS` | 5000 ms | App-level retry. If a slot's first publish doesn't get an ACK in 5 s, the firmware republishes. Defends against QoS 1 retry failures (broker restart with persistent-session expiry). The server's `last_processed_pulse_seqno` dedupes any duplicate that arrives because of this retry. |

---

## Software defense stack — what each layer kills

After inhibit gating was added, layers 2–6 below are **defense-in-depth, not load-bearing**. The acceptor literally cannot fire its output transistor outside an active claim window, so phantom pulses during idle are impossible at the hardware level. The software filters now catch a near-zero noise floor instead of 25+/minute. Layers retained anyway because they cost nothing and would still apply if the inhibit wire were ever disconnected.

Failure modes covered, in the order they're applied to a candidate edge on GPIO 18:

0. **Inhibit gating (GPIO 23 SET pin, OUTPUT LOW at idle)** — the dominant defense. Acceptor's output stage is physically held in reset outside claim windows. No FALLINGs can be generated, real or noise. Released only on server-issued LED-on; re-engaged on LED-off, safety auto-off, or boot.
1. **Hardware RC filter (R4 + C1, τ = 1 ms)** — kills sub-millisecond piezo / EMI transients during the brief claim windows when the inhibit is released.
2. **ISR debounce (`MIN_INTER_FALLING_MS` = 110 ms)** — kills Schmitt oscillation bursts on slow edges (counted in `pulseDedup`) and the 91–93 ms ghost echo after the first real pulse of a burst.
3. **Warm-up guard (`PULSE_WARMUP_MS` = 30 s)** — kills boot-time phantoms during GPIO settle and WiFi/MQTT bring-up. Logs as `DROP warm-up` so they're auditable. Now mostly redundant with the inhibit-at-boot rule, but kept in case a future change flips the inhibit default.
4. **Burst splitters (`NEXT_COIN_GAP_MS` = 195 ms, `INTER_PULSE_MAX_MS` = 220 ms)** — separate consecutive coin drops when there is a clear gap; fast ₱1 drops may merge safely because pulse count equals pesos.
5. **Min-count filter (`MIN_VALID_PULSE_COUNT` = 1)** — accepts the Allan's single-pulse ₱1 while relying on inhibit gating, RC filtering, and ISR debounce to suppress phantoms during live claim windows.
6. **Peso-count decoder** — count 1..20 maps directly to that many pesos. Anything above 20 is REJECTed with a log line.
7. **NVS persistence + retry (Round 3)** — once a burst makes it past 1–6 it gets a seqNo, gets written to NVS slot, gets published. The publisher and the server dedup pointer ensure the credit is delivered at-least-once (firmware retry) and at-most-once (server dedup), even across ESP32 reboots, broker outages, and WiFi drops.

---

## Round-trip flow — happy path

```
[server publishes led/on (claim grant)]
  → ESP32 handleLedCommand
  → setLed(true), setAcceptorInhibit(false)  ← GPIO 23 → INPUT, SET floats to 3 V
  → acceptor now ELECTRICALLY ACTIVE for the claim window

[customer drops coin]
  → ISR fires (20 pulses for ₱20, 170 ms apart)
  → burst forms in processPulses
  → COIN_FINALIZE_MS silence → finalizeBurst()
  → warm-up + count check pass
  → ++pulseSeqNo, persist counter to NVS
  → write PulseRecord to free NVS slot
  → publish on rental/coinacceptor/coin-01/pulse
  → server processes, advances last_processed_pulse_seqno in SQLite
  → server credits the claiming station, publishes ACK on .../pulse/ack
  → handlePulseAck → pulseNvsClearSlot → slot freed
  → server publishes led/off

[server publishes led/off (claim ended)]
  → ESP32 handleLedCommand
  → setLed(false), setAcceptorInhibit(true)  ← GPIO 23 → OUTPUT LOW, SET pulled to GND
  → acceptor back to ELECTRICALLY MUZZLED
```

Total wall-clock cost: ~3.5 s for a ₱20 (the pulse train itself spans ~3.2 s; firmware finalizes immediately after the twentieth pulse) + ~50–200 ms broker round-trip. Inhibit transition cost: < 1 ms (a single pinMode call). Acceptor doesn't need any post-release settling time because it's only logic-gated, not power-cycled.

---

## Round-trip flow — broker outage

```
[coin drops]
  → ... same up to "persist PulseRecord to NVS slot"
  → publish attempt → mqttClient.connected() == false
  → log: [pulse] queued (no MQTT) slot=N seqNo=M
  → return (slot stays filled, lastPubMs stays 0)
  
[broker comes back]
  → connectMqtt succeeds
  → pulseMarkAllForReplay sets every filled slot's lastPubMs to 0
  → next pulseRetryTick republishes every queued slot
  → server processes (or dedupes if already seen), ACKs
  → slots clear

[ESP32 reboots with slots still filled]
  → pulseNvsInit re-reads every NVS key on boot, rebuilds RAM mirror
  → log: [nvs] loaded slot N seqNo=M ...
  → log: [nvs] init complete — X unACK'd pulses recovered from flash
  → on MQTT connect, pulseMarkAllForReplay → retry → ACK → clear
```

Worst case verified on bench (S11 Round 3 validation, 2026-05-16): broker stopped, coin dropped, broker restarted, server restarted, ESP32 republished seqNo=3 thirteen times across ~65 s before the server finally ACK'd, server-side dedup pointer advanced exactly once.

---

## Known limitations

### One-pulse ₱1 support

The acceptor's H1 channel emits a single pulse for ₱1, and firmware accepts that
with `MIN_VALID_PULSE_COUNT = 1`. The trade-off is that count-level filtering can
no longer distinguish a real ₱1 from a single-edge phantom during a live claim
window. The intended protection stack is hardware inhibit outside claim windows,
the RC input filter, and the 110 ms ISR debounce. Fast physical ₱1 stacks are
allowed to merge into one burst in peso-count mode; seven fast ₱1 coins should
publish and credit as a total of ₱7, whether they arrive as one `pulseCount=7`
burst or several bursts that sum to 7.

### Phantom merge into a real coin

The live-claim merge window is intentional in peso-count mode: a burst of fast
₱1 coins can merge without changing the credited peso total. The remaining risk
to watch is a phantom edge or noisy duplicate pulse inside an active claim
window, because any accepted pulse from 1..20 now contributes directly to the
published peso count.

If this returns in production data, capture `rental/coinacceptor/+/#` with
`scripts\pulse-trace.cmd` and compare physical drop timing against published
`pulseCount`.

### LED frame as a noise source (required swap — bench-confirmed 2026-05-18)

The Allan RGB cycling LED frame is a noise source that the inhibit cannot block. It contains an internal RGB-sequencer driver chip that runs continuously while the frame is connected to 12 V (independently of whether Q1 is letting current flow through the LEDs themselves — the driver chip stays alive as long as the anode side sees 12 V). The driver's switching dumps noise onto the shared 12 V rail and through tied GND onto the wire that runs between the acceptor's COIN output transistor and the EL817's LED. That noise briefly pulls the COIN line LOW enough to fire the EL817 even when the acceptor's output transistor is held OFF by the inhibit — bypassing the inhibit path entirely.

**Bench measurements (2026-05-18, with the S11 firmware + inhibit gating active):**

| State | Phantom MQTT publish rate |
|---|---|
| No inhibit, frame connected to 12 V | ~25–30/min (the original problem) |
| Inhibit engaged, frame connected to 12 V | ~0.67/min |
| **Inhibit engaged, frame 12 V wire unplugged** | **0/min over 180 s** |

The inhibit handles roughly 50× of the noise. The residual ~0.67/min comes entirely from the frame's switching-noise injection downstream of the inhibit point. Disconnecting the frame's 12 V is sufficient to get the system to a clean zero.

**Implication: the LED-frame swap is required for production**, not optional polish as an earlier revision of this doc suggested. Replace the Allan RGB cycling frame with a plain single-color LED panel hard-switched by Q1 — same wiring topology, just a non-cycling load. No internal driver chip, no switching noise, no residual phantoms.

Until the swap is done, the safe operating configuration is:
- Inhibit engaged (production firmware default)
- LED frame 12 V physically unplugged (lose the claim-window visual cue temporarily; acceptable for bench/development)
- Bench debug LED on GPIO 19 + R5 + 5 mm LED still provides a visual claim cue for testing

Or, if you want the visual cue and accept the residual rate:
- Inhibit engaged + frame connected = ~0.67 phantom MQTT publishes/min in the old 1/2/3/4 decoder bench. With no active claim, the server orphan-logs each one — no station gets credited, but the event log gets noisy. With an active claim, any phantom pulse that passes the live filters would now credit its pulse count as pesos. This is unsafe for production but tolerable for short bench tests.

### Server-side auto-reconnect missing

Discovered during S11 Round 3 testing: the C# WPF server in `src/RentalServer/` does NOT automatically reconnect to Mosquitto after a broker restart. The ESP32 firmware handles broker outages correctly (its `[mqtt] reconnect attempt` loop with backoff), but the server side currently requires a manual relaunch. Tracked as a separate task. This is unrelated to anything in this spec — it's an S5–S8 implementation gap.

---

## Validation gates

Re-run these any time a threshold above is changed:

**Inhibit Phase A — idle, muzzled.** Boot the firmware (no LED-on command sent). Drop a ₱5. Expected: coin **physically rejected** through the coin-return slot; zero MQTT activity on the pulse topic. Tests that the inhibit pin is wired correctly and polarity is right.

**Inhibit Phase B — released, accepting.** Publish `{"state":"on","autoOffSec":60,"claimId":"test"}` to `rental/coinacceptor/coin-01/led`. Expected: debug LED lights up; bench Q1-driven LED lights up too. Drop a ₱5 within 60 s. Expected: coin accepted, `[pulse] persisted` and `[pulse] publish ok` log lines, pulse + ACK appear on MQTT, NVS slot cleared.

**Inhibit Phase C — re-inhibited.** Publish `{"state":"off","claimId":"test"}` to the LED topic. Expected: debug LED off. Drop a ₱5. Expected: coin physically rejected, zero MQTT activity. Same as Phase A.

The legacy S11 software-stack gates below are still meaningful but are now mostly checking the defense-in-depth layers rather than the primary defense:

**S11 Phase 1 — boot watch.** Boot the firmware. Drop nothing for 30 s. Expected: any FALLINGs in this window logged as `[pulse] DROP warm-up`. Zero MQTT publishes during the warm-up window. With inhibit gating in place, this should also normally see zero FALLINGs at all because the acceptor is muzzled at boot.

**S11 Phase 2 — denomination sweep (during an active claim window).** Send LED-on first, then drop ₱5, ₱5, ₱10, ₱10, ₱20, ₱20 with ~5 s gaps inside the autoOffSec window. Expected: 6 MQTT publishes with denominations 5, 5, 10, 10, 20, 20 and matching pulse counts 5, 5, 10, 10, 20, 20. Each burst's gap pattern is about 170 ms between pulses. No REJECTs unless a phantom merged into a coin (rare).

**S11 Phase 3 — idle silence.** After the sweep + LED-off, idle for 3–5 minutes. Expected: zero `publish` lines on the pulse topic. With inhibit gating, `DROP suspect single pulse` lines should also be zero or near-zero now (was 0.35–2/min before gating).

**S11 Round 3 — persistence.** Stop Mosquitto. Send LED-on and drop a coin within the window. Expected: `[pulse] persisted ... queued (no MQTT)`. Power-cycle the ESP32. On reboot, expected: `[nvs] loaded slot N seqNo=M ...` + `[nvs] init complete — 1 unACK'd pulses recovered from flash`. Start Mosquitto. Within ~30 s of broker availability, expected: pulse appears on the pulse topic + ACK appears on the ack topic + server's SQLite `last_processed_pulse_seqno` advances exactly once.

---

## File map (sibling firmware repo)

- `C:\Projects\RentalCoinAcceptor\src\main.cpp` — all firmware logic (one file by design — small project, easy to reason about)
- `C:\Projects\RentalCoinAcceptor\platformio.ini` — PlatformIO target board + library versions (PubSubClient ≥ 2.8, ArduinoJson ≥ 7.0.4)
- `C:\Projects\RentalCoinAcceptor\include\config.h` — WiFi creds, broker host/port, `ACCEPTOR_ID`. **Gitignored.**
- `C:\Projects\RentalCoinAcceptor\include\config.example.h` — template committed to git; copy to `config.h` and fill in.

This `coin-acceptor-spec.md` lives in the main RentalSystem repo (it's protocol-level documentation between firmware and server). The firmware repo only holds the firmware itself.
