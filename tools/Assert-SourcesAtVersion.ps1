#Requires -Version 7
<#
.SYNOPSIS
    Refuses unless the application's sources are those of the commit that set the manifest's version, committed. Returns
    that commit.

.DESCRIPTION
    A Store package names a version, and the GitHub release tags the sources it was built from: built from changes made
    after the version commit (or not committed at all), the Store certified code that no commit held, and the release
    then tagged other sources than the package it carried. Documentation and scripts may move on; WinQEMU, WinQEMU.Core
    and WinQEMU.Cli may not. Used by Build-StorePackage.ps1 and New-GitHubRelease.ps1.
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$PackageVersion
)

$ErrorActionPreference = 'Stop'
$application = 'WinQEMU', 'WinQEMU.Core', 'WinQEMU.Cli'
$versionCommit = (git -C $Root log -1 --format=%H "-SVersion=`"$PackageVersion`"" -- WinQEMU/Package.appxmanifest).Trim()
if (-not $versionCommit) { throw "No commit sets Version=`"$PackageVersion`" in the manifest: commit the version first." }
$changed = @(git -C $Root diff --name-only $versionCommit HEAD -- @application)
if ($changed) {
    throw "The application changed since $($versionCommit.Substring(0, 7)) set $PackageVersion ($($changed.Count) files, $($changed[0])…): raise the version again, or check out that commit."
}
if (git -C $Root status --porcelain -- @application) { throw 'The application sources have uncommitted changes: commit them with a new version, or set them aside.' }
$versionCommit
