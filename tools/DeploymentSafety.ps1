# Shared release primitives. These functions do not launch applications or choose Desktop targets.
function Get-ReleaseSourceState {
    $head = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine release commit.' }
    $status = & git status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine tracked source state.' }
    $diff = & git diff --binary HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot fingerprint tracked changes.' }
    # Include content, not only status: editing an already-dirty file must invalidate the release too.
    $bytes = [Text.Encoding]::UTF8.GetBytes(($diff -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($bytes)) }
    finally { $sha.Dispose() }
    [pscustomobject]@{ Head = ($head -join '').Trim(); Status = ($status -join "`n"); Fingerprint = $fingerprint }
}

function Assert-ReleaseSourceState($Expected, [scriptblock]$ReadState = { Get-ReleaseSourceState }) {
    $actual = & $ReadState
    if ($actual.Head -ne $Expected.Head -or $actual.Status -ne $Expected.Status -or $actual.Fingerprint -ne $Expected.Fingerprint) {
        throw 'Source changed during verification. Nothing further will be installed; rerun from a stable source tree.'
    }
}

function Get-ReleaseChildPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) { throw 'Invalid release relative path.' }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $target.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release path escapes its target folder.'
    }
    # Reject junctions/symlinks at every existing ancestor, including the named target root.
    $cursor = $target
    while ($cursor -and $cursor.Length -ge $base.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Release paths must not traverse symbolic links or junctions.'
            }
        }
        if ($cursor -eq $base) { break }
        $cursor = Split-Path -Parent $cursor
    }
    $target
}

function Backup-ReleaseUserData([string]$DataRoot, [string]$BackupRoot, [string[]]$Names) {
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    foreach ($name in $Names) {
        $source = Get-ReleaseChildPath $DataRoot $name
        $destination = Get-ReleaseChildPath $BackupRoot $name
        if (Test-Path -LiteralPath $source) {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
                Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
            }
        }
    }
}

function New-ReleaseFilePlan([string]$Publish, [string]$Destination, [string]$Backup) {
    $publishRoot = [IO.Path]::GetFullPath($Publish).TrimEnd('\', '/')
    foreach ($file in Get-ChildItem -LiteralPath $publishRoot -File -Recurse -Force) {
        $relative = $file.FullName.Substring($publishRoot.Length + 1)
        $top = ($relative -split '[\\/]')[0]
        if ($top -in @('model-packs', 'analyzer', 'settings', 'projects') -or $file.Extension -eq '.dsproj' -or $relative -in @('settings.json', 'projects.json')) { continue }
        $source = Get-ReleaseChildPath $publishRoot $relative
        $target = Get-ReleaseChildPath $Destination $relative
        $saved = Get-ReleaseChildPath $Backup $relative
        if (Test-Path -LiteralPath $target -PathType Container) { throw "Release file conflicts with a folder: $relative" }
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        if ($existed) {
            New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
            Copy-Item -LiteralPath $target -Destination $saved -Force
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $saved).Hash) { throw "Backup verification failed: $relative" }
        }
        [pscustomobject]@{ Relative = $relative; Source = $source; Target = $target; Backup = $saved; DestinationRoot = $Destination; BackupRoot = $Backup; Existed = $existed; Modified = $false; Hash = (Get-FileHash -LiteralPath $source).Hash }
    }
}

function Install-ReleaseFilePlan($Plan, [scriptblock]$AfterCopy = {}) {
    foreach ($entry in $Plan) {
        $target = Get-ReleaseChildPath $entry.DestinationRoot $entry.Relative
        if ((Get-FileHash -LiteralPath $entry.Source).Hash -ne $entry.Hash) { throw "Staged file changed: $($entry.Relative)" }
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        $entry.Modified = $true # also roll back a copy which failed part-way through
        Copy-Item -LiteralPath $entry.Source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.Hash) { throw "Verification failed: $($entry.Relative)" }
        & $AfterCopy $entry
    }
}

function Undo-ReleaseFilePlan($Plan) {
    foreach ($entry in $Plan) {
        if (-not $entry.Modified) { continue }
        $target = Get-ReleaseChildPath $entry.DestinationRoot $entry.Relative
        if ($entry.Existed) {
            $saved = Get-ReleaseChildPath $entry.BackupRoot $entry.Relative
            Copy-Item -LiteralPath $saved -Destination $target -Force
            if ((Get-FileHash -LiteralPath $saved).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw "Rollback verification failed: $($entry.Relative)" }
        } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
        $entry.Modified = $false
    }
}
