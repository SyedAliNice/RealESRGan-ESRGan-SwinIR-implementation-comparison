$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}

# ISCC is not required: these are static checks of the installer script and the app's data paths.
# A real install/upgrade/uninstall run on a Windows machine remains a release gate.
$issPath = Join-Path $repo "installer\DetailFrame.iss"
Check "exists: installer\DetailFrame.iss" (Test-Path $issPath)
if (-not (Test-Path $issPath)) { "FAILED: $($failures.Count)"; exit 1 }
$iss = Get-Content $issPath -Raw
$code = ($iss -split '(?m)^\[Code\]', 2)[1]
$nonCode = ($iss -split '(?m)^\[Code\]', 2)[0]

# Data location: per-user, outside {app}, and matching what the application uses.
$dl = Get-Content (Join-Path $repo "src\DetailFrame.App\Services\ModelDownloadService.cs") -Raw
Check "app model cache is LocalApplicationData\DetailFrame\Models" ($dl -match 'LocalApplicationData\), "DetailFrame", "Models"')
Check "installer data directory is {localappdata}\DetailFrame" ($code -match "UserDataDirName = '\\DetailFrame'" -and $code -match "ExpandConstant\('\{localappdata\}'\)")
Check "installer never targets the install directory for data" ($iss -notmatch '(?i)\{app\}\\(models|settings)')

# Upgrade: no section may delete anything, and files are only written under {app}.
Check "no [InstallDelete] section" ($iss -notmatch '(?mi)^\[InstallDelete\]')
Check "no [UninstallDelete] section" ($iss -notmatch '(?mi)^\[UninstallDelete\]')
$dests = [regex]::Matches($nonCode, '(?m)^Source:.*?DestDir:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
Check "all copied files land under {app}" (($dests | Where-Object { $_ -notmatch '^\{app\}' }).Count -eq 0 -and $dests.Count -ge 2)
$filesSection = [regex]::Match($nonCode, '(?ms)^\[Files\].*?(?=^\[)').Value
Check "payload excludes user-data paths and model weights" ($filesSection -ne "" -and $filesSection -notmatch '(?i)localappdata|\.pth|\.onnx|\.safetensors')
Check "AppId is fixed so upgrades reuse the same install" ($iss -match '(?m)^AppId=\{\{[0-9A-F-]{36}\}')

# Uninstall: deletion only via CurUninstallStepChanged, explicit, default-preserve.
Check "DelTree appears exactly once" (([regex]::Matches($iss, 'DelTree\(')).Count -eq 1)
Check "DelTree targets only the user-data directory" ($code -match 'DelTree\(UserDataDir,')
Check "no other delete calls (DeleteFile/RemoveDir/DelTree elsewhere)" (([regex]::Matches($code, '(?i)\b(DeleteFile|RemoveDir|DelTree)\(')).Count -eq 1)
Check "removal runs only after uninstall (usPostUninstall)" ($code -match 'CurUninstallStepChanged' -and $code -match 'CurUninstallStep <> usPostUninstall')
Check "interactive prompt defaults to No" ($code -match 'MB_YESNO or MB_DEFBUTTON2')
Check "prompt copy names the path and the consequence" (($code -match "UserDataDir \+ #13#10") -and ($code -match 'settings and downloaded models') -and ($code -match 'downloaded again'))
Check "silent uninstall deletes only with /REMOVEUSERDATA" (($code -match 'if UninstallSilent then\s+Remove := RemoveUserDataRequested') -and ($code -match "'/REMOVEUSERDATA'"))
Check "removal is gated by the Remove flag" ($code -match 'if Remove then\s+DelTree')
Check "shared Windows App SDK runtime is not uninstalled" ($iss -notmatch '(?i)WindowsAppRuntime.*(uninstall|/uninstall)' -and $iss -match 'runtime is not removed')

# Behaviour of the data layout itself: fake settings and verified model cache survive a simulated
# app-directory replacement (what an upgrade does to {app}) in a scratch area.
$scratch = Join-Path $repo "artifacts\test-fixture\lifecycle"
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
$app = Join-Path $scratch "ProgramFiles\DetailFrame"
$data = Join-Path $scratch "LocalAppData\DetailFrame"
New-Item -ItemType Directory -Force (Join-Path $app "worker"), (Join-Path $data "Models"), (Join-Path $data "Settings") | Out-Null
Set-Content (Join-Path $app "DetailFrame.App.exe") "v1"
Set-Content (Join-Path $data "Settings\settings.json") '{"theme":"dark"}'
Set-Content (Join-Path $data "Models\fake-model.bin") "fake verified cache"
Remove-Item -Recurse -Force $app
New-Item -ItemType Directory -Force $app | Out-Null
Set-Content (Join-Path $app "DetailFrame.App.exe") "v2"
Check "fixture: app dir replaced while settings and model cache persist" ((Test-Path (Join-Path $data "Settings\settings.json")) -and (Test-Path (Join-Path $data "Models\fake-model.bin")))
Check "fixture: data directory is not inside the app directory" (-not $data.StartsWith($app, [StringComparison]::OrdinalIgnoreCase))
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }

if ($failures.Count) { "FAILED: $($failures.Count)"; exit 1 }
"All checks passed"
