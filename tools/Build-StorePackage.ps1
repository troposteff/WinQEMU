<#
.SYNOPSIS
    Builds WinQEMU for the Microsoft Store: an unsigned upload package with the identity Partner Center gives.

.DESCRIPTION
    Writes artifacts\store\v<version>\WinQEMU_<version>_x64.msix, for Partner Center (Submissions, Packages). The Store
    signs the package itself: nothing here is signed, and no certificate is needed. A plain .msix rather than a
    .msixupload: Partner Center takes both, and the Windows App SDK's task that writes the .msixupload failed here with
    an exception of its own (System.Security.Permissions not found while reporting the real error).

    It differs from tools/Build-Package.ps1 (the GitHub package) in two ways only:
      - the identity: Package/Identity/Name, Publisher and PublisherDisplayName as reserved in Partner Center (Product
        management, Product identity), read from packaging\store\identity.json or given here. Package.appxmanifest keeps
        the GitHub identity: the manifest given to the build is a copy (WinQemuStoreManifest in WinQEMU.csproj).
      - no TAP-Windows driver installer (WinQemuStore): Store policy 10.2.4 generally refuses a product that depends on a
        non-Microsoft driver, so the user installs it, guided, as Npcap.

    The Store identity is another application to Windows: WinQEMU takes the GitHub installation's settings over at its
    first start (WinQEMU.Core.Services.SettingsImport). See docs/distribution.md, Microsoft Store.

.EXAMPLE
    pwsh ./tools/Build-StorePackage.ps1
.EXAMPLE
    pwsh ./tools/Build-StorePackage.ps1 -Name 12345troposteff.WinQEMU -Publisher CN=00000000-0000-0000-0000-000000000000 -PublisherDisplayName troposteff
#>
param(
    [string]$Name,
    [string]$Publisher,
    [string]$PublisherDisplayName
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$info = & (Join-Path $PSScriptRoot 'Get-WinQemuVersion.ps1') -Root $root

$identityFile = Join-Path $root 'packaging\store\identity.json'
if (Test-Path $identityFile) {
    $saved = Get-Content $identityFile -Raw | ConvertFrom-Json
    if (-not $Name) { $Name = $saved.Name }
    if (-not $Publisher) { $Publisher = $saved.Publisher }
    if (-not $PublisherDisplayName) { $PublisherDisplayName = $saved.PublisherDisplayName }
}
if (-not $Name -or -not $Publisher -or -not $PublisherDisplayName) {
    throw "The Store identity is missing: reserve the name in Partner Center, then fill packaging\store\identity.json (Name, Publisher, PublisherDisplayName) from Product identity, or pass -Name, -Publisher and -PublisherDisplayName."
}
# The GitHub identity would make the Store package the same application as the self-signed one, which the Store cannot
# sign for.
if ($Name -eq $info.Name -or $Publisher -eq $info.Publisher) {
    throw "That is the GitHub package's identity ($($info.Name), $($info.Publisher)); the Store's comes from Partner Center."
}

$out = Join-Path $root "artifacts\store\v$($info.Display)"
$work = Join-Path $out 'build'
New-Item -ItemType Directory -Force $work | Out-Null

$manifest = [xml](Get-Content (Join-Path $root 'WinQEMU\Package.appxmanifest') -Raw)
$manifest.Package.Identity.Name = $Name
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$storeManifest = Join-Path $work 'Package.appxmanifest'
$manifest.Save($storeManifest)

try {
    dotnet publish (Join-Path $root 'WinQEMU\WinQEMU.csproj') -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxBundle=Never `
        -p:UapAppxPackageBuildMode=SideloadOnly -p:WinQemuStore=true "-p:WinQemuStoreManifest=$storeManifest" `
        "-p:AppxPackageDir=$work\packages\"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    $msix = Get-ChildItem $work -Recurse -Filter "*_$($info.Package)_x64.msix" | Select-Object -First 1
    if (-not $msix) { throw "No .msix was produced." }

    # What the Store will certify: the identity given, and no driver installer.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $entries = $zip.Entries.FullName
        $identity = ([xml]([System.IO.StreamReader]::new(($zip.Entries | Where-Object FullName -eq 'AppxManifest.xml').Open()).ReadToEnd())).Package.Identity
    } finally {
        $zip.Dispose()
    }
    if ($entries | Where-Object { $_ -like 'Assets/Drivers/*' }) { throw 'The Store package still carries Assets\Drivers.' }
    if ($identity.Name -ne $Name -or $identity.Publisher -ne $Publisher) {
        throw "The package's identity is $($identity.Name), $($identity.Publisher): the Store manifest was not used."
    }
    $uploadName = "WinQEMU_$($info.Display)_x64.msix"
    Copy-Item $msix.FullName (Join-Path $out $uploadName) -Force
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

"Upload  : $(Join-Path $out $uploadName)"
"Identity: $Name, $Publisher ($PublisherDisplayName), version $($info.Package)"
'Partner Center, WinQEMU, Submissions: upload it under Packages. Listing and certification notes: packaging\store\.'
