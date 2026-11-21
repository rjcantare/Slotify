# Slotify: Owner Setup Guide

A free, open-source **centralized coinslot** system for Windows pisonet and
rental shops. There's **one coin box for the whole shop**: the customer taps
**Insert Coin** on their PC or tablet, drops the coin in the central coinslot,
and the time goes to their unit.

> **Tested so far:** our own shop. It runs **12 Windows 10 PCs, diskless
> (iCafe8)**, plus Android tablets, with one Allan 1239A Pro Max coinslot.
> **Not yet tested:** traditional setups (each PC with its own hard drive),
> other diskless systems (CCBoot, etc.), and other coin acceptor models. If you
> try one of these, please report it in [GitHub Issues](https://github.com/rjcantare/Slotify/issues).

**Time needed:** about 2–3 hours for the coin box (first build, with
soldering), 15 min for the server, 5 min per PC, 15 min per tablet.

---

## 1. Parts list

### Coin box

| Qty | Part | Notes |
|---:|------|-------|
| 1 | **Allan 1239A Pro Max** coin acceptor (HX-616 class) | Must have the **SET** header on the back (the inhibit input). Other models are untested. |
| 1 | **S-30-12** power supply (12V, 2.5A) | 220V AC in, 12V DC out. Powers everything. |
| 1 | **LM2596 (HW-411)** buck converter | 12V → 5V for the ESP32. **Adjust it before connecting it** (§2.4). |
| 1 | **ESP32-WROOM-32** dev board (38-pin) | USB chip is CH340 or CP2102 |
| 1 | **EL817 / PC817** optocoupler (DIP-4) | Protects the ESP32 from 12V. **Required.** |
| 1 | **2N2222A** NPN transistor (TO-92) | Switches the coinslot LED frame |
| 4 | 10 kΩ ¼W resistor | R1, R4, R7, R8 |
| 2 | 1 kΩ ¼W resistor | R2, R6 |
| 1 | 5.1 kΩ ¼W resistor | R3 |
| 1 | Perfboard about 30×70 mm, 2-pin screw terminal, headers, Dupont F/F jumper | Or use the [custom coin box board](../hardware/coin-box-board/) instead |
| 1 | 12V LED frame for the coinslot (optional) | Lights up when the slot is open. See the noise note in §2.6. |
| — | Multimeter | Required, for the checks in §2.4 and §2.5 |

All parts are sold on Shopee, Lazada and in local electronics shops. Prices change often, so check current listings.

### PCs, tablets, network

- **Server:** one always-on Windows 10/11 64-bit PC. In our shop it's the
  **iCafe8 diskless server**.
- **Rental PCs:** Windows 10/11 64-bit.
- **Tablets/phones (optional):** Android **8.0** or newer. Must be **factory
  reset** before setup.
- **One router / one network** for everything.

---

## 2. Coin box wiring

> **Easier version:** the [step-by-step wiring page](https://rjcantare.github.io/Slotify/wiring.html) splits this into 4 small colour-coded diagrams with a tick-box checklist.
> **Or skip the hand wiring:** the [custom coin box board](https://github.com/rjcantare/Slotify/tree/main/hardware/coin-box-board) has the same circuit with screw terminals. You can order it from a PCB maker or etch it yourself.

![Coin box breadboard prototype](images/S9-esp32-breadboard.png)

More detail: [hardware/production-wiring.md](hardware/production-wiring.md).

### 2.1 Pin summary

| ESP32 pin | Name | Goes to |
|-----------|------|---------|
| **GPIO 18** | COIN_PULSE_IN | Optocoupler output (coin pulses come in here) |
| **GPIO 19** | COIN_LED_OUT | Transistor that switches the LED frame |
| **GPIO 23** | ACCEPTOR_INHIBIT | Allan **SET** pin (blocks coins while the slot is closed) |
| **VIN / 5V** | Power | LM2596 OUT+ (5.0V) |
| **3V3** | 3.3V | Pull-up resistor R3 |
| **GND** | Ground | Common ground |

| Allan wire | Colour | Goes to |
|------------|--------|---------|
| DC12V | **Red** | +12V |
| GND | **Black** | GND |
| COIN | **White** | Optocoupler input (the COIN node) |
| COUNTER | Gray | Not used |
| **SET** (2-pin header on the back) | upper pin = signal | ESP32 **GPIO 23** (+ R8 to GND) |
| | lower pin = GND | Already common with the harness GND |

### 2.2 Diagram

```
 S-30-12 PSU
 +V (12V) ──┬──────────────► Allan RED
            ├──────────────► LED frame RED
            ├──────────────► LM2596 IN+      LM2596 OUT+ (5.0V) ──► ESP32 VIN
            ├── R2 1kΩ ──► EL817 pin 1 (anode)
            └── R1 10kΩ ─┐
                         ├── COIN node ◄─── Allan WHITE
     EL817 pin 2 ────────┘   (cathode)

 ESP32 3V3 ── R3 5.1kΩ ──┬── EL817 pin 4 (collector)
                         └── R4 10kΩ ──► ESP32 GPIO 18
                    EL817 pin 3 (emitter) ──► GND

 ESP32 GPIO 19 ── R6 1kΩ ──┬── 2N2222A base
                           └── R7 10kΩ ── GND
                 2N2222A emitter ── GND
                 2N2222A collector ── LED frame BLACK

 ESP32 GPIO 23 ──┬── Allan SET (upper pin)
                 └── R8 10kΩ ── GND

 PSU −V (GND) ── Allan BLACK, EL817 pin 3, LM2596 IN−/OUT−, ESP32 GND,
                 2N2222A emitter, R7, R8    ← ONE common ground
```

How it works: when a coin is accepted, the Allan pulls the WHITE wire to
ground once per peso. That lights the LED inside the EL817, and the EL817
pulls GPIO 18 low. The ESP32 counts the pulses. The 12V side never touches
the ESP32 pins directly.

### 2.3 Why these parts matter (don't skip them)

| Part | If you leave it out |
|------|---------------------|
| **EL817 + R2** | 12V can reach the ESP32 and kill it if the acceptor fails. Without R2, the EL817 burns out on the first coin. |
| **R8 (SET to GND)** | When the ESP32 restarts or goes offline, the acceptor **keeps swallowing coins with no credit**, and you lose money. With R8 it rejects coins instead. |
| **R7** | The LED frame flickers on at every power-up, and customers think the slot is open. |
| **One common GND** | Coins get miscounted or not counted at all. |

### 2.4 Adjust the LM2596 BEFORE connecting the ESP32

New LM2596 modules can output nearly **12V**, and that kills an ESP32 at once.

1. Connect **only** the input: PSU +V → IN+, PSU −V → IN−.
2. Multimeter on OUT+ / OUT−, DC volts.
3. Turn the small brass screw on the blue trim pot (clockwise raises the
   voltage) until it reads **5.0V (±0.1V)**.
4. Power off, then connect OUT+ → ESP32 VIN and OUT− → ESP32 GND.

### 2.5 Program the Allan coin acceptor

Use the buttons and display on the Allan (see its manual's coin-sampling
steps). **Pulse count = pesos:**

| Channel | Coin | Pulses |
|---------|------|-------:|
| H1 | ₱1 (old + new) | 1 |
| H2 | ₱5 new | 5 |
| H3 | ₱5 old | 5 |
| H4 | ₱10 new | 10 |
| H5 | ₱10 old | 10 |
| H6 | ₱20 | 20 |

Accuracy **F = 8**, **20 samples per channel**.
Switches on the back: output **NO** (normally open), speed **SLOW**.
The firmware timing is tuned for SLOW. FAST or MEDIUM will miscount coins.

### 2.6 Notes

- Keep the **WHITE (COIN) wire away from the LED frame's power wires**, and
  twist it with a GND wire. A cycling RGB frame creates electrical noise.
  If you get phantom (fake) credits, try a plain single-colour LED frame, or
  add a 100 nF capacitor from GPIO 18 to GND.
- Mount the board with hot glue or 3M VHB tape. The box vibrates when coins
  drop.
- Leave access to the ESP32 USB port for re-flashing, or plan to open the box.

### 2.7 Test before closing the box

1. **Idle test (3 min):** power on, don't touch anything. The server must
   show **no** coin credits.
2. **Power-cycle 3×:** the LED frame must stay **off** until someone taps
   Insert Coin.
3. **Reset test:** tap Insert Coin, then press the ESP32's EN/RST button. Drop
   a coin. It must **come back out** of the reject slot.
4. **Coin test:** in one Insert Coin window, drop ₱1, ₱5, ₱10, ₱20. The server
   must credit exactly **₱36**.
5. **24-hour test:** compare the server's coin total with the actual coins in
   the box.

---

## 3. Flash the coin box (one time, from Chrome)

1. Download **`coin-box-merged.bin`** from the Releases page. It's the full
   image (bootloader + partitions + app).
2. Install the USB driver if Windows doesn't see the ESP32: **CH340**
   (`CH341SER.EXE`) or **CP210x**.
3. Connect the ESP32 by USB (use a **data** cable, not charging-only). For
   flashing, you can unplug the 12V side.
4. Open **https://espressif.github.io/esptool-js/** in Chrome or Edge.
5. Baud **460800** → **Connect** → pick the COM port. If it won't connect,
   hold the **BOOT** button while clicking Connect.
6. **Erase Flash**, then set Flash Address **`0x0`**, choose
   `coin-box-merged.bin`, and click **Program**.
7. Unplug and replug, or press **EN/RST**.

### Connect it to the shop Wi-Fi

1. The box creates its own hotspot, **`Slotify-CoinBox-XXXX`**.
2. Connect a phone to it. The hotspot password is **`slotify-setup`**. The setup page opens (if not, go to `192.168.4.1`).
3. Choose the shop Wi-Fi, enter the password, and save.
4. The box finds the server by itself and shows as **`coin-01` online** on
   the server.

If the Wi-Fi password changes, the box opens the hotspot again. Repeat the
steps above.

> The public firmware has **wireless (OTA) updates turned off**. To update
> the coin box later, connect USB and repeat the flashing steps.

---

## 4. Network rules

1. **One router / one network** for the server, PCs, tablets, and coin box.
2. **Turn off "AP isolation" / "client isolation"** on the Wi-Fi.
3. **Give the server a fixed IP** with a **DHCP reservation** in the router.
   Reserve the IP it **already has**. On diskless setups, don't change the
   server's IP.

Server IP: `192.168.____.____`

---

## 5. Install the server (first!)

In our shop the server is the **iCafe8 diskless server PC**. Any always-on
Windows PC on the same network works.

1. Run **`RentalSystem-Server-Setup.exe`**. If you see "Windows protected
   your PC", click **More info → Run anyway**. It appears because the
   installer is unsigned. Check the SHA-256 in §10 if you're unsure.
2. Server connection: leave it at **`127.0.0.1`**, with username and password
   blank.
3. Finish. The installer installs the Mosquitto broker, the runtime and the
   firewall rules, then opens the **Slotify Server** window.

---

## 6. Install the rental PCs

### 6A. Diskless (iCafe8, tested)

1. In the iCafe8 console, give every PC a **unique lowercase hostname**:
   `pc-01`, `pc-02`, … Slotify uses the hostname as the station name.
2. Boot **one** client in the image's **update / super-workstation mode** (the
   mode where changes are saved to the master image).
3. Run **`RentalSystem-Client-Setup.exe`**. Server connection: **`auto`**, or
   type the server's IP. Leave "Replace Explorer" **off**.
4. Save/commit the image the usual iCafe8 way, then reboot the clients
   normally.
5. Each PC appears on the server within about 5 seconds and starts Slotify
   by itself on every boot.

New PC later: assign the next hostname in iCafe8 and boot it. No install
needed, because it's already in the image.

### 6B. Traditional (own hard drive, NOT yet tested)

1. Rename the PC to `pc-01`, `pc-02`, … (Settings → System → About →
   Rename). Reboot.
2. Run **`RentalSystem-Client-Setup.exe`** → **`auto`** → Finish with
   **Launch now** checked.
3. If you have Deep Freeze / Shadow Defender, **thaw** the PC first, install,
   then freeze again.

---

## 7. Tablets / phones (kiosk mode)

### 7.1 Prepare

1. **Factory reset.** Skip **every** account sign-in (Google, Xiaomi,
   Samsung). Kiosk mode can't be set while any account exists.
2. Settings → About → tap **Build number** 7× → Developer options → **USB
   debugging ON**.

### 7.2 Install and lock (from a PC)

1. Download **Android Platform Tools**
   (https://developer.android.com/tools/releases/platform-tools) and unzip it
   to `C:\platform-tools`. Put `RentalSystem-Tablet.apk` in the same folder.
2. Plug in the tablet → tap **Allow** on the USB debugging prompt.
3. Open PowerShell in `C:\platform-tools`:

```
.\adb devices
.\adb install RentalSystem-Tablet.apk
.\adb shell dpm set-device-owner com.rentalsystem.android/.RentalDeviceAdminReceiver
```

   You should see **"Success: Device owner set"**.
   - *"already some accounts on the device"*: factory reset again and skip
     all sign-ins.
   - *"INSTALL_FAILED_TEST_ONLY"*: use `.\adb install -t RentalSystem-Tablet.apk`.
   - **Xiaomi/Redmi/POCO** (*INSTALL_FAILED_USER_RESTRICTED*): copy the APK
     with `.\adb push RentalSystem-Tablet.apk /sdcard/Download/`, install it
     from the tablet's File Manager, then run the `dpm` command.
4. With the server running, open Slotify on the tablet. It **finds the server
   automatically** over Wi-Fi (no IP to type). That can take up to about 20
   seconds. If it never connects, check that AP/client isolation is off (§4)
   and that the server PC is on.
5. On the server: **Import from tablet** → pick the tablet → import its games.

### 7.3 Maintenance mode (to update apps or change settings)

Kiosk mode stops customers leaving the app. To get out of it yourself:

- **On the tablet:** tap **Admin** and enter the admin password (§8). If you
  don't see the Admin button, long-press the **Slotify logo** at the top to show
  the admin panel.
- **From the server:** select the tablet in the station list and use
  **Open as Admin** (the **…** button in the Selected Station panel).

The tablet shows its normal Android screen. Install updates or change settings,
then open Slotify and tap **Return to Kiosk**.

Device Owner stays active the whole time, so you never need the `adb` command again
unless you factory reset the tablet.

---

## 8. Admin password, prices and final test

0. **Change the admin password first.** On the server, open **Tablet Admin
   Password** and set a new one. It applies to the PCs and tablets. The
   default `admin@123!` is public, and anyone who knows it can open
   maintenance mode on your units.
1. Server → **Setup → Edit Rates** → set prices per device type → Save. The
   new prices reach every unit right away.
2. On a locked PC: **Insert Coin** → drop a coin → time is added and the
   screen unlocks.
3. Let it run out → it locks again.
4. Repeat on a tablet.

---

## 9. Troubleshooting

| Problem | Check |
|---------|-------|
| PC not on the server | Server window open? `ping <server IP>` from the PC. Reinstall the client and type the server IP instead of `auto`. Diskless: is the hostname unique? |
| Tablet won't connect | AP isolation off? Was the server running **before** you opened the app? |
| Coinslot rejects every coin | Normal when no one has tapped Insert Coin (the slot only opens during that window). If it still rejects during a claim: check the GPIO 23 ↔ SET wire and that `coin-01` is online. |
| Coin accepted but no credit | Is the WHITE wire reaching the EL817? Common GND? R2 present? |
| Wrong amount credited | The Allan must be on **SLOW + NO**, with pulses = pesos (§2.5). |
| Phantom (fake) credits | Move the WHITE wire away from the LED frame power, twist it with GND, add 100 nF on GPIO 18. |
| ESP32 dead after power-on | The LM2596 wasn't set to 5V (§2.4). |
| Box keeps making a hotspot | Wrong Wi-Fi password or weak signal. Set it up again from a phone. |
| "Mosquitto could not be installed" | Run `C:\Program Files\RentalSystem\Server\redist\mosquitto-install-windows-x64.exe`, then restart. |

---

## 10. File checksums (SHA-256)

Check a file in PowerShell with `Get-FileHash .\<file>`.

Values for **v1.3.0**. Every release also includes a `SHA256SUMS.txt` file with them.

| File | SHA-256 |
|------|---------|
| `RentalSystem-Server-Setup.exe` | `465543c673435b9761b7b300896311e8b290399217e1a060ed5ab70551284efd` |
| `RentalSystem-Client-Setup.exe` | `46b02f33a299f8cdfdbc5a9577f24bfd26ade386d5330a4e33d1e6f76a3cea74` |
| `RentalSystem-Tablet.apk` | `b088a286d6e209de7486e02ada9ef18c200b67b3dfa2baf24ef3a6b892b60431` |
| `coin-box-merged.bin` | `4759a3cd7ef2d53ddee2dadca54a9df8392047d675e44e8ebfa0bab2446e0d8b` |

---

## 11. License and disclaimer

Slotify is free and open source under the [GPL-3.0 license](../LICENSE). Slotify is provided **as-is, with no warranty**.
It handles your shop's money, so test it fully (§2.7, §8) before using it
with real customers.
