# One-off: creates the self-signed "CN=Morph Dev" code-signing cert that build/pack.ps1 signs local MSIX builds with.
# The cert and its private key stay in your user store (Cert:\CurrentUser\My); nothing here needs admin.
# Windows only installs an MSIX whose signer it trusts, so afterwards run ONE command elevated (printed below).
# Store builds are signed by the Store and never use this cert.
$ErrorActionPreference = 'Stop'
$subject = 'CN=Morph Dev'

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'Morph dev package signing' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -NotAfter (Get-Date).AddYears(2) `
        -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') # code signing EKU; not a CA
    Write-Host "Created $subject ($($cert.Thumbprint))"
} else {
    Write-Host "Using existing $subject ($($cert.Thumbprint))"
}

$root = Split-Path $PSScriptRoot -Parent
$cer = Join-Path $root 'artifacts/morph-dev.cer'
New-Item -ItemType Directory -Force (Split-Path $cer) | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
if ($trusted) {
    Write-Host 'Already trusted for package installs.'
} else {
    Write-Host ''
    Write-Host 'To let Windows install packages signed with it, run once in an elevated PowerShell:'
    Write-Host "  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}
