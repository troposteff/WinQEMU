<#
.SYNOPSIS
    Installs WinQEMU on this PC: trusts its signing certificate, then installs the package.

.DESCRIPTION
    Run once, as administrator (trusting a certificate for the whole PC needs it), from the folder holding
    WinQEMU.cer. Installs the .msix lying next to this script if there is one, else the latest release through its
    App Installer file, which also has Windows keep WinQEMU up to date.

    Before trusting anything it checks, when they are there, that the package matches SHA256SUMS.txt and that it is
    signed by the very certificate it is about to trust. The certificate goes to the PC's Trusted People only, which
    lets Windows install packages it signed; it is not made a root authority.

    An earlier WinQEMU is updated in place: its settings and its virtual machines are kept.

    WinQEMU does not install QEMU: install it too (https://qemu.weilnetz.de/w64/ or winget install SoftwareFreedomConservancy.QEMU).
#>
#Requires -RunAsAdministrator
param([string]$AppInstaller = 'https://github.com/troposteff/WinQEMU/releases/latest/download/WinQEMU.appinstaller')

$ErrorActionPreference = 'Stop'

$certificatePath = Join-Path $PSScriptRoot 'WinQEMU.cer'
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$msix = Get-ChildItem $PSScriptRoot -Filter 'WinQEMU_*_x64.msix' | Sort-Object Name -Descending | Select-Object -First 1

if ($msix) {
    # A package altered after it was built no longer matches the published list.
    $sums = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
    if (Test-Path $sums) {
        $expected = Get-Content $sums | Where-Object { $_ -match "^([0-9a-fA-F]{64}) \*?$([regex]::Escape($msix.Name))$" } |
            ForEach-Object { $Matches[1] } | Select-Object -First 1
        $actual = (Get-FileHash $msix.FullName -Algorithm SHA256).Hash
        if (-not $expected) { throw "$($msix.Name) is not listed in SHA256SUMS.txt." }
        if ($actual -ne $expected) { throw "$($msix.Name) does not match SHA256SUMS.txt: the download is damaged or altered." }
    }

    # Trusting a certificate that did not sign this package would trust it for nothing, or for someone else's package.
    # The signature must also still cover the contents: an altered package keeps its signer but reads HashMismatch.
    # Before the import, an intact one reads UnknownError or NotTrusted (its root is not trusted yet), never Valid.
    $signature = Get-AuthenticodeSignature $msix.FullName
    if ($signature.Status -notin 'Valid', 'UnknownError', 'NotTrusted') {
        throw "$($msix.Name) has no valid signature ($($signature.Status)): the download is damaged or altered."
    }
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "$($msix.Name) is not signed by WinQEMU.cer ($($certificate.Subject), $($certificate.Thumbprint))."
    }
}

# The package is signed by a certificate of its own author, not by a public authority: Windows installs it only
# once that certificate is among this PC's trusted people.
Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

if ($msix) {
    Add-AppxPackage -Path $msix.FullName
} else {
    Add-AppxPackage -AppInstallerFile $AppInstaller
}
'WinQEMU is installed: in the Start menu, and as "winqemu" in a terminal.'
