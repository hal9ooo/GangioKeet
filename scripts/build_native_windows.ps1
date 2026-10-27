param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../client-windows/native/build/transcribe-0.4.1'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$LockPath = Join-Path $PSScriptRoot '../client-windows/native/runtime-lock.json'
$Runtime = (Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json).runtimes |
    Where-Object { $_.id -eq 'transcribe-vulkan' }
if ($null -eq $Runtime) { throw 'No pinned transcribe runtime in runtime-lock.json.' }
$BuildRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
$Archive = Join-Path $BuildRoot ([IO.Path]::GetFileName(([Uri]$Runtime.archive_url).AbsolutePath))
if (-not (Test-Path -LiteralPath $Archive)) {
    $Partial = "$Archive.partial"
    Invoke-WebRequest -UseBasicParsing -Uri $Runtime.archive_url -OutFile $Partial
    if ((Get-FileHash -LiteralPath $Partial -Algorithm SHA256).Hash -ne $Runtime.archive_sha256) {
        throw 'Downloaded native archive failed SHA256 verification.'
    }
    Move-Item -LiteralPath $Partial -Destination $Archive
}
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Runtime.archive_sha256) {
    throw 'Native archive failed SHA256 verification.'
}
$PayloadRoot = Join-Path $BuildRoot 'payload'
$Payload = Join-Path $PayloadRoot $Runtime.payload_directory
if (-not (Test-Path -LiteralPath $Payload)) {
    New-Item -ItemType Directory -Path $PayloadRoot -Force | Out-Null
    & tar -xf $Archive -C $PayloadRoot
    if ($LASTEXITCODE -ne 0) { throw 'Native archive extraction failed.' }
}
$Prefix = [IO.Path]::GetFullPath($Payload).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
foreach ($Entry in $Runtime.files) {
    $File = [IO.Path]::GetFullPath((Join-Path $Payload $Entry.path))
    if (-not $File.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native manifest file escapes the payload directory.'
    }
    if (-not (Test-Path -LiteralPath $File) -or
        (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ne $Entry.sha256) {
        throw "Native payload verification failed: $($Entry.path)"
    }
}
Write-Output "Native runtime verified: $Payload"

# Development bootstrap only. Production receives these verified artifacts and
# does not execute this script, Python, dotnet restore, or subprocesses.
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet restore (Join-Path $RepoRoot 'client-windows/src/GangioKeet.Inference') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Pinned llama NuGet restore failed.' }
$NuGetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$LlamaRoot = Join-Path $RepoRoot 'client-windows/native/build/llama-0.30.0'
& python (Join-Path $PSScriptRoot 'namespace_llama_runtime.py') --source $NuGetRoot --destination $LlamaRoot --lock $LockPath
if ($LASTEXITCODE -ne 0) { throw 'Private llama namespace verification failed.' }
Write-Output "Namespaced llama runtime verified: $LlamaRoot"
