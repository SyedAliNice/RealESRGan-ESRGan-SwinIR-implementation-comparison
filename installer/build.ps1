[CmdletBinding()]
param(
    [switch]$SkipWorker,
    [switch]$SkipApp
)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appOut = Join-Path $repoRoot "artifacts\app\win-x64"

if (-not $SkipWorker) {
    & (Join-Path $repoRoot "worker\packaging\build.ps1")
}

if (-not $SkipApp) {
    & (Join-Path $PSScriptRoot "build-app.ps1") -RepoRoot $repoRoot
}

& (Join-Path $PSScriptRoot "verify-artifacts.ps1") -RepoRoot $repoRoot
& (Join-Path $PSScriptRoot "verify-no-machine-paths.ps1") -Root (Join-Path $repoRoot "artifacts")
if ($LASTEXITCODE -ne 0) { throw "machine path scan failed" }
