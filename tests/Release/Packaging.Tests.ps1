$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $root 'tools\New-PortablePackage.ps1')
$sandbox = Join-Path $root ('work\PackagingTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$script:passed = 0
function Check([string]$Name, [bool]$Value) {
    if (-not $Value) { throw "FAIL $Name" }
    $script:passed++; "PASS $Name"
}
function Reject([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $Name $rejected
}
function Put([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))
}
function Fixture([string]$Name) {
    $folder = Join-Path $sandbox $Name
    foreach ($path in @('Desensitizer.exe','Desensitizer.dll','Desensitizer.deps.json','Desensitizer.runtimeconfig.json','runtime-manifest.json','analyzer/worker.exe','model-packs/example/weights.bin','model-packs/example/vocabulary.csv','licenses/NOTICE.txt','cs/System.resources.dll','README.md')) {
        Put (Join-Path $folder $path) ('fictional fixture ' + $path)
    }
    $folder
}

$source = Fixture 'Portable Fixture'
$output = Join-Path $sandbox 'valid.zip'
$result = New-PortablePackage $source $output
Check 'full package finalized' (Test-Path -LiteralPath $output)
Check 'private sidecar finalized separately' (Test-Path -LiteralPath ($output + '.evidence.json'))
Check 'package classified as full portable' ($result.Kind -eq 'FullPortable')
Check 'all fixture files included in evidence' ($result.Files -eq 11 -and $result.Entries.Count -eq 11)
Check 'archive checksum matches evidence' ($result.SHA256 -eq (Get-FileHash -LiteralPath $output).Hash)
$inventory = @(Get-PortableInventory $source)
Assert-PortableArchive $output 'Portable Fixture' $inventory
Check 'every archive entry hash verified' $true
Check 'dependency CSV asset retained' ('model-packs/example/vocabulary.csv' -in $result.Entries.Path)
Check 'temporary files absent after success' (@(Get-ChildItem -LiteralPath $sandbox -File | Where-Object Name -like '*.partial').Count -eq 0)
Reject 'existing package is not overwritten' { New-PortablePackage $source $output }
$reservedOutput = Join-Path $sandbox 'reserved.zip'
Put ($reservedOutput + '.evidence.json') 'existing private evidence'
Reject 'existing evidence is not overwritten' { New-PortablePackage $source $reservedOutput }
Check 'existing evidence preserved' ((Get-Content -LiteralPath ($reservedOutput + '.evidence.json') -Raw) -eq 'existing private evidence')
Reject 'package cannot be written inside source' { New-PortablePackage $source (Join-Path $source 'nested/out.zip') }
Reject 'non ZIP extension rejected' { New-PortablePackage $source (Join-Path $sandbox 'bad.bin') }

foreach ($privatePath in @('document.docx','financial.csv','settings.json','projects.json','keep-as-is-labels.json','analyzer/error.log','model-packs/private.dsproj.bak','model-packs/secret.docx')) {
    $privateSource = Fixture ('private-' + [guid]::NewGuid().ToString('N'))
    Put (Join-Path $privateSource $privatePath) 'fictional confidential fixture'
    Reject ('private distribution entry rejected: ' + $privatePath) { New-PortablePackage $privateSource (Join-Path $sandbox ([guid]::NewGuid().ToString('N') + '.zip')) }
}
Reject 'unknown root text refused rather than silently included' { Assert-PortableFile 'private.txt' }
Reject 'unknown directory refused' { Assert-PortableFile 'work/report.json' }
Assert-PortableFile 'model-packs/tokenizer/config.json'
Check 'legitimate dependency JSON asset allowed' $true

$incomplete = Fixture 'incomplete'
Remove-Item -LiteralPath (Join-Path $incomplete 'Desensitizer.runtimeconfig.json')
Reject 'incomplete app is not labeled full portable' { New-PortablePackage $incomplete (Join-Path $sandbox 'incomplete.zip') }
$appOnly = Join-Path $sandbox 'app-only'
New-Item -ItemType Directory -Path $appOnly | Out-Null
foreach ($name in @('Desensitizer.exe','Desensitizer.dll','Desensitizer.deps.json','Desensitizer.runtimeconfig.json','runtime-manifest.json')) { Put (Join-Path $appOnly $name) 'fictional' }
Reject 'app-only folder is not labeled full portable' { New-PortablePackage $appOnly (Join-Path $sandbox 'app-only.zip') }

$mutated = Fixture 'mutated'
$mutatedOutput = Join-Path $sandbox 'mutated.zip'
Reject 'non DLL source mutation detected' { New-PortablePackage $mutated $mutatedOutput { param($record) if ($record.Relative -eq 'README.md') { Put $record.Source 'changed after copy' } } }
Check 'failed mutation has no final ZIP' (-not (Test-Path -LiteralPath $mutatedOutput))
Check 'failed mutation has no sidecar' (-not (Test-Path -LiteralPath ($mutatedOutput + '.evidence.json')))

$added = Fixture 'added'
Reject 'new source entry detected during packaging' { New-PortablePackage $added (Join-Path $sandbox 'added.zip') { param($record) if ($record.Relative -eq 'README.md') { Put (Join-Path $added 'new.dll') 'added after inventory' } } }
$faulted = Fixture 'faulted'
Reject 'copy callback failure surfaced' { New-PortablePackage $faulted (Join-Path $sandbox 'faulted.zip') { throw 'fictional test fault' } }
Check 'temporary files cleaned after failed runs' (@(Get-ChildItem -LiteralPath $sandbox -File | Where-Object Name -like '*.partial').Count -eq 0)

# Replace a same-length non-DLL entry to prove count/length checks alone cannot pass.
$tampered = Join-Path $sandbox 'tampered.zip'
Copy-Item -LiteralPath $output -Destination $tampered
$archive = [IO.Compression.ZipFile]::Open($tampered,[IO.Compression.ZipArchiveMode]::Update)
try {
    $old = $archive.GetEntry('Portable Fixture/README.md')
    $length = [int]$old.Length
    $old.Delete()
    $entry = $archive.CreateEntry('Portable Fixture/README.md')
    $writer = $entry.Open()
    try { $bytes = [Text.Encoding]::UTF8.GetBytes(('x' * $length)); $writer.Write($bytes,0,$bytes.Length) } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
Reject 'same-size non DLL archive tampering rejected' { Assert-PortableArchive $tampered 'Portable Fixture' $inventory }
$duplicate = Join-Path $sandbox 'duplicate.zip'
Copy-Item -LiteralPath $output -Destination $duplicate
$archive = [IO.Compression.ZipFile]::Open($duplicate,[IO.Compression.ZipArchiveMode]::Update)
try {
    $archive.GetEntry('Portable Fixture/README.md').Delete()
    $null = $archive.CreateEntry('Portable Fixture/Desensitizer.dll')
} finally { $archive.Dispose() }
Reject 'duplicate archive paths rejected even with same entry count' { Assert-PortableArchive $duplicate 'Portable Fixture' $inventory }

$junctionSource = Fixture 'junction-source'
$junctionTarget = Join-Path $sandbox 'junction-target'
New-Item -ItemType Directory -Path $junctionTarget | Out-Null
New-Item -ItemType Junction -Path (Join-Path $junctionSource 'model-packs/linked') -Target $junctionTarget | Out-Null
Reject 'dependency directory junction rejected without following' { New-PortablePackage $junctionSource (Join-Path $sandbox 'junction.zip') }
$outputLink = Join-Path $sandbox 'output-link'
New-Item -ItemType Junction -Path $outputLink -Target $junctionTarget | Out-Null
Reject 'output parent junction rejected' { New-PortablePackage $source (Join-Path $outputLink 'output.zip') }
Reject 'linked source root rejected' { New-PortablePackage $outputLink (Join-Path $sandbox 'linked-root.zip') }

"$script:passed passed, 0 failed"
