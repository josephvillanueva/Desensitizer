# Minimal local CI gate. Run from anywhere:  powershell -NoProfile -File tools\ci.ps1
# Builds both apps, runs every deterministic suite, and fails on any FAIL line,
# any build error, or any change the tests make to tracked files.
# Live-model checks are NOT run here; they are never part of this gate.
param(
    [string]$Dotnet = ''
)
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
. (Join-Path $PSScriptRoot 'CiResult.ps1')

function Find-Dotnet {
    if ($Dotnet) { return $Dotnet }
    if ($env:DESENSITIZER_DOTNET) { return $env:DESENSITIZER_DOTNET }
    $candidates = @(
        (Join-Path $root 'work\dotnet8\dotnet.exe'),
        (Join-Path $root '..\..\..\work\dotnet8\dotnet.exe'),   # when run from .claude\worktrees\<name>
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return (Resolve-Path $c).Path } }
    return 'dotnet'
}
$dotnet = Find-Dotnet
"Using SDK: $dotnet"

$before = (git status --porcelain --untracked-files=no) -join "`n"
$failed = New-Object System.Collections.Generic.List[string]
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add('NOT RUN: live-model accuracy, full real-document exports and target work-laptop certification (separate verification required).')

foreach ($project in 'Desensitizer\Desensitizer.csproj', 'AI.Desensitizer\Desensitizer.csproj') {
    $out = & $dotnet build $project -c Release --nologo -v q 2>&1 | Out-String
    $commandExit = $LASTEXITCODE
    $result = Get-CiCommandResult -Output $out -ExitCode $commandExit -Kind Build
    if ($result.Success) { $summary.Add("build OK   $project") }
    else { $summary.Add("build FAIL $project"); foreach ($failure in $result.Failures) { $failed.Add("build $project : $failure") } }
}

$suites = @(
    'tests\DocumentProcessing\Regular\DocumentProcessing.csproj',
    'tests\DocumentProcessing\AI\DocumentProcessing.csproj'
)
foreach ($extra in 'tests\RuntimeSecurity', 'tests\ProjectStore', 'tests\UiLogic', 'tests\Perf', 'tests\Formats', 'tests\Support', 'tests\Detection2', 'tests\ReviewPrivacy') {
    foreach ($proj in Get-ChildItem (Join-Path $root $extra) -Recurse -Filter *.csproj -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
        $suites += $proj.FullName.Substring($root.Length + 1)
    }
}
foreach ($suite in $suites) {
    $out = & $dotnet run --project $suite -c Release 2>&1 | Out-String
    $commandExit = $LASTEXITCODE
    $result = Get-CiCommandResult -Output $out -ExitCode $commandExit
    $pass = $result.Passed
    $fails = $result.Failures
    $summary.Add(("suite {0}: PASS={1} FAIL={2}" -f $suite, $pass, $fails.Count))
    foreach ($f in $fails) { $summary.Add("   $f"); $failed.Add("$suite : $f") }
}

foreach ($scriptTest in Get-ChildItem (Join-Path $root 'tests\Release') -Filter '*.Tests.ps1' -ErrorAction SilentlyContinue) {
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptTest.FullName 2>&1 | Out-String
    $commandExit = $LASTEXITCODE
    $result = Get-CiCommandResult -Output $out -ExitCode $commandExit
    $summary.Add("suite $($scriptTest.Name): PASS=$($result.Passed) FAIL=$($result.Failures.Count)")
    foreach ($failure in $result.Failures) { $failed.Add("$($scriptTest.Name) : $failure") }
}

# Full-value attacks across fixture formats.
$out = & $dotnet run --project 'tests\RedTeam\RedTeam.csproj' -c Release -- --full-value 2>&1 | Out-String
$commandExit = $LASTEXITCODE
$result = Get-CiCommandResult -Output $out -ExitCode $commandExit
$summary.Add("suite RedTeam full-value fixture formats: PASS=$($result.Passed) FAIL=$($result.Failures.Count)")
foreach ($failure in $result.Failures) { $failed.Add("RedTeam full-value fixture formats : $failure") }

# The default adversarial audit (all attacks, including the shortened-name cases blocked by the fragment guard) must exit 0.
$out = & $dotnet run --project 'tests\RedTeam\RedTeam.csproj' -c Release 2>&1 | Out-String
$commandExit = $LASTEXITCODE
$result = Get-CiCommandResult -Output $out -ExitCode $commandExit
$summary.Add("suite RedTeam default audit: PASS=$($result.Passed) FAIL=$($result.Failures.Count)")
foreach ($failure in $result.Failures) { $failed.Add("RedTeam default audit : $failure") }

$py = Get-Command python -ErrorAction SilentlyContinue
$test = Join-Path $root 'Desensitizer.Analyzer\test_analyzer.py'
if ($py -and (Test-Path $test)) {
    Push-Location (Join-Path $root 'Desensitizer.Analyzer')
    $pyOut = & python -I -m unittest discover -s . -p 'test_*.py' 2>&1 | Out-String
    $pyExit = $LASTEXITCODE
    Pop-Location
    if ($pyExit -eq 0) { $summary.Add('python unittest OK') } else { $summary.Add('python unittest FAIL'); $failed.Add('python unittest') }
} else { $summary.Add('python unittest SKIPPED (python or test file not found)') }

$after = (git status --porcelain --untracked-files=no) -join "`n"
if ($before -ne $after) { $summary.Add('tracked files CHANGED by the test run'); $failed.Add('tests modified tracked files') }

$summary | ForEach-Object { $_ }
if ($failed.Count -gt 0) { "`nCI FAILED:"; $failed | ForEach-Object { "  $_" }; exit 1 }
"`nCI PASSED"
exit 0
