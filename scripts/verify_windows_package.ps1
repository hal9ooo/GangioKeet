param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$Selection,
    [string]$RuntimeLock = 'client-windows/native/runtime-lock.json',
    [string]$CleanupPolicy = 'client-windows/cleanup.example.yaml'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Payload)
$SelectionPath = [IO.Path]::GetFullPath($Selection)
$LockPath = [IO.Path]::GetFullPath($RuntimeLock)
$CleanupPath = [IO.Path]::GetFullPath($CleanupPolicy)
$Errors = [Collections.Generic.List[string]]::new()

function Add-Error([string]$Message) { $Errors.Add($Message) }
function Check-File([string]$Relative, [string]$ExpectedSha256) {
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        Add-Error "missing expected hash: $Relative"
        return
    }
    $path = Join-Path $Root $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Error "missing: $Relative"
        return
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        Add-Error "hash mismatch: $Relative"
    }
}

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    Write-Error "payload directory not found: $Root"
    exit 2
}
$selected = Get-Content -LiteralPath $SelectionPath -Raw | ConvertFrom-Json
$selectionSchema = if ($selected.PSObject.Properties.Name -contains 'schema_version') { [int]$selected.schema_version } else { 0 }
$activeSelectionName = if ($selectionSchema -eq 2) { 'selection.json' } else { 'selected.json' }
if (-not (Test-Path -LiteralPath (Join-Path $Root 'GangioKeet.Client.exe') -PathType Leaf)) {
    Add-Error 'missing: GangioKeet.Client.exe'
}
# GGUF models are provisioned on demand; verify they do not ship in the installer package.
$modelsRoot = Join-Path $Root 'models'
$ggufFiles = @(Get-ChildItem -LiteralPath $modelsRoot -Recurse -File -Filter '*.gguf' -ErrorAction SilentlyContinue)
if ($ggufFiles.Count -gt 0) {
    Add-Error 'Heavy GGUF model files must not ship in the installer payload; models are provisioned on demand.'
}
Check-File "models/$activeSelectionName" (Get-FileHash -LiteralPath $SelectionPath -Algorithm SHA256).Hash
Check-File 'cleanup.yaml' (Get-FileHash -LiteralPath $CleanupPath -Algorithm SHA256).Hash
Check-File 'native/runtime-lock.json' (Get-FileHash -LiteralPath $LockPath -Algorithm SHA256).Hash

if ($selectionSchema -eq 1) {
    if ($selected.inference.mode -ne 'local') { Add-Error 'selection must use local inference mode' }
    $lockFileHash = (Get-FileHash -LiteralPath $LockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($selected.runtime.lock_sha256 -ne $lockFileHash) {
        Add-Error 'selected.json runtime lock hash does not match the lock file'
    }
    $cleanupText = [IO.File]::ReadAllText($CleanupPath).Replace("`r`n", "`n")
    $cleanupBytes = [Text.Encoding]::UTF8.GetBytes($cleanupText)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $cleanupHash = [BitConverter]::ToString($sha.ComputeHash($cleanupBytes)).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
    if ($selected.cleanup_policy.sha256 -ne $cleanupHash) {
        Add-Error 'cleanup policy does not match the selected pair'
    }
} elseif ($selectionSchema -eq 2) {
    if ([string]::IsNullOrWhiteSpace($selected.stt_id)) { Add-Error 'selection.json requires stt_id' }
    if ($selected.refinement_enabled -and [string]::IsNullOrWhiteSpace($selected.cleaner_id)) {
        Add-Error 'selection.json refinement requires cleaner_id'
    }
} else {
    Add-Error "unsupported selection schema: $selectionSchema"
}

$catalogPath = Join-Path $Root 'models/candidates.json'
$integrityPath = Join-Path $Root 'models/integrity.json'
$v2SelectionPath = Join-Path $Root 'models/selection.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    Add-Error 'missing: models/candidates.json'
}
if (-not (Test-Path -LiteralPath $integrityPath -PathType Leaf)) {
    Add-Error 'missing: models/integrity.json'
} else {
    $integrity = Get-Content -LiteralPath $integrityPath -Raw | ConvertFrom-Json
    Check-File 'models/candidates.json' $integrity.catalog_sha256
    Check-File 'native/runtime-lock.json' $integrity.runtime_lock_sha256
    Check-File 'cleanup.yaml' $integrity.cleanup_policy_sha256
    if (Test-Path -LiteralPath $catalogPath -PathType Leaf) {
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
        $catalogIds = @($catalog.candidates | ForEach-Object { [string]$_.id })
        if (-not (Test-Path -LiteralPath $v2SelectionPath -PathType Leaf)) {
            Add-Error 'missing: models/selection.json'
        } else {
            $v2 = Get-Content -LiteralPath $v2SelectionPath -Raw | ConvertFrom-Json
            if ($v2.schema_version -ne 2) { Add-Error 'models/selection.json must use schema_version 2' }
            if ($catalogIds -cnotcontains [string]$v2.stt_id) { Add-Error 'models/selection.json has an unknown stt_id' }
            if ($v2.refinement_enabled -and
                    ($null -eq $v2.cleaner_id -or $catalogIds -cnotcontains [string]$v2.cleaner_id)) {
                Add-Error 'models/selection.json has an unknown cleaner_id'
            }
        }
    }
}

