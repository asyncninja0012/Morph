# Builds artifacts/Morph_x64.msix (PLAN §8): self-contained ReadyToRun publish of the App and Worker into one layout,
# plus ffmpeg, assets, notices and the manifest, packed with makeappx and signed with the dev cert (build/dev-cert.ps1).
#   ./build/pack.ps1 -Configuration Release          # signed, installable locally
#   ./build/pack.ps1 -Configuration Release -NoSign  # CI and Store upload (the Store signs)
param(
    [string] $Configuration = 'Release',
    [string] $Publisher = 'CN=Morph Dev',
    # shortcut: default version is 0.1.<day of year>.<minute of day> so each local pack upgrades the last one; it goes
    # backwards on 1 January. Replace with a real version scheme before the first Store flight.
    [string] $Version = "0.1.$((Get-Date).DayOfYear).$([int](Get-Date).TimeOfDay.TotalMinutes)",
    [switch] $NoSign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$layout = Join-Path $artifacts 'layout'
$msix = Join-Path $artifacts 'Morph_x64.msix'

function Invoke-Native {
    & $args[0] @($args | Select-Object -Skip 1)
    if ($LASTEXITCODE -ne 0) { throw "$($args[0]) failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $artifacts 'binaries/ffmpeg.exe'))) {
    throw 'ffmpeg.exe missing: run ./build/fetch-binaries.ps1 first.'
}

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
foreach ($project in 'src/Morph.App/Morph.App.csproj', 'src/Morph.Worker/Morph.Worker.csproj') {
    Invoke-Native dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:DebugType=none -o $layout
}

Copy-Item (Join-Path $root 'package/Assets') (Join-Path $layout 'Assets') -Recurse
Copy-Item (Join-Path $root 'package/THIRD-PARTY-NOTICES.txt') $layout
(Get-Content (Join-Path $root 'package/AppxManifest.xml') -Raw).
    Replace('$PUBLISHER$', $Publisher).Replace('$VERSION$', $Version) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

# makeappx/signtool come from the Microsoft.Windows.SDK.BuildTools NuGet package (build-time only, never shipped).
Invoke-Native dotnet restore (Join-Path $PSScriptRoot 'tools/BuildTools.csproj') --verbosity quiet
$packages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$tools = Get-ChildItem (Join-Path $packages 'microsoft.windows.sdk.buildtools') -Recurse -Filter makeappx.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1 | ForEach-Object DirectoryName
if (-not $tools) { throw 'makeappx.exe not found in the Microsoft.Windows.SDK.BuildTools package.' }

Invoke-Native (Join-Path $tools 'makeappx.exe') pack /o /h SHA256 /d $layout /p $msix

if (-not $NoSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) { throw "No signing cert for ${Publisher}: run ./build/dev-cert.ps1 first (or pass -NoSign)." }
    Invoke-Native (Join-Path $tools 'signtool.exe') sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
}

Write-Host "Packed $msix (version $Version$(if ($NoSign) { ', unsigned' }))"
