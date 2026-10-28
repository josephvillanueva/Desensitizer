$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $root 'tools\DeploymentSafety.ps1')
$sandbox = Join-Path $root ('work\ReleaseSafety-20261010\DeploymentTests-' + [guid]::NewGuid().ToString('N'))
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
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}

$publish = Join-Path $sandbox 'publish'
$dest = Join-Path $sandbox 'installed'
$backup = Join-Path $sandbox 'backup'
Put (Join-Path $publish 'sample.dll') 'new app'
Put (Join-Path $publish 'new-runtime.dll') 'new runtime'
Put (Join-Path $publish 'licenses\sample.txt') 'new license'
Put (Join-Path $publish 'PORTABLE-START-HERE.txt') 'new help'
Put (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') 'new notice'
Put (Join-Path $publish 'model-packs\private.gguf') 'must not deploy'
Put (Join-Path $publish 'analyzer\worker.exe') 'must not deploy'
Put (Join-Path $publish 'settings.json') 'must not deploy'
Put (Join-Path $publish 'projects\sample.dsproj') 'must not deploy'
Put (Join-Path $dest 'sample.dll') 'old app'
Put (Join-Path $dest 'licenses\sample.txt') 'old license'
Put (Join-Path $dest 'model-packs\private.gguf') 'keep model'
Put (Join-Path $dest 'unrelated.txt') 'keep unrelated'
Put (Join-Path $dest 'settings.json') 'keep settings'
$plan = @(New-ReleaseFilePlan $publish $dest $backup)
Check 'plan includes documentation with app files' ($plan.Count -eq 5)
Check 'existing app backup captured before copy' ((Get-Content (Join-Path $backup 'sample.dll') -Raw) -eq 'old app')
Check 'existing license backup captured before copy' ((Get-Content (Join-Path $backup 'licenses\sample.txt') -Raw) -eq 'old license')
Reject 'injected copy failure is surfaced' { Install-ReleaseFilePlan $plan { param($entry) throw 'simulated fault' } }
Undo-ReleaseFilePlan $plan
Check 'partial copy rollback restores existing file or removes exact new file' ((Get-Content (Join-Path $dest 'sample.dll') -Raw) -eq 'old app' -and -not (Test-Path (Join-Path $dest 'new-runtime.dll')))
Install-ReleaseFilePlan $plan
Check 'all app and documentation hashes verified on install' (@($plan | Where-Object { (Get-FileHash -LiteralPath $_.Target).Hash -ne $_.Hash }).Count -eq 0)
Check 'models and settings untouched' ((Get-Content (Join-Path $dest 'model-packs\private.gguf') -Raw) -eq 'keep model' -and (Get-Content (Join-Path $dest 'settings.json') -Raw) -eq 'keep settings')
Undo-ReleaseFilePlan $plan
Check 'new runtime removed on full rollback' (-not (Test-Path (Join-Path $dest 'new-runtime.dll')))
Check 'new notices and help removed on rollback' (-not (Test-Path (Join-Path $dest 'THIRD-PARTY-NOTICES.txt')) -and -not (Test-Path (Join-Path $dest 'PORTABLE-START-HERE.txt')))
Check 'old license and app restored' ((Get-Content (Join-Path $dest 'licenses\sample.txt') -Raw) -eq 'old license' -and (Get-Content (Join-Path $dest 'sample.dll') -Raw) -eq 'old app')
Check 'unrelated file survives rollback' ((Get-Content (Join-Path $dest 'unrelated.txt') -Raw) -eq 'keep unrelated')
Reject 'fault after documentation copy is surfaced' { Install-ReleaseFilePlan $plan { param($entry) if ($entry.Relative -eq 'THIRD-PARTY-NOTICES.txt') { throw 'documentation fault' } } }
Undo-ReleaseFilePlan $plan
Check 'documentation failure rollback restores app and license' ((Get-Content (Join-Path $dest 'sample.dll') -Raw) -eq 'old app' -and (Get-Content (Join-Path $dest 'licenses\sample.txt') -Raw) -eq 'old license')
Put (Join-Path $publish 'sample.dll') 'tampered after staging'
Reject 'changed staged files rejected before copying' { Install-ReleaseFilePlan $plan }
Undo-ReleaseFilePlan $plan
Reject 'parent traversal rejected' { Get-ReleaseChildPath $dest '..\outside.txt' }
Reject 'absolute child path rejected' { Get-ReleaseChildPath $dest (Join-Path $sandbox 'outside.txt') }
$junctionTarget = Join-Path $sandbox 'junction-target'
New-Item -ItemType Directory -Path $junctionTarget | Out-Null
New-Item -ItemType Junction -Path (Join-Path $dest 'linked') -Target $junctionTarget | Out-Null
Reject 'junction escape rejected before exact rollback deletion' { Get-ReleaseChildPath $dest 'linked\outside.txt' }

$data = Join-Path $sandbox 'data'
$dataBackup = Join-Path $sandbox 'data-backup'
Put (Join-Path $data 'Fictional App\projects.json') 'legacy fictional catalog'
Backup-ReleaseUserData $data $dataBackup @('Fictional App')
# A simulated smoke launch migration takes place only after the helper returned its snapshot.
Put (Join-Path $data 'Fictional App\projects.json') 'migrated fictional catalog'
Check 'pre-launch user-data snapshot retains legacy content' ((Get-Content (Join-Path $dataBackup 'Fictional App\projects.json') -Raw) -eq 'legacy fictional catalog')

$expected = [pscustomobject]@{ Head = 'fictional-commit'; Status = ''; Fingerprint = 'first' }
Assert-ReleaseSourceState $expected { $expected }
Check 'stable source accepted' $true
Reject 'changed commit rejected' { Assert-ReleaseSourceState $expected { [pscustomobject]@{ Head='new'; Status=''; Fingerprint='first' } } }
Reject 'changed tracked cleanliness rejected' { Assert-ReleaseSourceState $expected { [pscustomobject]@{ Head='fictional-commit'; Status='dirty'; Fingerprint='first' } } }
Reject 'edit to already dirty source rejected' { Assert-ReleaseSourceState $expected { [pscustomobject]@{ Head='fictional-commit'; Status=''; Fingerprint='second' } } }

$scriptText = Get-Content (Join-Path $root 'tools\Publish-And-Deploy.ps1') -Raw
Check 'deployment backs up data before first staged smoke launch' ($scriptText.IndexOf('Backup-ReleaseUserData $env:LOCALAPPDATA') -lt $scriptText.IndexOf('if (-not (Test-AppLaunch'))
Check 'deployment documentation generation happens before file-plan enumeration' ($scriptText.IndexOf('-OutDir $publish') -lt $scriptText.IndexOf('New-ReleaseFilePlan $publish'))
Check 'failure message does not claim user data automatically rolled back' ($scriptText.Contains('User data was NOT automatically restored'))
"$script:passed passed, 0 failed"
