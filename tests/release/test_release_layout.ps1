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
# Inspects installer scripts (.iss/.wxs/.nsi) and the PowerShell build scripts/manifests.
function Find-BadInputs([string]$Text) {
    $bad = @()
    $norm = $Text -replace '/', '\'
    if ($norm -match '(?i)\.\.\\') { $bad += '..\' }
    foreach ($m in [regex]::Matches($norm, '(?i)(?:artifacts\\|\$ArtifactsRoot\s+")([A-Za-z0-9_.\\-]+)')) {
        $v = $m.Groups[1].Value
        if ($v -notmatch '(?i)^(app\\win-x64|worker\\win-x64|installer)(\\|$)') { $bad += "artifacts\$v" }
    }
    foreach ($m in [regex]::Matches($norm, '(?i)src\\[A-Za-z0-9_.\\-]+')) {
        if ($m.Value -notlike 'src\DetailFrame.App\Assets\Branding*' -and $m.Value -notlike '*.csproj') { $bad += $m.Value }
    }
    return $bad
}
$inspected = @(Get-ChildItem (Join-Path $repo "installer") -Include *.iss, *.wxs, *.nsi, *.ps1 -Recurse -File)
$bad = @(); $validRefs = 0
foreach ($s in $inspected) {
    $text = Get-Content $s.FullName -Raw
    $bad += Find-BadInputs $text | ForEach-Object { "$($s.Name): $_" }
    $validRefs += [regex]::Matches(($text -replace '/', '\'), '(?i)(app|worker)\\win-x64').Count
}
Check "input policy inspects build scripts (not vacuous)" ($inspected.Name -contains "build.ps1" -and $inspected.Name -contains "verify-artifacts.ps1" -and $validRefs -ge 4)
Check "installer/build scripts reference only allowed inputs ($($bad -join '; '))" ($bad.Count -eq 0)
Check "input policy rejects disallowed artifact dir" ((Find-BadInputs 'Source: "artifacts\other\x\*"').Count -gt 0)
Check "input policy rejects parent-relative path" ((Find-BadInputs 'Source: "..\secrets\*"').Count -gt 0)
Check "input policy rejects arbitrary src path" ((Find-BadInputs 'Source: "src\Other\bin\*"').Count -gt 0)
Check "input policy rejects bad ArtifactsRoot join" ((Find-BadInputs '$d = Join-Path $ArtifactsRoot "tools\x"').Count -gt 0)
Check "input policy accepts allowed inputs" ((Find-BadInputs 'Source: "artifacts\app\win-x64\*"; Source: "src\DetailFrame.App\Assets\Branding\app.ico"').Count -eq 0)
# Verifier behaviour on fixtures inside the ignored artifacts directory.
$verify = Join-Path $repo "installer\verify-artifacts.ps1"
$testRoot = Join-Path $repo ("artifacts\test-fixture\layout-" + [guid]::NewGuid().ToString("N"))
$fixture = Join-Path $testRoot "layout"
$pyVersion = [regex]::Match((Get-Content (Join-Path $repo "worker\pyproject.toml") -Raw), '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
$WorkerStubVersion = $pyVersion
$stubCache = @{}
# Compiles a tiny x64 console exe that prints a worker version, using the in-box .NET Framework C# compiler.
function New-WorkerStub([string]$Version) {
    if ($stubCache.ContainsKey($Version)) { return $stubCache[$Version] }
    $dir = Join-Path $testRoot "stubs"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $exe = Join-Path $dir "worker-$Version.exe"
    $cs = Join-Path $dir "worker-$Version.cs"
    Set-Content $cs "public class P { public static void Main() { System.Console.WriteLine(`"detailframe-worker $Version`"); } }"
    $csc = Join-Path $env:SystemRoot "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path $csc)) { throw "Fixture compiler not found: $csc" }
    $compilerOutput = & $csc /nologo /platform:x64 "/out:$exe" $cs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture compiler failed ($LASTEXITCODE): $compilerOutput" }
    $stubCache[$Version] = $exe
    return $exe
}
function New-Fixture {
    if (Test-Path $fixture) { Remove-Item -Recurse -Force $fixture }
    $app = Join-Path $fixture "app\win-x64"; $wk = Join-Path $fixture "worker\win-x64"
    New-Item -ItemType Directory -Force "$app\shared", "$app\Assets\Branding", "$app\sub", $wk | Out-Null
    # Real x64 PE binaries stand in for the produced executables.
    $stub = Join-Path $env:SystemRoot "System32\whoami.exe"
    Copy-Item $stub "$app\DetailFrame.App.exe"
    Copy-Item (New-WorkerStub $WorkerStubVersion) "$wk\detailframe-worker.exe"
    Set-Content "$app\System.Runtime.dll" "r"; Set-Content "$app\coreclr.dll" "c"; Set-Content "$app\sub\native.bin" "n"
    Set-Content "$app\shared\models.json" "{}"; Set-Content "$app\shared\models.schema.json" "{}"; Set-Content "$app\shared\reviewed-manifests.json" "{}"
    Set-Content "$app\Assets\Branding\app.ico" "x"
    Set-Content "$wk\dependency-manifest.txt" "numpy==1.0`nattrs==2.0"
}
function Invoke-Verify([hashtable]$Extra = @{}) {
    try { & pwsh -NoProfile -File $verify -RepoRoot $repo -ArtifactsRoot $fixture @Extra *> $null; return ($LASTEXITCODE -eq 0) }
    catch { return $false }
}

try {
    New-Fixture
    Check "verify passes on complete fixture" (Invoke-Verify)
    Remove-Item "$fixture\app\win-x64\shared\reviewed-manifests.json"
    Check "verify fails when trust data is missing" (-not (Invoke-Verify))
    Set-Content "$fixture\app\win-x64\shared\reviewed-manifests.json" "{}"
    $m1 = Get-Content (Join-Path $fixture "installer\release-manifest.json") -Raw
    Invoke-Verify | Out-Null
    $m2 = Get-Content (Join-Path $fixture "installer\release-manifest.json") -Raw
    Check "manifest is deterministic" ($m1 -eq $m2)
    $json = $m1 | ConvertFrom-Json
    Check "manifest records versions, arch, hashes" ($json.appVersion -and $json.workerVersion -and $json.architecture -eq "x64" -and ($json.files | Where-Object { $_.sha256.Length -ne 64 }).Count -eq 0)
    Check "manifest records dependency inventory" ($json.workerPackages.Count -eq 2)
    Check "manifest worker version comes from built exe" ($json.workerVersion -eq $pyVersion)
    Check "runtime inventory is comprehensive (all runtime files, hashed)" (($json.appRuntime.path -contains "app/win-x64/System.Runtime.dll") -and ($json.appRuntime.path -contains "app/win-x64/coreclr.dll") -and ($json.appRuntime.path -contains "app/win-x64/sub/native.bin") -and ($json.appRuntime | Where-Object { $_.sha256.Length -ne 64 }).Count -eq 0)
    Check "app/runtime/worker roles separated" (($json.files | Where-Object { $_.path -eq "app/win-x64/DetailFrame.App.exe" }).role -eq "app" -and ($json.files | Where-Object { $_.path -eq "app/win-x64/coreclr.dll" }).role -eq "runtime" -and ($json.files | Where-Object { $_.path -eq "worker/win-x64/detailframe-worker.exe" }).role -eq "worker")
    $hardlinkSource = Join-Path $testRoot "outside-file"
    Set-Content $hardlinkSource "outside"
    $hardlink = Join-Path $fixture "app\win-x64\linked.bin"
    New-Item -ItemType HardLink -Path $hardlink -Target $hardlinkSource | Out-Null
    Check "verify rejects hardlinked payload files" (-not (Invoke-Verify))
    Remove-Item $hardlink, $hardlinkSource
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

    $script:WorkerStubVersion = "0.0.1-stale"
    New-Fixture
    Check "verify fails on stale worker binary (version mismatch)" (-not (Invoke-Verify))
    $script:WorkerStubVersion = $pyVersion

    New-Fixture
    $outW = Join-Path $fixture "..\outside-w"
    New-Item -ItemType Directory -Force $outW | Out-Null
    Remove-Item -Recurse -Force "$fixture\worker\win-x64"
    cmd /c mklink /J "$fixture\worker\win-x64" "$outW" *> $null
    Check "worker root junction created" (Test-Path "$fixture\worker\win-x64")
    Check "verify fails when worker root is a junction" (-not (Invoke-Verify))
    cmd /c rmdir "$fixture\worker\win-x64"

    New-Fixture
    Remove-Item -Recurse -Force "$fixture\app"
    cmd /c mklink /J "$fixture\app" "$outW" *> $null
    Check "verify fails when staging parent (app) is a junction" (-not (Invoke-Verify))
    cmd /c rmdir "$fixture\app"

    $ok = $true
    try { & pwsh -NoProfile -File $verify -RepoRoot $repo -ArtifactsRoot $repo *> $null; $ok = ($LASTEXITCODE -eq 0) } catch { $ok = $false }
    Check "verify rejects artifact root outside repository" (-not $ok)
}
finally {
    if (Test-Path $testRoot) { Remove-Item -Recurse -Force $testRoot }
}

if ($failures.Count -gt 0) { "FAILED: $($failures.Count) check(s)"; exit 1 }
"All release layout checks passed."
