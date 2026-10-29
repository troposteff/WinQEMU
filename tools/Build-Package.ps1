<#
.SYNOPSIS
    Builds WinQEMU's installable package: a signed MSIX and what a PC needs to install it.

.DESCRIPTION
    Writes artifacts\v<version>\, <version> being the version as people read it (2026.0.1_beta, 2026.1.0):
      WinQEMU_<version>_x64.msix   the package, signed with the "CN=troposteff" certificate of the current user's store
      WinQEMU.cer                  the certificate's public part, trusted once on each PC (Install-WinQEMU.ps1)
      Install-WinQEMU.ps1          trusts the certificate and installs (run as administrator on the target PC)
      WinQEMU.appinstaller         releases only, not betas: points at the GitHub release, so that Windows updates
                                   WinQEMU by itself
      SHA256SUMS.txt               the SHA-256 of each of these files, to check a download against

    The version is the manifest's (Package.appxmanifest), raised by hand for each release: App Installer only updates
    to a higher one. Numbering chosen by the user: 2026.0.1, 2026.0.2… are betas (minor version 0, published as GitHub
    pre-releases, which "latest" never points at: no automatic update until the release), 2026.1.0 the release. The
    package's fourth number stays 0 — MSIX takes four numbers and nothing else (see WinQEMU.Core.AppVersion). The
    Windows App SDK is inside the package (WindowsAppSDKSelfContained): a PC needs nothing but QEMU.

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
$info = & (Join-Path $PSScriptRoot 'Get-WinQemuVersion.ps1') -Root $root
$identity = [pscustomobject]@{ Name = $info.Name; Publisher = $info.Publisher }
$version = $info.Package
$beta = $info.Beta
$display = $info.Display

if (-not $Thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $identity.Publisher -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "No certificate '$($identity.Publisher)' with its private key in Cert:\CurrentUser\My." }
    $Thumbprint = $cert.Thumbprint
}

$out = Join-Path $root "artifacts\v$display"
$work = Join-Path $out 'build'
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root 'WinQEMU\WinQEMU.csproj') -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
    -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$Thumbprint `
    -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly "-p:AppxPackageDir=$work\"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$built = Get-ChildItem $work -Recurse -Filter "WinQEMU_${version}_x64.msix" | Select-Object -First 1
if (-not $built) { throw "WinQEMU_${version}_x64.msix was not produced." }
$msixName = "WinQEMU_${display}_x64.msix"
Copy-Item $built.FullName (Join-Path $out $msixName) -Force
Copy-Item (Join-Path $root 'packaging\WinQEMU.cer') $out -Force
Copy-Item (Join-Path $root 'packaging\Install-WinQEMU.ps1') $out -Force
Remove-Item $work -Recurse -Force

if (-not $beta) {
    # The App Installer file sits at a fixed address, the latest release's; it names this release's package. Windows
    # checks it at each start of WinQEMU and updates in the background (no prompt, nothing blocked).
    $latest = "https://github.com/$Repository/releases/latest/download"
    $release = "https://github.com/$Repository/releases/download/v$display"
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
}

# What a download can be checked against (README, "Verifying a download"), in sha256sum's format: Get-FileHash on Windows,
# sha256sum -c elsewhere. Install-WinQEMU.ps1 checks the package against it before trusting anything.
Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) *$($_.Name)"
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

$signature = Get-AuthenticodeSignature (Join-Path $out $msixName)
"Package : $(Join-Path $out $msixName) ($([math]::Round((Get-Item (Join-Path $out $msixName)).Length / 1MB, 1)) MB)"
"Signed by: $($signature.SignerCertificate.Subject)"
"Release : gh release create v$display --repo $Repository --title `"WinQEMU $display`"$(if ($beta) { ' --prerelease' } else { ' --latest' }) $(Join-Path $out '*')"
