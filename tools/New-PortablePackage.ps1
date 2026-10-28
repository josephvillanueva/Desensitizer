<#
Creates a complete portable distribution, including its existing worker/model folders.
Only package a tested distribution folder, never the development repository.
Every archive entry is SHA256-verified and the source inventory is checked again before
the temporary archive receives its final name. Evidence stays beside the ZIP, not inside it.
Dot-source this file to use the helpers in fixture tests without running the command.
#>
[CmdletBinding()]
param([string]$SourceDir, [string]$OutputPath)

function Assert-PortablePath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Portable packaging does not allow symbolic links or junctions.'
            }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Assert-PortableFile([string]$Relative) {
    $parts = $Relative -split '/'
    $name = $parts[-1]
    $ext = [IO.Path]::GetExtension($name).ToLowerInvariant()
    $dependency = $parts.Count -gt 1 -and $parts[0] -in @('analyzer','model-packs')
    # CSV/TXT/JSON inside dependency packs can be vocabulary, tokenizer or model assets.
    # This is a distribution guard, not a confidential-content detector.
    if ($name -match '(?i)\.dsproj(?:\.|$)' -or $ext -in @('.doc','.docx','.xlsx','.xls','.ppt','.pptx','.pdf','.rtf','.odt','.log','.bak','.tmp') -or
        ($ext -eq '.csv' -and -not $dependency) -or
        $name -in @('settings.json','projects.json','keep-as-is-labels.json')) {
        throw 'Private documents, project data, settings or diagnostics are not distribution files.'
    }
    if ($parts.Count -eq 1) {
        if ($ext -in @('.exe','.dll','.pdb') -or $name -match '(?i)\.(deps|runtimeconfig)\.json$' -or
            $name -in @('runtime-manifest.json','PORTABLE-START-HERE.txt','START-HERE.txt','README.md','AI-ARCHITECTURE.md','THIRD-PARTY-NOTICES.txt','USER-ISSUES.md')) { return }
        throw 'Unknown file in the portable distribution root.'
    }
    $allowedFolders = @('analyzer','model-packs','licenses','docs','cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant','en','en-US')
    if ($parts[0] -notin $allowedFolders) { throw 'Unknown directory in the portable distribution root.' }
}

function Get-PortableInventory([string]$Root) {
    Assert-PortablePath $Root
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    $records = [Collections.Generic.List[object]]::new()
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Portable packaging does not allow symbolic links or junctions.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = $item.FullName.Substring($Root.Length + 1).Replace('\','/')
            Assert-PortableFile $relative
            $records.Add([pscustomobject]@{ Relative=$relative; Source=$item.FullName; Length=$item.Length; SHA256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash })
        }
    }
    @($records | Sort-Object Relative)
}

function Get-PortableStreamHash($Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-','') }
    finally { $sha.Dispose() }
}

function Assert-PortableArchive([string]$Path, [string]$FolderName, $Inventory) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -ne $Inventory.Count) { throw 'Portable archive entry count mismatch.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if (-not $seen.Add($entry.FullName)) { throw 'Portable archive contains duplicate paths.' }
        }
        foreach ($record in $Inventory) {
            $entry = $archive.GetEntry($FolderName + '/' + $record.Relative)
            if (-not $entry -or $entry.Length -ne $record.Length) { throw 'Portable archive entry mismatch.' }
            $input = $entry.Open()
            try { if ((Get-PortableStreamHash $input) -ne $record.SHA256) { throw 'Portable archive entry hash mismatch.' } }
            finally { $input.Dispose() }
        }
    } finally { $archive.Dispose() }
}

