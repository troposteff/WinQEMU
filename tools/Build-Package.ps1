<#
.SYNOPSIS
    Builds WinQEMU's installable package: a signed MSIX, its App Installer file and what a PC needs to install it.

.DESCRIPTION
    Writes artifacts\v<version>\ :
      WinQEMU_<version>_x64.msix   the package, signed with the "CN=troposteff" certificate of the current user's store
      WinQEMU.appinstaller         points at the GitHub release, so that Windows updates WinQEMU by itself
      WinQEMU.cer                  the certificate's public part, trusted once on each PC (Install-WinQEMU.ps1)
      Install-WinQEMU.ps1          trusts the certificate and installs (run as administrator on the target PC)

    The version is the manifest's (Package.appxmanifest), raised by hand for each release: App Installer only updates
    to a higher one. The Windows App SDK is inside the package (WindowsAppSDKSelfContained): a PC needs nothing but QEMU.

.EXAMPLE
    pwsh ./tools/Build-Package.ps1
#>
param(
    # The certificate that signs the package; its subject must be the manifest's Publisher.
    [string]$Thumbprint,
    [string]$Repository = 'troposteff/WinQEMU'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$manifest = Get-Content (Join-Path $root 'WinQEMU\Package.appxmanifest')
$identity = $manifest.Package.Identity
$version = $identity.Version

if (-not $Thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $identity.Publisher -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "No certificate '$($identity.Publisher)' with its private key in Cert:\CurrentUser\My." }
    $Thumbprint = $cert.Thumbprint
}

$out = Join-Path $root "artifacts\v$version"
$work = Join-Path $out 'build'
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root 'WinQEMU\WinQEMU.csproj') -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
    -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$Thumbprint `
    -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly "-p:AppxPackageDir=$work\"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$msixName = "WinQEMU_${version}_x64.msix"
$msix = Get-ChildItem $work -Recurse -Filter $msixName | Select-Object -First 1
if (-not $msix) { throw "$msixName was not produced." }
Copy-Item $msix.FullName (Join-Path $out $msixName) -Force
Copy-Item (Join-Path $root 'packaging\WinQEMU.cer') $out -Force
Copy-Item (Join-Path $root 'packaging\Install-WinQEMU.ps1') $out -Force
Remove-Item $work -Recurse -Force

# The App Installer file sits at a fixed address, the latest release's; it names this release's package. Windows
# checks it at each start of WinQEMU and updates in the background (no prompt, nothing blocked).
$latest = "https://github.com/$Repository/releases/latest/download"
$release = "https://github.com/$Repository/releases/download/v$version"
@"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Version="$version" Uri="$latest/WinQEMU.appinstaller">
  <MainPackage Name="$($identity.Name)" Publisher="$($identity.Publisher)" Version="$version" ProcessorArchitecture="x64" Uri="$release/$msixName" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="0" ShowPrompt="false" UpdateBlocksActivation="false" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
"@ | Set-Content (Join-Path $out 'WinQEMU.appinstaller') -Encoding utf8

$signature = Get-AuthenticodeSignature (Join-Path $out $msixName)
"Package : $(Join-Path $out $msixName) ($([math]::Round((Get-Item (Join-Path $out $msixName)).Length / 1MB, 1)) MB)"
"Signed by: $($signature.SignerCertificate.Subject)"
"Release : gh release create v$version --repo $Repository $(Join-Path $out '*')"
