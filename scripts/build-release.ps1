# Builds every public release file for one Slotify version into release\v<Version>\:
#   RentalSystem-Server-Setup.exe, RentalSystem-Client-Setup.exe,
#   RentalSystem-Tablet.apk, coin-box-merged.bin, SHA256SUMS.txt
# plus the in-place update files the shop server's Update button uses:
#   RentalServer.exe, RentalClient.Windows.exe (single-file), update.json
#
# Needs: .NET 8 SDK, Inno Setup 6, PlatformIO, Android SDK + JDK 17/21.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.3.0
#
# The tablet APK must be signed with the project's private keystore (never commit it):
#   $env:SLOTIFY_KEYSTORE          = "<path to the .jks>"
#   $env:SLOTIFY_KEY_ALIAS         = "<alias>"
#   $env:SLOTIFY_KEYSTORE_PASSWORD = "<store password>"
#   $env:SLOTIFY_KEY_PASSWORD      = "<key password>"
# Every APK update has to be signed with the same key as the installed app, or
# Android refuses it. Forks must use their own key.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipFirmware,
    [switch]$SkipWindows,
    [switch]$SkipAndroid,
    [switch]$SkipTests,
    # GitHub repo whose release hosts the files; update.json links point there.
    [string]$Repo = "rjcantare/Slotify"
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.3.0 (got '$Version')." }
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
$versionCode = $parts[0] * 1000000 + $parts[1] * 1000 + $parts[2]   # 1.3.0 -> 1003000 (same scheme as earlier tablet builds)

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "release\v$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkDir = $root) {
    Push-Location $WorkDir
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
    } finally { Pop-Location }
}

function Find-Tool([string]$Name, [string[]]$Candidates) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($c in $Candidates) { if (Test-Path $c) { return (Resolve-Path $c).Path } }
    return $null
}

# ---------------------------------------------------------------- firmware --
if (-not $SkipFirmware) {
    Write-Host "`n=== Coin box firmware ===" -ForegroundColor Cyan
    $pioHome = Join-Path $env:USERPROFILE ".platformio"
    $pio = Find-Tool "pio" @("$pioHome\penv\Scripts\pio.exe")
    if (-not $pio) { throw "PlatformIO not found. Install the VS Code PlatformIO extension or 'pip install platformio'." }

    $fw = Join-Path $root "firmware"
    $configH = Join-Path $fw "include\config.h"
    $backup = "$configH.release-backup"
    $devOtaPassword = $null
    if (Test-Path $configH) {
        $m = Select-String -Path $configH -Pattern '#define\s+OTA_PASSWORD\s+"([^"]*)"'
        if ($m) { $devOtaPassword = $m.Matches[0].Groups[1].Value }
        Copy-Item $configH $backup -Force
    }

    try {
        # Generic public build: no Wi-Fi baked in (box opens its setup hotspot),
        # server found by mDNS, OTA disabled (a shared OTA password would let
        # anyone on a shop's Wi-Fi reflash that shop's coin box).
        $cfg = Get-Content (Join-Path $fw "include\config.example.h") -Raw
        $cfg = $cfg -replace '#define\s+WIFI_SSID\s+"[^"]*"', '#define WIFI_SSID       ""'
        $cfg = $cfg -replace '#define\s+WIFI_PASSWORD\s+"[^"]*"', '#define WIFI_PASSWORD   ""'
        $cfg = $cfg -replace '#define\s+MQTT_HOST\s+"[^"]*"', '#define MQTT_HOST       "auto"'
        $cfg = $cfg -replace '#define\s+OTA_PASSWORD\s+"[^"]*"', '#define OTA_PASSWORD     ""'
        $cfg = $cfg -replace '#define\s+FIRMWARE_VERSION\s+"[^"]*"', "#define FIRMWARE_VERSION `"$Version`""
        Set-Content -Path $configH -Value $cfg -Encoding ascii

        Invoke-Checked $pio @("run", "-e", "esp32dev") $fw

        $build = Join-Path $fw ".pio\build\esp32dev"
        $bootApp0 = Join-Path $pioHome "packages\framework-arduinoespressif32\tools\partitions\boot_app0.bin"
        foreach ($f in @("$build\bootloader.bin", "$build\partitions.bin", "$build\firmware.bin", $bootApp0)) {
            if (-not (Test-Path $f)) { throw "Missing $f after the PlatformIO build." }
        }
        # esptool: a pip-installed one (CI: pip install "esptool<5") or PlatformIO's bundled copy
        # run with PlatformIO's own Python, which has esptool's dependencies.
        $python = Find-Tool "python" @()
        $esptoolCmd = $null
        if ($python) {
            # try/catch: Windows PowerShell turns native stderr into a terminating error under "Stop".
            try { & $python -c "import esptool" 2>$null; if ($LASTEXITCODE -eq 0) { $esptoolCmd = @($python, "-m", "esptool") } } catch { }
        }
        if (-not $esptoolCmd -and (Test-Path "$pioHome\penv\Scripts\python.exe") -and (Test-Path "$pioHome\packages\tool-esptoolpy\esptool.py")) {
            $esptoolCmd = @("$pioHome\penv\Scripts\python.exe", "$pioHome\packages\tool-esptoolpy\esptool.py")
        }
        if (-not $esptoolCmd) { throw "esptool not found. Run: pip install `"esptool<5`"" }
        $merged = Join-Path $out "coin-box-merged.bin"
        # One image flashable at 0x0 from the browser flasher.
        Invoke-Checked $esptoolCmd[0] (@($esptoolCmd | Select-Object -Skip 1) + @("--chip", "esp32", "merge_bin", "-o", $merged,
            "--flash_mode", "dio", "--flash_size", "4MB",
            "0x1000", "$build\bootloader.bin", "0x8000", "$build\partitions.bin",
            "0xe000", $bootApp0, "0x10000", "$build\firmware.bin")) $fw

        # Refuse to ship if a private password slipped into the image.
        $bytes = [System.IO.File]::ReadAllBytes($merged)
        $text = [System.Text.Encoding]::ASCII.GetString($bytes)
        foreach ($secret in @($devOtaPassword, "change-me-per-shop") | Where-Object { $_ }) {
            if ($text.Contains($secret)) { throw "coin-box-merged.bin contains an OTA password. Not shipping it." }
        }
        Write-Host "Firmware OK: $merged"
    } finally {
        if (Test-Path $backup) { Move-Item $backup $configH -Force } else { Remove-Item $configH -Force -ErrorAction SilentlyContinue }
    }
}

