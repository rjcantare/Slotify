# Downloads the third-party redistributables the SERVER installer bundles:
#   1. Eclipse Mosquitto (MQTT broker) Windows x64 installer
#   2. Microsoft Visual C++ 2015-2022 x64 redistributable (Mosquitto 2.1.x is
#      MSVC-built and will not start without it on a fresh Windows VM)
#
# These are NOT committed to git (see installer\redist\.gitignore) to avoid
# bloating the repo. Run this once on any build machine before building the
# server installer. Re-running is a no-op when the files already exist.
#
# Pin versions here so every build produces the same bundle.
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$MosquittoVersion = "2.1.2"
$redistDir = Join-Path $PSScriptRoot "redist"
New-Item -ItemType Directory -Force -Path $redistDir | Out-Null

$downloads = @(
    @{
        Name = "Mosquitto $MosquittoVersion (x64)"
        Url  = "https://mosquitto.org/files/binary/win64/mosquitto-$MosquittoVersion-install-windows-x64.exe"
        File = Join-Path $redistDir "mosquitto-install-windows-x64.exe"
    },
    @{
        Name = "VC++ 2015-2022 x64 redistributable"
        Url  = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
        File = Join-Path $redistDir "vc_redist.x64.exe"
    }
)

foreach ($d in $downloads) {
    if ((Test-Path $d.File) -and -not $Force) {
        Write-Host "[skip] $($d.Name) already present: $($d.File)"
        continue
    }

    Write-Host "[get ] $($d.Name) <- $($d.Url)"
    Invoke-WebRequest -Uri $d.Url -OutFile $d.File -UseBasicParsing -TimeoutSec 120
    $sizeMB = [math]::Round((Get-Item $d.File).Length / 1MB, 1)
    Write-Host "[ ok ] saved $($d.File) ($sizeMB MB)"
}

Write-Host ""
Write-Host "Redist ready in $redistDir"
Get-ChildItem $redistDir -File | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
