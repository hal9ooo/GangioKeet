param(
    [string]$Selection = 'client-windows/models/selected.json',
    [string]$OutputDirectory = 'artifacts/package',
    [string]$Configuration = 'Release',
    [string]$Iscc,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$SelectionPath = [IO.Path]::GetFullPath((Join-Path $RepoRoot $Selection))
$Payload = [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDirectory))
$RepoRootNormalized = $RepoRoot.TrimEnd([char[]]'\/')
$PayloadNormalized = $Payload.TrimEnd([char[]]'\/')
$RepoPrefix = $RepoRootNormalized + [IO.Path]::DirectorySeparatorChar
if ($PayloadNormalized -eq $RepoRootNormalized -or
        -not $Payload.StartsWith($RepoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must resolve inside the repository: $Payload"
}
$LockPath = Join-Path $RepoRoot 'client-windows/native/runtime-lock.json'
$CleanupPath = Join-Path $RepoRoot 'client-windows/cleanup.example.yaml'
$TranscribeSource = Join-Path $RepoRoot 'client-windows/native/build/transcribe-0.4.1/payload/transcribe-native-windows-x86_64-cpu-vulkan'
$LlamaSource = Join-Path $RepoRoot 'client-windows/native/build/llama-0.30.0'
$ModelRoot = Join-Path $RepoRoot 'models'

if (-not (Test-Path -LiteralPath $SelectionPath -PathType Leaf)) { throw "selection not found: $SelectionPath" }
if (Test-Path -LiteralPath $Payload) { Remove-Item -LiteralPath $Payload -Recurse -Force }

Write-Output "Publishing client to $Payload"
& dotnet publish (Join-Path $RepoRoot 'client-windows/src/GangioKeet.Client/GangioKeet.Client.csproj') `
    -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:PublishTrimmed=false -o $Payload
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit $LASTEXITCODE" }
Get-ChildItem -LiteralPath $Payload -Recurse -File -Filter '*.pdb' | Remove-Item -Force
$Runtimes = Join-Path $Payload 'runtimes'
if (Test-Path -LiteralPath $Runtimes) {
    # NuGet native assets are unpatched duplicates; the app loads only the
    # verified runtime under native/transcribe and native/llama.
    Remove-Item -LiteralPath $Runtimes -Recurse -Force
}

$selected = Get-Content -LiteralPath $SelectionPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path (Join-Path $Payload 'models') -Force | Out-Null
$selectionSchema = if ($selected.PSObject.Properties.Name -contains 'schema_version') { [int]$selected.schema_version } else { 0 }
$activeName = if ($selectionSchema -eq 2) { 'selection.json' } else { 'selected.json' }
$SelectionV2 = Join-Path $RepoRoot 'client-windows/models/selection.json'
if (Test-Path -LiteralPath $SelectionV2 -PathType Leaf) {
    Copy-Item -LiteralPath $SelectionV2 -Destination (Join-Path $Payload 'models/selection.json') -Force
}
Copy-Item -LiteralPath $SelectionPath -Destination (Join-Path $Payload "models/$activeName") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'client-windows/models/candidates.json') `
    -Destination (Join-Path $Payload 'models/candidates.json') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'client-windows/models/integrity.json') `
    -Destination (Join-Path $Payload 'models/integrity.json') -Force
$ParakeetSelection = Join-Path $RepoRoot 'client-windows/models/selected-parakeet-qwen.json'
if (Test-Path -LiteralPath $ParakeetSelection -PathType Leaf) {
    Copy-Item -LiteralPath $ParakeetSelection -Destination (Join-Path $Payload 'models/selected-parakeet-qwen.json') -Force
}
Copy-Item -LiteralPath $CleanupPath -Destination (Join-Path $Payload 'cleanup.yaml') -Force
# Models are downloaded on demand on first app launch via ModelProvisioner;
# do not bundle 7+ GB GGUF files in the installer payload.

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
foreach ($runtime in $lock.runtimes) {
    if ($runtime.id -eq 'transcribe-vulkan') {
        foreach ($file in $runtime.files) {
            $source = Join-Path $TranscribeSource $file.path
            $destination = Join-Path $Payload ('native/transcribe/' + $file.path)
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    } elseif ($runtime.id -eq 'llama-vulkan') {
        foreach ($file in $runtime.files) {
            $source = Join-Path $LlamaSource $file.path
            $destination = Join-Path $Payload ('native/llama/' + $file.path)
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }
}
Copy-Item -LiteralPath $LockPath -Destination (Join-Path $Payload 'native/runtime-lock.json') -Force
Copy-Item -LiteralPath (Join-Path $LlamaSource 'LLamaSharp.dll') -Destination (Join-Path $Payload 'LLamaSharp.dll') -Force

$PackageManifestPath = Join-Path $RepoRoot 'client-windows/installer/package-manifest.json'
$PackageManifest = Get-Content -LiteralPath $PackageManifestPath -Raw | ConvertFrom-Json
$MsvcSource = Join-Path $RepoRoot 'client-windows/installer/msvc'
foreach ($file in $PackageManifest.msvc_runtime.files) {
    $source = Join-Path $MsvcSource $file.path
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "MSVC runtime file missing: $source" }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $file.sha256.ToLowerInvariant()) { throw "MSVC runtime hash mismatch: $($file.path)" }
    foreach ($destinationDirectory in @($Payload,
            (Join-Path $Payload 'native/transcribe'), (Join-Path $Payload 'native/llama'))) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $destinationDirectory $file.path) -Force
    }
}

Copy-Item -LiteralPath (Join-Path $RepoRoot 'LICENSE') -Destination (Join-Path $Payload 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $Payload 'THIRD_PARTY_NOTICES.md') -Force
Copy-Item -LiteralPath $PackageManifestPath -Destination (Join-Path $Payload 'package-manifest.json') -Force

$LicenseSource = Join-Path $RepoRoot 'client-windows/installer/licenses'
New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses/llama') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses/dotnet') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses/msvc') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses/models') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses/managed') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $LicenseSource 'llama.cpp.LICENSE.txt') -Destination (Join-Path $Payload 'licenses/llama/llama.cpp.LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'LLamaSharp.LICENSE.txt') -Destination (Join-Path $Payload 'licenses/llama/LLamaSharp.LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'dotnet.LICENSE.txt') -Destination (Join-Path $Payload 'licenses/dotnet/LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'dotnet.ThirdPartyNotices.txt') -Destination (Join-Path $Payload 'licenses/dotnet/ThirdPartyNotices.txt') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'client-windows/installer/msvc/SOURCE.txt') -Destination (Join-Path $Payload 'licenses/msvc/SOURCE.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'msvc/LICENSE.txt') -Destination (Join-Path $Payload 'licenses/msvc/LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'models/APACHE-2.0.txt') -Destination (Join-Path $Payload 'licenses/models/APACHE-2.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'models/CC-BY-4.0.txt') -Destination (Join-Path $Payload 'licenses/models/CC-BY-4.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $LicenseSource 'models/MODELS.txt') -Destination (Join-Path $Payload 'licenses/models/MODELS.txt') -Force
Copy-Item -Path (Join-Path $LicenseSource 'managed/*') -Destination (Join-Path $Payload 'licenses/managed') -Force

& (Join-Path $PSScriptRoot 'verify_windows_package.ps1') -Payload $Payload -Selection $SelectionPath `
    -RuntimeLock $LockPath -CleanupPolicy $CleanupPath
