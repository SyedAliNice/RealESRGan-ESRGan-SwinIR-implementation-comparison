$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}
function ExitsNonZero([string]$Script, [string[]]$ScriptArgs) {
    & pwsh -NoProfile -File $Script @ScriptArgs *> $null
    return $LASTEXITCODE -ne 0
}

$buildApp = Join-Path $repo "installer\build-app.ps1"
$workerBuild = Join-Path $repo "worker\packaging\build.ps1"
$scan = Join-Path $repo "installer\verify-no-machine-paths.ps1"
foreach ($f in $buildApp, $workerBuild, $scan) { Check "exists: $f" (Test-Path $f) }

# Positive: the real checkout satisfies the inputs.
& pwsh -NoProfile -File $buildApp -ValidateOnly *> $null
Check "build-app accepts repo inputs" ($LASTEXITCODE -eq 0)
& pwsh -NoProfile -File $workerBuild -ValidateOnly *> $null
Check "worker build accepts repo inputs" ($LASTEXITCODE -eq 0)

$scratch = Join-Path $repo "artifacts\test-scratch\clean-build"
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
try {
    # Missing app project, solution and branding: build-app must fail.
    New-Item -ItemType Directory -Force $scratch | Out-Null
    Check "build-app fails on empty root" (ExitsNonZero $buildApp @("-RepoRoot", $scratch, "-ValidateOnly"))

    # Each required file removed in turn must fail.
    $files = "DetailFrame.sln", "src\DetailFrame.App\DetailFrame.App.csproj", "src\DetailFrame.App\Assets\Branding\app.ico", "src\DetailFrame.App\Assets\Branding\detailframe.svg", "src\DetailFrame.App\Assets\Branding\app-256.png"
    foreach ($f in $files) {
        $full = Join-Path $scratch $f
        New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
        Set-Content $full "x"
    }
    Check "build-app passes with all inputs" (-not (ExitsNonZero $buildApp @("-RepoRoot", $scratch, "-ValidateOnly")))
    foreach ($f in $files) {
        $full = Join-Path $scratch $f
        Rename-Item $full "$full.bak"
        Check "build-app fails without $f" (ExitsNonZero $buildApp @("-RepoRoot", $scratch, "-ValidateOnly"))
        Rename-Item "$full.bak" $full
    }

    # Worker package absent: copy only the script into a tree with no worker inputs.
    $wpk = Join-Path $scratch "worker\packaging"
    New-Item -ItemType Directory -Force $wpk | Out-Null
    Copy-Item $workerBuild (Join-Path $wpk "build.ps1")
    Check "worker build fails without pyproject/uv.lock/spec" (ExitsNonZero (Join-Path $wpk "build.ps1") @("-ValidateOnly"))

    # Spec/metadata present but worker entry point absent must still fail.
    foreach ($n in "pyproject.toml", "uv.lock") { Set-Content (Join-Path $scratch "worker\$n") "x" }
    Set-Content (Join-Path $wpk "detailframe-worker.spec") "x"
    Check "worker build fails without entry point" (ExitsNonZero (Join-Path $wpk "build.ps1") @("-ValidateOnly"))
    $ep = Join-Path $scratch "worker\src\detailframe_worker"
    New-Item -ItemType Directory -Force $ep | Out-Null
    Set-Content (Join-Path $ep "__main__.py") "x"
    Check "worker build passes with entry point" (-not (ExitsNonZero (Join-Path $wpk "build.ps1") @("-ValidateOnly")))

    # Machine-path scanner behaviour.
    $art = Join-Path $scratch "art"
    New-Item -ItemType Directory -Force $art | Out-Null
    Set-Content (Join-Path $art "ok.txt") "numpy==2.0.0"
    Check "scanner passes clean artifacts" (-not (ExitsNonZero $scan @("-Root", $art)))
    Set-Content (Join-Path $art "bad.txt") ("path " + $repo + "\worker")
    Check "scanner flags source worktree path" (ExitsNonZero $scan @("-Root", $art))
    Remove-Item (Join-Path $art "bad.txt")
    Set-Content (Join-Path $art "bad.json") 'C:\Users\someone\AppData\x'
    Check "scanner flags user profile path" (ExitsNonZero $scan @("-Root", $art))
    Remove-Item (Join-Path $art "bad.json")
    Set-Content (Join-Path $art "bad.cfg") 'api_key = abcdef1234567890abcd'
    Check "scanner flags credentials" (ExitsNonZero $scan @("-Root", $art))
    Remove-Item (Join-Path $art "bad.cfg")
    # Binary artifacts: embedded paths in ASCII and UTF-16LE must be caught; clean binaries pass.
    $junk = [byte[]](0..255)
    $bin = Join-Path $art "bin"
    New-Item -ItemType Directory -Force $bin | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $bin "clean.dll"), $junk)
    Check "scanner passes clean binary" (-not (ExitsNonZero $scan @("-Root", $bin)))
    $ascii = [Text.Encoding]::ASCII.GetBytes("src\" + $repo + "\worker\x.py")
    $wide = [Text.Encoding]::Unicode.GetBytes('C:\Users\someone\proj\x.cs')
    $cases = @{ "a.pdb" = ($junk + $ascii + $junk); "b.exe" = ($junk + [byte]0 + $wide + $junk); "c.dll" = ($junk + $wide + $junk) }
    foreach ($k in $cases.Keys) {
        [IO.File]::WriteAllBytes((Join-Path $bin "clean.dll"), $junk)
        Get-ChildItem $bin -Exclude clean.dll | Remove-Item
        [IO.File]::WriteAllBytes((Join-Path $bin $k), [byte[]]$cases[$k])
        Check "scanner flags embedded path in $k" (ExitsNonZero $scan @("-Root", $bin))
    }
    Remove-Item -Recurse -Force $bin
    Set-Content (Join-Path $art "model.pth") "x"
    Check "scanner flags model weights" (ExitsNonZero $scan @("-Root", $art))
}
finally {
    if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
}

# Scripts must not reference attachment/OneDrive/user-profile inputs or download weights.
$texts = foreach ($f in $buildApp, $workerBuild, (Join-Path $repo "installer\build.ps1")) { Get-Content $f -Raw }
$joined = $texts -join "`n"
Check "no OneDrive/user-profile input reads" ($joined -notmatch '(?i)onedrive|\$env:USERPROFILE|\\Users\\|attachments')
Check "no model downloads in build" ($joined -notmatch '(?i)Invoke-WebRequest|curl |wget|huggingface|\.pth\b')
Check "worker spec exists" (Test-Path (Join-Path $repo "worker\packaging\detailframe-worker.spec"))

if ($failures.Count -gt 0) { "FAILED: $($failures.Count)"; exit 1 }
"ALL PASSED"

