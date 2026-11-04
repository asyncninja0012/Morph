# Downloads the native binaries Morph ships (PLAN §8) into artifacts/binaries/, verifying SHA-256 pins.
# Run once after cloning and whenever a pin changes: ./build/fetch-binaries.ps1
#
# shortcut: FFmpeg comes from a BtbN LGPL autobuild, which BtbN deletes after a while. When the URL 404s, re-pin to a
# newer win64-lgpl build. The plan's fix is a custom small LGPL build hosted on Morph's own GitHub release (PLAN §5).
$ErrorActionPreference = 'Stop'

$ffmpeg = @{
    Url    = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-11-03-12-50/ffmpeg-n9.0.2-45-g1e65c6cd5d-win64-lgpl-9.0.zip'
    Sha256 = 'b1bae38cca5d84eb0f004f80cfb712d7bb755ef060e5a34207391228793c4406'
    Files  = @('ffmpeg.exe', 'ffprobe.exe')
}

$root = Split-Path $PSScriptRoot -Parent
$downloads = Join-Path $root 'artifacts/downloads'
$binaries = Join-Path $root 'artifacts/binaries'
New-Item -ItemType Directory -Force $downloads, $binaries | Out-Null

$stamp = Join-Path $binaries 'ffmpeg.sha256'
if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $ffmpeg.Sha256 -and
    -not ($ffmpeg.Files | Where-Object { -not (Test-Path (Join-Path $binaries $_)) })) {
    Write-Host 'FFmpeg is up to date.'
    return
}

$zip = Join-Path $downloads ([IO.Path]::GetFileName($ffmpeg.Url))
if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $ffmpeg.Sha256) {
    Write-Host "Downloading $($ffmpeg.Url)"
    $ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower
    Invoke-WebRequest $ffmpeg.Url -OutFile $zip
}

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($hash -ne $ffmpeg.Sha256) {
    Remove-Item $zip
    throw "FFmpeg SHA-256 mismatch: expected $($ffmpeg.Sha256), got $hash"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in $ffmpeg.Files) {
        $entry = $archive.Entries | Where-Object { $_.FullName -like "*/bin/$name" } | Select-Object -First 1
        if (-not $entry) { throw "$name not found in $zip" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $binaries $name), $true)
    }
} finally {
    $archive.Dispose()
}

Set-Content $stamp $ffmpeg.Sha256
Write-Host "FFmpeg extracted to $binaries"
