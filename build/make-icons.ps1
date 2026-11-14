# Renders Morph's logo PNGs and morph.ico from package/Assets/morph-logo.svg (32 px and up) and morph-logo-small.svg
# (16-24 px, tray). Needs Microsoft Edge (renders the SVG) and Windows PowerShell's System.Drawing (scales it down).
# Run after changing either SVG; the outputs are committed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'package\Assets'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found.' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) "morph-icons-$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null

# Edge draws the SVG at 1024 px on a transparent page; every size is scaled down from that.
function Render([string] $svg) {
    $uri = ([Uri](Join-Path $assets $svg)).AbsoluteUri
    $html = Join-Path $tmp "$svg.html"
    $png = Join-Path $tmp "$svg.png"
    Set-Content $html "<html><body style=`"margin:0`"><img src=`"$uri`" style=`"display:block;width:100vw;height:100vh`"></body></html>" -Encoding utf8
    # Start-Process, not &: Edge logs harmless noise to stderr, which Windows PowerShell turns into a terminating error.
    Start-Process $edge -Wait -WindowStyle Hidden -RedirectStandardError (Join-Path $tmp 'edge.log') -ArgumentList @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000', '--window-size=1024,1024',
        "--screenshot=`"$png`"", ([Uri]$html).AbsoluteUri)
    if (-not (Test-Path $png)) { throw "Edge did not render $svg." }
    $bmp = [Drawing.Bitmap]::FromFile($png)
    $copy = New-Object Drawing.Bitmap $bmp
    $bmp.Dispose()
    $copy
}

# Draws $src at $logo px, centred on a transparent $w x $h canvas, and returns the PNG bytes.
function Scale([Drawing.Bitmap] $src, [int] $w, [int] $h, [int] $logo) {
    $dst = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $attr = New-Object Drawing.Imaging.ImageAttributes
    $attr.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $rect = New-Object Drawing.Rectangle ([int](($w - $logo) / 2)), ([int](($h - $logo) / 2)), $logo, $logo
    $g.DrawImage($src, $rect, 0, 0, $src.Width, $src.Height, [Drawing.GraphicsUnit]::Pixel, $attr)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $dst.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    , $ms.ToArray()
}

try {
    $big = Render 'morph-logo.svg'
    $small = Render 'morph-logo-small.svg'

    [IO.File]::WriteAllBytes((Join-Path $assets 'Square44x44Logo.png'), (Scale $big 44 44 44))
    [IO.File]::WriteAllBytes((Join-Path $assets 'StoreLogo.png'), (Scale $big 50 50 50))
    [IO.File]::WriteAllBytes((Join-Path $assets 'Square150x150Logo.png'), (Scale $big 150 150 100))
    [IO.File]::WriteAllBytes((Join-Path $assets 'Wide310x150Logo.png'), (Scale $big 310 150 100))
    [IO.File]::WriteAllBytes((Join-Path $root 'src\Morph.App\tray.png'), (Scale $small 64 64 64))

    # .ico with PNG entries (Windows Vista and later read them): small art up to 24 px, the full logo above.
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $images = foreach ($s in $sizes) { , (Scale $(if ($s -le 24) { $small } else { $big }) $s $s $s) }
    $ico = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ico
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $root 'src\Morph.App\morph.ico'), $ico.ToArray())
    $big.Dispose(); $small.Dispose()
    Write-Host 'Wrote package/Assets/*.png, src/Morph.App/tray.png and src/Morph.App/morph.ico'
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
