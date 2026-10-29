<#
.SYNOPSIS
    Fills Partner Center's exported Store listing with WinQEMU's English and French texts and screenshots, ready to import.

.DESCRIPTION
    Partner Center (WinQEMU, Store listings, Export listings) gives a .csv whose Field, ID and Type columns must come back
    unchanged: the IDs are Partner Center's own. So this script starts from that export rather than writing a .csv of its
    own, fills the default, en-us and fr-fr columns from listing.en.md and listing.fr.md (one "## " section per field),
    and writes artifacts\store\listing\WinQEMU-listing\: the filled .csv and the screenshots it names. Import it with
    Import listings, Import folder, choosing the WinQEMU-listing folder.

    Fields the export does not have are reported, not invented. Rows the listings do not fill are left as exported.

.EXAMPLE
    pwsh ./packaging/store/New-ListingImport.ps1 -Export "$env:USERPROFILE\Downloads\<exported file>.csv"
#>
param(
    [Parameter(Mandatory)][string]$Export
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path (Split-Path $here -Parent) -Parent
$folderName = 'WinQEMU-listing'
$out = Join-Path $root "artifacts\store\listing\$folderName"

# One "## Heading" section per field, in either language; the text is everything up to the next heading.
$headings = @{
    'Product name' = 'Title'; 'Nom du produit' = 'Title'
    'Description' = 'Description'
    'Short description' = 'ShortDescription'; 'Description courte' = 'ShortDescription'
    "What's new in this version" = 'ReleaseNotes'; 'Nouveautés de cette version' = 'ReleaseNotes'
    'Product features' = 'Features'; 'Fonctionnalités du produit' = 'Features'
    'Search terms' = 'SearchTerms'; 'Termes de recherche' = 'SearchTerms'
    'Copyright and trademark info' = 'Copyright'; 'Copyright et marques' = 'Copyright'
    'Additional license terms' = 'LicenseTerms'; 'Conditions de licence supplémentaires' = 'LicenseTerms'
}

function Read-Listing([string]$path) {
    $sections = @{}
    $current = $null
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in (Get-Content $path -Encoding utf8) + '## end') {
        if ($line -match '^## (.+)$') {
            if ($current) { $sections[$current] = ($lines -join "`n").Trim() }
            $current = $headings[$Matches[1].Trim()]
            $lines.Clear()
        } elseif ($current) {
            $lines.Add($line)
        }
    }
    $sections
}

$languages = [ordered]@{
    'en-us' = @{
        Listing = Read-Listing (Join-Path $here 'listing.en.md')
        Shots = @('summary-en.png', 'console-en.png')
        Captions = @("A machine's summary, with its resource charts", 'The integrated console')
    }
    'fr-fr' = @{
        Listing = Read-Listing (Join-Path $here 'listing.fr.md')
        Shots = @('summary-fr.png', 'console-fr.png')
        Captions = @("Le résumé d'une machine, avec ses graphiques de ressources", 'La console intégrée')
    }
}

# The value of an exported field for a language, or $null when the listings have nothing for it.
function Get-Value($field, $lang) {
    $l = $languages[$lang].Listing
    switch -Regex ($field) {
        '^Title$' { return $l.Title }
        '^Description$' { return $l.Description }
        '^ShortDescription$' { return $l.ShortDescription }
        '^ReleaseNotes$' { return $l.ReleaseNotes }
        '^Copyright' { return $l.Copyright }
        '^AdditionalLicenseTerms$' { return $l.LicenseTerms }
        '^Feature(\d+)$' {
            # Before the list is read: its -match replaces $Matches, and Feature1 got the last feature.
            $i = [int]$Matches[1] - 1
            $items = @($l.Features -split "`n" | Where-Object { $_.StartsWith('- ') } | ForEach-Object { $_.Substring(2).Trim() })
            return $(if ($i -lt $items.Count) { $items[$i] } else { $null })
        }
        '^SearchTerm(\d+)$' {
            $terms = @($l.SearchTerms -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            $i = [int]$Matches[1] - 1
            return $(if ($i -lt $terms.Count) { $terms[$i] } else { $null })
        }
        '^DesktopScreenshot(\d+)$' {
            $i = [int]$Matches[1] - 1
            $shots = $languages[$lang].Shots
            return $(if ($i -lt $shots.Count) { "$folderName/images/$($shots[$i])" } else { $null })
        }
        '^DesktopScreenshotCaption(\d+)$' {
            $i = [int]$Matches[1] - 1
            $captions = $languages[$lang].Captions
            return $(if ($i -lt $captions.Count) { $captions[$i] } else { $null })
        }
    }
    $null
}

$rows = @(Import-Csv $Export -Encoding utf8)
if (-not $rows -or -not ($rows[0].PSObject.Properties.Name -contains 'Field')) {
    throw "$Export is not a Partner Center listing export (no Field column)."
}
$columns = [System.Collections.Generic.List[string]]::new([string[]]$rows[0].PSObject.Properties.Name)
foreach ($lang in $languages.Keys) {
    if (-not $columns.Contains($lang)) { $columns.Add($lang) }
}

$filled = @{}
$result = foreach ($row in $rows) {
    $record = [ordered]@{}
    foreach ($c in $columns) { $record[$c] = if ($row.PSObject.Properties.Name -contains $c) { $row.$c } else { '' } }
    foreach ($lang in $languages.Keys) {
        $value = Get-Value $row.Field $lang
        if ($null -ne $value) {
            $record[$lang] = $value
            $filled[$row.Field] = $true
        } elseif (-not $record[$lang] -and $record['en-us'] -in 'True', 'False') {
            # A switch Partner Center set for the English listing (OverrideLogosForWin10): the same for a language added here.
            $record[$lang] = $record['en-us']
        }
    }
    [pscustomobject]$record
}

# What the listings provide that the export has no row for: said, not guessed.
$expected = 'Title', 'Description', 'ShortDescription', 'ReleaseNotes', 'Feature1', 'SearchTerm1', 'AdditionalLicenseTerms', 'DesktopScreenshot1'
$missing = $expected | Where-Object { -not ($rows.Field -contains $_) }
if (-not ($rows.Field | Where-Object { $_ -match '^Copyright' })) { $missing += 'Copyright…' }

if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $out 'images') | Out-Null
foreach ($lang in $languages.Keys) {
    foreach ($shot in $languages[$lang].Shots) {
        Copy-Item (Join-Path $root "docs\images\$shot") (Join-Path $out 'images') -Force
    }
}
# UTF-8 with a BOM, as Excel's "CSV UTF-8" writes it.
$csv = ($result | ConvertTo-Csv -NoTypeInformation) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $out 'listing.csv'), $csv + "`r`n", [System.Text.UTF8Encoding]::new($true))

"Folder : $out"
"Filled : $($filled.Keys.Count) fields, for $($languages.Keys -join ' and ')"
if ($missing) { "Not in the export, to fill in Partner Center: $($missing -join ', ')" }
'Partner Center, Store listings: Import listings, Import folder, then the WinQEMU-listing folder.'
