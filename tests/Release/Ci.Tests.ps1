$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\tools\CiResult.ps1')
$script:count = 0
function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL $Name" }
    $script:count++
    Write-Output "PASS $Name"
}
Check 'successful suite' (Get-CiCommandResult "PASS sample" 0).Success
Check 'PASS then nonzero exit fails' (-not (Get-CiCommandResult "PASS sample" 7).Success)
Check 'zero exit with FAIL fails' (-not (Get-CiCommandResult "PASS sample`nFAIL second" 0).Success)
Check 'zero exit without tests fails' (-not (Get-CiCommandResult 'no checks' 0).Success)
Check 'summary-only suite succeeds' (Get-CiCommandResult '12 passed, 0 failed' 0).Success
Check 'summary failure with PASS lines fails' (-not (Get-CiCommandResult "PASS sample`n12 passed, 1 failed" 0).Success)
Check 'summary-only nonzero exit fails' (-not (Get-CiCommandResult '12 passed, 0 failed' 2).Success)
Check 'build success' (Get-CiCommandResult '    0 Error(s)' 0 Build).Success
Check 'build zero errors but failed process fails' (-not (Get-CiCommandResult '    0 Error(s)' 1 Build).Success)
Check 'build without success summary fails' (-not (Get-CiCommandResult 'unhandled exception' 0 Build).Success)
Check 'FAIL with PowerShell native prefix fails' (-not (Get-CiCommandResult "PASS sample`ndotnet.exe : FAIL second" 0).Success)
Check 'PASSING is not a passing check' (-not (Get-CiCommandResult 'PASSING' 0).Success)
# Exercise the real native exit-code capture order used by ci.ps1.
$captured = & powershell -NoProfile -Command 'Write-Output "PASS fictional check"; exit 7' | Out-String
$nativeExit = $LASTEXITCODE
Check 'native runner late failure retained' ($nativeExit -eq 7 -and -not (Get-CiCommandResult $captured $nativeExit).Success)
Write-Output "$script:count passed, 0 failed"
exit 0
