# Downloads the native binaries Morph ships (PLAN §8) into artifacts/binaries/, verifying SHA-256 pins.
# Run once after cloning and whenever a pin changes: ./build/fetch-binaries.ps1
#
# shortcut: FFmpeg comes from a BtbN LGPL autobuild, which BtbN deletes after a while. When the URL 404s, re-pin to a
# newer win64-lgpl build. The plan's fix is a custom small LGPL build hosted on Morph's own GitHub release (PLAN §5).
$ErrorActionPreference = 'Stop'

$pins = @(
    @{
        Name   = 'ffmpeg'
        Url    = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-11-03-12-50/ffmpeg-n9.0.2-45-g1e65c6cd5d-win64-lgpl-9.0.zip'
        Sha256 = 'b1bae38cca5d84eb0f004f80cfb712d7bb755ef060e5a34207391228793c4406'
        Files  = @{ 'ffmpeg.exe' = '*/bin/ffmpeg.exe'; 'ffprobe.exe' = '*/bin/ffprobe.exe' }
    },
    @{
        Name   = 'pdfium'
        Url    = 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8125/pdfium-win-x64.tgz'
        Sha256 = '9a964261760cff52570aabf038ab346d3a5034dd8b30d0d5fe8861235d356b8b'
        Files  = @{ 'pdfium.dll' = 'bin/pdfium.dll' }
    }
)

$root = Split-Path $PSScriptRoot -Parent
$downloads = Join-Path $root 'artifacts/downloads'
$binaries = Join-Path $root 'artifacts/binaries'
New-Item -ItemType Directory -Force $downloads, $binaries | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($pin in $pins) {
    $stamp = Join-Path $binaries "$($pin.Name).sha256"
    if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $pin.Sha256 -and
        -not ($pin.Files.Keys | Where-Object { -not (Test-Path (Join-Path $binaries $_)) })) {
        Write-Host "$($pin.Name) is up to date."
        continue
    }

    $archivePath = Join-Path $downloads "$($pin.Name)-$([IO.Path]::GetFileName($pin.Url))"
    if (-not (Test-Path $archivePath) -or (Get-FileHash $archivePath -Algorithm SHA256).Hash -ne $pin.Sha256) {
        Write-Host "Downloading $($pin.Url)"
        $ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower
        Invoke-WebRequest $pin.Url -OutFile $archivePath
    }

    $hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash
    if ($hash -ne $pin.Sha256) {
        Remove-Item $archivePath
        throw "$($pin.Name) SHA-256 mismatch: expected $($pin.Sha256), got $hash"
    }

    if ($archivePath.EndsWith('.zip')) {
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($name in $pin.Files.Keys) {
                $entry = $archive.Entries | Where-Object { $_.FullName -like $pin.Files[$name] } | Select-Object -First 1
                if (-not $entry) { throw "$name not found in $archivePath" }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $binaries $name), $true)
            }
        } finally {
            $archive.Dispose()
        }
    } else {
        # .tgz: Windows' bsdtar (System32\tar.exe) extracts the listed entries to a scratch folder.
        $scratch = Join-Path $downloads $pin.Name
        New-Item -ItemType Directory -Force $scratch | Out-Null
        & "$env:SystemRoot\System32\tar.exe" -xzf $archivePath -C $scratch @($pin.Files.Values)
        if ($LASTEXITCODE -ne 0) { throw "tar failed on $archivePath" }
        foreach ($name in $pin.Files.Keys) {
            Copy-Item (Join-Path $scratch $pin.Files[$name]) (Join-Path $binaries $name) -Force
        }
    }

    Set-Content $stamp $pin.Sha256
    Write-Host "$($pin.Name) extracted to $binaries"
}