# ----------------------------------------------------------------- windows --
if (-not $SkipWindows) {
    Write-Host "`n=== Windows server + client installers ===" -ForegroundColor Cyan
    if (-not (Find-Tool "dotnet" @("$env:ProgramFiles\dotnet\dotnet.exe"))) { throw ".NET 8 SDK not found." }
    $win = Join-Path $root "windows"
    if (-not $SkipTests) { Invoke-Checked "dotnet" @("test", "RentalSystem.sln", "-c", "Release") $win }
    Invoke-Checked "powershell" @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$win\installer\fetch-redist.ps1") $win
    Invoke-Checked "powershell" @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$win\installer\build-server.ps1", "-Version", $Version) $win
    Invoke-Checked "powershell" @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$win\installer\build-client.ps1", "-Version", $Version) $win
    foreach ($n in "RentalSystem-Server-Setup.exe", "RentalSystem-Client-Setup.exe") {
        Copy-Item (Join-Path $win "installer\output\$n") (Join-Path $out $n) -Force
    }

    # In-place update EXEs: one self-contained file each (native DLLs bundled),
    # the same shape the Update button has always downloaded.
    foreach ($app in @(@{ Proj = "src\RentalServer\RentalServer.csproj"; Exe = "RentalServer.exe"; Dir = "server" },
                       @{ Proj = "src\RentalClient.Windows\RentalClient.Windows.csproj"; Exe = "RentalClient.Windows.exe"; Dir = "client" })) {
        $otaDir = Join-Path $win "artifacts\ota\$($app.Dir)"
        if (Test-Path $otaDir) { Remove-Item $otaDir -Recurse -Force }
        Invoke-Checked "dotnet" @("publish", (Join-Path $win $app.Proj), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:Version=$Version", "-o", $otaDir) $win
        Copy-Item (Join-Path $otaDir $app.Exe) (Join-Path $out $app.Exe) -Force
    }
}

# ----------------------------------------------------------------- android --
if (-not $SkipAndroid) {
    Write-Host "`n=== Android tablet app ===" -ForegroundColor Cyan
    if (-not $env:SLOTIFY_KEYSTORE -or -not (Test-Path $env:SLOTIFY_KEYSTORE)) {
        throw "Set `$env:SLOTIFY_KEYSTORE to your private release keystore (see the top of this script)."
    }
    $and = Join-Path $root "android"
    if (-not (Test-Path "$and\local.properties") -and -not $env:ANDROID_HOME -and -not $env:ANDROID_SDK_ROOT) {
        throw "Android SDK not found. Set ANDROID_HOME or create android\local.properties with sdk.dir=..."
    }
    Invoke-Checked "$and\gradlew.bat" @("--no-daemon", ":app:assembleDebug", "-PversionName=$Version", "-PversionCode=$versionCode") $and
    Copy-Item "$and\app\build\outputs\apk\debug\app-debug.apk" (Join-Path $out "RentalSystem-Tablet.apk") -Force
}

# ------------------------------------------------------------- update.json --
# Manifest for the shop server's Update button. Only written when all three
# apps were built, so it can never point at a file this release doesn't have.
if (-not $SkipWindows -and -not $SkipAndroid) {
    $commit = (& git -C $root rev-parse --short=12 HEAD).Trim()
    $base = "https://github.com/$Repo/releases/download/v$Version"
    function New-Section([string]$File) {
        $p = Join-Path $out $File
        return [ordered]@{ version = $Version; url = "$base/$File"; sha256 = (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant();
                           sizeBytes = (Get-Item $p).Length; sourceCommit = $commit }
    }
    $manifest = [ordered]@{
        version     = $Version
        publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        server      = (New-Section "RentalServer.exe")
        client      = (New-Section "RentalClient.Windows.exe")
        android     = (New-Section "RentalSystem-Tablet.apk")
    }
    # UTF-8 without BOM: a BOM breaks the JSON readers on the server and tablets.
    [System.IO.File]::WriteAllText((Join-Path $out "update.json"), ($manifest | ConvertTo-Json -Depth 5) + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

# ------------------------------------------------------------------ hashes --
$files = Get-ChildItem $out -File | Where-Object Name -ne "SHA256SUMS.txt" | Sort-Object Name
$lines = $files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
Set-Content -Path (Join-Path $out "SHA256SUMS.txt") -Value $lines -Encoding ascii

Write-Host "`n=== Release v$Version ready in $out ===" -ForegroundColor Green
$files | ForEach-Object { "{0,-34} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
""
$lines
