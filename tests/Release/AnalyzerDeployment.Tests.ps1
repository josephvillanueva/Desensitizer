$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $root 'tools\Install-AnalyzerRelease.ps1')
$sandbox=Join-Path $root ('work\AnalyzerDeploymentTests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$passed=0
function Check($Name,$Condition) { if(-not $Condition) { throw "FAIL $Name" }; $script:passed++; "PASS $Name" }
function Put($Path,$Value) { New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null; [IO.File]::WriteAllText($Path,$Value) }
function Reject($Name,$Action) { $bad=$false; try { & $Action | Out-Null } catch { $bad=$true }; Check $Name $bad }
$stage=Join-Path $sandbox 'stage'
$summary=@()
foreach($name in @('DesensitizerAnalyzer','PrivacyFilterAnalyzer','BusinessContextAnalyzer')) {
    $folder=Join-Path $stage ("dist\$name\$name")
    Put (Join-Path $folder ($name+'.exe')) ('new '+$name)
    Put (Join-Path $folder '_internal\dependency.dll') ('new dependency '+$name)
    $summary+=@([pscustomobject]@{Name=$name;Smoke='passed';ExeSha256=(Get-FileHash (Join-Path $folder ($name+'.exe'))).Hash})
}
Put (Join-Path $stage 'summary.json') ($summary | ConvertTo-Json)
$apps=@((Join-Path $sandbox 'Regular'),(Join-Path $sandbox 'AI'))
foreach($app in $apps) {
    Put (Join-Path $app 'analyzer\DesensitizerAnalyzer.exe') 'old presidio'
    Put (Join-Path $app 'model-packs\privacy-filter\PrivacyFilterAnalyzer.exe') 'old privacy'
    Put (Join-Path $app 'model-packs\business-context\BusinessContextAnalyzer.exe') 'old business'
    Put (Join-Path $app 'model-packs\privacy-filter\model\weights.bin') 'keep model'
    Put (Join-Path $app 'runtime-manifest.json') 'old manifest'
}
$backup=Join-Path $sandbox 'backup-fault'
$script:copiesReached=0
Reject 'failed copy is rolled back across both apps' { Install-AnalyzerRelease $stage $apps $backup { $script:copiesReached++; if($script:copiesReached -eq 12) { throw 'fixture copy fault' } } }
Check 'fault injection reached last copy in second app' ($script:copiesReached -eq 12)
foreach($app in $apps) {
    Check 'old worker retained after rollback' ((Get-Content (Join-Path $app 'analyzer\DesensitizerAnalyzer.exe') -Raw) -eq 'old presidio')
    Check 'old manifest retained after rollback' ((Get-Content (Join-Path $app 'runtime-manifest.json') -Raw) -eq 'old manifest')
    Check 'old privacy worker retained after rollback' ((Get-Content (Join-Path $app 'model-packs\privacy-filter\PrivacyFilterAnalyzer.exe') -Raw) -eq 'old privacy')
    Check 'old business worker retained after rollback' ((Get-Content (Join-Path $app 'model-packs\business-context\BusinessContextAnalyzer.exe') -Raw) -eq 'old business')
    Check 'new dependency removed after rollback' (-not (Test-Path (Join-Path $app 'analyzer\_internal\dependency.dll')))
}
Reject 'failure after manifest generation rolls back the complete worker transaction' { Install-AnalyzerRelease $stage $apps (Join-Path $sandbox 'backup-final-fault') {} { throw 'source state changed fixture' } }
foreach($app in $apps) {
    Check 'manifest restored after final validation failure' ((Get-Content (Join-Path $app 'runtime-manifest.json') -Raw) -eq 'old manifest')
    Check 'worker restored after final validation failure' ((Get-Content (Join-Path $app 'analyzer\DesensitizerAnalyzer.exe') -Raw) -eq 'old presidio')
}
$result=Install-AnalyzerRelease $stage $apps (Join-Path $sandbox 'backup-good')
Check 'all worker files verified' ($result.Verified -and $result.Workers.Count -eq 12)
Check 'both manifests regenerated' ($result.Manifests.Count -eq 2)
foreach($app in $apps) {
    Check 'model weights unchanged' ((Get-Content (Join-Path $app 'model-packs\privacy-filter\model\weights.bin') -Raw) -eq 'keep model')
    Check 'worker replacement completed' ((Get-Content (Join-Path $app 'analyzer\DesensitizerAnalyzer.exe') -Raw) -eq 'new DesensitizerAnalyzer')
    $manifest=Get-Content (Join-Path $app 'runtime-manifest.json') -Raw | ConvertFrom-Json
    Check 'runtime manifest protects new executable' ($manifest.files.'analyzer/DesensitizerAnalyzer.exe' -eq (Get-FileHash (Join-Path $app 'analyzer\DesensitizerAnalyzer.exe')).Hash.ToLowerInvariant())
}
$exe=Join-Path $stage 'dist\DesensitizerAnalyzer\DesensitizerAnalyzer\DesensitizerAnalyzer.exe'
Put $exe 'tampered'
Reject 'staged executable must match smoke-tested hash' { New-AnalyzerReleasePlan $stage $apps (Join-Path $sandbox 'backup-bad') }
Put $exe 'new DesensitizerAnalyzer'
Put (Join-Path $stage 'dist\DesensitizerAnalyzer\DesensitizerAnalyzer\model\weights.bin') 'must not install model'
Reject 'staged model data is never deployed' { New-AnalyzerReleasePlan $stage $apps (Join-Path $sandbox 'backup-model') }
$expectedState=[pscustomobject]@{Head='stable';Status='';Fingerprint='stable'}
$script:stateReads=0
$readState={ $script:stateReads++; $expectedState }
$progress=[pscustomobject]@{LastCheck=[datetime]::MinValue}
$startTime=[datetime]'2026-10-11T00:00:00Z'
Assert-AnalyzerSourceProgress $expectedState $progress $readState $startTime
Check 'first source check is immediate' ($script:stateReads -eq 1)
1..100 | ForEach-Object { Assert-AnalyzerSourceProgress $expectedState $progress $readState ($startTime.AddSeconds(1)) }
Check 'rapid copies do not repeat repository checks' ($script:stateReads -eq 1)
Assert-AnalyzerSourceProgress $expectedState $progress $readState ($startTime.AddSeconds(2))
Check 'source validation repeats after bounded interval' ($script:stateReads -eq 2)
$changedState={ [pscustomobject]@{Head='changed';Status='';Fingerprint='stable'} }
Reject 'source drift is rejected at next interval' { Assert-AnalyzerSourceProgress $expectedState $progress $changedState ($startTime.AddSeconds(4)) }
Reject 'final source validation catches drift even inside interval' { Assert-ReleaseSourceState $expectedState $changedState }
"$passed passed, 0 failed"
