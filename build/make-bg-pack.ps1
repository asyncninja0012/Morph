# Builds the background pack (PK-2, 4.3): BiRefNet-lite 512×512 ONNX (MIT) as model.onnx, plus onnxruntime.dll and its
# licence notices from the Microsoft.ML.OnnxRuntime NuGet package (MIT), the version Morph's managed API is built against.
# Both downloads are pinned by SHA-256. Prints the PackStore.Known line for the release URL.
#   ./build/make-bg-pack.ps1
# Then: GitHub → Releases → Draft a new release, tag pack-bg-model-<version>, upload the ZIP, untick "Set as the latest release".
param(
    [string] $Version = '1',
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
$ortVersion = '1.31.0' # = Microsoft.ML.OnnxRuntime.Managed in Directory.Packages.props
$model = @{
    Url = 'https://huggingface.co/studioludens/birefnet-lite-512/resolve/962e67969ec6acec9c19fab76acee4f5e22e990c/onnx/model.onnx'
    Sha = '1CB0FB360DADD15AF77C639085D77A9DF67DB0C64315560C3DE005F676345AC2'
}
$work = Join-Path $root "artifacts/packs/bg-model-$Version"
$content = Join-Path $work 'x'
$zip = Join-Path $root "artifacts/packs/bg-model-$Version.zip"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $content | Out-Null

if (-not (Get-File $model.Url (Join-Path $content 'model.onnx'))) { throw 'The model is gone from Hugging Face.' }
$sha = (Get-FileHash (Join-Path $content 'model.onnx') -Algorithm SHA256).Hash
if ($sha -ne $model.Sha) { throw "model.onnx SHA-256 is $sha, expected $($model.Sha)." }

$nupkg = Join-Path $work 'ort.zip'
if (-not (Get-File "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/$ortVersion/microsoft.ml.onnxruntime.$ortVersion.nupkg" $nupkg)) { throw "ONNX Runtime $ortVersion not on NuGet." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    foreach ($entry in 'runtimes/win-x64/native/onnxruntime.dll', 'LICENSE', 'ThirdPartyNotices.txt') {
        $item = $archive.Entries | Where-Object { $_.FullName -eq $entry } | Select-Object -First 1
        if (-not $item) { throw "$entry missing from the ONNX Runtime package." }
        $target = Join-Path $content ($(if ($entry -eq 'LICENSE') { 'onnxruntime-LICENSE.txt' } elseif ($entry -like '*.txt') { 'onnxruntime-ThirdPartyNotices.txt' } else { 'onnxruntime.dll' }))
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($item, $target, $true)
    }
} finally {
    $archive.Dispose()
}
$signature = Get-AuthenticodeSignature (Join-Path $content 'onnxruntime.dll')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "onnxruntime.dll isn't signed by Microsoft ($($signature.Status))."
}

@"
Morph background pack $Version
model.onnx: BiRefNet_lite by Peng Zheng et al. (https://github.com/ZhengPeng7/BiRefNet), MIT licence;
512x512 ONNX export https://huggingface.co/studioludens/birefnet-lite-512 (MIT).
onnxruntime.dll: ONNX Runtime $ortVersion (https://github.com/microsoft/onnxruntime), MIT licence; see the two onnxruntime-*.txt files.
"@ | Set-Content (Join-Path $content 'NOTICE.txt') -Encoding utf8

if (Test-Path $zip) { Remove-Item $zip }
[System.IO.Compression.ZipFile]::CreateFromDirectory($content, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$size = (Get-Item $zip).Length
$installed = (Get-ChildItem $content -Recurse -File | Measure-Object Length -Sum).Sum
$zipSha = (Get-FileHash $zip -Algorithm SHA256).Hash
$url = "https://github.com/$Repo/releases/download/pack-bg-model-$Version/bg-model-$Version.zip"
Write-Host "Built $zip ($([math]::Round($size / 1MB)) MB, $([math]::Round($installed / 1MB)) MB unpacked)"
Write-Host 'PackStore.Known entry, once the ZIP is on the release:'
Write-Host "new(`"bg-model`", `"Background pack`", `"$Version`", new(`"$url`"), $size, $installed, `"$zipSha`", new(`"https://github.com/ZhengPeng7/BiRefNet/blob/main/LICENSE`")),"
