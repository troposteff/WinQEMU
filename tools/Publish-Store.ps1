#Requires -Version 7
<#
.SYNOPSIS
    Publishes a new version of WinQEMU on the Microsoft Store: builds the Store package, uploads it into a draft
    submission with the release notes of packaging\store\, and sends it to certification only once you have confirmed.

.DESCRIPTION
    Uses the Microsoft Store Developer CLI (msstore), configured once on this PC with Partner Center's Entra application
    (docs/distribution.md, Microsoft Store). Its secret stays in this PC's Credential Manager: nothing here reads it.

    Steps:
      1. Checks that no submission is pending and that the manifest's version is higher than the Store's.
      2. Builds the package (tools/Build-StorePackage.ps1), unless -SkipBuild.
      3. msstore publish --noCommit: a draft with the new package. Nothing is submitted yet.
      4. Release notes ("What's new in this version"), description and features of listing.en.md and listing.fr.md
         set on the draft, and the previous packages marked for removal.
      5. Shows the draft and asks; only "yes" sends it to certification (msstore submission publish). Otherwise the draft
         stays in Partner Center, to finish there or delete (msstore submission delete 9NTWD738Z396).

    Once certified and published, tools/New-GitHubRelease.ps1 makes the GitHub release (docs/distribution.md, Releasing
    a version).

    The rest of the listing (short description, search terms, screenshots) is the published one, carried over by the
    draft: change it in Partner Center, or import it with packaging/store/New-ListingImport.ps1.

.EXAMPLE
    pwsh ./tools/Publish-Store.ps1
.EXAMPLE
    pwsh ./tools/Publish-Store.ps1 -SkipBuild -RolloutPercentage 20
#>
param(
    # Uses the package already in artifacts\store\v<version>\ rather than building it again.
    [switch]$SkipBuild,
    # Gradual rollout: the share of users offered the update at first (Partner Center finishes or halts it).
    [ValidateRange(1, 100)][int]$RolloutPercentage,
    # Passed to msstore only if it refuses the submission's price (per-market pricing); 'Free' for WinQEMU.
    [string]$PriceId,
    # After the submission, waits until the Store publishes it or fails (certification can take hours).
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
$productId = '9NTWD738Z396'
$root = Split-Path $PSScriptRoot -Parent
$info = & (Join-Path $PSScriptRoot 'Get-WinQemuVersion.ps1') -Root $root

if (-not (Get-Command msstore -ErrorAction SilentlyContinue)) {
    throw 'msstore is not installed: winget install "Microsoft Store Developer CLI", then msstore reconfigure (docs/distribution.md).'
}

function Invoke-Msstore {
    # Progress goes to the console (stderr), the JSON some commands write to stdout is returned.
    $output = & msstore @args
    if ($LASTEXITCODE -ne 0) { throw "msstore $($args[0]) $($args[1]) failed ($LASTEXITCODE)." }
    $output
}

function Get-Submission {
    # The pending submission if there is one, otherwise the last published one. Edited as a JSON tree rather than
    # through ConvertFrom-Json, which turns dates into DateTime and would write them back differently.
    # The comma keeps the object whole: a JsonObject is enumerable, and a function's output would be unrolled into its
    # key/value pairs (Status then read as empty: "A submission is already pending ()").
    , [System.Text.Json.Nodes.JsonNode]::Parse((Invoke-Msstore submission get $productId) -join "`n")
}

# A "## Heading" section of a listing file: the text up to the next heading.
function Read-Section([string]$file, [string]$heading, [int]$max) {
    $lines = Get-Content (Join-Path $root "packaging\store\$file") -Encoding utf8
    $start = [array]::IndexOf($lines, "## $heading")
    if ($start -lt 0) { throw "$file has no '## $heading' section." }
    $text = ($lines[($start + 1)..($lines.Count - 1)] | ForEach-Object -Begin { $stop = $false } -Process {
            if ($_ -match '^## ') { $stop = $true }
            if (-not $stop) { $_ }
        }) -join "`n"
    $text = $text.Trim()
    if (-not $text) { throw "The '## $heading' section of $file is empty." }
    if ($text.Length -gt $max) { throw "The '## $heading' section of $file has $($text.Length) characters; the Store takes $max." }
    $text
}

# What the draft takes from the listing files: the release notes, and the description and features, which change with
# what WinQEMU does (the rest of the listing is carried over from the published submission).
function Read-Listing([string]$file, [string]$notesHeading, [string]$featuresHeading) {
    $features = @((Read-Section $file $featuresHeading 4000) -split "`n" | Where-Object { $_.StartsWith('- ') } |
            ForEach-Object { $_.Substring(2).Trim() })
    if ($features.Count -gt 20 -or ($features | Where-Object Length -gt 200)) {
        throw "The features of ${file}: at most 20, of 200 characters each."
    }
    @{
        ReleaseNotes = Read-Section $file $notesHeading 1500
        Description = Read-Section $file 'Description' 10000
        Features = $features
    }
}

$listings = [ordered]@{
    'en-us' = Read-Listing 'listing.en.md' "What's new in this version" 'Product features'
    'fr-fr' = Read-Listing 'listing.fr.md' 'Nouveautés de cette version' 'Fonctionnalités du produit'
}

# 1. Nothing pending, and a version the Store will take. A draft that already holds this version's package (a previous run
# stopped after the upload) is taken up where it was left: steps 2 and 3 are skipped.
$packageDir = Join-Path $root "artifacts\store\v$($info.Display)"
$msixName = "WinQEMU_$($info.Display)_x64.msix"
$msix = Join-Path $packageDir $msixName
$current = Get-Submission
$status = [string]$current['Status']
$resume = $status -eq 'PendingCommit' -and
    @($current['ApplicationPackages'].AsArray() | Where-Object { [string]$_['FileName'] -eq $msixName }).Count -gt 0
if ($resume) {
    "A draft already holds ${msixName}: taking it up (no new upload)."
} elseif ($status -ne 'Published') {
    throw "A submission is already pending ($status). Finish it in Partner Center, or delete a draft: msstore submission delete $productId"
}
# A package the Store has not processed yet has no version.
$published = @($current['ApplicationPackages'].AsArray() | Where-Object { $_['Version'] } | ForEach-Object { [version][string]$_['Version'] }) |
    Sort-Object -Descending | Select-Object -First 1
if (-not $resume -and $published -and [version]$info.Package -le $published) {
    throw "The manifest's version ($($info.Package)) is not higher than the Store's ($published): raise Version in WinQEMU\Package.appxmanifest."
}
foreach ($lang in $(if ($resume) { @() } else { $listings.Keys })) {
    $old = [string]$current['Listings']?[$lang]?['BaseListing']?['ReleaseNotes']
    if ($old.Trim() -eq $listings[$lang].ReleaseNotes) {
        Write-Warning "The $lang release notes are the published version's: update packaging\store\listing.*.md first?"
        if ((Read-Host 'Continue anyway? (yes/no)') -ne 'yes') { return }
    }
}

if (-not $resume) {
    # 2. The package.
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'Build-StorePackage.ps1')
    }
    if (-not (Test-Path $msix)) { throw "$msix not found: build it (without -SkipBuild)." }

    # 3. The draft. The .msix itself is given: msstore then takes the file as it is, rather than looking at the repository
    # for a project to build (it recognises WinQEMU.csproj as a WinUI project).
    $publishArgs = @('publish', $msix, '--appId', $productId, '--noCommit')
    if ($RolloutPercentage) { $publishArgs += '--packageRolloutPercentage', $RolloutPercentage }
    if ($PriceId) { $publishArgs += '--priceId', $PriceId }
    Invoke-Msstore @publishArgs | Out-Host
}

