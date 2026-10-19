[CmdletBinding()]
param(
    [string]$Root = "artifacts",
    [string[]]$ForbiddenPath = @(),
    [string]$AllowListPath = (Join-Path $PSScriptRoot "machine-path-allowlist.json")
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Root not found: $Root" }
$rootFull = (Resolve-Path -LiteralPath $Root).Path

$repoRoot = Split-Path -Parent $PSScriptRoot
$forbidden = @($repoRoot, $env:USERPROFILE, $env:OneDrive) + $ForbiddenPath |
    Where-Object { $_ } | Sort-Object -Unique
$weightExt = '.pth', '.pt', '.onnx', '.safetensors', '.ckpt', '.gguf', '.h5'
$textExt = '.txt', '.json', '.xml', '.config', '.manifest', '.ps1', '.py', '.toml', '.cfg', '.ini', '.iss', '.md', '.yaml', '.yml', '.csv'
$patterns = @(
    @{ Name = "user profile path"; Rx = '(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|All Users\b)[^\\/\s"'']+' },
    @{ Name = "OneDrive path"; Rx = '(?i)[\\/]OneDrive[^\\/]*[\\/]' },
    @{ Name = "copilot worktree path"; Rx = '(?i)copilot-worktrees' },
    @{ Name = "credential"; Rx = '(?i)(ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(password|secret|api[_-]?key|token)\s*[=:]\s*[''"]?[A-Za-z0-9+/_-]{12,})' }
)

$binaryPatterns = $patterns | Where-Object { $_.Name -ne "credential" }
function Find-BinaryPathHits([string]$Path) {
    $chunk = 4MB; $overlap = 1024
    $buf = New-Object byte[] ($chunk + $overlap)
    $hits = New-Object System.Collections.Generic.HashSet[string]
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        $carry = 0
        while ($true) {
            $n = $fs.Read($buf, $carry, $chunk)
            if ($n -le 0) { break }
            $len = $carry + $n
            $views = @([Text.Encoding]::Latin1.GetString($buf, 0, $len),
                       [Text.Encoding]::Unicode.GetString($buf, 0, $len - ($len % 2)))
            if ($len -gt 1) { $views += [Text.Encoding]::Unicode.GetString($buf, 1, ($len - 1) - (($len - 1) % 2)) }
            foreach ($v in $views) {
                foreach ($p in $forbidden) {
                    if ($v.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { [void]$hits.Add("machine path '$p' (binary)") }
                }
                foreach ($p in $binaryPatterns) {
                    if ($v -match $p.Rx) { [void]$hits.Add("$($p.Name) (binary)") }
                }
            }
            $carry = [Math]::Min($overlap, $len)
            [Array]::Copy($buf, $len - $carry, $buf, 0, $carry)
        }
    }
    finally { $fs.Dispose() }
    return $hits
}

$allow = @()
if ($AllowListPath -and (Test-Path -LiteralPath $AllowListPath -PathType Leaf)) {
    $allow = @(Get-Content -LiteralPath $AllowListPath -Raw | ConvertFrom-Json)
}
# An allow entry exempts exactly one pattern in exactly one file (path relative to the scanned root).
function Test-Allowed([string]$Rel, [string]$Pattern) {
    foreach ($a in $allow) {
        if ($a.path -ieq ($Rel -replace '\\', '/') -and $a.pattern -eq $Pattern) { return $true }
    }
    return $false
}
function Add-Problem([string]$Pattern, [string]$Rel, [string]$Message) {
    if (-not (Test-Allowed $Rel $Pattern)) { $problems.Add($Message) }
}

$problems = New-Object System.Collections.Generic.List[string]
foreach ($f in Get-ChildItem -LiteralPath $rootFull -Recurse -File) {
    $rel = $f.FullName.Substring($rootFull.Length).TrimStart('\')
    $ext = $f.Extension.ToLowerInvariant()
    if ($weightExt -contains $ext) { $problems.Add("unverified model file: $rel"); continue }
    if ($textExt -notcontains $ext) {
        # Binaries (exe, dll, pdb, pyd...) can embed source paths: stream-scan ASCII and UTF-16LE forms.
        foreach ($hit in Find-BinaryPathHits $f.FullName) { Add-Problem ($hit -replace ' \(binary\)$','') $rel "$hit in $rel" }
        continue
    }
    $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }
    foreach ($p in $forbidden) {
        if ($text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { Add-Problem "machine path" $rel "machine path '$p' in $rel" }
    }
    foreach ($p in $patterns) {
        if ($text -match $p.Rx) { Add-Problem $p.Name $rel "$($p.Name) in $rel" }
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { "FAIL  $_" }
    exit 1
}
"PASS  no machine paths, credentials or model weights under $rootFull"

