# Samples the total working set of an app process and all of its descendants
# (the local workers: analyzers, llama-server) once per interval, writes a CSV
# and a one-line peak summary. Numbers only: no document text, no command lines,
# no file names are recorded.
#
# Usage (start the app first, then run this beside it):
#   powershell -NoProfile -File tools\Measure-PeakMemory.ps1 -ProcessName Desensitizer
#   powershell -NoProfile -File tools\Measure-PeakMemory.ps1 -ProcessId 1234 -OutDir C:\temp\mem
# Stops when the root process exits, or after -DurationSeconds (0 = until exit).
# Same CSV layout as Shared\PerfMemoryLog.cs: elapsed_seconds,total_working_set_mb,process_count
param(
    [int]$ProcessId = 0,
    [string]$ProcessName = '',
    [double]$IntervalSeconds = 1,
    [int]$DurationSeconds = 0,
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
if ($ProcessId -le 0 -and -not $ProcessName) { throw 'Give -ProcessId or -ProcessName.' }
if ($IntervalSeconds -lt 0.2) { $IntervalSeconds = 0.2 }
if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) 'desensitizer-memory' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($ProcessId -le 0) {
    $found = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { throw "No running process named $ProcessName." }
    $ProcessId = $found[0].Id
}

# Total working set (bytes) and process count of the root plus every descendant.
function Get-TreeSample([int]$rootId) {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, WorkingSetSize)
    $children = @{}
    foreach ($p in $all) {
        $parent = [int]$p.ParentProcessId
        if (-not $children.ContainsKey($parent)) { $children[$parent] = New-Object System.Collections.Generic.List[object] }
        $children[$parent].Add($p)
    }
    $byId = @{}
    foreach ($p in $all) { $byId[[int]$p.ProcessId] = $p }
    if (-not $byId.ContainsKey($rootId)) { return $null }
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $queue = New-Object System.Collections.Generic.Queue[int]
    [void]$seen.Add($rootId); $queue.Enqueue($rootId)
    $bytes = [int64]0
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        $bytes += [int64]$byId[$id].WorkingSetSize
        if ($children.ContainsKey($id)) {
            foreach ($c in $children[$id]) { if ($seen.Add([int]$c.ProcessId)) { $queue.Enqueue([int]$c.ProcessId) } }
        }
    }
    [pscustomobject]@{ Bytes = $bytes; Count = $seen.Count }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csvPath = Join-Path $OutDir "peak-memory-$stamp.csv"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('elapsed_seconds,total_working_set_mb,process_count')
$clock = [Diagnostics.Stopwatch]::StartNew()
$peakBytes = [int64]0; $peakCount = 0; $peakAt = 0.0
while ($true) {
    $s = Get-TreeSample $ProcessId
    if ($null -eq $s) { break }
    $t = $clock.Elapsed.TotalSeconds
    $lines.Add(('{0:0.0},{1:0.0},{2}' -f $t, ($s.Bytes / 1MB), $s.Count))
    if ($s.Bytes -gt $peakBytes) { $peakBytes = $s.Bytes; $peakCount = $s.Count; $peakAt = $t }
    if ($DurationSeconds -gt 0 -and $t -ge $DurationSeconds) { break }
    Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000))
}
[IO.File]::WriteAllLines($csvPath, $lines)
$summary = 'Peak {0:0.00} GB across {1} processes at {2:0} s ({3} samples).' -f ($peakBytes / 1GB), $peakCount, $peakAt, ($lines.Count - 1)
$summary
"CSV: $csvPath"
