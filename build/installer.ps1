# Builds artifacts/Morph-<version>-setup.exe (PLAN D12, 4.6): self-contained ReadyToRun publish of the App and Worker into one
# folder (ffmpeg, pdfium and the licence notices come with it), then Inno Setup 6 (ISCC from the Tools.InnoSetup NuGet package,
# no install needed) with build/installer.iss. Unsigned for now (PLAN §12 question 8).
#   ./build/installer.ps1                    # version from Directory.Build.props
#   ./build/installer.ps1 -Version 0.9.1     # what the release workflow passes from the v* tag
param(
    [string] $Configuration = 'Release',
    [string] $Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$app = Join-Path $artifacts 'app'

function Invoke-Native {
    & $args[0] @($args | Select-Object -Skip 1)
    if ($LASTEXITCODE -ne 0) { throw "$($args[0]) failed with exit code $LASTEXITCODE" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'." }

foreach ($binary in 'ffmpeg.exe', 'pdfium.dll') {
    if (-not (Test-Path (Join-Path $artifacts "binaries/$binary"))) {
        throw "$binary missing: run ./build/fetch-binaries.ps1 first."
    }
}

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
foreach ($project in 'src/Morph.App/Morph.App.csproj', 'src/Morph.Worker/Morph.Worker.csproj') {
    Invoke-Native dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:DebugType=none "-p:Version=$Version" -o $app
}

Invoke-Native dotnet restore (Join-Path $PSScriptRoot 'tools/BuildTools.csproj') --verbosity quiet
$packages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$iscc = Get-ChildItem (Join-Path $packages 'tools.innosetup') -Recurse -Filter ISCC.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $iscc) { throw 'ISCC.exe not found in the Tools.InnoSetup package.' }

Invoke-Native $iscc /Q "/DVersion=$Version" "/DSource=$app" "/DOutput=$artifacts" "/DRoot=$root" (Join-Path $PSScriptRoot 'installer.iss')
$setup = Join-Path $artifacts "Morph-$Version-setup.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Morph-$Version-setup.exe" | Set-Content "$setup.sha256" -Encoding ascii
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB, unsigned), SHA-256 $hash"
