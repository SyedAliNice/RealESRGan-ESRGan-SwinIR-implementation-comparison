[CmdletBinding()]
param(
    [switch]$ValidateOnly
)
$ErrorActionPreference = "Stop"

$workerRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $workerRoot

# Fail early if any repository-managed input is missing; model weights are never bundled or downloaded here.
foreach ($r in @((Join-Path $workerRoot "pyproject.toml"), (Join-Path $workerRoot "uv.lock"), (Join-Path $PSScriptRoot "detailframe-worker.spec"), (Join-Path $workerRoot "src\detailframe_worker\__main__.py"))) {
    if (-not (Test-Path -LiteralPath $r -PathType Leaf)) { throw "Missing required worker build input: $r" }
}
if ($ValidateOnly) { "Inputs OK"; return }

$python = Join-Path $workerRoot ".venv\Scripts\python.exe"
$output = Join-Path $repoRoot "artifacts\worker"
$buildDir = Join-Path $repoRoot "artifacts\build\worker"
$target = Join-Path $output "win-x64"

Push-Location $workerRoot
try {
    uv sync --frozen --extra dev --extra build
    if ($LASTEXITCODE -ne 0) { throw "uv sync failed" }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }

    & $python -m PyInstaller --noconfirm --clean `
        --distpath $output --workpath $buildDir `
        (Join-Path $PSScriptRoot "detailframe-worker.spec")
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }

    $manifest = Join-Path $target "dependency-manifest.txt"
    & (Join-Path $PSScriptRoot "write-dependency-manifest.ps1") -OutFile $manifest -Python $python
}
finally {
    Pop-Location
}

$size = (Get-ChildItem -Recurse -File $target | Measure-Object Length -Sum).Sum / 1MB
"Built $target ({0:N0} MiB)" -f $size

