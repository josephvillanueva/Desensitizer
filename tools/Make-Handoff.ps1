<#
.SYNOPSIS
  Makes a source-only handoff zip from the committed HEAD using git archive.
.DESCRIPTION
  Only committed files are included (source, docs, tools). The project folder itself must
  never be zipped because work\ and outputs\ hold original confidential text.
  After the archive is written, its listing is checked and the zip is DELETED and the script
  fails if it contains a .dsproj, a .gguf, or any path under work\ or outputs\.
.PARAMETER OutFile
  Path of the zip to create. Its folder must not be inside the repository.
.EXAMPLE
  tools\Make-Handoff.ps1 -OutFile D:\Handoff\desensitizer-source.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = [IO.Path]::GetFullPath($OutFile)

if ($out.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write the handoff zip inside the repository ($out). Choose a folder outside it."
}
$dir = Split-Path -Parent $out
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }

Push-Location $root
try {
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty) { Write-Warning 'Uncommitted changes exist and are NOT included: only HEAD is archived.' }
    git archive --format=zip --prefix=Desensitizer-source/ -o $out HEAD
    if ($LASTEXITCODE -ne 0) { throw "git archive failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($out)
try { $names = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }

$bad = @($names | Where-Object {
        $n = $_.ToLowerInvariant() -replace '\\', '/'
        $n -match '\.dsproj' -or $n -match '\.gguf$' -or $n -match '/work/' -or $n -match '/outputs/' -or $n -match '/model-packs/'
    })
if ($bad.Count -gt 0) {
    Remove-Item -LiteralPath $out -Force
    $bad | Select-Object -First 20 | ForEach-Object { Write-Host "  forbidden: $_" }
    throw "Handoff refused: the archive contained $($bad.Count) forbidden path(s) (.dsproj, .gguf, work\, outputs\ or model-packs\). The zip was deleted."
}

Write-Host ("Handoff archive written: {0} ({1} files, {2:N0} KB)" -f $out, $names.Count, ((Get-Item -LiteralPath $out).Length / 1KB))
