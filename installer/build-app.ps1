[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$ValidateOnly
)
$ErrorActionPreference = "Stop"

$project = Join-Path $RepoRoot "src\DetailFrame.App\DetailFrame.App.csproj"
$solution = Join-Path $RepoRoot "DetailFrame.sln"
$branding = Join-Path $RepoRoot "src\DetailFrame.App\Assets\Branding"
$appOut = Join-Path $RepoRoot "artifacts\app\win-x64"

# All inputs must come from the repository checkout.
$required = @($solution, $project, (Join-Path $branding "app.ico"), (Join-Path $branding "detailframe.svg"), (Join-Path $branding "app-256.png"))
foreach ($r in $required) {
    if (-not (Test-Path -LiteralPath $r -PathType Leaf)) { throw "Missing required build input: $r" }
}
if ($ValidateOnly) { "Inputs OK"; return }

if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$dotnetVersion = (dotnet --version)
$manifest = Join-Path $appOut "build-manifest.txt"
@("dotnet-sdk=$dotnetVersion", "runtime=win-x64", "configuration=Release", "self-contained=true") |
    Out-File -Encoding ascii $manifest
"Published $appOut"
