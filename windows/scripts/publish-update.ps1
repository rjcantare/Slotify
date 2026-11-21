param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$ServerIp,

    [string]$BrokerHost = $ServerIp,
    [int]$BrokerPort = 1883,
    [switch]$SkipBuild,
    [switch]$AndroidOnly,
    [switch]$WindowsOnly,
    [switch]$AllowDirty,
    [switch]$AllowVersionReuse
)

# =====================================================================
#  BEFORE YOU PUBLISH: read ProjectContext/ota-publish-runbook.md
#  This script builds the WORKING TREE on disk (gradlew clean
#  :app:assembleDebug + dotnet publish), NOT a checked-out commit, so the
#  artifact is only as correct as the tree right now. Android 1.0.31
#  shipped a stale build that dropped the coin-accumulation + "Call staff"
#  UI because the tree did not match committed source. Golden rule: clean
#  working tree at the exact commit you intend to ship, for server +
#  client + Android. Never use -SkipBuild for a real release. After the
#  device installs, smoke-test the locked coin window, not just "it opens".
# =====================================================================

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be in semver format, e.g. 0.2.0"
}

if ($AndroidOnly -and $WindowsOnly) {
    throw "-AndroidOnly and -WindowsOnly are mutually exclusive. Pick one, or neither for a full publish."
}

# Per-component build plan. Each component is either BUILT from the working tree
# this run, or CARRIED FORWARD unchanged from the current retained manifest:
#   full          -> build server + client + android
#   -AndroidOnly  -> build android;  carry server + client
#   -WindowsOnly  -> build client;   carry server + android (server stays pinned,
#                    so a client-only fix never forces a server update/restart)
$buildServer  = -not ($AndroidOnly -or $WindowsOnly)
$buildClient  = -not $AndroidOnly
$buildAndroid = -not $WindowsOnly

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$updatesDir = Join-Path $repoRoot "data\updates"
$serverProject = Join-Path $repoRoot "src\RentalServer\RentalServer.csproj"
$clientProject = Join-Path $repoRoot "src\RentalClient.Windows\RentalClient.Windows.csproj"
$androidProject = Join-Path $repoRoot "..\RentalAndroid"
$mosquittoPub = "C:\Program Files\mosquitto\mosquitto_pub.exe"
$mosquittoSub = "C:\Program Files\mosquitto\mosquitto_sub.exe"

if (-not (Test-Path $mosquittoPub)) {
    throw "mosquitto_pub not found at $mosquittoPub"
}

if (-not (Test-Path $mosquittoSub)) {
    throw "mosquitto_sub not found at $mosquittoSub"
}

New-Item -ItemType Directory -Force -Path $updatesDir | Out-Null

# ---- Source-integrity gate ----
# This script builds the WORKING TREE, not a checked-out commit, so a dirty tree
# ships untraceable bytes — this is how 1.0.31 shipped a stale build that dropped
# the coin-accumulation + "Call staff" UI. Refuse a dirty tree for any repo we
# will actually BUILD this run, capture each HEAD SHA, and record it in the
# manifest + data\updates\publish-log.txt so every release maps to a commit.
# See ProjectContext/ota-publish-runbook.md.

function Get-RepoHead([string]$repoPath, [string]$label, [string]$scope) {
    $resolved = (Resolve-Path $repoPath).Path
    $sha = (& git -C $resolved rev-parse --short=12 HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        throw "Cannot read git HEAD for $label at $resolved. Is git on PATH and is this a git repo?"
    }
    # Scope the dirty check to the build inputs (e.g. RentalSystem 'src') so a
    # binary publish isn't blocked by unrelated doc edits elsewhere in the repo.
    $porcelain = if ([string]::IsNullOrEmpty($scope)) {
        & git -C $resolved status --porcelain
    }
    else {
        & git -C $resolved status --porcelain -- $scope
    }
    $isDirty = -not [string]::IsNullOrWhiteSpace(($porcelain -join "`n"))
    return [pscustomobject]@{ Label = $label; Sha = $sha.Trim(); Dirty = $isDirty; Files = $porcelain }
}

