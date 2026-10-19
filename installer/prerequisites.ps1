[CmdletBinding()]
param(
    [ValidateSet("Check", "Download")]
    [string]$Mode = "Check",
    # Check mode: report whether the Windows App SDK runtime is missing without installing it.
    [switch]$ProbeOnly,
    [string]$RuntimeInstaller,
    [string]$DestinationDir
)
$ErrorActionPreference = "Stop"

# Windows App SDK runtime required by Microsoft.WindowsAppSDK 2.5.1 (see DetailFrame.App.csproj).
# The bootstrapper below was downloaded from the aka.ms URL, hashed, and confirmed to carry a valid
# Microsoft Authenticode signature (installer reports version 2.5.1).
$script:RuntimePackageName = "Microsoft.WindowsAppRuntime.2"
$script:RuntimeMinVersion = [version]"2.5.1.0"
$script:RuntimeInstallerUrl = "https://aka.ms/windowsappsdk/2.5/2.5.1/windowsappruntimeinstall-x64.exe"
$script:RuntimeInstallerSha256 = "931a421e8dc3e6e67724806cb67fecdbb88dfe323f0170842eb4a4b4b149f1e2"
$script:RuntimeInstallerFileName = "WindowsAppRuntimeInstall-x64.exe"
$script:MinWindowsBuild = 22000   # Windows 11

# Exit codes consumed by DetailFrame.iss.
$script:ExitOk = 0
$script:ExitUnsupportedOs = 10
$script:ExitUnsupportedArchitecture = 11
$script:ExitRuntimeInstallFailed = 12
$script:ExitRuntimeMissing = 20

function New-PrereqResult([bool]$Ok, [int]$Code, [string]$Message, [bool]$RuntimeInstalled = $false) {
    [pscustomobject]@{ Ok = $Ok; Code = $Code; Message = $Message; RuntimeInstalled = $RuntimeInstalled }
}

function Test-RuntimePresent([scriptblock]$RuntimeProbe) {
    foreach ($pkg in @(& $RuntimeProbe)) {
        if ($pkg -and ([version]$pkg.Version) -ge $script:RuntimeMinVersion -and ([version]$pkg.Version).Major -eq $script:RuntimeMinVersion.Major) { return $true }
    }
    return $false
}

# Every provider is injectable so the checks can be tested without touching the host.
function Invoke-PrerequisiteCheck {
    param(
        [Parameter(Mandatory)][int]$OsBuild,
        [Parameter(Mandatory)][string]$Architecture,
        [Parameter(Mandatory)][scriptblock]$RuntimeProbe,
        [string]$RuntimeInstallerPath,
        [scriptblock]$InstallerRunner = { param($Path) (Start-Process -FilePath $Path -ArgumentList "--quiet" -Wait -PassThru).ExitCode },
        [string]$ExpectedSha256 = $script:RuntimeInstallerSha256,
        [switch]$ProbeOnly
    )
    if ($OsBuild -lt $script:MinWindowsBuild) {
        return New-PrereqResult $false $script:ExitUnsupportedOs "DetailFrame requires Windows 11 (build $($script:MinWindowsBuild) or later). This system is build $OsBuild."
    }
    if ($Architecture -notin @("AMD64", "x64")) {
        return New-PrereqResult $false $script:ExitUnsupportedArchitecture "DetailFrame requires a 64-bit x64 version of Windows. This system reports '$Architecture'."
    }
    if (Test-RuntimePresent $RuntimeProbe) {
        return New-PrereqResult $true $script:ExitOk "Windows App SDK runtime $($script:RuntimeMinVersion) or later is installed."
    }
    if ($ProbeOnly) {
        return New-PrereqResult $true $script:ExitRuntimeMissing "Windows App SDK runtime $($script:RuntimeMinVersion) is not installed."
    }

    $fail = { param($m) New-PrereqResult $false $script:ExitRuntimeInstallFailed "Windows App SDK runtime $($script:RuntimeMinVersion) is required and could not be installed: $m" }
    if (-not $RuntimeInstallerPath -or -not (Test-Path -LiteralPath $RuntimeInstallerPath -PathType Leaf)) {
        return & $fail "the bundled runtime installer was not found."
    }
    $actual = (Get-FileHash -LiteralPath $RuntimeInstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        return & $fail "the runtime installer failed its integrity check (SHA-256 mismatch)."
    }
    try { $exit = & $InstallerRunner $RuntimeInstallerPath }
    catch { return & $fail $_.Exception.Message }
    if ($exit -ne 0) { return & $fail "the runtime installer exited with code $exit." }
    if (-not (Test-RuntimePresent $RuntimeProbe)) {
        return & $fail "the runtime was not detected after installation."
    }
    return New-PrereqResult $true $script:ExitOk "Windows App SDK runtime $($script:RuntimeMinVersion) installed." $true
}

function Get-HostOsBuild {
    [int](Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion").CurrentBuildNumber
}
function Get-HostArchitecture {
    if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
}
function Get-HostRuntimePackages {
    $all = $false
    try { $all = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch {}
    if ($all) { Get-AppxPackage -AllUsers -Name $script:RuntimePackageName } else { Get-AppxPackage -Name $script:RuntimePackageName }
}

# Build-time helper: fetch the official bootstrapper and refuse it unless hash and signature match.
function Save-RuntimeInstaller([string]$Directory) {
    New-Item -ItemType Directory -Force $Directory | Out-Null
    $target = Join-Path $Directory $script:RuntimeInstallerFileName
    Invoke-WebRequest -Uri $script:RuntimeInstallerUrl -OutFile $target -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $script:RuntimeInstallerSha256) {
        Remove-Item -LiteralPath $target -Force
        throw "Downloaded runtime installer hash $actual does not match pinned $($script:RuntimeInstallerSha256)."
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $target
    if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notlike "*O=Microsoft Corporation*") {
        Remove-Item -LiteralPath $target -Force
        throw "Runtime installer signature is not a valid Microsoft signature ($($sig.Status))."
    }
    return $target
}

if ($MyInvocation.InvocationName -ne ".") {
    if ($Mode -eq "Download") {
        if (-not $DestinationDir) { throw "-DestinationDir is required for Download mode." }
        Save-RuntimeInstaller $DestinationDir
        return
    }
    $r = Invoke-PrerequisiteCheck -OsBuild (Get-HostOsBuild) -Architecture (Get-HostArchitecture) `
        -RuntimeProbe { Get-HostRuntimePackages } -RuntimeInstallerPath $RuntimeInstaller -ProbeOnly:$ProbeOnly
    Write-Output $r.Message
    exit $r.Code
}
