# Interpret captured output only after the native process exit code has been saved.
# A positive-looking line is not proof that a build or test process completed.
function Get-CiCommandResult {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [ValidateSet('Build', 'Suite')][string]$Kind = 'Suite'
    )
    $failures = New-Object System.Collections.Generic.List[string]
    if ($ExitCode -ne 0) { $failures.Add("process exited with code $ExitCode") }
    $passes = 0
    if ($Kind -eq 'Build') {
        if ($Output -notmatch '(?m)^\s*0 Error\(s\)\s*$') {
            $failures.Add('build did not report zero errors')
        }
    } else {
        $passes = ([regex]::Matches($Output, '(?m)^PASS(?:\s|$)')).Count
        foreach ($line in [regex]::Matches($Output, '(?m)^(?:dotnet\.exe : )?FAIL.*')) {
            if (-not $failures.Contains($line.Value)) { $failures.Add($line.Value) }
        }
        # Some harnesses emit a summary rather than individual PASS lines.
        foreach ($summary in [regex]::Matches($Output, '(\d+) passed, (\d+) failed')) {
            if ($passes -eq 0) { $passes = [int]$summary.Groups[1].Value }
            if ([int]$summary.Groups[2].Value -gt 0) {
                $failures.Add("$($summary.Groups[2].Value) checks failed")
            }
        }
        if ($passes -eq 0) { $failures.Add('produced no passing checks (did it run?)') }
    }
    [pscustomobject]@{ Passed = $passes; Failures = @($failures.ToArray()); Success = $failures.Count -eq 0 }
}
