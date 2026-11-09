#Requires -Version 7
<#
.SYNOPSIS
    Prepares the GitHub release of the version published on the Microsoft Store: the Store-signed package, its offline
    licence, their SHA-256 and the release notes. Publishes nothing: it prints the gh command to run once checked.

.DESCRIPTION
    Same shape as NanaZip's releases: the package the Store signed (so no certificate to trust, and Smart App Control lets
    it start) and the licence that lets it start without the Store (Add-AppxProvisionedPackage -LicensePath). Both come
    from the Store itself, by winget download, which needs offline licensing turned on in Partner Center (it is) and, for
    the licence, a Microsoft Entra account that is Global, User or License Administrator of the author's tenant: winget
    asks for the sign-in if needed.

    Run it once the Store has published the manifest's version (tools/Publish-Store.ps1, then certification). Writes
    artifacts\github\v<version>\:
      WinQEMU_<version>_x64.msix          the package, as the Store serves it
      WinQEMU_<version>_License.xml       its offline licence
      SHA256SUMS.txt                      the SHA-256 of both
      notes.md                            the release notes: "What's new" of listing.en.md, how to install, the hashes

.EXAMPLE
    pwsh ./tools/New-GitHubRelease.ps1
#>
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$productId = '9NTWD738Z396'
$info = & (Join-Path $PSScriptRoot 'Get-WinQemuVersion.ps1') -Root $Root
if ($info.Beta) { throw "$($info.Display) is a beta: only versions published on the Store get a GitHub release." }

$out = Join-Path $Root "artifacts\github\$($info.Tag)"
$download = Join-Path $out 'download'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $download | Out-Null

# 1. The package and its licence, from the Store.
winget download --id $productId --source msstore --architecture x64 --download-directory $download `
    --accept-source-agreements --accept-package-agreements
if ($LASTEXITCODE -ne 0) { throw "winget download failed ($LASTEXITCODE)." }
$package = @(Get-ChildItem $download -Filter '*.msix')
$license = @(Get-ChildItem $download -Filter '*_License.xml')
if ($package.Count -ne 1) { throw "Expected one .msix from the Store, got $($package.Count)." }
if ($license.Count -ne 1) { throw 'The Store gave no licence: sign in with an administrator of the tenant (Global, User or License Administrator).' }

# 2. The Store's version must be the manifest's: a release names the sources it was built from.
if ($package[0].Name -notmatch '_(\d+\.\d+\.\d+\.\d+)_') { throw "No version in $($package[0].Name)." }
if ($Matches[1] -ne $info.Package) {
    throw "The Store serves $($Matches[1]), the manifest says $($info.Package): wait for the Store to publish it, or check out the matching commit."
}
$signature = Get-AuthenticodeSignature $package[0].FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Issuer -notmatch 'Microsoft Marketplace') {
    throw "The package is not signed by the Store ($($signature.Status), $($signature.SignerCertificate.Issuer))."
}

$msixName = "WinQEMU_$($info.Display)_x64.msix"
$licenseName = "WinQEMU_$($info.Display)_License.xml"
Move-Item $package[0].FullName (Join-Path $out $msixName)
Move-Item $license[0].FullName (Join-Path $out $licenseName)
Remove-Item $download -Recurse -Force

$hashes = foreach ($name in $msixName, $licenseName) {
    [pscustomobject]@{ Name = $name; Hash = (Get-FileHash (Join-Path $out $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
($hashes | ForEach-Object { "$($_.Hash)  $($_.Name)" }) -join "`n" | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8NoBOM -NoNewline

# 3. The notes.
$lines = Get-Content (Join-Path $Root 'packaging\store\listing.en.md') -Encoding utf8
$start = [array]::IndexOf($lines, "## What's new in this version")
if ($start -lt 0) { throw "listing.en.md has no '## What's new in this version' section." }
$whatsNew = (($lines[($start + 1)..($lines.Count - 1)] | ForEach-Object -Begin { $stop = $false } -Process {
            if ($_ -match '^## ') { $stop = $true }
            if (-not $stop) { $_ }
        }) -join "`n").Trim()

$notes = @"
## What's new

$whatsNew

## Installing

The recommended way is the [Microsoft Store](https://apps.microsoft.com/detail/$productId), which also keeps WinQEMU up
to date. Without the Store, use these two files: the package, signed by the Microsoft Store, and its offline licence. In
PowerShell **as administrator**, from the folder where they are:

``````powershell
Add-AppxProvisionedPackage -Online -PackagePath .\$msixName -LicensePath .\$licenseName
``````

WinQEMU then appears in the Start menu of every account of this PC. It is the same application as the Store's: the settings,
the library and the virtual machines are shared. QEMU is installed separately (see the README).

## Files

- ``$msixName``
  SHA-256: ``$($hashes[0].Hash)``
- ``$licenseName``
  SHA-256: ``$($hashes[1].Hash)``
"@
$notes | Set-Content (Join-Path $out 'notes.md') -Encoding utf8NoBOM

$commit = (git -C $Root rev-parse HEAD).Trim()
''
"Prepared in ${out}:"
Get-ChildItem $out | ForEach-Object { "  $($_.Name)" }
''
'Check the notes, then publish (the tag is created on the current commit, which must be pushed):'
"  gh release create $($info.Tag) `"$out\$msixName`" `"$out\$licenseName`" `"$out\SHA256SUMS.txt`" --repo troposteff/WinQEMU --target $commit --title `"WinQEMU $($info.Display)`" --notes-file `"$out\notes.md`" --latest"
