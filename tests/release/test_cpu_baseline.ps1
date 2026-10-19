$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}
$iss = Get-Content (Join-Path $repo "installer\DetailFrame.iss") -Raw
$build = Get-Content (Join-Path $repo "installer\build.ps1") -Raw
$lock = Get-Content (Join-Path $repo "worker\uv.lock") -Raw
$config = Get-Content (Join-Path $repo "src\DetailFrame.App\Views\ConfigurePage.xaml.cs") -Raw
$download = Get-Content (Join-Path $repo "src\DetailFrame.App\Services\ModelDownloadService.cs") -Raw

Check "installer includes the WinUI app" ($iss -match 'artifacts\\app\\win-x64')
Check "installer includes the packaged inference worker" ($iss -match 'artifacts\\worker\\win-x64')
Check "worker dependency is the CPU-only PyTorch build" ($lock -match 'torch.*2\.9\.1\+cpu|2\.9\.1\+cpu')
Check "missing weights are reported as a download requirement" ($config -match 'needs to be downloaded.*before use')
Check "download installation verifies the expected digest" ($download -match 'HashMismatch|expected checksum')
Check "installer baseline has no CUDA payload or component" ($iss -notmatch '(?i)cuda')
Check "release build accepts a Release configuration" ($build -match '\$Configuration' -and $build -match 'Release')
Check "release build compiles DetailFrame.iss with ISCC" ($build -match '(?i)ISCC\.exe' -and $build -match 'DetailFrame\.iss')
Check "release build requires a compiled installer in final artifact verification" ($build -match 'RequireInstaller')

if ($failures.Count) { "FAILED: $($failures.Count)"; exit 1 }
"CPU baseline static checks passed."
