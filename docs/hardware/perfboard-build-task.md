# Perfboard build task - coin acceptor board

> Wiring at a glance: [all-in-one diagram](../images/wiring-all-in-one.png) (same circuit as the perfboard). Want to skip the perfboard? See the [Slotify PCB](../../hardware/coin-box-board/README.md) or the [Denz ready-made board](denz-subvendo-board.md) (untested).

Use this task when moving the validated breadboard circuit onto perfboard.
The goal is not to redesign the circuit. The goal is to copy the exact
production-safe breadboard state that already passed bench tests.

## ✅ BUILD COMPLETE — as-built (2026-06-05)

This build is **done and installed in the coin box.** Coin → opto → ESP32 → MQTT → credit
works end-to-end with **zero ghost pulses**; ₱1/₱5/₱10/₱20 all decode and credit correctly;
the SET-pin inhibit physically rejects coins outside a claim window.

The row/column layout further down was the *starting proposal* for a 9×15 cm board. The
**actual board is 33×54 holes** with a different coordinate scheme (lettered columns
`1a`–`3b`, numbered rows 1–33, origin top-left). For the real as-built component map, the
deviations, and the build gotchas (insulated-rail tap cold-joints, the LM2596 input-cap
false-short on an ohmmeter, opto flux-leakage), see the **S12 completion entry in
CLAUDE.md → Current Status**. The circuit *topology* is unchanged from the validated
breadboard — treat the specific coordinates below as illustrative procedure, not as-built.

## Prompt to start the task

```text
Currently working on: moving the validated ESP32 coin acceptor circuit from
breadboard to perfboard. Use
C:\Projects\RentalSystem\ProjectContext\perfboard-build-task.md as the
primary guide, and cross-check
C:\Projects\RentalSystem\ProjectContext\production-wiring.md.

Walk me one physical step at a time. Give exact row/column placement for each
component on the reference 9x15 cm perfboard layout. Stop after each soldering
stage for continuity checks, power checks, or a top-down photo. Do not let me
connect ESP32 VIN until LM2596 OUT+ / OUT- is measured at 4.9V to 5.1V.
```

## Current validated circuit state

Validated on breadboard on 2026-05-27:

- ESP32 powered from the same 12V PSU through LM2596.
- LM2596 output under ESP32 load measured 4.9V.
- MQTT status published online:
  `rental/coinacceptor/coin-01/status {"online":true,...}`
- Bench debug LED and R5 330 ohm were removed.
- Two parallel 10k opto pull-ups were replaced by one 5.1k pull-up.
- R4 10k retained as a plain series resistor into GPIO18; C1 100nF was NOT
  populated (removed when the opto pull-up was stiffened to 5.1k), and the
  board validated cleanly without it.
- 3-minute idle pulse watch printed nothing.
- 3x power-cycle test passed: Allan frame stayed off during boot.
- Reset-mid-claim passed: frame went off during ESP32 reset and no phantom
  credit appeared.
- One each of PHP1, PHP5, PHP10, PHP20 credited correctly. Current rate total
  was 3h 36m.

Firmware pin map in `C:\Projects\RentalCoinAcceptor\src\main.cpp`:

- GPIO18 = `COIN_PULSE_IN`
- GPIO19 = `COIN_LED_OUT`
- GPIO23 = `ACCEPTOR_INHIBIT`
- SET LOW rejects coins; SET HIGH accepts coins.

OTA is already in firmware:

- Normal USB build: `pio run -e esp32dev`
- OTA build/upload env: `pio run -e esp32dev-ota -t upload`
- OTA password stays only in gitignored `include/config.h` and
  `$env:RENTAL_COIN_OTA_PASSWORD`.

## Production circuit to copy

Remove from breadboard/final design:

- D1 small 5mm debug LED
- R5 330 ohm debug LED resistor
- Duplicate second 10k opto pull-up

Keep:

- LM2596 12V to 5V buck converter
- R1 10k COIN pull-up
- R2 1k EL817 input current limit
- U1 EL817 / PC817 optocoupler
- R3 5.1k opto output pull-up
- R4 10k plain series resistor into GPIO18 (no filtering on its own once C1 is
  unpopulated)
- C1 100nF anti-pitik shunt cap — NOT populated; pad left reserved (open) so
  the RC filter can be re-added in ~2 min if a future 24h in-box soak shows noise
- R6 1k Q1 base resistor
- R7 10k Q1 base pulldown
- Q1 2N2222A / PN2222A low-side frame switch
- R8 10k SET pulldown

## Reference perfboard coordinate system

