$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}

$scriptPath = Join-Path $repo "installer\prerequisites.ps1"
Check "exists: installer\prerequisites.ps1" (Test-Path $scriptPath)
Check "exists: installer\DetailFrame.iss" (Test-Path (Join-Path $repo "installer\DetailFrame.iss"))
if (-not (Test-Path $scriptPath)) { "FAILED: $($failures.Count)"; exit 1 }
. $scriptPath

$scratch = Join-Path $repo "artifacts\test-fixture\prereq"
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
New-Item -ItemType Directory -Force $scratch | Out-Null
$fake = Join-Path $scratch "WindowsAppRuntimeInstall-x64.exe"
Set-Content $fake "not a real installer"
$fakeHash = (Get-FileHash $fake -Algorithm SHA256).Hash.ToLowerInvariant()

$present = { [pscustomobject]@{ Version = "2.5.1.0" } }
$absent = { @() }
$old = { [pscustomobject]@{ Version = "2.4.0.0" } }

$r = Invoke-PrerequisiteCheck -OsBuild 19045 -Architecture AMD64 -RuntimeProbe $present
Check "Windows 10 is rejected with a clear message" ((-not $r.Ok) -and $r.Code -eq 10 -and $r.Message -match "Windows 11")
$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture ARM64 -RuntimeProbe $present
Check "ARM64 is rejected with a clear message" ((-not $r.Ok) -and $r.Code -eq 11 -and $r.Message -match "x64")
$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture x86 -RuntimeProbe $present
Check "x86 is rejected" ((-not $r.Ok) -and $r.Code -eq 11)

$script:ran = 0
$runner = { param($p) $script:ran++; 0 }
$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $present -InstallerRunner $runner
Check "supported system with runtime proceeds without installing" ($r.Ok -and $r.Code -eq 0 -and $script:ran -eq 0 -and -not $r.RuntimeInstalled)

$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $absent -ProbeOnly -InstallerRunner $runner
Check "probe-only reports missing runtime without installing" ($r.Ok -and $r.Code -eq 20 -and $script:ran -eq 0)

# The runtime appears only after the (mock) installer ran.
$script:installed = $false
$probe = { if ($script:installed) { [pscustomobject]@{ Version = "2.5.1.0" } } else { @() } }
$okRunner = { param($p) $script:ran++; $script:installed = $true; 0 }
$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $probe -RuntimeInstallerPath $fake -ExpectedSha256 $fakeHash -InstallerRunner $okRunner
Check "absent runtime invokes the bootstrap installer and succeeds" ($r.Ok -and $script:ran -eq 1 -and $r.RuntimeInstalled)

$script:ran = 0
$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $old -RuntimeInstallerPath $fake -ExpectedSha256 $fakeHash -InstallerRunner { param($p) $script:ran++; 5 }
Check "outdated runtime is treated as absent; installer exit code failure is explicit" ((-not $r.Ok) -and $script:ran -eq 1 -and $r.Code -eq 12 -and $r.Message -match "exited with code 5")

$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $absent -RuntimeInstallerPath $fake -ExpectedSha256 $fakeHash -InstallerRunner { param($p) 0 }
Check "installer success without detectable runtime is a failure" ((-not $r.Ok) -and $r.Code -eq 12 -and $r.Message -match "not detected")

$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $absent -RuntimeInstallerPath $fake -ExpectedSha256 $fakeHash -InstallerRunner { param($p) throw "boom" }
Check "installer exception is reported" ((-not $r.Ok) -and $r.Code -eq 12 -and $r.Message -match "boom")

$script:ran = 0
$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $absent -RuntimeInstallerPath $fake -ExpectedSha256 ("0" * 64) -InstallerRunner { param($p) $script:ran++; 0 }
Check "hash mismatch blocks execution of the installer" ((-not $r.Ok) -and $script:ran -eq 0 -and $r.Message -match "integrity")

$r = Invoke-PrerequisiteCheck -OsBuild 26100 -Architecture AMD64 -RuntimeProbe $absent -RuntimeInstallerPath (Join-Path $scratch "missing.exe") -InstallerRunner { param($p) 0 }
Check "missing bundled installer fails explicitly" ((-not $r.Ok) -and $r.Code -eq 12 -and $r.Message -match "not found")

Check "pinned hash is a 64-char SHA-256" ($script:RuntimeInstallerSha256 -match '^[0-9a-f]{64}$')
Check "runtime requirement matches the Windows App SDK package version" ((Get-Content (Join-Path $repo "src\DetailFrame.App\DetailFrame.App.csproj") -Raw) -match 'Microsoft\.WindowsAppSDK" Version="2\.5\.')

# Static checks of the Inno Setup script (ISCC is not required for these).
$iss = Get-Content (Join-Path $repo "installer\DetailFrame.iss") -Raw
Check "iss: installs to the selected program directory" ($iss -match 'DefaultDirName=\{autopf\}\\DetailFrame')
Check "iss: output is DetailFrame-Setup.exe" ($iss -match 'OutputBaseFilename=DetailFrame-Setup')
Check "iss: shortcuts are optional tasks" (($iss -match 'Name: "startmenuicon"') -and ($iss -match 'Name: "desktopicon"') -and ($iss -match 'Tasks: startmenuicon') -and ($iss -match 'Tasks: desktopicon'))
Check "iss: uses the DetailFrame icon" (($iss -match 'SetupIconFile=.*Branding\\app\.ico') -and ($iss -match 'UninstallDisplayIcon='))
Check "iss: x64 only" (($iss -match 'ArchitecturesAllowed=x64compatible') -and ($iss -match 'MinVersion=10\.0\.22000'))
Check "iss: no CUDA payload" ($iss -notmatch '(?i)cuda')
Check "iss: nothing written to the install directory for settings or models" ($iss -notmatch '(?i)\{app\}\\(models|settings)')
Check "iss: files come only from app and worker artifacts" ((([regex]::Matches($iss, '(?m)^Source:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) | Where-Object { $_ -notmatch 'artifacts\\(app\\win-x64|worker\\win-x64|installer\\prereq)\\|installer\\prerequisites\.ps1' }).Count -eq 0)
$ps = Get-Content $scriptPath -Raw
Check "iss: exit codes match prerequisites.ps1" (($iss -match 'Code = 20') -and ($ps -match 'ExitRuntimeMissing = 20') -and ($iss -match '10: Result') -and ($ps -match 'ExitUnsupportedOs = 10') -and ($iss -match '11: Result') -and ($iss -match '12: Result'))

if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
if ($failures.Count) { "FAILED: $($failures.Count)"; exit 1 }
"All checks passed"
