$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { "PASS  $Name" } else { "FAIL  $Name"; $failures.Add($Name) }
}
$iss = Get-Content (Join-Path $repo "installer\DetailFrame.iss") -Raw
$build = Get-Content (Join-Path $repo "installer\build.ps1") -Raw
$lock = Get-Content (Join-Path $repo "worker\uv.lock") -Raw
$readme = Get-Content (Join-Path $repo "installer\README.md") -Raw

Check "no CUDA component is offered without validation evidence" ($iss -notmatch '(?im)^\s*Name:\s*"?cuda')
Check "installer has no CUDA payload source" ($iss -notmatch '(?i)cuda|cudnn|nvidia')
Check "CPU build does not accept an unvalidated CUDA package" ($build -notmatch '(?i)IncludeCuda|CudaPackage|cuda\\')
Check "worker dependency lock uses CPU-only torch" ($lock -match '2\.9\.1\+cpu')
Check "release docs state CUDA is currently not offered" ($readme -match '(?i)CUDA.{0,100}(not offered|not included|not currently)')
Check "CUDA requires documented runtime matrix and per-model validation" (($readme -match '(?i)driver/runtime matrix') -and ($readme -match '(?i)per-model CUDA validation'))
Check "CUDA omission is explicitly part of baseline release" ($readme -match '(?i)CPU-only baseline')

if ($failures.Count) { "FAILED: $($failures.Count)"; exit 1 }
"Optional CUDA static checks passed."
