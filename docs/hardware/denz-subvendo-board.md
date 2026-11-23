# Ready-made board: Denz Customized "Mini Wireless Sub Vendo Board" v2.0

> **UNTESTED.** This profile was written from photos of the board. Nobody
> has run Slotify on a real one yet. Do the bench test below before you put
> it in a shop, and tell us how it went (Slotify Facebook group or a GitHub
> issue).

Use this if you'd rather buy a finished board than build the Slotify coin box
board or perfboard. It's sold by Denz Customized on Facebook. No soldering:
you plug in an ESP32, set two jumpers, and connect the coin acceptor.

## What's on the board (from the silkscreen)

| Part | Label on the board | Used by Slotify? |
|------|--------------------|------------------|
| Dev board sockets | `ESP8266` and `38P ESP32` | **38-pin ESP32 only.** The Slotify firmware does not run on ESP8266. |
| Coin acceptor header | `COINSLOT`: `DC12V` / `COIN` / `GND` / `COUNTER` | Yes: 12V, COIN, GND. COUNTER is not used. |
| Jumpers | `SWITCH SETTINGS`: COIN and RELAY | Yes: set **COIN → 27** and **RELAY → 25** |
| Relay | power cut to the acceptor's 12V | Yes: this is how Slotify locks the slot |
| LED outputs | `2 or 3WIRE LED` (GND / TRIG / 12V), `2WIRE LED` (12V / GND) | **Not yet.** The TRIG pin's GPIO isn't printed, so the firmware doesn't drive it. |
| Power | DC jack, on-board 5V buck | Yes: 12V in |

## How Slotify uses it

- **No SET wire.** This board has no line to the Allan's SET pin. Slotify
  locks the slot by **cutting the acceptor's power** through the relay
  instead. The acceptor is powered only while a customer's Insert Coin
  window is open. An unpowered acceptor returns every coin, so a crash,
  reboot or lost Wi-Fi leaves the slot locked.
- **Start-up glitch filter.** An Allan can make a stray pulse when it powers
  on. The firmware ignores any pulse in the first 1.5 seconds after the relay
  turns the acceptor on. Customers can't drop a coin that fast anyway.
- **Pins** (firmware env `esp32dev-denz`): COIN = GPIO 27 (internal pull-up
  on), RELAY = GPIO 25. GPIO 12 is avoided on purpose: it's an ESP32 boot
  pin, and a relay circuit holding it high at power-on can stop the ESP32
  from booting.
- **Relay polarity** is assumed to be "GPIO HIGH = acceptor powered". If the
  bench test shows the opposite, rebuild with `-D ACCEPTOR_POWER_ON_LEVEL=0`
  (see `firmware/platformio.ini`).

## Setup

1. Plug a **38-pin ESP32 DevKit** (ESP32-WROOM-32) into the `38P ESP32`
   socket. Match the board's pin labels; don't put it in the ESP8266 socket.
2. Set the jumpers: **COIN → 27**, **RELAY → 25**.
3. Program the Allan exactly as for the Slotify box: pulses = pesos, **NO**,
   **SLOW** ([OWNER-GUIDE §2.5](../OWNER-GUIDE.md)).
4. Connect the Allan to `COINSLOT` (DC12V, COIN, GND).
5. Flash **`coin-box-denz-merged.bin`** (not `coin-box-merged.bin`) the same
   way as [OWNER-GUIDE §3](../OWNER-GUIDE.md).
6. Join it to the shop Wi-Fi from its `Slotify-CoinBox-XXXX` hotspot.

## Bench test (do all of these before using it in the shop)

| # | Do this | Expected |
|---|---------|----------|
| 1 | Power on, wait 1 minute, drop a coin **without** tapping Insert Coin | The coin comes back out. The acceptor has no power (no light). |
| 2 | Tap Insert Coin on a PC | The relay clicks; the acceptor powers on. |
| 3 | Drop ₱1, ₱5, ₱10, ₱20 | Each one credits the right amount once. |
| 4 | Wait for the window to close | The relay clicks off; coins come back out again. |
| 5 | Unplug the box's Wi-Fi router during a claim | Within about 65 seconds, the relay turns off by itself. |
| 6 | Power-cycle the whole box 5 times | No credit appears on any PC. |

If test 1 fails (the acceptor has power when idle), the relay polarity is
reversed: **stop** and rebuild with `ACCEPTOR_POWER_ON_LEVEL=0`.

## Still unknown (needs the real board or the maker)

- Which GPIO the LED `TRIG` output uses (to light the coin slot during a claim).
- The relay's exact wiring (polarity, and whether it switches 12V or GND).
- Whether `COIN` reaches the GPIO through an optocoupler or directly.