function New-PortablePackage([string]$SourceDir, [string]$OutputPath, [scriptblock]$AfterFile = {}) {
    $ErrorActionPreference = 'Stop'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\','/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Portable source must be a directory.' }
    $output = [IO.Path]::GetFullPath($OutputPath)
    if ([IO.Path]::GetExtension($output) -ne '.zip') { throw 'Portable output must have the .zip extension.' }
    if ($output.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be outside the source directory.' }
    Assert-PortablePath $output
    $sidecar = $output + '.evidence.json'
    if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $sidecar)) { throw 'Package or evidence already exists. Do not overwrite release evidence.' }
    $inventory = @(Get-PortableInventory $root)
    if (-not $inventory.Count) { throw 'Portable source is empty.' }
    $folderName = Split-Path -Leaf $root
    $prefix = if (Test-Path -LiteralPath (Join-Path $root 'AI.Desensitizer.exe')) { 'AI.Desensitizer' } else { 'Desensitizer' }
    foreach ($required in @("$prefix.exe","$prefix.dll","$prefix.deps.json","$prefix.runtimeconfig.json",'runtime-manifest.json')) {
        if ($required -notin $inventory.Relative) { throw 'Portable distribution is missing an application or runtime manifest file.' }
    }
    foreach ($dependencyRoot in @('analyzer/','model-packs/')) {
        if (-not @($inventory | Where-Object { $_.Relative.StartsWith($dependencyRoot,[StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'Complete portable distribution requires worker and model folders.' }
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    $token = [guid]::NewGuid().ToString('N')
    $temp = $output + '.' + $token + '.partial'
    $tempEvidence = $sidecar + '.' + $token + '.partial'
    $promoted = $false
    $evidencePromoted = $false
    try {
        $stream = [IO.File]::Open($temp,[IO.FileMode]::CreateNew)
        try {
            $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
            try {
                foreach ($record in $inventory) {
                    Assert-PortablePath $record.Source
                    $entry = $zip.CreateEntry($folderName + '/' + $record.Relative,[IO.Compression.CompressionLevel]::Fastest)
                    $input = [IO.File]::OpenRead($record.Source)
                    try {
                        $entryStream = $entry.Open()
                        try { $input.CopyTo($entryStream) } finally { $entryStream.Dispose() }
                    } finally { $input.Dispose() }
                    & $AfterFile $record | Out-Null
                }
            } finally { $zip.Dispose() }
        } finally { $stream.Dispose() }
        Assert-PortableArchive $temp $folderName $inventory
        $after = @(Get-PortableInventory $root)
        if ($after.Count -ne $inventory.Count) { throw 'Portable source inventory changed during packaging.' }
        for ($i=0; $i -lt $inventory.Count; $i++) {
            if ($after[$i].Relative -cne $inventory[$i].Relative -or $after[$i].SHA256 -ne $inventory[$i].SHA256 -or $after[$i].Length -ne $inventory[$i].Length) { throw 'Portable source changed during packaging.' }
        }
        $evidence = [pscustomobject]@{
            Kind='FullPortable'; SourceDir=$root; OutputPath=$output; CreatedUtc=[DateTime]::UtcNow.ToString('o')
            Files=$inventory.Count; Bytes=(Get-Item -LiteralPath $temp).Length; SHA256=(Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash
            Entries=@($inventory | ForEach-Object { [pscustomobject]@{Path=$_.Relative;Bytes=$_.Length;SHA256=$_.SHA256} })
        }
        [IO.File]::WriteAllText($tempEvidence,($evidence | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
        Assert-PortablePath $output
        [IO.File]::Move($temp,$output)
        $promoted = $true
        [IO.File]::Move($tempEvidence,$sidecar)
        $evidencePromoted = $true
        $evidence
    } catch {
        if ($promoted -and -not $evidencePromoted) { Remove-Item -LiteralPath $output -Force }
        throw
    } finally {
        # Only remove exact temporary paths created by this invocation, never recurse.
        foreach ($temporary in @($temp,$tempEvidence)) { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if (-not $SourceDir -or -not $OutputPath) { throw 'Specify -SourceDir and -OutputPath.' }
    New-PortablePackage -SourceDir $SourceDir -OutputPath $OutputPath
}