# Gate only the repos this run will compile, scoped to their build inputs.
$builtRepos = @()
if (-not $SkipBuild) {
    if ($buildAndroid) {
        $builtRepos += (Get-RepoHead $androidProject "RentalAndroid" $null)
    }
    if ($buildServer -or $buildClient) {
        $builtRepos += (Get-RepoHead $repoRoot "RentalSystem" "src")
    }
}
foreach ($r in $builtRepos) {
    if ($r.Dirty) {
        Write-Host "Uncommitted changes in $($r.Label):" -ForegroundColor Yellow
        $r.Files | ForEach-Object { Write-Host "  $_" }
        if (-not $AllowDirty) {
            throw "Refusing to publish: $($r.Label) working tree is dirty. The build ships the working tree, not HEAD - commit or stash first, or pass -AllowDirty to override. See ProjectContext/ota-publish-runbook.md."
        }
        Write-Warning "Publishing a DIRTY $($r.Label) tree (-AllowDirty). Shipped bytes will NOT match commit $($r.Sha)."
    }
}
function Get-CommitText([string]$label) {
    $repo = $builtRepos | Where-Object Label -eq $label | Select-Object -First 1
    if ($null -eq $repo) { return $null }
    if ($repo.Dirty) { return "$($repo.Sha)-dirty" }
    return $repo.Sha
}
$androidCommitText = Get-CommitText "RentalAndroid"
$serverCommitText  = Get-CommitText "RentalSystem"
if ($builtRepos.Count -gt 0) {
    Write-Host "Source commits being published:" -ForegroundColor Cyan
    $builtRepos | ForEach-Object { Write-Host ("  {0}: {1}{2}" -f $_.Label, $_.Sha, $(if ($_.Dirty) { " (DIRTY)" } else { "" })) }
}

# Android versionCode from semver: Major*1e6 + Minor*1e3 + Patch. Must stay
# strictly above every code already on a device (a lower code is rejected as a
# downgrade). Computed here so it's available for the build AND the publish log.
$verParts = $Version.Split('.')
$androidVersionCode = ([int]$verParts[0]) * 1000000 + ([int]$verParts[1]) * 1000 + ([int]$verParts[2])

# ---- Version safety gate ----

function Convert-ToComparableVersion([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    try {
        return [version]$value
    }
    catch {
        return $null
    }
}

function Get-RetainedUpdateManifest {
    $output = & $mosquittoSub -h $BrokerHost -p $BrokerPort -C 1 -W 3 -t "rental/config/update" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($output)) {
        return $null
    }

    $payload = ($output -join "`n").TrimStart([char]0xFEFF)
    try {
        return $payload | ConvertFrom-Json
    }
    catch {
        throw "Existing retained rental/config/update payload is not parseable JSON. Clear or repair it before publishing."
    }
}

$requestedVersion = Convert-ToComparableVersion $Version
$currentManifest = Get-RetainedUpdateManifest
$currentVersionText = $currentManifest.version
$currentVersion = Convert-ToComparableVersion $currentVersionText

if ($currentVersion -ne $null -and $requestedVersion -le $currentVersion -and -not $AllowVersionReuse) {
    throw "Refusing to publish v$Version because retained rental/config/update is already v$currentVersionText. Bump the version. Reusing OTA versions strands clients on old binaries."
}

if ($currentVersion -ne $null -and $requestedVersion -le $currentVersion -and $AllowVersionReuse) {
    Write-Warning "Publishing non-incremental v$Version over retained v$currentVersionText because -AllowVersionReuse was supplied. Do not use this in production."
}

# ---- Build ----