# 4. Listings, and only the new package left in the draft.
$draft = Get-Submission
if ([string]$draft['Status'] -ne 'PendingCommit') {
    throw "The draft was expected, msstore left a submission in state $($draft['Status'])."
}
foreach ($lang in $listings.Keys) {
    $listing = $draft['Listings']?[$lang]?['BaseListing']
    if ($null -eq $listing) { throw "The draft has no $lang listing." }
    $listing['ReleaseNotes'] = $listings[$lang].ReleaseNotes
    $listing['Description'] = $listings[$lang].Description
    $features = [System.Text.Json.Nodes.JsonArray]::new()
    foreach ($feature in $listings[$lang].Features) { $features.Add([System.Text.Json.Nodes.JsonValue]::Create([string]$feature)) }
    $listing['Features'] = $features
}
# By file name: the package just uploaded is "PendingUpload", without a version until the Store has processed it.
foreach ($package in $draft['ApplicationPackages'].AsArray()) {
    if ([string]$package['FileName'] -ne $msixName) { $package['FileStatus'] = 'PendingDelete' }
}
$payload = Join-Path ([System.IO.Path]::GetTempPath()) "winqemu-store-$([guid]::NewGuid().ToString('n')).json"
try {
    [System.IO.File]::WriteAllText($payload, $draft.ToJsonString(), [System.Text.UTF8Encoding]::new($false))
    Invoke-Msstore submission update $productId --payload $payload | Out-Null
} finally {
    Remove-Item $payload -ErrorAction SilentlyContinue
}

# 5. What would be submitted, read back from the Store.
$draft = Get-Submission
''
"Draft submission of WinQEMU $($info.Display) ($productId)"
'Packages:'
foreach ($package in $draft['ApplicationPackages'].AsArray()) {
    "  $($package['FileName'])  $($package['Version'])  $($package['FileStatus'])"
}
$rollout = $draft['PackageDeliveryOptions']?['PackageRollout']
if ($rollout -and $rollout['IsPackageRollout'].GetValue[bool]()) { "Rollout: $($rollout['PackageRolloutPercentage']) %" }
foreach ($lang in $listings.Keys) {
    $listing = $draft['Listings'][$lang]['BaseListing']
    ''
    "Release notes ($lang):"
    "  $($listing['ReleaseNotes'])"
    "Features ($lang): $($listing['Features'].AsArray().Count), description: $(([string]$listing['Description']).Length) characters"
}
''
if ((Read-Host 'Send this submission to certification? (yes/no)') -ne 'yes') {
    "Not submitted. The draft stays in Partner Center: finish it there, or delete it with msstore submission delete $productId"
    return
}
Invoke-Msstore submission publish $productId | Out-Host
if ($Wait) {
    Invoke-Msstore submission poll $productId | Out-Host
} else {
    "Submitted. Partner Center sends an email once certified; meanwhile: msstore submission status $productId"
}
'Once the Store serves this version, the GitHub release: pwsh ./tools/New-GitHubRelease.ps1'
