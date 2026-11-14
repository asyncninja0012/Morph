# Writes the winget manifests for a published Morph release (PLAN 5.5) into artifacts/winget/<version>/:
# version, installer and en-US locale files for the per-user Inno Setup installer on the GitHub release.
# Submit them as a pull request to https://github.com/microsoft/winget-pkgs (manifests/a/asyncninja0012/Morph/<version>/),
# for example with `wingetcreate submit artifacts/winget/<version>`. Run after the release is published, so the URL works.
param(
    [Parameter(Mandatory)] [string] $Version,
    # SHA-256 of Morph-<version>-setup.exe; default: read from artifacts/Morph-<version>-setup.exe.sha256 (installer.ps1 writes it).
    [string] $Sha256
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Sha256) {
    $hashFile = Join-Path $root "artifacts/Morph-$Version-setup.exe.sha256"
    if (-not (Test-Path $hashFile)) { throw "No $hashFile; pass -Sha256 (the .sha256 file on the release)." }
    $Sha256 = ((Get-Content $hashFile -Raw).Trim() -split '\s+')[0]
}
if ($Sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "Not a SHA-256: $Sha256" }

$id = 'asyncninja0012.Morph'
$schema = '1.9.0'
$url = "https://github.com/asyncninja0012/Morph/releases/download/v$Version/Morph-$Version-setup.exe"
$out = New-Item -ItemType Directory (Join-Path $root "artifacts/winget/$Version") -Force
function Write-Manifest([string] $Name, [string] $Text) {
    [IO.File]::WriteAllText((Join-Path $out $Name), $Text, (New-Object Text.UTF8Encoding $false)) # UTF-8 without BOM
}

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | ForEach-Object { Write-Manifest "$id.yaml" $_ }

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.22000.0
InstallerType: inno
Scope: user
UpgradeBehavior: install
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $($Sha256.ToUpperInvariant())
ManifestType: installer
ManifestVersion: $schema
"@ | ForEach-Object { Write-Manifest "$id.installer.yaml" $_ }

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Morph
PublisherUrl: https://github.com/asyncninja0012/Morph
PublisherSupportUrl: https://github.com/asyncninja0012/Morph/issues
PackageName: Morph
PackageUrl: https://github.com/asyncninja0012/Morph
License: MIT
LicenseUrl: https://github.com/asyncninja0012/Morph/blob/main/LICENSE
ShortDescription: Drag files, hold a key, and drop on a wheel to convert, edit or act on them. Offline.
Description: Morph opens a radial wheel at the pointer while you drag files on Windows 11. Drop on a wedge to convert images, video, audio, PDF and documents, run a tool (compress, crop, trim, remove background, OCR and more) or act on the files. Results are saved next to the original; everything runs offline.
Tags:
- converter
- file-converter
- image
- video
- pdf
- drag-and-drop
ReleaseNotesUrl: https://github.com/asyncninja0012/Morph/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | ForEach-Object { Write-Manifest "$id.locale.en-US.yaml" $_ }

Write-Host "Wrote $out"
Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }
