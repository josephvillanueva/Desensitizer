<#
Create a new source-only directory from a clean committed checkout. No Git
history, agent boards, private workspace, models or compiled assets are copied.
This is a path/secret guard, not a substitute for a human privacy review.
#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$sourceRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $destination){throw 'Choose a new empty snapshot directory.'}
if($destination -eq $sourceRoot){throw 'The snapshot must not replace the source repository.'}
Push-Location $sourceRoot
try {
    $state=@(& git status --porcelain --untracked-files=no)
    if($LASTEXITCODE -ne 0 -or $state.Count){throw 'Commit reviewed source before making a public snapshot.'}
    $files=@(& git ls-files)
    if($LASTEXITCODE -ne 0){throw 'Cannot list committed source.'}
    $allowedRoots=@('Desensitizer','AI.Desensitizer','Shared','Desensitizer.Analyzer','Desensitizer.Regression','tests','tools')
    $allowedDocuments=@('docs/MODEL-SWAPPING.md','docs/ANALYST-QUICKSTART.md','docs/TROUBLESHOOTING.md','docs/ANALYZER-BUILD.md','docs/LICENSE-REVIEW.md','docs/HANDOFF.md','docs/third-party-components.json')
    $selected=@($files | Where-Object {
        $path=$_ -replace '\\','/'
        $top=($path -split '/')[0]
        $top -in $allowedRoots -or $path -in @('README.md','THIRD-PARTY-NOTICES.txt','.gitignore') -or $path -in $allowedDocuments -or $path.StartsWith('docs/licenses/')
    })
    if(!$selected.Count){throw 'No public source selected.'}
    $planned=@()
    foreach($relative in $selected){
        if($relative -match '(?i)(^|/)(work|outputs|model-packs|bin|obj|\.git|\.claude|__pycache__|benchmark-output|results)(/|$)' -or
            $relative -match '(?i)\.(gguf|dsproj.*|exe|dll|zip|log|docx|xlsx|pptx|pdf|bak|tmp)$'){throw 'Private or binary path in public source plan.'}
        $item=Get-Item -LiteralPath (Join-Path $sourceRoot $relative)
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Public source does not allow linked files.'}
        if($item.Length -gt 5MB){throw 'Unexpectedly large source asset. Review it before publishing.'}
        if($item.Extension -ne '.ico'){
            $content=[IO.File]::ReadAllText($item.FullName)
            if($content -match '(?i)(gh[pousr]_[A-Za-z0-9]{24,}|github_pat_[A-Za-z0-9_]{25,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|AKIA[A-Z0-9]{16})'){
                throw ('Possible credential in '+$relative+'. No snapshot written.')
            }
        }
        $planned += [pscustomobject]@{Path=$relative;Source=$item.FullName;SHA256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash}
    }
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach($file in $planned){
        $target=Join-Path $destination $file.Path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $target
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.SHA256){throw 'Snapshot file failed verification.'}
    }
    $head=& git rev-parse HEAD
    [pscustomobject]@{Directory=$destination;SourceCommit=$head;Files=$planned.Count;HistoryIncluded=$false;RequiresPrivacyReview=$true}
} finally {Pop-Location}