if (-not $SkipBuild) {
    # /p:Version stamps the manifest version into the EXE's FileVersion
    # resource so the running binary self-reports it. Without this, the
    # default 1.0.0.0 is baked in and the update badge keeps reappearing
    # after a successful swap because the EXE still claims to be 1.0.0.
    # /p:IncludeNativeLibrariesForSelfExtract is required for WPF: without it
    # PresentationNative_cor3.dll isn't found at runtime and HwndSubclass
    # throws DllNotFoundException.
    if ($buildServer) {
        Write-Host "[1/3] Building RentalServer (Release, win-x64, self-contained)..." -ForegroundColor Cyan
        dotnet publish $serverProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o "$repoRoot\artifacts\publish\server"
    }
    else {
        Write-Host "[1/3] Skipping RentalServer build; carried forward from the retained manifest." -ForegroundColor Yellow
    }

    if ($buildClient) {
        Write-Host "[2/3] Building RentalClient.Windows (Release, win-x64, self-contained)..." -ForegroundColor Cyan
        dotnet publish $clientProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o "$repoRoot\artifacts\publish\client"
    }
    else {
        Write-Host "[2/3] Skipping RentalClient.Windows build; carried forward from the retained manifest." -ForegroundColor Yellow
    }

    if ($buildAndroid) {
        Write-Host "[3/3] Building RentalAndroid (Debug APK, versionName=$Version versionCode=$androidVersionCode)..." -ForegroundColor Cyan
        Push-Location $androidProject
        try {
            # --no-daemon: without it the idle Gradle daemon keeps this cmd /c
            # stdout pipe open after a successful build, so the script hangs
            # before publishing the manifest (logged in CLAUDE.md mDNS gotchas).
            cmd /c ".\gradlew.bat clean :app:assembleDebug -PversionName=$Version -PversionCode=$androidVersionCode --no-daemon 2>&1"
            if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
        }
        finally {
            Pop-Location
        }
    }
    else {
        Write-Host "[3/3] Skipping RentalAndroid build; carried forward from the retained manifest." -ForegroundColor Yellow
    }
}

# ---- Copy artifacts ----

if ($buildAndroid) {
    $androidApk = "$androidProject\app\build\outputs\apk\debug\app-debug.apk"
    Copy-Item $androidApk "$updatesDir\RentalAndroid.apk" -Force
}
if ($buildServer) {
    Copy-Item "$repoRoot\artifacts\publish\server\RentalServer.exe" "$updatesDir\RentalServer.exe" -Force
}
if ($buildClient) {
    Copy-Item "$repoRoot\artifacts\publish\client\RentalClient.Windows.exe" "$updatesDir\RentalClient.Windows.exe" -Force
}

# ---- Hash + size ----

function Get-Sha256($path) {
    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
    return $hash.ToLowerInvariant()
}

# Build each manifest section from the freshly built artifact, or carry the
# component forward unchanged from the current retained manifest.
function ConvertTo-Section($node, $name) {
    if ($null -eq $node) {
        throw "Cannot carry $name forward: the retained rental/config/update has no $name section. Run a full publish first."
    }
    $h = @{}
    foreach ($p in $node.PSObject.Properties) { $h[$p.Name] = $p.Value }
    return $h
}

if ($buildAndroid) {
    $androidHash = Get-Sha256 "$updatesDir\RentalAndroid.apk"
    $androidSize = (Get-Item "$updatesDir\RentalAndroid.apk").Length
    $androidSection = @{
        version      = $Version
        url          = "http://${ServerIp}:8081/RentalAndroid.apk"
        sha256       = $androidHash
        sizeBytes    = $androidSize
        sourceCommit = $androidCommitText
    }
}
else {
    $androidSection = ConvertTo-Section $currentManifest.android "android"
    Write-Host "  Carrying forward android v$($androidSection.version) unchanged." -ForegroundColor DarkGray
}

if ($buildServer) {
    $serverHash = Get-Sha256 "$updatesDir\RentalServer.exe"
    $serverSize = (Get-Item "$updatesDir\RentalServer.exe").Length
    $serverSection = @{
        version      = $Version
        url          = "http://${ServerIp}:8081/RentalServer.exe"
        sha256       = $serverHash
        sizeBytes    = $serverSize
        sourceCommit = $serverCommitText
    }
}
else {
    $serverSection = ConvertTo-Section $currentManifest.server "server"
    Write-Host "  Carrying forward server v$($serverSection.version) unchanged." -ForegroundColor DarkGray
}

if ($buildClient) {
    $clientHash = Get-Sha256 "$updatesDir\RentalClient.Windows.exe"
    $clientSize = (Get-Item "$updatesDir\RentalClient.Windows.exe").Length
    $clientSection = @{
        version      = $Version
        url          = "http://${ServerIp}:8081/RentalClient.Windows.exe"
        sha256       = $clientHash
        sizeBytes    = $clientSize
        sourceCommit = $serverCommitText
    }
}
else {
    $clientSection = ConvertTo-Section $currentManifest.client "client"
    Write-Host "  Carrying forward client v$($clientSection.version) unchanged." -ForegroundColor DarkGray
}

# ---- Build manifest JSON ----

$publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

