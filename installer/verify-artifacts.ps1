[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactsRoot,
    [switch]$RequireInstaller,
    [string]$ManifestPath
)
$ErrorActionPreference = "Stop"

$RepoRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
$repoArtifacts = [IO.Path]::GetFullPath((Join-Path $RepoRoot "artifacts")).TrimEnd('\')
if (-not $ArtifactsRoot) { $ArtifactsRoot = $repoArtifacts }
$ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\')
if (-not $ManifestPath) { $ManifestPath = Join-Path $ArtifactsRoot "installer\release-manifest.json" }
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)

function Test-Under([string]$Path, [string]$Root) {
    return [IO.Path]::GetFullPath($Path).StartsWith($Root + "\", [StringComparison]::OrdinalIgnoreCase)
}
function Fail([string]$Message) { throw "verify-artifacts: $Message" }

if ($ArtifactsRoot -ne $repoArtifacts -and -not (Test-Under $ArtifactsRoot $repoArtifacts)) {
    Fail "artifact root '$ArtifactsRoot' is not inside '$repoArtifacts'"
}
if (-not (Test-Under $ManifestPath $ArtifactsRoot)) { Fail "manifest path escapes the artifact staging directory" }

$appDir = Join-Path $ArtifactsRoot "app\win-x64"
$workerDir = Join-Path $ArtifactsRoot "worker\win-x64"
$installerExe = Join-Path $ArtifactsRoot "installer\DetailFrame-Setup.exe"

$required = @(
    @{ Dir = $appDir; File = "DetailFrame.App.exe" },
    @{ Dir = $appDir; File = "shared\models.json" },
    @{ Dir = $appDir; File = "Assets\Branding\app.ico" },
    @{ Dir = $workerDir; File = "detailframe-worker.exe" },
    @{ Dir = $workerDir; File = "dependency-manifest.txt" }
)
foreach ($r in $required) {
    if (-not (Test-Path -LiteralPath $r.Dir -PathType Container)) { Fail "missing input directory $($r.Dir)" }
    if (-not (Test-Path -LiteralPath (Join-Path $r.Dir $r.File) -PathType Leaf)) { Fail "missing required input $(Join-Path $r.Dir $r.File)" }
}
if ($RequireInstaller -and -not (Test-Path -LiteralPath $installerExe -PathType Leaf)) {
    Fail "missing installer output $installerExe"
}

function Get-PeMachine([string]$Path) {
    $fs = [IO.File]::OpenRead($Path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $fs.Position = $br.ReadInt32()
        if ($br.ReadUInt32() -ne 0x4550) { Fail "$Path is not a PE file" }
        return $br.ReadUInt16()
    }
    finally { $fs.Dispose() }
}
foreach ($exe in @((Join-Path $appDir "DetailFrame.App.exe"), (Join-Path $workerDir "detailframe-worker.exe"))) {
    if ((Get-PeMachine $exe) -ne 0x8664) { Fail "$exe is not an x64 binary" }
}

function Get-FileEntries([string]$Dir, [string]$Prefix) {
    $root = [IO.Path]::GetFullPath($Dir).TrimEnd('\')
    $entries = foreach ($item in (Get-ChildItem -LiteralPath $root -Recurse -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail "reparse point not allowed: $($item.FullName)" }
        if ($item.PSIsContainer) { continue }
        $full = [IO.Path]::GetFullPath($item.FullName)
        if (-not (Test-Under $full $root)) { Fail "path resolves outside staging directory: $full" }
        [pscustomobject]@{
            path   = "$Prefix/" + $full.Substring($root.Length + 1).Replace('\', '/')
            bytes  = $item.Length
            sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return @($entries | Sort-Object -Property path -CaseSensitive)
}

$appVersion = (Get-Item (Join-Path $appDir "DetailFrame.App.exe")).VersionInfo.ProductVersion
if (-not $appVersion) { Fail "DetailFrame.App.exe has no product version" }
$appVersion = ($appVersion -split '\+')[0]

$pyproject = Join-Path $RepoRoot "worker\pyproject.toml"
$vm = Select-String -Path $pyproject -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $vm) { Fail "worker version not found in $pyproject" }
$workerVersion = $vm.Matches[0].Groups[1].Value

$workerInventory = @(Get-Content -LiteralPath (Join-Path $workerDir "dependency-manifest.txt") |
    Where-Object { $_ -and $_ -notmatch '^-e ' } | Sort-Object -CaseSensitive)
if ($workerInventory.Count -eq 0) { Fail "worker dependency manifest is empty" }

$appRuntime = @(Get-ChildItem -LiteralPath $appDir -File |
    Where-Object { $_.Name -match '^(Microsoft\..+\.dll|.+\.runtimeconfig\.json|.+\.deps\.json)$' } |
    Sort-Object Name -CaseSensitive |
    ForEach-Object {
        $v = $_.VersionInfo.FileVersion
        if ($v) { "$($_.Name) $v" } else { $_.Name }
    })

$files = @(Get-FileEntries $appDir "app/win-x64") + @(Get-FileEntries $workerDir "worker/win-x64")
if (Test-Path -LiteralPath $installerExe -PathType Leaf) {
    $files += [pscustomobject]@{
        path   = "installer/DetailFrame-Setup.exe"
        bytes  = (Get-Item $installerExe).Length
        sha256 = (Get-FileHash -LiteralPath $installerExe -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

# No timestamps: identical inputs must yield an identical manifest.
$manifest = [ordered]@{
    schema         = 1
    architecture   = "x64"
    appVersion     = $appVersion
    workerVersion  = $workerVersion
    appRuntime     = $appRuntime
    workerPackages = $workerInventory
    files          = $files
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ManifestPath) | Out-Null
($manifest | ConvertTo-Json -Depth 5) | Out-File -Encoding utf8 -LiteralPath $ManifestPath

"Verified app $appVersion, worker $workerVersion (x64): $($files.Count) files hashed -> $ManifestPath"
