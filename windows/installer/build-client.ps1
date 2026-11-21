param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "artifacts\publish\client"
$project = Join-Path $repoRoot "src\RentalClient.Windows\RentalClient.Windows.csproj"
$iss = Join-Path $repoRoot "installer\client.iss"

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

# Clean the publish dir first. Incremental single-file publishes can drop the
# loose WPF native DLLs (wpfgfx_cor3, vcruntime140_cor3, ...), which then crash
# on a clean machine with DllNotFoundException.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Invoke-NativeChecked "dotnet" @(
    "publish",
    $project,
    "-c",
    $Configuration,
    "-r",
    "win-x64",
    "--self-contained",
    "true",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o",
    $publishDir
)

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
$isccPath = $null
if ($null -ne $iscc) {
    $isccPath = $iscc.Source
}

if ($null -eq $iscc) {
    $candidatePaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path $candidatePath) {
            $isccPath = (Get-Item $candidatePath).FullName
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($isccPath)) {
    throw "Inno Setup compiler not found. Install Inno Setup 6, then rerun installer\build-client.ps1."
}

Invoke-NativeChecked $isccPath @($iss)
