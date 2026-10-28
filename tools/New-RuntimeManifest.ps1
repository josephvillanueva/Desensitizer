<#
Creates runtime-manifest.json beside a built or packaged app folder.

The manifest lists the SHA-256 of every .exe, .dll and .gguf in the subfolders of the app folder
(analyzer, model-packs). The app's own top-level files are not listed. The app refuses to start a
worker whose hash differs from the manifest (see Shared/RuntimeIntegrity.cs). Nothing is downloaded:
only files that already exist in the folder are hashed. Run this at the end of packaging, after the
models and executables are in place.

  powershell -File tools/New-RuntimeManifest.ps1 -AppFolder "path\to\portable folder"
#>
param([Parameter(Mandatory=$true)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $AppFolder).Path.TrimEnd('\')
$files = [ordered]@{}
Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.DirectoryName.TrimEnd('\') -ne $root -and @('.exe', '.dll', '.gguf') -contains $_.Extension.ToLowerInvariant() } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
if ($files.Count -eq 0) { throw 'No worker files found under the app folder; no manifest was written.' }
$json = [ordered]@{ version = 1; files = $files } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $root 'runtime-manifest.json'), $json, (New-Object Text.UTF8Encoding($false)))
"Wrote runtime-manifest.json with $($files.Count) entries."
