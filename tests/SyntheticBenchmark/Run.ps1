param(
    [ValidateSet('Regular','AI')][string]$App = 'Regular',
    # Detector: deterministic layers only, no model, no LLM (default, safe, fast).
    # Oracle: ground truth offered to the policy, post-processing and identity layers as if a perfect model proposed it.
    # Full: local detector workers plus local LLM reviewers. Heavy. Not run by default.
    [ValidateSet('Detector','Oracle','Full')][string]$Mode = 'Detector',
    [string]$Seeds = '1,2,3,4,5',
    [int]$Entities = 60,
    # Parent folder for new timestamped result folders. Default: tests/SyntheticBenchmark/results (git-ignored).
    [string]$Results = '',
    [string]$Dotnet = $(if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' })
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repo
if ($Mode -eq 'Full') {
    # Explicit model paths for a separately provisioned local distribution.
    $packageName = if ($App -eq 'AI') { 'AI Desensitizer Portable Latest' } else { 'Desensitizer Portable Latest' }
    $package = Join-Path ([Environment]::GetFolderPath('Desktop')) $packageName
    $env:DESENSITIZER_DEV = '1'   # live benchmark only: the path overrides below are honoured only with this flag
    $env:DESENSITIZER_ANALYZER_PATH = Join-Path $package 'analyzer/DesensitizerAnalyzer.exe'
    $env:DESENSITIZER_GLINER_PATH = Join-Path $package 'model-packs/business-context/BusinessContextAnalyzer.exe'
    $env:DESENSITIZER_PRIVACY_FILTER_PATH = Join-Path $package 'model-packs/privacy-filter/PrivacyFilterAnalyzer.exe'
    $env:DESENSITIZER_GLINER_MODEL = Join-Path $package 'model-packs/business-context/model'
    $env:DESENSITIZER_PRIVACY_MODEL = Join-Path $package 'model-packs/privacy-filter/model'
    $env:DESENSITIZER_LLM_RUNTIME = Join-Path $package 'model-packs/context-llm/llama-server.exe'
    $env:DESENSITIZER_LLM_MODEL = Join-Path $package 'model-packs/context-llm/Qwen3-4B-Q4_K_M.gguf'
    $env:AI_DESENSITIZER_QWEN_MODEL = $env:DESENSITIZER_LLM_MODEL
    $env:AI_DESENSITIZER_NUEXTRACT_MODEL = Join-Path $package 'model-packs/consensus-llm/NuExtract-2.0-2B-Q4_K_M.gguf'
    $env:AI_DESENSITIZER_GEMMA_MODEL = Join-Path $package 'model-packs/consensus-llm/gemma-3-4b-it-q4_0.gguf'
}
$runArgs = @('run', '--mode', $Mode.ToLowerInvariant(), '--seeds', $Seeds, '--entities', "$Entities")
if ($Results) { $runArgs += @('--results', $Results) }
& $Dotnet run --project "tests/SyntheticBenchmark/$App" -c Release -- @runArgs
exit $LASTEXITCODE
