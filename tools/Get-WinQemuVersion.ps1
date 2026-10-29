<#
.SYNOPSIS
    WinQEMU's version, read from Package.appxmanifest: the package's four numbers, and the version as people read it.

.DESCRIPTION
    Returns Package (2026.0.1.0), Display (2026.0.1_beta), Tag (v2026.0.1_beta) and Beta. A minor version of 0 is a beta, as
    WinQEMU.Core.AppVersion decides in the application; the fourth number must stay 0, MSIX taking four numbers and nothing
    else. Used by Build-Package.ps1 and by the release-check workflow, so that the tag, the files and the application agree.
#>
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

[xml]$manifest = Get-Content (Join-Path $Root 'WinQEMU\Package.appxmanifest')
$identity = $manifest.Package.Identity
$version = $identity.Version
$parts = $version.Split('.')
if ($parts.Count -ne 4 -or $parts[3] -ne '0') { throw "The manifest's version is ${version}: its fourth number must be 0 (2026.0.1.0)." }
$beta = $parts[1] -eq '0'
$display = "$($parts[0]).$($parts[1]).$($parts[2])" + $(if ($beta) { '_beta' } else { '' })

[pscustomobject]@{
    Package   = $version
    Display   = $display
    Tag       = "v$display"
    Beta      = $beta
    Name      = $identity.Name
    Publisher = $identity.Publisher
}
