# Publishes the two apps from the current source and installs them into the Desktop portable folders.
# Safe by design:
#   - refuses to run on a dirty tree (tracked changes) unless -AllowDirty
#   - runs tools\ci.ps1 first unless -SkipCi
#   - refuses if an app is running
#   - backs up EVERY file it replaces, verifies a hash for EVERY file it copies, and rolls back on failure
#   - never touches model-packs, analyzer, settings or projects
# Example:  powershell -NoProfile -File tools\Publish-And-Deploy.ps1 -App Both
param(
    [ValidateSet('Regular', 'AI', 'Both')][string]$App = 'Both',
    [switch]$SkipCi,
    [switch]$AllowDirty,
    [string]$Dotnet = '',
    [string]$RegularDesktop = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desensitizer Portable Latest'),
    [string]$AiDesktop = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'AI Desensitizer Portable Latest')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
. (Join-Path $PSScriptRoot 'DeploymentSafety.ps1')

function Find-Dotnet {
    if ($Dotnet) { return $Dotnet }
    if ($env:DESENSITIZER_DOTNET) { return $env:DESENSITIZER_DOTNET }
    foreach ($c in @((Join-Path $root 'work\dotnet8\dotnet.exe'), (Join-Path $root '..\..\..\work\dotnet8\dotnet.exe'),
                     (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))) {
        if (Test-Path -LiteralPath $c) { return (Resolve-Path $c).Path }
    }
    return 'dotnet'
}
$dotnet = Find-Dotnet

$sourceState = Get-ReleaseSourceState
if (-not $AllowDirty -and $sourceState.Status) { throw 'The working tree has uncommitted tracked changes. Commit them first (or pass -AllowDirty).' }
$commit = $sourceState.Head

if (-not $SkipCi) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'ci.ps1') -Dotnet $dotnet
    if ($LASTEXITCODE -ne 0) { throw 'tools\ci.ps1 failed. Nothing was installed.' }
}
Assert-ReleaseSourceState $sourceState

