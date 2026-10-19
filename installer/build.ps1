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
    if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
    dotnet publish (Join-Path $repoRoot "src\DetailFrame.App\DetailFrame.App.csproj") `
        -c Release -r win-x64 -p:Platform=x64 --self-contained true `
        -p:Deterministic=true -p:ContinuousIntegrationBuild=true -o $appOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

& (Join-Path $PSScriptRoot "verify-artifacts.ps1") -RepoRoot $repoRoot
