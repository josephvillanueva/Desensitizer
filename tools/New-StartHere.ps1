<#
.SYNOPSIS
  Generates PORTABLE-START-HERE.txt for each portable app folder from the csproj versions.
.DESCRIPTION
  Reads <Version> from Desensitizer\Desensitizer.csproj and AI.Desensitizer\Desensitizer.csproj and
  writes one PORTABLE-START-HERE.txt per app into the folder given by -OutDir (one subfolder
  per app). Point -OutDir at a staging folder or a freshly published portable folder.
  It never touches the Desktop folders unless you name them yourself.
.PARAMETER OutDir
  Folder that receives "Desensitizer\PORTABLE-START-HERE.txt" and "AI.Desensitizer\PORTABLE-START-HERE.txt".
  Pass -Flat with a single app to write straight into OutDir.
.PARAMETER App
  'Both' (default), 'Desensitizer' or 'AI'.
.PARAMETER Flat
  Write PORTABLE-START-HERE.txt directly into OutDir (requires -App Desensitizer or AI).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [ValidateSet('Both', 'Desensitizer', 'AI')][string]$App = 'Both',
    [switch]$Flat
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Flat -and $App -eq 'Both') { throw '-Flat needs -App Desensitizer or -App AI.' }

function Get-ProjectVersion([string]$csproj) {
    $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { throw "No <Version> in $csproj" }
    $m.Groups[1].Value
}

$apps = @(
    @{ Key = 'Desensitizer'; Folder = 'Desensitizer'; Name = 'Desensitizer'; Exe = 'Desensitizer.exe'; Csproj = 'Desensitizer\Desensitizer.csproj'
        Data = '%LOCALAPPDATA%\Desensitizer'
        Layers = 'rules, Presidio, GLiNER and OpenAI Privacy Filter detectors, then one local Qwen3-4B review.' },
    @{ Key = 'AI'; Folder = 'AI.Desensitizer'; Name = 'AI Desensitizer'; Exe = 'AI.Desensitizer.exe'; Csproj = 'AI.Desensitizer\Desensitizer.csproj'
        Data = '%LOCALAPPDATA%\AI Desensitizer'
        Layers = 'rules, Presidio, GLiNER and OpenAI Privacy Filter detectors, then three local reviewers in turn (Qwen3-4B, NuExtract 2.0 2B, Gemma 3 4B) with two-of-three agreement before automatic replacement. Experimental.' }
)

$stamp = (Get-Date).ToString('yyyy-MM-dd')
foreach ($a in $apps) {
    if ($App -ne 'Both' -and $App -ne $a.Key) { continue }
    $version = Get-ProjectVersion (Join-Path $root $a.Csproj)
    $text = @"
$($a.Name) $version
Generated $stamp from the project file. This file is regenerated for every release.

HOW TO START
  1. Open $($a.Exe) from this folder. No installation is needed.
  2. This is a self-contained build: you do NOT need to install .NET, Python or any model server.
  3. Keep the whole folder together (analyzer, model-packs and every DLL). Do not copy single files over it.
  4. If Windows blocks the app (SmartScreen, antivirus, AppLocker), see docs\TROUBLESHOOTING.md or ask IT to allow-list this folder.

WHAT THE APP DOES
  Replaces identifying values in a document with tokens such as [PERSON_001] before you send it to a cloud AI,
  and restores them in the returned file using the same encrypted project.
  Detectors: $($a.Layers)

WHAT STAYS OFFLINE
  Everything. No accounts, telemetry, cloud storage or model downloads. The language models run on this computer
  through a loopback-only process. Only the exported copy you choose to upload ever leaves it.

SUPPORTED FILES
  .docx, .xlsx, .pptx, .csv, .md and .txt. Old formats (.doc, .pdf, .xls, .ppt, .rtf, .odt) are not supported: save a copy as .docx, .xlsx or .pptx first. In Excel and PowerPoint files, pictures, charts, embedded objects and external links are removed.

BASIC STEPS
  Create a project (passphrase of 12 or more characters) > add the document > scan > give every finding Replace,
  Keep as-is or Review > Export desensitized copy > check the copy yourself > upload only that copy >
  Rehydrate the returned file in the same project.

WHERE YOUR DATA LIVES
  Settings and projects: $($a.Data) (projects are in the Projects subfolder).
  A project is an encrypted .dsproj file. It does not contain your original document, but it does map tokens back to
  real values. Older app versions cannot open a project after you accept the format upgrade; keep the .bak copy until verified.

NEVER SEND OR SHARE
  Project files (.dsproj), mapping lists, checkpoints, logs and diagnostics contain original confidential text.
  Do not send them with the document or zip the project folder.

LIMITS
  Images and embedded objects are replaced by placeholders and are not analyzed. AI findings are not calibrated
  probabilities. The tool reduces risk but does not guarantee anonymity. Always review before sending to a cloud AI.

MORE HELP
  docs\ANALYST-QUICKSTART.md, docs\TROUBLESHOOTING.md, docs\LICENSE-REVIEW.md and THIRD-PARTY-NOTICES.txt
  in the source repository (copy them next to this file if you share the folder).
"@
    $target = if ($Flat) { $OutDir } else { Join-Path $OutDir $a.Folder }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $path = Join-Path $target 'PORTABLE-START-HERE.txt'
    [IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $path (version $version)"
}
