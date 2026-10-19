[CmdletBinding()]
param(
    [string]$Root = "artifacts",
    [string[]]$ForbiddenPath = @()
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

$problems = New-Object System.Collections.Generic.List[string]
foreach ($f in Get-ChildItem -LiteralPath $rootFull -Recurse -File) {
    $rel = $f.FullName.Substring($rootFull.Length).TrimStart('\')
    $ext = $f.Extension.ToLowerInvariant()
    if ($weightExt -contains $ext) { $problems.Add("unverified model file: $rel"); continue }
    if ($textExt -notcontains $ext) { continue }
    $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }
    foreach ($p in $forbidden) {
        if ($text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("machine path '$p' in $rel") }
    }
    foreach ($p in $patterns) {
        if ($text -match $p.Rx) { $problems.Add("$($p.Name) in $rel") }
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { "FAIL  $_" }
    exit 1
}
"PASS  no machine paths, credentials or model weights under $rootFull"
