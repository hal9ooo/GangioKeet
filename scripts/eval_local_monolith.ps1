param(
    [Parameter(Mandatory)][string]$Runner,
    [ValidateSet('development', 'heldout')][string]$Stage = 'development'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Runner = [IO.Path]::GetFullPath($Runner)
$Manifest = if ($Stage -eq 'development') { 'tests/fixtures/stt_eval/public/fleurs_it_v2/manifest.jsonl' }
    else { 'tests/fixtures/stt_eval/public/fleurs_it_test_v2/manifest.jsonl' }
$Passes = if ($Stage -eq 'development') { 3 } else { 1 }
$Common = @('--catalog', 'client-windows/models/candidates.json', '--native-dir',
    'client-windows/native/build/transcribe-0.4.1/payload/transcribe-native-windows-x86_64-cpu-vulkan',
    '--llama-dir', 'client-windows/native/build/llama-0.30.0', '--runtime-lock',
    'client-windows/native/runtime-lock.json', '--cleanup-policy', 'client-windows/cleanup.example.yaml')
$ReportDirectory = Join-Path $RepoRoot "reports/local-monolith/$Stage"
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null

function Invoke-Replay([string]$Label, [string]$Phase, [string]$InputManifest,
    [string]$Stt, [string]$Cleaner, [string]$Backend = 'vulkan') {
    $Report = Join-Path $ReportDirectory "$Label.json"
    $Cli = @('replay') + $Common + @('--phase', $Phase, '--manifest', $InputManifest,
        '--passes', "$Passes", '--backend', $Backend, '--output', $Report)
    if ($Stt) { $Cli += @('--stt', $Stt) }
    if ($Cleaner) { $Cli += @('--cleaner', $Cleaner) }
    Write-Output "Starting $Stage/$Label at $([DateTimeOffset]::Now.ToString('o'))"
    # Native diagnostic stderr must not become a terminating PowerShell error.
    $PreviousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ([IO.Path]::GetExtension($Runner) -eq '.dll') { & dotnet $Runner @Cli *> (Join-Path $ReportDirectory "$Label.log") }
    else { & $Runner @Cli *> (Join-Path $ReportDirectory "$Label.log") }
    $Result = $LASTEXITCODE
    $ErrorActionPreference = $PreviousPreference
    if ($Result -ne 0) { throw "Replay $Label failed with exit $Result; inspect its JSON/log." }
    $Score = @((Join-Path $PSScriptRoot 'stt_eval_native_report.py'), $Report,
        '--output', (Join-Path $ReportDirectory "$Label-summary.json"))
    if ($Phase -eq 'asr') { $Score += @('--freeze-raw', (Join-Path $ReportDirectory "$Label-fixed.jsonl")) }
    & (Join-Path $RepoRoot '.venv/Scripts/python.exe') @Score
    if ($LASTEXITCODE -ne 0) { throw "Scoring $Label failed." }
    Write-Output "Completed $Stage/$Label at $([DateTimeOffset]::Now.ToString('o'))"
}

Push-Location $RepoRoot
try {
    foreach ($Model in @('parakeet_v3', 'qwen3_asr_1_7b', 'whisper_large_v3')) {
        Invoke-Replay "$Model-asr-vulkan" 'asr' $Manifest $Model ''
    }
    $Fixed = Join-Path $ReportDirectory 'parakeet_v3-asr-vulkan-fixed.jsonl'
    foreach ($Model in @('qwen3_1_7b', 'gemma4_e2b', 'qwen3_4b')) {
        Invoke-Replay "$Model-cleaner-vulkan" 'cleaner' $Fixed '' $Model
    }
    # The two Qwen cleaners failed the frozen targeted direct-question gate;
    # Gemma alone proceeds to ASR-pair trials. Keep their isolated reports.
    foreach ($Model in @('parakeet_v3', 'qwen3_asr_1_7b', 'whisper_large_v3')) {
        Invoke-Replay "$Model-gemma4_e2b-pipeline-vulkan" 'pipeline' $Manifest $Model 'gemma4_e2b'
    }
    if ($Stage -eq 'development') {
        Invoke-Replay 'parakeet_v3-asr-cpu' 'asr' $Manifest 'parakeet_v3' '' 'cpu'
    }
}
finally { Pop-Location }
