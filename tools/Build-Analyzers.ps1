<#
Builds the three Python detector workers as lean PyInstaller onedir folders into a STAGING folder
(work\AnalyzerBuild-<stamp>\dist) and smoke tests them against the currently installed executables.
It never writes to the Desktop folders or AppData. See docs\ANALYZER-BUILD.md.

  .\tools\Build-Analyzers.ps1                       # build all three, then smoke
  .\tools\Build-Analyzers.ps1 -Only Presidio        # Presidio | PrivacyFilter | BusinessContext
  .\tools\Build-Analyzers.ps1 -SkipSmoke

No downloads: the build venv must already exist (default work\presidio-env of the main checkout).
#>
param(
    [string]$EnvPath = (Join-Path $PSScriptRoot '..\work\presidio-env'),
    [string]$InstalledRoot = (Join-Path $env:USERPROFILE 'Desktop\Desensitizer Portable Latest'),
    [ValidateSet('All','Presidio','PrivacyFilter','BusinessContext')][string]$Only = 'All',
    [int]$TimeoutMinutes = 20,
    [switch]$SkipSmoke
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$py = Join-Path $EnvPath 'Scripts\python.exe'
if (-not (Test-Path $py)) { throw "Build venv not found: $py. Nothing is downloaded by this script; see docs\ANALYZER-BUILD.md for the install list." }
foreach ($m in 'PyInstaller','presidio_analyzer','spacy','gliner','onnxruntime','tokenizers') {
    & $py -c "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('$m') else 1)"
    if ($LASTEXITCODE -ne 0) { throw "Python package '$m' is missing from $EnvPath." }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $repo "work\AnalyzerBuild-$stamp"
New-Item -ItemType Directory -Force "$stage\dist","$stage\build","$stage\logs" | Out-Null

$targets = @(
  @{ Key='Presidio';        Name='DesensitizerAnalyzer';    Spec='DesensitizerAnalyzer.spec';    Old='analyzer\DesensitizerAnalyzer.exe';                         Model=$null },
  @{ Key='PrivacyFilter';   Name='PrivacyFilterAnalyzer';   Spec='PrivacyFilterAnalyzer.spec';   Old='model-packs\privacy-filter\PrivacyFilterAnalyzer.exe';       Model='model-packs\privacy-filter\model' },
  @{ Key='BusinessContext'; Name='BusinessContextAnalyzer'; Spec='BusinessContextAnalyzer.spec'; Old='model-packs\business-context\BusinessContextAnalyzer.exe';   Model='model-packs\business-context\model' }
) | Where-Object { $Only -eq 'All' -or $_.Key -eq $Only }

function Invoke-Worker([string]$Exe, [string]$Json, [int]$TimeoutMs = 300000) {
    # Windows PowerShell 5.1 does not expose StandardInputEncoding. Write explicit
    # BOM-free UTF-8 bytes to the input pipe while both output pipes drain asynchronously.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $utf8 = New-Object Text.UTF8Encoding($false)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$Exe; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.StandardOutputEncoding=$utf8; $info.StandardErrorEncoding=$utf8
    $p = New-Object Diagnostics.Process
    $p.StartInfo=$info
    try {
        $null=$p.Start()
        $outTask=$p.StandardOutput.ReadToEndAsync(); $errTask=$p.StandardError.ReadToEndAsync()
        $bytes=$utf8.GetBytes($Json)
        $writeTask=$p.StandardInput.BaseStream.WriteAsync($bytes,0,$bytes.Length)
        $timedOut=-not $writeTask.Wait($TimeoutMs)
        if (-not $timedOut) {
            $p.StandardInput.Close()
            $remaining=[math]::Max(1,$TimeoutMs-[int]$sw.ElapsedMilliseconds)
            $timedOut=-not $p.WaitForExit($remaining)
        }
        if ($timedOut) { $p.Kill(); $p.WaitForExit() }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask),10000)) { throw 'Worker pipes did not close after exit.' }
        [pscustomobject]@{ Exit=$(if($timedOut){-1}else{$p.ExitCode}); Ms=$sw.ElapsedMilliseconds; Out=$outTask.Result; Err=$errTask.Result }
    } finally {
        try { if (-not $p.HasExited) { $p.Kill() } } catch {}
        $p.Dispose()
    }
}

$summary = @()
foreach ($t in $targets) {
    $started = Get-Date
    $pyArgs = @('-m','PyInstaller','--noconfirm','--clean','--distpath',"$stage\dist\$($t.Name)",'--workpath',"$stage\build\$($t.Name)","$repo\Desensitizer.Analyzer\$($t.Spec)")
    $p = Start-Process $py -ArgumentList $pyArgs -RedirectStandardOutput "$stage\logs\$($t.Name).out.log" -RedirectStandardError "$stage\logs\$($t.Name).err.log" -PassThru -WindowStyle Hidden
    $handle = $p.Handle
    if (-not $p.WaitForExit($TimeoutMinutes * 60000)) {
        Stop-Process -Id $p.Id -Force
        throw "$($t.Name): PyInstaller exceeded $TimeoutMinutes minutes and was stopped. See $stage\logs."
    }
    if ($p.ExitCode -ne 0) { throw "$($t.Name): PyInstaller failed (exit $($p.ExitCode)). See $stage\logs." }
    $dir = "$stage\dist\$($t.Name)\$($t.Name)"
    $files = Get-ChildItem $dir -Recurse -File
    $exe = Get-Item "$dir\$($t.Name).exe"
    $row = [ordered]@{ Name=$t.Name; BuildSeconds=[int]((Get-Date) - $started).TotalSeconds; Files=$files.Count
        MB=[math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1); ExeSha256=(Get-FileHash $exe.FullName).Hash; Smoke='skipped' }
    if (-not $SkipSmoke) {
        $junction = $null
        if ($t.Model) { $junction = "$dir\model"; New-Item -ItemType Junction -Path $junction -Target (Join-Path $InstalledRoot $t.Model) | Out-Null }
        try {
            if ($t.Key -eq 'BusinessContext') {
                $req = @{ texts = @('Project Falcon runs on the Orion Ledger system for Acme Holdings, owned by the Finance department.','Nothing special here.') } | ConvertTo-Json -Compress
                $expectedBatches = 2
            } else {
                $req = @{ texts = @('Maria de la Cruz signed the lease. Contact jose.pena@example.com. Tax identification number (TIN): 123-456-789-000.','Business Analyst reviews the workflow. Version 3.1.1.1 unchanged.','Santos, Maria T. and Juan dela Cruz Jr. met in Manila.') } | ConvertTo-Json -Compress
                $expectedBatches = 3
            }
            $new = Invoke-Worker $exe.FullName $req
            $old = Invoke-Worker (Join-Path $InstalledRoot $t.Old) $req
            $accentedName = 'Maria Pe' + [char]0x00f1 + 'a'
            $accentedCompany = 'Pe' + [char]0x00f1 + 'a Holdings'
            $unicodeReq = @{ texts=@("$accentedName works for $accentedCompany. Contact maria.pe$([char]0x00f1)a@example.com.") } | ConvertTo-Json -Compress
            $escapedReq = [regex]::Replace($unicodeReq,'[^\u0000-\u007f]',{param($m) '\u{0:x4}' -f [int][char]$m.Value})
            $unicode = Invoke-Worker $exe.FullName $unicodeReq
            $escaped = Invoke-Worker $exe.FullName $escapedReq
            $unicodePassed = $unicode.Exit -eq 0 -and $escaped.Exit -eq 0 -and $unicode.Out -ceq $escaped.Out -and ($unicode.Out | ConvertFrom-Json).batches.Count -eq 1
            $bad1 = Invoke-Worker $exe.FullName 'not json SECRET-ZEBRA'
            $bad2 = Invoke-Worker $exe.FullName '{"texts":[7]}'
            $newResp = $new.Out | ConvertFrom-Json
            $checks = @(
                ($new.Exit -eq 0 -and $newResp.batches.Count -eq $expectedBatches),
                (-not ($bad1.Out -match 'SECRET-ZEBRA') -and [bool]($bad1.Out | ConvertFrom-Json).error),
                [bool]($bad2.Out | ConvertFrom-Json).error,
                $unicodePassed
            )
            if ($t.Key -eq 'Presidio') {
                $values = @($newResp.batches | ForEach-Object { $_ } | ForEach-Object { $_.value })
                $checks += ($values -contains 'Maria de la Cruz'), ($values -contains '123-456-789-000')
            }
            $row.Smoke = if ($checks -contains $false) { 'FAILED' } else { 'passed' }
            $row.UnicodeProtocol = if ($unicodePassed) { 'passed' } else { 'FAILED' }
            $row.NewMs = $new.Ms; $row.OldMs = $old.Ms; $row.SameAsOld = ($new.Out -eq $old.Out)
            [IO.File]::WriteAllText("$stage\logs\$($t.Name).new.json", $new.Out)
            [IO.File]::WriteAllText("$stage\logs\$($t.Name).old.json", $old.Out)
        } finally {
            if ($junction -and (Test-Path $junction)) { [IO.Directory]::Delete($junction) }  # removes the link only
        }
    }
    $summary += [pscustomobject]$row
}
$summary | ConvertTo-Json | Set-Content "$stage\summary.json" -Encoding utf8
$summary | Format-Table -AutoSize | Out-String -Width 250
Write-Host "Staged in $stage. Nothing was installed."
if (@($summary | Where-Object { $_.Smoke -eq 'FAILED' }).Count) { throw 'One or more staged analyzer smoke tests failed. Do not install these workers.' }
