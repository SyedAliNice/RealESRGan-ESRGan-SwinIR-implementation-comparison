[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutFile,
    [string]$Python,
    [string]$FreezeText
)
$ErrorActionPreference = "Stop"

# Editable installs and direct-URL requirements embed local source paths; the manifest lists only
# index-resolved third-party pins so it never carries machine-specific paths.
if (-not $PSBoundParameters.ContainsKey("FreezeText")) {
    $freeze = @(uv pip freeze --exclude-editable --python $Python)
    if ($LASTEXITCODE -ne 0) { throw "dependency manifest failed" }
}
if ($PSBoundParameters.ContainsKey("FreezeText")) { $freeze = $FreezeText -split "\r?\n" }
$kept = @($freeze | Where-Object { $_ -and $_.Trim() -and $_ -notmatch '^\s*-e\s' -and $_ -notmatch '(?i)\sfile:|@\s*file:' })
$leak = @($kept | Where-Object { $_ -match '(?i)[A-Z]:[\\/]|file:|copilot-worktrees' })
if ($leak.Count -gt 0) { throw "dependency manifest still contains machine paths: $($leak -join '; ')" }
$kept | Out-File -Encoding ascii $OutFile
