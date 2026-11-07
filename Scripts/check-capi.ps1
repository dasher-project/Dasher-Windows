# RFC 0023 item 3: CAPI compatibility gate (called from Directory.Build.props).
# The build FAILS if the pinned DasherCore's dasher.h exposes a
# DASHER_CAPI_VERSION below the requirement. Kept as a script file —
# embedding PowerShell in an MSBuild <Exec> breaks on newlines, and reading
# C headers into MSBuild properties mangles their semicolons/quotes.
param(
    [Parameter(Mandatory = $true)][string]$HeaderPath,
    [Parameter(Mandatory = $true)][int]$Required
)
$ErrorActionPreference = 'Stop'
$h = Get-Content -Raw -LiteralPath $HeaderPath
if ($h -match '#define\s+DASHER_CAPI_VERSION\s+(\d+)') {
    $pinned = [int]$Matches[1]
    if ($pinned -lt $Required) {
        Write-Error "CAPI gate: pinned DasherCore exposes CAPI version $pinned, this frontend requires >= $Required. Bump the DasherCore submodule (see .github/workflows/shared-deps-bump.yml)."
        exit 1
    }
    Write-Host "CAPI gate: pinned DasherCore CAPI version $pinned (required >= $Required) — OK"
} else {
    Write-Error "CAPI gate: DASHER_CAPI_VERSION not found in $HeaderPath"
    exit 1
}
