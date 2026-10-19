$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}

foreach ($f in "installer\README.md", "installer\build.ps1", "installer\verify-artifacts.ps1") {
    Check "exists: $f" (Test-Path (Join-Path $repo $f))
}

# Generated output must be ignored, never tracked.
foreach ($p in "artifacts/app/win-x64/DetailFrame.App.exe", "artifacts/worker/win-x64/detailframe-worker.exe", "artifacts/installer/DetailFrame-Setup.exe") {
    git -C $repo check-ignore -q $p
    Check "git-ignored: $p" ($LASTEXITCODE -eq 0)
}
Check "no tracked artifacts" (-not (git -C $repo ls-files artifacts))

# Installer inputs: only the two artifact dirs plus versioned installer/branding files.
$allowed = @('artifacts\app\win-x64', 'artifacts\worker\win-x64', 'installer\', 'src\DetailFrame.App\Assets\Branding')
$scriptFiles = Get-ChildItem (Join-Path $repo "installer") -Include *.iss, *.wxs, *.nsi -Recurse -ErrorAction SilentlyContinue
$bad = @()
foreach ($s in $scriptFiles) {
    foreach ($m in Select-String -Path $s.FullName -Pattern '(?i)(\.\.\\|artifacts\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_.-]+|src\\[^"'';\r\n]+)') {
        foreach ($g in $m.Matches) {
            $v = $g.Value
            if (-not ($allowed | Where-Object { $v -like "*$_*" })) { $bad += "$($s.Name): $v" }
        }
    }
}
Check "installer scripts reference only allowed inputs ($($bad -join '; '))" ($bad.Count -eq 0)

# Verifier behaviour on fixtures inside the ignored artifacts directory.
$verify = Join-Path $repo "installer\verify-artifacts.ps1"
$fixture = Join-Path $repo "artifacts\test-fixture\layout"
function New-Fixture {
    if (Test-Path $fixture) { Remove-Item -Recurse -Force $fixture }
    $app = Join-Path $fixture "app\win-x64"; $wk = Join-Path $fixture "worker\win-x64"
    New-Item -ItemType Directory -Force "$app\shared", "$app\Assets\Branding", $wk | Out-Null
    # Real x64 PE binaries stand in for the produced executables.
    $stub = Join-Path $env:SystemRoot "System32\whoami.exe"
    Copy-Item $stub "$app\DetailFrame.App.exe"; Copy-Item $stub "$wk\detailframe-worker.exe"
    Set-Content "$app\shared\models.json" "{}"; Set-Content "$app\Assets\Branding\app.ico" "x"
    Set-Content "$wk\dependency-manifest.txt" "numpy==1.0`nattrs==2.0"
}
function Invoke-Verify([hashtable]$Extra = @{}) {
    try { & pwsh -NoProfile -File $verify -RepoRoot $repo -ArtifactsRoot $fixture @Extra *> $null; return ($LASTEXITCODE -eq 0) }
    catch { return $false }
}

try {
    New-Fixture
    Check "verify passes on complete fixture" (Invoke-Verify)
    $m1 = Get-Content (Join-Path $fixture "installer\release-manifest.json") -Raw
    Invoke-Verify | Out-Null
    $m2 = Get-Content (Join-Path $fixture "installer\release-manifest.json") -Raw
    Check "manifest is deterministic" ($m1 -eq $m2)
    $json = $m1 | ConvertFrom-Json
    Check "manifest records versions, arch, hashes" ($json.appVersion -and $json.workerVersion -and $json.architecture -eq "x64" -and ($json.files | Where-Object { $_.sha256.Length -ne 64 }).Count -eq 0)
    Check "manifest records dependency inventory" ($json.workerPackages.Count -eq 2)
    Check "verify fails when installer required but absent" (-not (Invoke-Verify @{ RequireInstaller = $true }))

    Remove-Item "$fixture\worker\win-x64\detailframe-worker.exe"
    Check "verify fails when worker exe missing" (-not (Invoke-Verify))

    New-Fixture
    Remove-Item -Recurse -Force "$fixture\app"
    Check "verify fails when app dir missing" (-not (Invoke-Verify))

    New-Fixture
    $outside = Join-Path $fixture "..\outside"
    New-Item -ItemType Directory -Force $outside | Out-Null
    $link = "$fixture\app\win-x64\escape"
    cmd /c mklink /J "$link" "$outside" *> $null
    Check "junction fixture created" (Test-Path $link)
    Check "verify fails on junction escaping staging" (-not (Invoke-Verify))
    cmd /c rmdir "$link"

    $ok = $true
    try { & pwsh -NoProfile -File $verify -RepoRoot $repo -ArtifactsRoot $env:TEMP *> $null; $ok = ($LASTEXITCODE -eq 0) } catch { $ok = $false }
    Check "verify rejects artifact root outside repository" (-not $ok)
}
finally {
    if (Test-Path (Join-Path $repo "artifacts\test-fixture")) { Remove-Item -Recurse -Force (Join-Path $repo "artifacts\test-fixture") }
}

if ($failures.Count -gt 0) { "FAILED: $($failures.Count) check(s)"; exit 1 }
"All release layout checks passed."
