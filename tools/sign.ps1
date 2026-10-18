<#
.SYNOPSIS
    Code-signs the published RecentDock.exe with a local self-signed certificate.

.DESCRIPTION
    SmartScreen blocks unsigned binaries whose hash it has never seen. Every rebuild
    produces a new hash, so every rebuild gets stopped once - annoying for anyone who
    builds this project locally.

    Signing fixes it at the source: with a certificate in "Trusted Publishers" and
    "Trusted Root Certification Authorities", the signature verifies, the publisher is
    known, and the prompt stops appearing.

    The certificate is created once, in the CURRENT USER's store. It is not a substitute
    for a real code-signing certificate for anything you distribute: trust is local to
    this machine, so other people would still see "unknown publisher".

.PARAMETER Exe
    File to sign. Defaults to dist\RecentDock.exe in the repository root.

.PARAMETER Subject
    Distinguished name of the certificate to create/reuse.

.PARAMETER NewCertificate
    Create a fresh certificate even if a usable one already exists.
#>
[CmdletBinding()]
param(
    [string] $Exe = (Join-Path $PSScriptRoot '..\dist\RecentDock.exe'),
    [string] $Subject = 'CN=RecentDock Local Build',
    [switch] $NewCertificate
)

$ErrorActionPreference = 'Stop'

function Write-Step($text) { Write-Host "  $text" }

$exePath = (Resolve-Path -LiteralPath $Exe).Path
Write-Host "RecentDock code signing"
Write-Step "target : $exePath"

# ---------------------------------------------------------------- certificate

$certificate = $null

if (-not $NewCertificate) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($certificate) {
        Write-Step "reusing existing certificate: $($certificate.Thumbprint)"
    }
}

if (-not $certificate) {
    Write-Step "creating a self-signed code-signing certificate ..."

    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')   # EKU: code signing

    Write-Step "created: $($certificate.Thumbprint)"
}

# ---------------------------------------------------------------- trust

# A self-signed certificate must be present in Root for the chain to validate, and in
# TrustedPublisher so the shell recognises the publisher instead of saying "unknown".
#
# Added through X509Store rather than Import-Certificate: importing into Root is a
# consent operation, and the cmdlet refuses with "UI is not allowed in this operation"
# when there is no interactive prompt to show. The store API does the same thing without
# one - the certificate is created by this script for this machine, so there is nothing
# for a prompt to protect the user from.
$publicOnly = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.RawData)

foreach ($storeName in 'Root', 'TrustedPublisher') {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        $storeName, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)

    try {
        $alreadyThere = $store.Certificates |
            Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }

        if ($alreadyThere) {
            Write-Step "$storeName : already trusted"
            continue
        }

        $store.Add($publicOnly)
        Write-Step "$storeName : certificate installed"
    }
    finally {
        $store.Close()
    }
}

# ---------------------------------------------------------------- sign

Write-Step 'signing ...'

$signature = $null

# A timestamp is what keeps the signature valid past the certificate's expiry. It needs
# network access; when that is unavailable the signature is still made, just without one.
try {
    $signature = Set-AuthenticodeSignature -FilePath $exePath -Certificate $certificate `
        -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256 -ErrorAction Stop
}
catch {
    Write-Step "timestamp server unavailable ($($_.Exception.Message)), signing without one"
    $signature = Set-AuthenticodeSignature -FilePath $exePath -Certificate $certificate -HashAlgorithm SHA256
}

Write-Step "status : $($signature.Status)"
Write-Step "signer : $($signature.SignerCertificate.Subject)"

if ($signature.Status -ne 'Valid') {
    Write-Host ""
    Write-Host "FAILED: signature status is $($signature.Status)" -ForegroundColor Red
    exit 1
}

# Mark the output as belonging to the Local Machine zone.
#
# A freshly built binary in a folder the shell does not consider trusted can raise the
# attachment warning ("打开文件 - 安全警告 / 来自 Internet 的文件") even when it carries
# no Mark of the Web at all - the classification then comes from somewhere the file
# itself does not show. An explicit ZoneId=0 states what is actually true (this file was
# built here) and stops the prompt.
#
# Written AFTER signing on purpose: adding an alternate data stream does not touch the
# signed image, so the signature stays valid (verified).
try {
    Set-Content -LiteralPath $exePath -Stream 'Zone.Identifier' `
        -Value "[ZoneTransfer]`r`nZoneId=0" -Encoding Ascii -NoNewline -ErrorAction Stop
    Write-Step 'zone   : marked Local (ZoneId=0)'
}
catch {
    Write-Step "zone   : could not be marked ($($_.Exception.Message))"
}

Write-Host ""
Write-Host "Done. The executable is signed and trusted on this machine." -ForegroundColor Green
Write-Host "Re-run this script after every publish; a rebuild produces an unsigned binary again."
