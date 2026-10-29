<#
.SYNOPSIS
    Installs WinQEMU on this PC: trusts its signing certificate, then installs the package.

.DESCRIPTION
    Run once, as administrator (trusting a certificate for the whole PC needs it), from the folder holding
    WinQEMU.cer. Installs the .msix lying next to this script if there is one, else the latest release through its
    App Installer file, which also has Windows keep WinQEMU up to date.

    WinQEMU does not install QEMU: install it too (https://qemu.weilnetz.de/w64/ or winget install SoftwareFreedomConservancy.QEMU).
#>
#Requires -RunAsAdministrator
param([string]$AppInstaller = 'https://github.com/troposteff/WinQEMU/releases/latest/download/WinQEMU.appinstaller')

$ErrorActionPreference = 'Stop'

# The package is signed by a certificate of its own author, not by a public authority: Windows installs it only
# once that certificate is among this PC's trusted people.
Import-Certificate -FilePath (Join-Path $PSScriptRoot 'WinQEMU.cer') -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

$msix = Get-ChildItem $PSScriptRoot -Filter 'WinQEMU_*_x64.msix' | Sort-Object Name -Descending | Select-Object -First 1
if ($msix) {
    Add-AppxPackage -Path $msix.FullName
} else {
    Add-AppxPackage -AppInstallerFile $AppInstaller
}
'WinQEMU is installed: in the Start menu, and as "winqemu" in a terminal.'