if ($LASTEXITCODE -ne 0) { throw "payload verification failed" }

if ($SkipInstaller) {
    Write-Output 'Payload verified; installer skipped.'
    exit 0
}

$iscc = if ($Iscc) { $Iscc }
    elseif ($env:GANGIOKEET_ISCC) { $env:GANGIOKEET_ISCC }
    else {
        @(
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $env:TEMP 'opencode\inno6\ISCC.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
if (-not $iscc -or -not (Test-Path -LiteralPath $iscc -PathType Leaf)) {
    throw 'Inno Setup 6 (ISCC.exe) not found; payload is verified but no installer was produced.'
}
$isccHash = (Get-FileHash -LiteralPath $iscc -Algorithm SHA256).Hash.ToLowerInvariant()
if ($PackageManifest.installer.iscc_sha256 -and
        $isccHash -ne $PackageManifest.installer.iscc_sha256.ToLowerInvariant()) {
    throw "ISCC.exe hash $isccHash does not match the pinned compiler $($PackageManifest.installer.iscc_sha256)."
}
$installerDirectory = Join-Path $RepoRoot 'artifacts/installer'
$installerBase = "GangioKeet-Setup-$($PackageManifest.version)"
$stagingDirectory = Join-Path $RepoRoot 'artifacts/installer.staging'
if (Test-Path -LiteralPath $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

& $iscc "/O$stagingDirectory" (Join-Path $RepoRoot 'client-windows/installer/GangioKeet.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit $LASTEXITCODE" }
$stagedInstaller = Join-Path $stagingDirectory "$installerBase.exe"
if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
    throw "Inno Setup reported success but the installer is missing: $stagedInstaller"
}
$stagedSlices = @(Get-ChildItem -LiteralPath $stagingDirectory -File -Filter "$installerBase-*.bin" |
    Sort-Object Name)
if ($stagedSlices.Count -gt 0) {
    foreach ($slice in $stagedSlices) {
        if ($slice.Length -le 0) { throw "Installer data slice is empty: $($slice.Name)" }
    }
}
$stagedFiles = @(Get-Item -LiteralPath $stagedInstaller) + $stagedSlices

New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $installerDirectory -File -Filter "$installerBase*" -ErrorAction SilentlyContinue |
    Remove-Item -Force
foreach ($file in $stagedFiles) {
    Move-Item -LiteralPath $file.FullName -Destination (Join-Path $installerDirectory $file.Name)
}
Remove-Item -LiteralPath $stagingDirectory -Recurse -Force

Write-Output "Installer: $(Join-Path $installerDirectory "$installerBase.exe")"
$checksums = foreach ($file in $stagedFiles) {
    $finalPath = Join-Path $installerDirectory $file.Name
    $finalFile = Get-Item -LiteralPath $finalPath
    $hash = (Get-FileHash -LiteralPath $finalPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "$($finalFile.Name) $($finalFile.Length) $hash"
    "$hash  $($finalFile.Name)"
}
Set-Content -LiteralPath (Join-Path $installerDirectory 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
