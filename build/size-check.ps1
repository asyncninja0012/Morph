# Reports Morph's download and installed size and fails over budget (PRD §7, PLAN §8): 120 MB download, 300 MB installed.
# Run after build/pack.ps1. Download = the .msix (compressed); installed = the expanded package layout.
param(
    [double] $MaxDownloadMB = 120,
    [double] $MaxInstalledMB = 300
)
$ErrorActionPreference = 'Stop'
$artifacts = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'
$msix = Join-Path $artifacts 'Morph_x64.msix'
$layout = Join-Path $artifacts 'layout'
if (-not (Test-Path $msix) -or -not (Test-Path $layout)) { throw 'Run ./build/pack.ps1 first.' }

$mb = 1MB
$download = (Get-Item $msix).Length / $mb
$files = Get-ChildItem $layout -Recurse -File
$installed = ($files | Measure-Object Length -Sum).Sum / $mb

Write-Host ('Download:  {0,7:N1} MB (budget {1} MB)' -f $download, $MaxDownloadMB)
Write-Host ('Installed: {0,7:N1} MB (budget {1} MB)' -f $installed, $MaxInstalledMB)
Write-Host 'Largest files:'
$files | Sort-Object Length -Descending | Select-Object -First 10 |
    ForEach-Object { Write-Host ('  {0,7:N1} MB  {1}' -f ($_.Length / $mb), $_.FullName.Substring($layout.Length + 1)) }

$over = @()
if ($download -gt $MaxDownloadMB) { $over += 'download' }
if ($installed -gt $MaxInstalledMB) { $over += 'installed' }
if ($over) {
    Write-Error "Over budget: $($over -join ', ')"
    exit 1
}