$manifest = @{
    version     = $Version
    publishedAt = $publishedAt
    server      = $serverSection
    client      = $clientSection
    android     = $androidSection
}

# -Compress produces single-line JSON with no newlines. This matters for the
# `mosquitto_pub -f` publish below: mosquitto_pub rejects a file containing
# CRLF line endings with "Invalid input", and the manifest is machine-consumed
# only, so compact is both required and harmless.
$manifestJson = $manifest | ConvertTo-Json -Depth 3 -Compress

# ---- Publish manifest to MQTT ----

Write-Host "[4/5] Publishing manifest to MQTT broker at ${BrokerHost}:${BrokerPort}..." -ForegroundColor Cyan

# PowerShell 5.1 pipes stdin to native commands with a UTF-8 BOM
# ($OutputEncoding fallback), and mosquitto includes the BOM in the
# retained payload — which breaks System.Text.Json on the receiving end
# (0xEF is an invalid start of a value).
#
# Workaround: drive mosquitto_pub via Process.Start() with redirected
# stdin. Our manifest JSON is pure ASCII so the default ANSI encoding
# produces the correct bytes without any BOM.
# mosquitto_pub's -f mode is also unreliable on Windows (rejects files
# saved by PowerShell/File.WriteAllText with "Invalid input").
# Publish via a temp file + `mosquitto_pub -f`, NOT stdin. The stdin path
# (`-s`) is unreliable on Windows: regardless of how the bytes are written
# (StreamWriter or raw BaseStream), the retained payload comes back with a
# UTF-8 BOM (EF BB BF) prepended and CRLF normalized — text-mode processing on
# the pipe we cannot suppress from PowerShell. That BOM breaks every receiver:
# System.Text.Json throws "invalid start value" (server Check for Updates) and
# kotlinx.serialization fails to parse (Android tablet, so its update badge
# never appears). Writing the compact JSON to a file as UTF-8 with NO BOM and
# publishing with `-f` round-trips byte-exact (verified: payload starts with
# 0x7B '{', no BOM, no CRLF).
$manifestFile = Join-Path ([System.IO.Path]::GetTempPath()) "rental-update-manifest.json"
[System.IO.File]::WriteAllBytes(
    $manifestFile,
    [System.Text.UTF8Encoding]::new($false).GetBytes($manifestJson)
)
try {
    & $mosquittoPub -h $BrokerHost -p $BrokerPort -t "rental/config/update" -f $manifestFile -r -q 1
    if ($LASTEXITCODE -ne 0) {
        throw "mosquitto_pub failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item $manifestFile -ErrorAction SilentlyContinue
}

# ---- Publish log (traceability) ----
# Append-only record so a future regression can be traced to the exact source.
$logMode = if ($AndroidOnly) { "android-only" } elseif ($WindowsOnly) { "windows-only" } else { "all" }
$logServer  = if ($buildServer)  { "built@$serverCommitText" }  else { "carried(v$($serverSection.version))" }
$logClient  = if ($buildClient)  { "built@$serverCommitText" }  else { "carried(v$($clientSection.version))" }
$logAndroid = if ($buildAndroid) { "built@$androidCommitText(vc$androidVersionCode)" } else { "carried(v$($androidSection.version))" }
$logLine = "$publishedAt  v$Version  mode=$logMode  server=$logServer  client=$logClient  android=$logAndroid"
Add-Content -Path (Join-Path $updatesDir "publish-log.txt") -Value $logLine -Encoding UTF8
Write-Host "Logged to data\updates\publish-log.txt:" -ForegroundColor DarkGray
Write-Host "  $logLine" -ForegroundColor DarkGray

# ---- Summary ----

Write-Host "[5/5] Done!" -ForegroundColor Green
Write-Host ""
Write-Host "Published update v${Version} (mode: $logMode):" -ForegroundColor White
Write-Host "  Server:     v$($serverSection.version)  SHA256=$($serverSection.sha256)"
Write-Host "  Client:     v$($clientSection.version)  SHA256=$($clientSection.sha256)"
Write-Host "  Android:    v$($androidSection.version)  SHA256=$($androidSection.sha256)"
Write-Host "  Manifest:   rental/config/update (retained)"
Write-Host ""
Write-Host "Artifacts in: $updatesDir"
Write-Host "HTTP server:  http://${ServerIp}:8081/"