Assume a 9x15 cm FR4 double-sided perfboard, 2.54 mm pitch, long side
horizontal.

Place the board component-side up:

- Columns run left to right: `1` to about `58`.
- Rows run top to bottom: `A` to about `AJ`.
- If the actual board has more or fewer holes, keep the same relative zones.
- Mark the board edges with masking tape before soldering.

Zones:

- Left side: external connectors and 12V input.
- Center-left: optocoupler and pulse input.
- Center-right: Q1 frame switch and SET pulldown.
- Right side: ESP32 female socket.
- Top: 12V and 5V routing.
- Bottom: GND routing.

Do not solder the ESP32 directly. Use female headers so the ESP32 can be
removed.

## Reference row/column layout

This is the proposed layout. Dry-fit first. If the ESP32 dev board footprint
does not match the header columns, adjust the ESP32 header columns and keep
the rest of the nodes in the same zones.

### Buses and headers

| Item | Placement | Notes |
|---|---:|---|
| +12V bus | row `B`, cols `2-34` | Red insulated wire on copper side or component side. |
| GND bus | row `AH`, cols `2-56` | Black insulated wire. All grounds tie here. |
| +5V bus | row `E`, cols `40-56` | LM2596 OUT+ only. Do not connect to 3.3V. |
| +3.3V bus | row `H`, cols `40-56` | From ESP32 3V3 only. |
| ESP32 left header | col `42`, rows `G-Y` | 19 holes. Confirm with actual ESP32. |
| ESP32 right header | col `53`, rows `G-Y` | 19 holes. Confirm with actual ESP32. |
| TP1 pulse test point | `N27` | GPIO18 pulse node. |
| TP2 inhibit test point | `R31` | GPIO23 / SET node. |
| TP3 GND test point | `AH31` | Ground reference. |

### External connectors

Use JST-XH 2.54 mm if that matches the coin slot/frame harness. If using
screw terminals, keep the same signal order.

| Connector | Pins / placement | Wire order |
|---|---|---|
| J1 12V PSU input | `B2` = +12V, `B4` = GND | 5.08 mm 2-pin screw terminal is OK. |
| J2 coin acceptor 4-pin | `F2-F5` | `F2` red +12V, `F3` black GND, `F4` white COIN, `F5` gray NC/parked. |
| J3 Allan frame 2-pin | `J2-J3` | `J2` red +12V, `J3` black to Q1 collector. |
| J4 SET 2-pin | `R2-R3` | `R2` SET signal, `R3` SET GND. |

### LM2596

Prefer mounting the LM2596 module mechanically with screws, standoffs, glue,
or tie-wrap holes, then run short wires to the perfboard buses. Do not depend
on the module pads lining up with perfboard holes.

| LM2596 pad | Perfboard destination |
|---|---|
| IN+ | +12V bus, row `B` |
| IN- | GND bus, row `AH` |
| OUT+ | +5V bus, row `E` |
| OUT- | GND bus, row `AH` |

Mandatory check before ESP32 insertion:

```text
LM2596 OUT+ / OUT- = 4.9V to 5.1V
```

### Optocoupler and pulse input

Place U1 across columns `18` and `21`.

| Part | Placement / nodes |
|---|---|
| U1 EL817 pin 1 | `K18` |
| U1 EL817 pin 2 | `L18` |
| U1 EL817 pin 3 | `L21` |
| U1 EL817 pin 4 | `K21` |
| R1 10k | +12V bus to COIN node `J14` |
| R2 1k | +12V bus to U1 pin 1 `K18` |
| COIN node | J2 white `F4` to `J14`, then to U1 pin 2 `L18` |
| U1 pin 3 | `L21` to GND bus |
| R3 5.1k | +3.3V bus to U1 pin 4 / OPTO_RAW `K21` |
| R4 10k | OPTO_RAW `K21` to pulse node `N27` |
| C1 100nF | NOT populated — leave the `N27`-to-GND pad open (reserved) |
| ESP32 GPIO18 | pulse node `N27` to ESP32 GPIO18 pin |

EL817/PC817 DIP-4 pin reminder, top view with notch/dot up:

```text
pin 1  pin 4
pin 2  pin 3
```

Check the physical package marking before soldering. Pin 1 usually has the
dot/notch side.

### Frame LED low-side switch

Q1 pinout is package-dependent. For common PN2222A / 2N2222A TO-92 with the
flat face toward you, many parts are `E-B-C` left to right, but this is not
universal. Verify the transistor pinout from the exact part or seller photo
before final solder.

Target functional nodes:

| Part | Placement / nodes |
|---|---|
| Q1 emitter | `V28`, to GND bus |
| Q1 base | `V30`, base node |
| Q1 collector | `V32`, to J3 frame black `J3` |
| R6 1k | ESP32 GPIO19 to Q1 base node `V30` |
| R7 10k | Q1 base node `V30` to GND bus |
| J3 frame red | `J2` to +12V bus |
| J3 frame black | `J3` to Q1 collector node `V32` |

If the transistor pinout is not E-B-C, bend/rotate the transistor so the
functions still land on emitter/base/collector nodes above.

### SET / inhibit fail-safe

| Part | Placement / nodes |
|---|---|
| SET signal node | J4 SET signal `R2` to TP2 `R31` |
| R8 10k | SET signal node `R31` to GND bus |
| ESP32 GPIO23 | SET signal node `R31` to ESP32 GPIO23 pin |
| SET GND | J4 `R3` to GND bus |

Fail-safe expectation:

- ESP32 reset/offline/unpowered = SET held LOW by R8 = acceptor rejects coins.
- Claim granted = firmware drives GPIO23 HIGH = acceptor accepts coins.

## Walk-through stages

The assistant must walk the user stage by stage and stop after each stage.
Do not dump all soldering instructions at once during the live build.

### Stage 0 - dry fit and mark

1. Put the empty perfboard component-side up.
2. Mark row/column orientation with tape.
3. Place, but do not solder:
   - ESP32 female headers
   - LM2596
   - J1/J2/J3/J4
   - U1
   - Q1
4. Confirm the coin box has room for wire bends and USB emergency access.

Stop and ask for a top-down photo before soldering if anything looks cramped.

### Stage 1 - power only

Solder only:

- J1 12V input
- +12V bus
- GND bus
- LM2596 IN/OUT wires
- +5V bus
- ESP32 female headers
- ESP32 VIN/5V and GND wiring

Checks:

1. ESP32 removed.
2. Apply 12V.
3. Measure LM2596 OUT+ / OUT-: must be 4.9V to 5.1V.
4. Power off.
5. Insert ESP32.
6. Power on.
7. ESP32 LED turns on.
8. MQTT status appears:

```powershell
& "C:\Program Files\mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "rental/coinacceptor/coin-01/status" -C 1 -v
```

### Stage 2 - SET/inhibit and frame switch

Solder:

- J3 frame connector
- J4 SET connector
- Q1
- R6
- R7
- R8
- GPIO19 to R6
- GPIO23 to SET node

Checks:

1. Power-cycle 3 times. Frame must stay OFF during boot.
2. Tap Insert Coin. Frame turns ON.
3. Press ESP32 EN/RST mid-claim. Frame turns OFF during reset.
4. No phantom credit appears.

### Stage 3 - optocoupler pulse path

Solder:

- J2 coin acceptor connector
- U1 EL817/PC817
- R1
- R2
- R3 5.1k
- R4
- (C1 pad left open — not populated)
- GPIO18 pulse wire

Checks:

1. With no claim open, run a 3-minute idle pulse watch:

```powershell
& "C:\Program Files\mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "rental/coinacceptor/coin-01/pulse" -W 180 -v
```

Expected: timeout with no messages.

2. Tap Insert Coin.
3. Drop one each if available: PHP1, PHP5, PHP10, PHP20.
4. Expected current total at validated rates: 3h 36m.
5. Frame turns OFF after DONE/timeout.

### Stage 4 - pre-install vibration check

Before screwing the coin box shut:

1. Tug each connector gently.
2. Wiggle the board lightly while idle pulse watch is running.
3. Confirm no pulse messages.
4. Power-cycle once more.
5. Repeat one PHP1 coin claim.

## Things the task assistant must not do

- Do not populate C1. The board is validated without it — leave its pad
  reserved (open). Keep R4 as the plain series resistor.
- Do not connect LM2596 OUT+ to the 3.3V rail.
- Do not power ESP32 VIN from 12V.
- Do not solder ESP32 directly to perfboard.
- Do not assume Q1 pinout without checking the exact transistor.
- Do not assume GPIO18/GPIO19/GPIO23 row numbers without confirming the
  user's exact ESP32 dev board labels.
- Do not continue after a failed continuity/power check.

## Continuity checks before first power

With power OFF:

- +12V to GND: no short.
- +5V to GND: no short.
- +3.3V to GND: no short.
- SET signal to GND should measure about 10k through R8, not a direct short.
- Q1 collector to GND should not be a direct short.
- GPIO18 pulse node to GND should not be a direct short.

If any check fails, stop and inspect solder bridges before applying power.
