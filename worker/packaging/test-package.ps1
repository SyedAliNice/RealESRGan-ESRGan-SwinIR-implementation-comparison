$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dist = Join-Path $repoRoot "artifacts\worker\win-x64"
$exe = Join-Path $dist "detailframe-worker.exe"
$initFile = Join-Path $PSScriptRoot "..\src\detailframe_worker\__init__.py"
$expectedVersion = (Select-String -Path $initFile -Pattern '__version__ = "([^"]+)"').Matches[0].Groups[1].Value

function Assert-That($condition, $message) {
    if (-not $condition) { throw "FAIL: $message" }
}

function New-WorkerProcess($arguments) {
    # Minimal environment: no Python on PATH, no PYTHON* variables.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment.Clear()
    $psi.Environment["PATH"] = "$env:SystemRoot\System32;$env:SystemRoot"
    $psi.Environment["SystemRoot"] = $env:SystemRoot
    $psi.Environment["TEMP"] = $env:TEMP
    $psi.Environment["TMP"] = $env:TEMP
    return [System.Diagnostics.Process]::Start($psi)
}

Assert-That (Test-Path $exe) "packaged worker not found at $exe"
Assert-That (Test-Path (Join-Path $dist "dependency-manifest.txt")) "dependency manifest missing"

$process = New-WorkerProcess "--version"
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(120000)) { $process.Kill(); $process.WaitForExit(); throw "FAIL: worker --version timed out" }
$versionOutput = $stdoutTask.Result.Trim()
Assert-That ($process.ExitCode -eq 0) "--version exited with $($process.ExitCode)"
Assert-That ($versionOutput -eq "detailframe-worker $expectedVersion") "unexpected --version output: '$versionOutput'"

$modelsDir = Join-Path $env:TEMP ("detailframe-pkg-test-" + [guid]::NewGuid())
New-Item -ItemType Directory $modelsDir | Out-Null
try {
    Copy-Item (Join-Path $repoRoot "shared\models.json") (Join-Path $modelsDir "models.json")
    $process = New-WorkerProcess "--protocol-stdio --models-dir `"$modelsDir`""
    $requestId = [guid]::NewGuid().ToString()
    $request = @{ protocol_version = 1; type = "capabilities"; request_id = $requestId } | ConvertTo-Json -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Close()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) { $process.Kill(); $process.WaitForExit(); throw "FAIL: worker did not exit" }
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    Assert-That ($process.ExitCode -eq 0) "worker exited with $($process.ExitCode): $stderr"

    $lines = @($stdout -split "`r?`n" | Where-Object { $_.Trim() })
    Assert-That ($lines.Count -ge 1) "no response received. stderr: $stderr"
    $response = $lines[0] | ConvertFrom-Json
    Assert-That ($response.protocol_version -eq 1) "protocol_version is not 1"
    Assert-That ($response.type -eq "capabilities_result") "unexpected type '$($response.type)'"
    Assert-That ($response.request_id -eq $requestId) "request_id mismatch"
    Assert-That ($response.worker_version -eq $expectedVersion) "worker_version mismatch"
    Assert-That ($null -ne $response.models) "models missing"
    Assert-That (@($response.devices) -contains "cpu") "cpu device not advertised"
}
finally {
    Remove-Item -Recurse -Force $modelsDir -ErrorAction SilentlyContinue
}

"PASS: packaged worker $expectedVersion responds to --version and capabilities"
