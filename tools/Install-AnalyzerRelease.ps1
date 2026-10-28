<# Install the three smoke-tested worker folders, never their model data.
   Dot-source for fixture tests. The CLI refuses running apps/workers and dirty source.
   Rollback covers copied workers and regenerated runtime manifests in both apps.
#>
[CmdletBinding()]
param([string]$StageDir,
      [string]$RegularDesktop=(Join-Path ([Environment]::GetFolderPath('Desktop')) 'Desensitizer Portable Latest'),
      [string]$AiDesktop=(Join-Path ([Environment]::GetFolderPath('Desktop')) 'AI Desensitizer Portable Latest'))
. (Join-Path $PSScriptRoot 'DeploymentSafety.ps1')

# Reading Git state for every dependency dominates a large worker deployment.
# Check at bounded intervals, then always validate again before completion.
# Every individual source/destination file still receives its SHA256 checks.
function Assert-AnalyzerSourceProgress($Expected,$Progress,[scriptblock]$ReadState={Get-ReleaseSourceState},[datetime]$Now=[datetime]::UtcNow) {
    if($Progress.LastCheck -eq [datetime]::MinValue -or ($Now-$Progress.LastCheck).TotalSeconds -ge 2) {
        Assert-ReleaseSourceState $Expected $ReadState
        $Progress.LastCheck=$Now
    }
}

function New-AnalyzerReleasePlan([string]$Stage,[string[]]$Destinations,[string]$BackupRoot) {
    $ErrorActionPreference='Stop'
    $stageRoot=(Resolve-Path -LiteralPath $Stage).Path
    $summary=Get-Content -LiteralPath (Join-Path $stageRoot 'summary.json') -Raw | ConvertFrom-Json
    $targets=@(
        @{Name='DesensitizerAnalyzer';Relative='analyzer'},
        @{Name='PrivacyFilterAnalyzer';Relative='model-packs\privacy-filter'},
        @{Name='BusinessContextAnalyzer';Relative='model-packs\business-context'}
    )
    if(@($summary).Count -ne 3) { throw 'All three workers must be rebuilt and smoke-tested together.' }
    $plans=@()
    $appIndex=0
    foreach($destination in $Destinations) {
        $desktop=(Resolve-Path -LiteralPath $destination).Path
        foreach($target in $targets) {
            $record=@($summary | Where-Object Name -eq $target.Name)
            if($record.Count -ne 1 -or $record[0].Smoke -ne 'passed') { throw 'Analyzer smoke checks are missing or incomplete.' }
            $source=Get-ReleaseChildPath $stageRoot ("dist\{0}\{0}" -f $target.Name)
            $exe=Get-ReleaseChildPath $source ($target.Name+'.exe')
            if((Get-FileHash -LiteralPath $exe).Hash -ne $record[0].ExeSha256) { throw 'Staged analyzer executable differs from its smoke-tested hash.' }
            foreach($item in Get-ChildItem -LiteralPath $source -Force) {
                if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Analyzer staging must not contain links.' }
                if($item.Name -notin @(($target.Name+'.exe'),'_internal')) { throw 'Analyzer staging contains unexpected files or model data.' }
            }
            $workerDestination=Get-ReleaseChildPath $desktop $target.Relative
            $backup=Get-ReleaseChildPath $BackupRoot ("App$appIndex\"+$target.Relative)
            $plans+=@(New-ReleaseFilePlan $source $workerDestination $backup)
        }
        $appIndex++
    }
    $plans
}

function Install-AnalyzerRelease([string]$Stage,[string[]]$Destinations,[string]$BackupRoot,[scriptblock]$AfterCopy={},[scriptblock]$BeforeComplete={}) {
    $ErrorActionPreference='Stop'
    $plan=@(New-AnalyzerReleasePlan $Stage $Destinations $BackupRoot)
    $manifests=@()
    $index=0
    foreach($destination in $Destinations) {
        $root=(Resolve-Path -LiteralPath $destination).Path
        $path=Get-ReleaseChildPath $root 'runtime-manifest.json'
        $saved=Get-ReleaseChildPath $BackupRoot "App$index\runtime-manifest.json"
        $existed=Test-Path -LiteralPath $path -PathType Leaf
        if($existed) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $path -Destination $saved
            if((Get-FileHash $path).Hash -ne (Get-FileHash $saved).Hash) { throw 'Runtime manifest backup verification failed.' }
        }
        $manifests+=@([pscustomobject]@{Root=$root;Path=$path;Backup=$saved;Existed=$existed;Modified=$false})
        $index++
    }
    try {
        Install-ReleaseFilePlan $plan $AfterCopy
        foreach($manifest in $manifests) {
            $manifest.Modified=$true
            & (Join-Path $PSScriptRoot 'New-RuntimeManifest.ps1') -AppFolder $manifest.Root | Out-Null
        }
        & $BeforeComplete | Out-Null
        [pscustomobject]@{Verified=$true;Workers=@($plan | Select-Object Target,Hash);Manifests=@($manifests | ForEach-Object {[pscustomobject]@{Path=$_.Path;Hash=(Get-FileHash $_.Path).Hash}});BackupRoot=$BackupRoot}
    } catch {
        $failure=$_
        # Roll back every modified file, not only the worker which failed.
        Undo-ReleaseFilePlan $plan
        foreach($manifest in $manifests) {
            if(-not $manifest.Modified) { continue }
            if($manifest.Existed) {
                Copy-Item -LiteralPath $manifest.Backup -Destination $manifest.Path -Force
                if((Get-FileHash $manifest.Path).Hash -ne (Get-FileHash $manifest.Backup).Hash) { throw 'Runtime manifest rollback verification failed.' }
            } elseif(Test-Path -LiteralPath $manifest.Path -PathType Leaf) { Remove-Item -LiteralPath $manifest.Path -Force }
        }
        throw $failure
    }
}

if($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference='Stop'
    if(-not $StageDir) { throw 'Specify the smoke-tested StageDir.' }
    foreach($name in @('Desensitizer','AI.Desensitizer','DesensitizerAnalyzer','PrivacyFilterAnalyzer','BusinessContextAnalyzer')) {
        if(Get-Process -Name $name -ErrorAction SilentlyContinue) { throw 'Close both apps and their workers before updating analyzers.' }
    }
    $state=Get-ReleaseSourceState
    if($state.Status) { throw 'Commit the verified source before installing analyzers.' }
    $repo=Split-Path -Parent $PSScriptRoot
    $backup=Join-Path $repo ('work\backups\analyzers-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    Assert-ReleaseSourceState $state
    $progress=[pscustomobject]@{LastCheck=[datetime]::MinValue}
    $result=Install-AnalyzerRelease $StageDir @($RegularDesktop,$AiDesktop) $backup { Assert-AnalyzerSourceProgress $state $progress } { Assert-ReleaseSourceState $state }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($backup+'.json') -Encoding utf8
    $result
}
