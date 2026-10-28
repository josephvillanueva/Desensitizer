<#
.SYNOPSIS
  Regenerates THIRD-PARTY-NOTICES.txt from docs\third-party-components.json.
.DESCRIPTION
  Edit the JSON table when a version or licence changes, then run this script.
  Also checks that every licence file named in the table exists under docs\ and
  prints a warning for each reference that does not resolve (MISSING entries are
  expected and reported, not errors).
.PARAMETER OutFile
  Output path. Default: THIRD-PARTY-NOTICES.txt in the repository root.
.PARAMETER Check
  Only verify that the existing file matches what would be generated.
.NOTES
  Technical inventory, not legal advice.
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$jsonPath = Join-Path $root 'docs\third-party-components.json'
if (-not $OutFile) { $OutFile = Join-Path $root 'THIRD-PARTY-NOTICES.txt' }

$data = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json

$sb = New-Object System.Text.StringBuilder
function Add-Line([string]$s = '') { [void]$sb.Append($s).Append("`r`n") }

Add-Line 'THIRD-PARTY NOTICES'
Add-Line 'Desensitizer and AI Desensitizer'
Add-Line ('Inventory reviewed: ' + $data.reviewedOn)
Add-Line
Add-Line 'This file lists the third-party software and model weights that are compiled into,'
Add-Line 'or shipped beside, the two applications, with their licences and the obligations'
Add-Line 'each licence places on the people who distribute or use them.'
Add-Line
Add-Line 'IMPORTANT: this is a technical inventory prepared by the developers. It is NOT legal'
Add-Line 'advice. Items marked MISSING or "unverified" could not be confirmed. Licence texts'
Add-Line 'are in docs\licenses\ in the source repository and should be copied into any'
Add-Line 'portable package that is shared. See docs\LICENSE-REVIEW.md for the plain-language'
Add-Line 'summary and the open questions for legal and IT review.'
Add-Line
Add-Line 'The applications themselves: the project owner has not yet chosen a licence for'
Add-Line 'the application source code (open question, see docs\LICENSE-REVIEW.md).'
Add-Line
Add-Line ('=' * 78)

$missing = New-Object System.Collections.Generic.List[string]
$n = 0
foreach ($section in $data.sections) {
    Add-Line
    Add-Line $section.name.ToUpperInvariant()
    Add-Line ('-' * 78)
    foreach ($c in $section.components) {
        $n++
        Add-Line
        Add-Line ('{0}. {1}' -f $n, $c.name)
        Add-Line ('   Version        : ' + $c.version)
        Add-Line ('   Used by        : ' + $c.apps)
        Add-Line ('   Licence        : ' + $c.license)
        Add-Line ('   Source         : ' + $c.source)
        Add-Line ('   Licence text   : ' + $c.licenseFile)
        Add-Line ('   Text status    : ' + $c.licenseStatus)
        Add-Line ('   Obligation     : ' + $c.obligation)
        if ($c.notes) { Add-Line ('   Notes          : ' + $c.notes) }
        if ($c.licenseFile -like 'MISSING*' -or $c.licenseStatus -like '*MISSING*') {
            [void]$missing.Add($c.name)
        }
        foreach ($m in [regex]::Matches([string]$c.licenseFile, 'licenses/[A-Za-z0-9_.\-/]+\.txt')) {
            $p = Join-Path (Join-Path $root 'docs') ($m.Value -replace '/', '\')
            if (-not (Test-Path -LiteralPath $p)) { Write-Warning ("Referenced licence file not found: " + $m.Value) }
        }
    }
}

Add-Line
Add-Line ('=' * 78)
Add-Line 'COMPONENTS WITH A MISSING OR PARTLY MISSING LICENCE TEXT'
Add-Line ('-' * 78)
foreach ($m in $missing) { Add-Line ('  - ' + $m) }
Add-Line
Add-Line 'No telemetry, accounts or cloud services are used by these components while the'
Add-Line 'applications run. Models are loaded from local disk.'

$text = $sb.ToString()
if ($Check) {
    $existing = if (Test-Path -LiteralPath $OutFile) { [IO.File]::ReadAllText($OutFile) } else { '' }
    if ($existing -ne $text) { Write-Error 'THIRD-PARTY-NOTICES.txt is out of date. Run tools\New-ThirdPartyNotices.ps1.'; exit 1 }
    Write-Host 'THIRD-PARTY-NOTICES.txt is up to date.'
    return
}
[IO.File]::WriteAllText($OutFile, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Wrote {0} ({1} components, {2} with missing text)" -f $OutFile, $n, $missing.Count)