$manifestPath = Join-Path $Root 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Add-Error 'missing: package-manifest.json'
} else {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($selectionSchema -eq 1) {
        $selectionHash = (Get-FileHash -LiteralPath $SelectionPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($manifest.selection.sha256 -ne $selectionHash) { Add-Error 'package-manifest selection hash mismatch' }
    }
    $lockHash = (Get-FileHash -LiteralPath $LockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.runtime_lock.sha256 -ne $lockHash) { Add-Error 'package-manifest runtime lock hash mismatch' }
    foreach ($file in $manifest.msvc_runtime.files) {
        foreach ($directory in @('', 'native/transcribe', 'native/llama')) {
            $relative = if ($directory) { "$directory/$($file.path)" } else { $file.path }
            Check-File $relative $file.sha256
        }
    }
}

foreach ($legal in @('LICENSE', 'THIRD_PARTY_NOTICES.md',
        'licenses/llama/llama.cpp.LICENSE.txt', 'licenses/llama/LLamaSharp.LICENSE.txt',
        'licenses/dotnet/LICENSE.txt', 'licenses/dotnet/ThirdPartyNotices.txt',
        'licenses/msvc/SOURCE.txt', 'licenses/msvc/LICENSE.txt',
        'licenses/models/APACHE-2.0.txt', 'licenses/models/CC-BY-4.0.txt',
        'licenses/models/MODELS.txt',
        'licenses/managed/NAudio.LICENSE.txt',
        'licenses/managed/Hardcodet.NotifyIcon.Wpf.LICENSE.txt',
        'licenses/managed/YamlDotNet.LICENSE.txt',
        'licenses/managed/CommunityToolkit.HighPerformance.LICENSE.txt')) {
    $path = Join-Path $Root $legal
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Add-Error "missing: $legal" }
    elseif ((Get-Item -LiteralPath $path).Length -eq 0) { Add-Error "empty: $legal" }
}
$noticesPath = Join-Path $Root 'THIRD_PARTY_NOTICES.md'
if (Test-Path -LiteralPath $noticesPath -PathType Leaf) {
    $notices = Get-Content -LiteralPath $noticesPath -Raw
    foreach ($token in @('Qwen3-ASR', 'Gemma 4', 'Parakeet', 'Qwen3-1.7B',
            'transcribe.cpp', 'LLamaSharp', 'NAudio', 'YamlDotNet',
            'Visual C++', 'CC-BY-4.0')) {
        if ($notices -notmatch [regex]::Escape($token)) { Add-Error "third-party notices missing: $token" }
    }
}

foreach ($file in Get-ChildItem -LiteralPath $modelsRoot -Recurse -File -ErrorAction SilentlyContinue) {
    $relative = $file.FullName.Substring($modelsRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ($relative -notin @('selected.json', 'selected-parakeet-qwen.json', 'selection.json',
            'candidates.json', 'integrity.json')) {
        Add-Error "unselected model artifact must not ship: models/$relative"
    }
}
$parakeetSelectionPath = Join-Path (Split-Path -Parent $SelectionPath) 'selected-parakeet-qwen.json'
if (Test-Path -LiteralPath $parakeetSelectionPath -PathType Leaf) {
    Check-File 'models/selected-parakeet-qwen.json' (Get-FileHash -LiteralPath $parakeetSelectionPath -Algorithm SHA256).Hash
}
$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
foreach ($runtime in $lock.runtimes) {
    $subdirectory = if ($runtime.id -eq 'transcribe-vulkan') { 'transcribe' }
        elseif ($runtime.id -eq 'llama-vulkan') { 'llama' }
        else { $null }
    if ($null -eq $subdirectory) { continue }
    foreach ($file in $runtime.files) {
        Check-File "native/$subdirectory/$($file.path)" $file.sha256
    }
}
$llamaRuntime = $lock.runtimes | Where-Object { $_.id -eq 'llama-vulkan' }
foreach ($file in $llamaRuntime.files) {
    if ($file.path -eq 'LLamaSharp.dll') {
        Check-File 'LLamaSharp.dll' $file.sha256
    }
}

foreach ($forbidden in @('GangioKeet.Eval.exe', 'tools', 'GangioKeet.Eval.dll', 'runtimes')) {
    if (Test-Path -LiteralPath (Join-Path $Root $forbidden)) {
        Add-Error "development artifact must not ship: $forbidden"
    }
}
$nativePatterns = @('ggml*.dll', 'llama.dll', 'mtmd.dll', 'lgml*.dll')
foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue) {
    if (-not ($nativePatterns | Where-Object { $file.Name -like $_ })) { continue }
    $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
    $allowed = $relative -like 'native/transcribe/*' -or $relative -like 'native/llama/*'
    if (-not $allowed) {
        Add-Error "native inference DLL outside the pinned runtime: $relative"
    }
}
if (Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.py' -ErrorAction SilentlyContinue) {
    Add-Error 'Python files must not ship in the product payload.'
}
foreach ($pattern in @('python*.exe', 'ollama*.exe', 'llama-cli.exe', 'llama-server.exe', 'whisper*.exe')) {
    if (Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue) {
        Add-Error "separate inference runtime must not ship: $pattern"
    }
}
if (Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue) {
    Add-Error 'Debug symbols must not ship in the product payload.'
}

if ($Errors.Count -gt 0) {
    $Errors | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Output "Verified payload: $Root"
exit 0
