# Contributing / testing

Slotify has only been proven in **one shop**: 12 Windows 10 PCs on **iCafe8
diskless**, Android tablets, and one **Allan 1239A Pro Max** coinslot.
Reports from other setups are the most useful contribution right now.

## Tester report

Open an issue titled `Tester report: <your setup>` and include:

- **PC setup:** diskless (which: iCafe8 / CCBoot / other) or traditional (own drive)
- **Freeze software:** none / Deep Freeze / Shadow Defender / other
- **Windows version** and number of PCs
- **Tablets:** brand/model, Android version (Xiaomi/HyperOS? say so)
- **Coin acceptor model**, and whether it has a SET/inhibit pin
- **Router model**, and whether the PCs are wired or on Wi-Fi
- **What worked / what didn't** (screenshots or photos help)
- Server log if something failed

## Code changes

- `windows/`: .NET 8 (WPF server + Windows client + tests). `dotnet test windows/RentalSystem.sln`
- `android/`: Kotlin, Gradle. `./gradlew :app:assembleDebug`
- `firmware/`: PlatformIO (ESP32). Copy `include/config.example.h` to `include/config.h` first.

Anything that changes how coins are counted or credited needs a test and a
note on how you tested it with real coins.