# Launch the exe, confirm the process stays alive with a real window (the .NET host failure dialog is titled
# like the exe, e.g. "Desensitizer.exe"), then close it. Returns $true only for a real running window.
function Test-AppLaunch([string]$exe) {
    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    Start-Sleep -Seconds 12
    $proc.Refresh()
    $ok = (-not $proc.HasExited) -and ($proc.MainWindowTitle -ne '') -and ($proc.MainWindowTitle -notmatch '\.exe$')
    if (-not $proc.HasExited) {
        $null = $proc.CloseMainWindow(); Start-Sleep -Seconds 3
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
    return $ok
}
$all = @(
    @{ Name = 'Regular'; Prefix = 'Desensitizer';    Project = 'Desensitizer\Desensitizer.csproj';    Desktop = $RegularDesktop },
    @{ Name = 'AI';      Prefix = 'AI.Desensitizer'; Project = 'AI.Desensitizer\Desensitizer.csproj'; Desktop = $AiDesktop }
)
$targets = $all | Where-Object { $App -eq 'Both' -or $_.Name -eq $App }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$releaseRoot = Join-Path $root "work\release-$stamp"
# Refuse running applications before backing up data and again immediately before copying files.
foreach ($t in $targets) {
    if (Get-Process -Name $t.Prefix -ErrorAction SilentlyContinue) { throw "Please close $($t.Prefix) before updating." }
}
# Staged launches use the real profile too, so this must precede the FIRST launch, not just installation.
$userDataBackup = Join-Path $root "work\backups\userdata-$stamp"
$dataNames = @($targets | ForEach-Object { if ($_.Name -eq 'AI') { 'AI Desensitizer' } else { 'Desensitizer' } })
Backup-ReleaseUserData $env:LOCALAPPDATA $userDataBackup $dataNames
"User data backed up before smoke tests to: $userDataBackup"
$prepared = @()
try {
foreach ($t in $targets) {
    $dest = (Resolve-Path -LiteralPath $t.Desktop).Path
    if (Get-Process -Name $t.Prefix -ErrorAction SilentlyContinue) { throw "Please close $($t.Prefix) before updating." }
    [xml]$xml = Get-Content -LiteralPath (Join-Path $root $t.Project)
    $version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $version) { throw "No <Version> in $($t.Project)." }
    $publish = Join-Path $releaseRoot $t.Name
    & $dotnet publish (Join-Path $root $t.Project) -c Release -r win-x64 --self-contained true -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($t.Name)." }
    Assert-ReleaseSourceState $sourceState
    $dll = Join-Path $publish ($t.Prefix + '.dll')
    $built = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
    if (-not $built.StartsWith($version)) { throw "Published version '$built' does not match csproj '$version'." }
    foreach ($suffix in '.exe', '.dll', '.deps.json', '.runtimeconfig.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $publish ($t.Prefix + $suffix)))) { throw "Published file missing: $($t.Prefix)$suffix" }
    }
    # Documentation is staged BEFORE building the transactional file plan, so it is backed up and hashed too.
    $notices = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $notices) { Copy-Item -LiteralPath $notices -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -Force }
    $licences = Join-Path $root 'docs\licenses'
    if (Test-Path -LiteralPath $licences) {
        $licenseTarget = Join-Path $publish 'licenses'
        New-Item -ItemType Directory -Force -Path $licenseTarget | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $licences -Force) { Copy-Item -LiteralPath $item.FullName -Destination $licenseTarget -Recurse -Force }
    }
    $startApp = if ($t.Name -eq 'AI') { 'AI' } else { 'Desensitizer' }
    & (Join-Path $PSScriptRoot 'New-StartHere.ps1') -OutDir $publish -App $startApp -Flat | Out-Null
    Assert-ReleaseSourceState $sourceState
    if (-not (Test-AppLaunch (Join-Path $publish ($t.Prefix + '.exe')))) { throw "The published $($t.Name) app did not start from its publish folder. No app files were installed." }
    $backup = Join-Path $root "work\backups\$($t.Prefix)-$stamp"
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $plan = @(New-ReleaseFilePlan $publish $dest $backup)
    $prepared += @{ T = $t; Dest = $dest; Publish = $publish; Plan = $plan; Backup = $backup; Version = $version }
}
    $records = @()
    foreach ($p in $prepared) {
        Assert-ReleaseSourceState $sourceState
        foreach ($t in $targets) {
            if (Get-Process -Name $t.Prefix -ErrorAction SilentlyContinue) { throw "Please close $($t.Prefix) before updating." }
        }
        Install-ReleaseFilePlan $p.Plan
        if (-not (Test-AppLaunch (Join-Path $p.Dest ($p.T.Prefix + '.exe')))) { throw "$($p.T.Prefix) did not start after installation." }
        Assert-ReleaseSourceState $sourceState
        $records += [pscustomobject]@{
            App = $p.T.Prefix; Version = $p.Version; Commit = $commit; Desktop = $p.Dest; Backup = $p.Backup
            UserDataBackup = $userDataBackup; SourceDirty = [bool]$sourceState.Status; SourceFingerprint = $sourceState.Fingerprint; CiRun = -not [bool]$SkipCi
            FilesCopied = $p.Plan.Count
            FileHashes = @($p.Plan | ForEach-Object { [pscustomobject]@{ Path = $_.Relative; Sha256 = $_.Hash } })
            DllSha256 = (Get-FileHash -LiteralPath (Join-Path $p.Dest ($p.T.Prefix + '.dll'))).Hash
            Verified = $true
        }
    }
    $recordPath = Join-Path $root "work\backups\deployment-$stamp.json"
    $records | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $recordPath -Encoding utf8
    $records | Format-List
    "Deployment record: $recordPath"
}
catch {
    $failure = $_.Exception.Message
    $rollbackErrors = @()
    foreach ($p in $prepared) {
        try { Undo-ReleaseFilePlan $p.Plan } catch { $rollbackErrors += $_.Exception.Message }
    }
    $rollback = if ($rollbackErrors.Count) { 'App-file rollback incomplete: ' + ($rollbackErrors -join '; ') } else { 'Any copied app files were rolled back.' }
    throw "Release failed: $failure $rollback User data was NOT automatically restored; smoke launches may have migrated it. Pre-launch backup: $userDataBackup"
}
