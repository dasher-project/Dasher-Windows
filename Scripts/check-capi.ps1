# RFC 0023 item 3: CAPI compatibility gate (called from Directory.Build.props).
# The build FAILS if the pinned DasherCore's dasher.h exposes a
# DASHER_CAPI_VERSION below the requirement.
param(
    [Parameter(Mandatory = $true)][string]$HeaderPath,
    [Parameter(Mandatory = $true)][int]$Required
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $HeaderPath)) {
    Write-Error "CAPI gate: header not found: $HeaderPath"
    exit 1
}
# Read as UTF-8 explicitly - Windows PowerShell 5.1's default encoding can
# misread the UTF-8 header (non-ASCII comments) and break the regex.
$h = [System.IO.File]::ReadAllText($HeaderPath)
$m = [regex]::Match($h, '#define\s+DASHER_CAPI_VERSION\s+(\d+)')
if ($m.Success) {
    $pinned = [int]$m.Groups[1].Value
    if ($pinned -lt $Required) {
        Write-Error "CAPI gate: pinned DasherCore exposes CAPI version $pinned, this frontend requires >= $Required. Bump the DasherCore submodule (see .github/workflows/shared-deps-bump.yml)."
        exit 1
    }
    Write-Host "CAPI gate: pinned DasherCore CAPI version $pinned (required >= $Required) - OK"
} else {
    $head = ($h -split "`n" | Select-Object -First 3) -join ' '
    Write-Error "CAPI gate: DASHER_CAPI_VERSION not found in $HeaderPath (file starts: $head)"
    exit 1
}
