# Builds the office pack (PK-1): official LibreOffice for Windows x64, unpacked from its MSI without installing
# (msiexec /a), zipped so that program\soffice.exe sits at the top. Prints the PackStore.Known line for the release URL.
#   ./build/make-office-pack.ps1 -Version 26.8.2
# Then: GitHub → Releases → Draft a new release, tag pack-office-<version>, upload the ZIP, untick "Set as the latest release".
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Repo = 'asyncninja0012/Morph'
)
$ErrorActionPreference = 'Stop'
# curl.exe ships with Windows; it resumes a dropped download (-C -), which Hugging Face's CDN does now and then.
function Get-File([string] $Url, [string] $OutFile) {
    $ErrorActionPreference = 'Continue' # Windows PowerShell turns curl's stderr into a stopping error when output is redirected
    for ($try = 1; $try -le 6; $try++) {
        curl.exe -fsSL --retry 3 -C - -o $OutFile $Url
        if ($LASTEXITCODE -eq 0) { return $true }
        if ($LASTEXITCODE -eq 22) { return $false } # HTTP error (404): not there
    }
    throw "Download of $Url kept failing."
}
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root "artifacts/packs/office-$Version"
$unpacked = Join-Path $work 'x'
$zip = Join-Path $root "artifacts/packs/office-$Version.zip"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

$name = "LibreOffice_${Version}_Win_x86-64.msi"
$msi = Join-Path $work $name
$found = $false
foreach ($base in "https://download.documentfoundation.org/libreoffice/stable/$Version", "https://downloadarchive.documentfoundation.org/libreoffice/old/$Version") {
    if (Get-File "$base/win/x86_64/$name" $msi) {
        $found = $true
        break
    }

    Write-Host "Not at $base"
}
if (-not $found) { throw "LibreOffice $Version not found on the Document Foundation's servers." }

$signature = Get-AuthenticodeSignature $msi
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=The Document Foundation') {
    throw "The MSI isn't signed by The Document Foundation ($($signature.Status), $($signature.SignerCertificate.Subject))."
}

$exit = (Start-Process msiexec.exe -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$unpacked`"" -Wait -PassThru).ExitCode
if ($exit -ne 0) { throw "msiexec /a failed with exit code $exit." }
Get-ChildItem $unpacked -Filter *.msi | Remove-Item # the administrative install keeps a copy of the MSI
$soffice = Join-Path $unpacked 'program/soffice.exe'
if (-not (Test-Path $soffice)) { throw 'program\soffice.exe missing after unpacking.' }

if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($unpacked, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$size = (Get-Item $zip).Length
$installed = (Get-ChildItem $unpacked -Recurse -File | Measure-Object Length -Sum).Sum
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
$url = "https://github.com/$Repo/releases/download/pack-office-$Version/office-$Version.zip"
Write-Host "Built $zip ($([math]::Round($size / 1MB)) MB, $([math]::Round($installed / 1MB)) MB unpacked)"
Write-Host 'PackStore.Known entry, once the ZIP is on the release:'
Write-Host "new(`"office`", `"Office pack (LibreOffice)`", `"$Version`", new(`"$url`"), $size, $installed, `"$sha`", new(`"https://www.libreoffice.org/about-us/licenses/`")),"
