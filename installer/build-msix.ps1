# Builds a self-signed MSIX package from a self-contained publish folder.
# Usage: ./build-msix.ps1 -AppSource ../../publish/app -Version 0.1.0.0 -OutputDir ../../publish
param(
    [Parameter(Mandatory)][string]$AppSource,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDir = "../publish"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$msixDir = Join-Path $scriptDir "msix"
$staging = Join-Path $OutputDir "msix-staging"

# ── Resolve Windows SDK + MSIX tooling ────────────────────────────────────────
$sdkBase = "C:\Program Files (x86)\Windows Kits\10\bin"
$sdkVer = (Get-ChildItem "$sdkBase" -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object Name -Descending | Select-Object -First 1).Name
if (-not $sdkVer) { throw "Windows SDK not found" }
Write-Host "Using Windows SDK: $sdkVer"

$makeAppx = "$sdkBase\$sdkVer\x64\MakeAppx.exe"
$signTool = "$sdkBase\$sdkVer\x64\signtool.exe"
if (-not (Test-Path $makeAppx)) { throw "MakeAppx.exe not found at $makeAppx" }
if (-not (Test-Path $signTool)) { throw "signtool.exe not found at $signTool" }

# ── Stage package contents ────────────────────────────────────────────────────
Write-Host "Staging MSIX package contents..."
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Copy published app
Copy-Item -Recurse "$AppSource\*" $staging

# Copy manifest (update version)
$manifest = Get-Content (Join-Path $msixDir "Package.appxmanifest") -Raw
$manifest = $manifest -replace 'Version="0\.1\.0\.0"', "Version=""$Version"""
$manifest | Set-Content (Join-Path $staging "AppxManifest.xml")

# Copy assets
$assetsDest = Join-Path $staging "Assets"
New-Item -ItemType Directory -Path $assetsDest -Force | Out-Null
Copy-Item (Join-Path $msixDir "Assets\*.png") $assetsDest

# ── Generate self-signed certificate ──────────────────────────────────────────
Write-Host "Generating self-signed certificate..."
$certSubject = "CN=Dasher Project Testing"
$certPath = Join-Path $OutputDir "DasherTesting.pfx"
$cerPath = Join-Path $OutputDir "DasherTesting.cer"
$certPassword = "DasherTest123!"

# Delete existing cert from store if present
Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq $certSubject } | Remove-Item -ErrorAction SilentlyContinue

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $certSubject `
    -KeyUsage DigitalSignature `
    -FriendlyName "Dasher Testing" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")

# Export PFX and CER
$certPfxBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $certPassword)
[System.IO.File]::WriteAllBytes($certPath, $certPfxBytes)
$certCerBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes($cerPath, $certCerBytes)

# ── Build MSIX ────────────────────────────────────────────────────────────────
Write-Host "Building MSIX package..."
$msixPath = Join-Path $OutputDir "Dasher-Windows.msix"
& $makeAppx pack /d $staging /p $msixPath /o /v
if ($LASTEXITCODE -ne 0) { throw "MakeAppx pack failed with exit code $LASTEXITCODE" }

# ── Sign MSIX ─────────────────────────────────────────────────────────────────
Write-Host "Signing MSIX package..."
& $signTool sign /fd SHA256 /a /f $certPath /p $certPassword $msixPath
if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE" }

# Clean cert from store
Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq $certSubject } | Remove-Item -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "SUCCESS" -ForegroundColor Green
Write-Host "  MSIX:  $msixPath"
Write-Host "  Cert:  $cerPath (testers must install this first)"
Get-ChildItem $msixPath, $cerPath | Format-Table Name, Length
