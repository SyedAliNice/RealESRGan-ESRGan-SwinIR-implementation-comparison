$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}
$scan = Join-Path $repo "installer\verify-no-machine-paths.ps1"
$allow = Join-Path $repo "installer\machine-path-allowlist.json"
$mkManifest = Join-Path $repo "worker\packaging\write-dependency-manifest.ps1"
$buildText = Get-Content (Join-Path $repo "installer\build.ps1") -Raw

# build.ps1 scans the shipped payload, not all of artifacts.
Check "build scans app payload" ($buildText -match 'artifacts\\app\\win-x64')
Check "build scans worker payload" ($buildText -match 'artifacts\\worker\\win-x64')
Check "build does not scan whole artifacts root" ($buildText -notmatch '-Root \(Join-Path \$repoRoot "artifacts"\)')

# Allow list shape: each entry has path, pattern and reason; no wildcards.
$entries = @(Get-Content $allow -Raw | ConvertFrom-Json)
Check "allowlist entries are exact and justified" (@($entries | Where-Object { -not $_.path -or -not $_.pattern -or -not $_.reason -or $_.path -match '[*?]' }).Count -eq 0)

$scratch = Join-Path $repo "artifacts\test-scratch\machine-path-scan"
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
try {
    $root = Join-Path $scratch "worker"
    New-Item -ItemType Directory -Force (Join-Path $root "_internal\torch") | Out-Null
    $hub = Join-Path $root "_internal\torch\hub.py"
    Set-Content $hub 'TOKEN = "GITHUB_TOKEN"'
    & pwsh -NoProfile -File $scan -Root $root -AllowListPath $allow *> $null
    Check "allowlisted torch/hub.py credential false positive passes" ($LASTEXITCODE -eq 0)
    & pwsh -NoProfile -File $scan -Root $root -AllowListPath (Join-Path $scratch "none.json") *> $null
    Check "same file fails without the allow entry" ($LASTEXITCODE -ne 0)

    # Allow is per file and per pattern.
    Set-Content $hub 'TOKEN = "GITHUB_TOKEN"; x = "C:\Users\someone\file"'
    & pwsh -NoProfile -File $scan -Root $root -AllowListPath $allow *> $null
    Check "allowlisted file still fails on a different pattern" ($LASTEXITCODE -ne 0)
    Set-Content $hub 'x = 1'
    Set-Content (Join-Path $root "other.py") 'TOKEN = "GITHUB_TOKEN"'
    & pwsh -NoProfile -File $scan -Root $root -AllowListPath $allow *> $null
    Check "same pattern in a non-allowlisted file fails" ($LASTEXITCODE -ne 0)
    Remove-Item (Join-Path $root "other.py")
    Set-Content (Join-Path $root "dependency-manifest.txt") "-e file:///C:/Users/someone/worker"
    & pwsh -NoProfile -File $scan -Root $root -AllowListPath $allow *> $null
    Check "manifest with machine path fails" ($LASTEXITCODE -ne 0)

    # Manifest generator drops editable/file sources and rejects leaks.
    $out = Join-Path $scratch "manifest.txt"
    & pwsh -NoProfile -File $mkManifest -OutFile $out -FreezeText "numpy==2.3.3`n-e file:///C:/Users/someone/worker`npkg @ file:///C:/Users/someone/pkg" *> $null
    $clean = (Test-Path $out) -and ((Get-Content $out -Raw) -notmatch 'file:|Users') -and ((Get-Content $out -Raw) -match 'numpy==2.3.3')
    Check "manifest generator emits no machine paths" ($LASTEXITCODE -eq 0 -and $clean)
    & pwsh -NoProfile -File $mkManifest -OutFile $out -FreezeText "pkg @ C:\src\pkg" *> $null
    Check "manifest generator rejects residual drive paths" ($LASTEXITCODE -ne 0)
}
finally {
    if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
}
if ($failures.Count -gt 0) { exit 1 }
