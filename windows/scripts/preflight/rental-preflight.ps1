param(
    [ValidateSet("Server", "Client")]
    [string]$Mode = "Server",

    [string]$BrokerHost = "127.0.0.1",

    [int]$BrokerPort = 1883,

    [string]$InstallRoot = "",

    [string]$ReportDirectory = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    if ($Mode -eq "Server") {
        $InstallRoot = Join-Path $env:ProgramFiles "RentalSystem\Server"
    } else {
        $InstallRoot = Join-Path $env:ProgramFiles "RentalSystem\Client"
    }
}

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $programData = [Environment]::GetFolderPath("CommonApplicationData")
    $ReportDirectory = Join-Path $programData "RentalSystem\preflight"
}

New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null

$checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $checks.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    }) | Out-Null
}

function Test-DirectoryWritable {
    param([string]$Path)

    try {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
        $probe = Join-Path $Path ("preflight-" + [Guid]::NewGuid().ToString("N") + ".tmp")
        Set-Content -Path $probe -Value "ok" -Encoding ASCII
        Remove-Item -LiteralPath $probe -Force
        return $true
    } catch {
        return $false
    }
}

function Test-TcpPort {
    param(
        [string]$HostName,
        [int]$Port
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(2000)) {
            return $false
        }

        $client.EndConnect($iar)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Get-ScheduledTaskSafe {
    param([string]$TaskName)

    try {
        return Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    } catch {
        return $null
    }
}

function Test-JsonFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return [pscustomobject]@{ exists = $false; valid = $false; detail = "missing" }
    }

    try {
        Get-Content -Raw -Path $Path | ConvertFrom-Json | Out-Null
        return [pscustomobject]@{ exists = $true; valid = $true; detail = "valid JSON" }
    } catch {
        return [pscustomobject]@{ exists = $true; valid = $false; detail = $_.Exception.Message }
    }
}

$os = Get-CimInstance Win32_OperatingSystem
Add-Check "Windows version" ($os.Version -ge "10.0") "$($os.Caption) $($os.Version)"

$programDataRoot = Join-Path ([Environment]::GetFolderPath("CommonApplicationData")) "RentalSystem"
Add-Check "ProgramData writable" (Test-DirectoryWritable $programDataRoot) $programDataRoot

$portOpen = Test-TcpPort -HostName $BrokerHost -Port $BrokerPort
Add-Check "Broker TCP $BrokerHost`:$BrokerPort" $portOpen ($(if ($portOpen) { "reachable" } else { "not reachable" }))

if ($Mode -eq "Server") {
    $serverExe = Join-Path $InstallRoot "RentalServer.exe"
    Add-Check "RentalServer install path" (Test-Path $serverExe) $serverExe

    $mosquitto = Get-Service -Name "mosquitto" -ErrorAction SilentlyContinue
    Add-Check "Mosquitto service installed" ($null -ne $mosquitto) ($(if ($mosquitto) { $mosquitto.Status.ToString() } else { "missing" }))
    Add-Check "Mosquitto service running" ($null -ne $mosquitto -and $mosquitto.Status -eq "Running") ($(if ($mosquitto) { $mosquitto.Status.ToString() } else { "missing" }))

    $task = Get-ScheduledTaskSafe -TaskName "RentalServer"
    Add-Check "RentalServer scheduled task" ($null -ne $task) ($(if ($task) { $task.State.ToString() } else { "missing" }))

    $dbPath = Join-Path $programDataRoot "rental.db"
    $dbParentWritable = Test-DirectoryWritable (Split-Path -Parent $dbPath)
    $dbDetail = if (Test-Path $dbPath) { "exists: $dbPath" } else { "parent writable: $dbPath" }
    Add-Check "Rental database path" $dbParentWritable $dbDetail

    $backupRoot = Join-Path $programDataRoot "backups"
    Add-Check "Backup folder writable" (Test-DirectoryWritable $backupRoot) $backupRoot

    # mDNS responder needs inbound UDP 5353 so Wi-Fi devices (tablets, ESP32)
    # can discover the broker. The installer adds this rule; flag if it's gone.
    $mdnsRule = Get-NetFirewallRule -DisplayName "RentalSystem mDNS (UDP 5353)" -ErrorAction SilentlyContinue
    $mdnsPresent = $null -ne $mdnsRule
    Add-Check "mDNS firewall rule (UDP 5353)" $mdnsPresent ($(if ($mdnsPresent) { "present - Wi-Fi devices can discover the broker" } else { "missing - Wi-Fi auto-discovery may fail" }))
}
else {
    $clientExe = Join-Path $InstallRoot "RentalClient.Windows.exe"
    Add-Check "RentalClient install path" (Test-Path $clientExe) $clientExe

    $task = Get-ScheduledTaskSafe -TaskName "RentalClient"
    Add-Check "RentalClient scheduled task" ($null -ne $task) ($(if ($task) { $task.State.ToString() } else { "missing" }))

    $configPath = Join-Path $InstallRoot "appsettings.json"
    $config = Test-JsonFile $configPath
    Add-Check "Client appsettings.json" ($config.exists -and $config.valid) "$configPath - $($config.detail)"

    $stationId = ([string]$env:COMPUTERNAME).ToLowerInvariant()
    Add-Check "Hostname station id" ($stationId -match "^(pc|phn|tab)-[0-9]+$") $stationId
}

$failed = @($checks | Where-Object { -not $_.passed })
$summary = [pscustomobject]@{
    mode = $Mode
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    brokerHost = $BrokerHost
    brokerPort = $BrokerPort
    installRoot = $InstallRoot
    passed = ($failed.Count -eq 0)
    failedCount = $failed.Count
    checks = $checks
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonPath = Join-Path $ReportDirectory "rental-preflight-$($Mode.ToLowerInvariant())-$stamp.json"
$txtPath = Join-Path $ReportDirectory "rental-preflight-$($Mode.ToLowerInvariant())-$stamp.txt"

$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("RentalSystem $Mode preflight") | Out-Null
$lines.Add("Generated UTC: $($summary.generatedAtUtc)") | Out-Null
$lines.Add("Overall: $(if ($summary.passed) { "PASS" } else { "FAIL" })") | Out-Null
$lines.Add("") | Out-Null
foreach ($check in $checks) {
    $status = if ($check.passed) { "PASS" } else { "FAIL" }
    $lines.Add("[$status] $($check.name) - $($check.detail)") | Out-Null
}
$lines | Set-Content -Path $txtPath -Encoding UTF8

Write-Host "Overall: $(if ($summary.passed) { "PASS" } else { "FAIL" })"
Write-Host "Text report: $txtPath"
Write-Host "JSON report: $jsonPath"

if (-not $summary.passed) {
    exit 1
}
