[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [switch]$SkipWorker,
    [switch]$SkipApp
)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$issPath = Join-Path $PSScriptRoot "DetailFrame.iss"

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6.3+ and make its compiler available on PATH before building the installer."
}
if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) {
    throw "Missing Inno Setup source: $issPath"
}

if (-not $SkipWorker) {
    & (Join-Path $repoRoot "worker\packaging\build.ps1")
}

if (-not $SkipApp) {
    & (Join-Path $PSScriptRoot "build-app.ps1") -RepoRoot $repoRoot
}

& (Join-Path $PSScriptRoot "verify-artifacts.ps1") -RepoRoot $repoRoot
# Scan only the payload the installer packages (not build intermediates).
foreach ($payload in "artifacts\app\win-x64", "artifacts\worker\win-x64") {
    & (Join-Path $PSScriptRoot "verify-no-machine-paths.ps1") -Root (Join-Path $repoRoot $payload)
    if ($LASTEXITCODE -ne 0) { throw "machine path scan failed for $payload" }
}

$manifestPath = Join-Path $repoRoot "artifacts\installer\release-manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$appVersion = [string]$manifest.appVersion
if (-not $appVersion) { throw "The artifact manifest does not contain an app version." }

& $iscc.Source "/DAppVersion=$appVersion" $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE" }

& (Join-Path $PSScriptRoot "verify-artifacts.ps1") -RepoRoot $repoRoot -RequireInstaller
if ($LASTEXITCODE -ne 0) { throw "final artifact verification failed" }
"Built $Configuration installer: $(Join-Path $repoRoot 'artifacts\installer\DetailFrame-Setup.exe')"
