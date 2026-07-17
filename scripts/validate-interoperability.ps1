[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory = 'artifacts/interop-native',
    [string]$InstallDirectory = 'artifacts/interop-native-install',
    [string]$PythonArtifactsDirectory = 'artifacts/interop-python-validation',
    [string]$CMakeExecutable = 'cmake',
    [string]$PythonExecutable = 'python',
    [switch]$SkipBuild,
    [switch]$ArtifactsPrevalidated,
    [switch]$Stress,
    [ValidateRange(1, 100000)]
    [int]$StressValueCount = 1000,
    [ValidateRange(1, 1000000)]
    [int]$StressLifecycleCycleCount = 10000,
    [switch]$Docker,
    [switch]$SkipDockerBuild,
    [string]$DockerImage = 'shared-memory-store-interop:local',
    [string]$EvidencePath = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $BuildDirectory))
$installPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $InstallDirectory))
$pythonArtifactsPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PythonArtifactsDirectory))
$testProject = Join-Path $repositoryRoot 'tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj'
$resolvedEvidencePath = if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $null
}
elseif ([IO.Path]::IsPathFullyQualified($EvidencePath)) {
    [IO.Path]::GetFullPath($EvidencePath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $EvidencePath))
}
if ($null -ne $resolvedEvidencePath) {
    $artifactsPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedEvidencePath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Interoperability evidence must remain below '$artifactsPrefix'."
    }
    if (Test-Path -LiteralPath $resolvedEvidencePath) {
        throw "Refusing to overwrite interoperability evidence '$resolvedEvidencePath'."
    }
    if ($Docker -and $SkipDockerBuild) {
        throw 'Artifact-bound Docker evidence cannot reuse an unproven pre-existing image.'
    }
    if (-not $Docker -and $SkipBuild -and -not $ArtifactsPrevalidated) {
        throw 'Artifact-bound host evidence with -SkipBuild requires explicit -ArtifactsPrevalidated after a same-run clean native/Python validation.'
    }
}
if ($ArtifactsPrevalidated -and -not $SkipBuild) {
    throw '-ArtifactsPrevalidated is valid only together with -SkipBuild.'
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Find-NativeAgent {
    param([string]$Root, [string]$BuildConfiguration)

    $fileName = if ($IsWindows) { 'sms_cpp_interop_agent.exe' } else { 'sms_cpp_interop_agent' }
    $candidates = @(
        (Join-Path $Root "tests/cpp/$BuildConfiguration/$fileName"),
        (Join-Path $Root "tests/cpp/$fileName"),
        (Join-Path $Root $fileName)
    )
    $agent = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $agent) {
        throw "The C++ interoperability agent was not found under '$Root'."
    }

    return [IO.Path]::GetFullPath($agent)
}

function Find-PythonCheckpointLibrary {
    param([string]$Root, [string]$BuildConfiguration)

    $fileName = if ($IsWindows) {
        'shared_memory_store_python_checkpoint.dll'
    }
    else {
        'libshared_memory_store_python_checkpoint.so'
    }
    $candidates = @(
        (Join-Path $Root "src/cpp/$BuildConfiguration/$fileName"),
        (Join-Path $Root "src/cpp/$fileName"),
        (Join-Path $Root $fileName)
    )
    $library = $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ($null -eq $library) {
        throw "The test-only Python checkpoint runtime was not found under '$Root'."
    }

    return [IO.Path]::GetFullPath($library)
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-InteroperabilityEvidence {
    param(
        [Parameter(Mandatory)][ValidateSet('host', 'docker')][string]$Mode,
        [string[]]$ArtifactPaths = @(),
        [string]$DockerImageId = '')

    if ($null -eq $resolvedEvidencePath) {
        return
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $rootPrefix = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $uniquePaths = [Collections.Generic.HashSet[string]]::new($pathComparer)
    $artifacts = [Collections.Generic.List[object]]::new()
    foreach ($candidate in $ArtifactPaths) {
        if ([string]::IsNullOrWhiteSpace($candidate) `
            -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        $fullPath = [IO.Path]::GetFullPath($candidate)
        if (-not $uniquePaths.Add($fullPath)) {
            continue
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            continue
        }
        $recordedPath = if ($fullPath.StartsWith($rootPrefix, $comparison)) {
            [IO.Path]::GetRelativePath($repositoryRoot, $fullPath).Replace('\', '/')
        }
        else {
            $fullPath.Replace('\', '/')
        }
        $artifacts.Add([pscustomobject][ordered]@{
            path = $recordedPath
            fullPath = $fullPath
            length = [int64]$item.Length
            sha256 = Get-FileSha256 $fullPath
        })
    }
    $sourceArtifacts = @($artifacts | Sort-Object path)
    if ($Mode -eq 'host' -and $sourceArtifacts.Count -lt 3) {
        throw 'Host interoperability evidence did not discover the native agent, installed Python native library, and packaged/install artifacts.'
    }
    if ($Mode -eq 'docker' -and $DockerImageId -notmatch '^sha256:[0-9a-f]{64}$') {
        throw "Docker interoperability evidence has invalid image id '$DockerImageId'."
    }
    $evidenceArtifacts = [Collections.Generic.List[object]]::new()
    if ($Mode -eq 'host') {
        $bundlePath = Join-Path `
            (Split-Path -Parent $resolvedEvidencePath) `
            ([IO.Path]::GetFileNameWithoutExtension($resolvedEvidencePath) + '.files')
        if (Test-Path -LiteralPath $bundlePath) {
            throw "Refusing to reuse interoperability artifact bundle '$bundlePath'."
        }
        New-Item -ItemType Directory -Path $bundlePath -Force | Out-Null
        for ($index = 0; $index -lt $sourceArtifacts.Count; $index++) {
            $sourceArtifact = $sourceArtifacts[$index]
            $safeName = ([IO.Path]::GetFileName([string]$sourceArtifact.path) -replace '[^A-Za-z0-9._-]', '-')
            $destination = Join-Path $bundlePath (('{0:D4}-{1}' -f $index, $safeName))
            [IO.File]::Copy([string]$sourceArtifact.fullPath, $destination, $false)
            $copied = Get-Item -LiteralPath $destination -Force
            $copiedPath = [IO.Path]::GetRelativePath($repositoryRoot, $destination).Replace('\', '/')
            $copiedSha256 = Get-FileSha256 $destination
            if ([int64]$copied.Length -ne [int64]$sourceArtifact.length `
                -or $copiedSha256 -cne [string]$sourceArtifact.sha256) {
                throw "Interoperability evidence copy changed artifact '$($sourceArtifact.path)'."
            }
            $evidenceArtifacts.Add([pscustomobject][ordered]@{
                path = $copiedPath
                sourcePath = [string]$sourceArtifact.path
                length = [int64]$copied.Length
                sha256 = $copiedSha256
            })
        }
    }
    $orderedArtifacts = @($evidenceArtifacts | Sort-Object path)
    $canonicalArtifactRows = [Collections.Generic.List[string]]::new()
    foreach ($artifact in $orderedArtifacts) {
        $canonicalArtifactRows.Add("$($artifact.path)|$($artifact.length)|$($artifact.sha256)")
    }
    $canonicalArtifactRows.Sort([StringComparer]::Ordinal)
    $canonicalArtifacts = @($canonicalArtifactRows) -join "`n"
    $sourceStatus = @(& git -C $repositoryRoot status --porcelain=v2 --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not record interoperability source status.'
    }
    $sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40,64}$') {
        throw 'Could not record interoperability source commit.'
    }
    $report = [pscustomobject][ordered]@{
        schemaVersion = 1
        mode = $Mode
        configuration = $Configuration
        stressEnabled = [bool]$Stress.IsPresent
        orderedRuntimeCells = 9
        stressValueCount = $StressValueCount
        stressLifecycleCycleCount = $StressLifecycleCycleCount
        artifactBuildPerformed = if ($Mode -eq 'docker') {
            -not [bool]$SkipDockerBuild
        } else {
            -not [bool]$SkipBuild
        }
        artifactsPrevalidated = if ($Mode -eq 'host') {
            -not [bool]$SkipBuild -or [bool]$ArtifactsPrevalidated
        } else { $false }
        sourceCommit = $sourceCommit
        sourceWorkingTreeState = if ($sourceStatus.Count -eq 0) { 'clean' } else { 'dirty' }
        scriptSha256 = Get-FileSha256 $PSCommandPath
        dockerfileSha256 = if ($Mode -eq 'docker') {
            Get-FileSha256 (Join-Path $repositoryRoot 'tests/SharedMemoryStore.InteropTests/Dockerfile')
        } else { $null }
        dockerImage = if ($Mode -eq 'docker') { $DockerImage } else { $null }
        dockerImageId = if ($Mode -eq 'docker') { $DockerImageId } else { $null }
        artifactSetSha256 = if ($Mode -eq 'host') {
            $bytes = [Text.Encoding]::UTF8.GetBytes($canonicalArtifacts)
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        } else { $null }
        artifacts = $orderedArtifacts
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedEvidencePath) -Force | Out-Null
    [IO.File]::WriteAllText(
        $resolvedEvidencePath,
        ($report | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
}

if ($Docker) {
    $dockerCommand = (Get-Command docker -ErrorAction Stop).Source
    $dockerfile = Join-Path $repositoryRoot 'tests/SharedMemoryStore.InteropTests/Dockerfile'
    if (-not $SkipDockerBuild) {
        Invoke-Checked $dockerCommand 'build' '--file' $dockerfile '--tag' $DockerImage $repositoryRoot
    }

    $runArguments = @(
        'run', '--rm', '--shm-size', '256m',
        '--env', "SMS_RUN_INTEROP_STRESS=$([int]$Stress.IsPresent)",
        '--env', "SMS_INTEROP_STRESS_VALUES=$StressValueCount",
        '--env', "SMS_INTEROP_STRESS_LIFECYCLE_CYCLES=$StressLifecycleCycleCount",
        $DockerImage
    )
    Invoke-Checked $dockerCommand @runArguments
    $dockerImageId = (& $dockerCommand 'image' 'inspect' '--format' '{{.Id}}' $DockerImage |
        Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not bind Docker interoperability evidence to '$DockerImage'."
    }
    Write-InteroperabilityEvidence -Mode docker -DockerImageId $dockerImageId
    Write-Host 'Docker C# / C++ / Python interoperability validation passed.'
    return
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'validate-native.ps1') `
        -Configuration $Configuration `
        -BuildDirectory $BuildDirectory `
        -InstallDirectory $InstallDirectory `
        -CMakeExecutable $CMakeExecutable
    if ($LASTEXITCODE -ne 0) {
        throw 'Native install/current-artifact validation failed before interoperability.'
    }
    & (Join-Path $PSScriptRoot 'validate-python.ps1') `
        -Configuration $Configuration `
        -ArtifactsDirectory $PythonArtifactsDirectory `
        -CMakeExecutable $CMakeExecutable `
        -PythonExecutable $PythonExecutable
    if ($LASTEXITCODE -ne 0) {
        throw 'Installed Python wheel/sdist validation failed before interoperability.'
    }
    Invoke-Checked $dotnet 'build' $testProject '-c' $Configuration
}

$nativeAgent = Find-NativeAgent $buildPath $Configuration
$pythonCheckpointLibrary = Find-PythonCheckpointLibrary $buildPath $Configuration
$installedPython = if (-not $SkipBuild) {
    $installedPythonRelativePath = if ($IsWindows) {
        'wheel-environment/Scripts/python.exe'
    }
    else {
        'wheel-environment/bin/python'
    }
    Join-Path $pythonArtifactsPath $installedPythonRelativePath
}
else {
    (Get-Command $PythonExecutable -ErrorAction Stop).Source
}
if (-not (Test-Path -LiteralPath $installedPython -PathType Leaf)) {
    throw "The installed-wheel Python interpreter is missing: '$installedPython'."
}

$savedPythonPathForProbe = [Environment]::GetEnvironmentVariable('PYTHONPATH', 'Process')
try {
    [Environment]::SetEnvironmentVariable('PYTHONPATH', $null, 'Process')
    $pythonPackageRoot = (& $installedPython '-c' `
        'import pathlib, shared_memory_store; print(pathlib.Path(shared_memory_store.__file__).resolve().parent.parent)')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pythonPackageRoot)) {
        throw 'The installed-wheel Python interpreter could not import shared_memory_store.'
    }
    $pythonPackageRoot = [IO.Path]::GetFullPath(([string]$pythonPackageRoot).Trim())
    $nativePythonLibrary = (& $installedPython '-c' `
        'import shared_memory_store; print(shared_memory_store.native_library_path())')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath ([string]$nativePythonLibrary).Trim() -PathType Leaf)) {
        throw 'The installed Python wheel did not resolve its adjacent native library.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PYTHONPATH', $savedPythonPathForProbe, 'Process')
}

$savedEnvironment = @{
    SMS_CPP_AGENT = [Environment]::GetEnvironmentVariable('SMS_CPP_AGENT', 'Process')
    SMS_PYTHON_EXECUTABLE = [Environment]::GetEnvironmentVariable('SMS_PYTHON_EXECUTABLE', 'Process')
    SMS_PYTHONPATH = [Environment]::GetEnvironmentVariable('SMS_PYTHONPATH', 'Process')
    SMS_PYTHON_CHECKPOINT_LIBRARY = [Environment]::GetEnvironmentVariable('SMS_PYTHON_CHECKPOINT_LIBRARY', 'Process')
    SMS_RUN_INTEROP_STRESS = [Environment]::GetEnvironmentVariable('SMS_RUN_INTEROP_STRESS', 'Process')
    SMS_INTEROP_STRESS_VALUES = [Environment]::GetEnvironmentVariable('SMS_INTEROP_STRESS_VALUES', 'Process')
    SMS_INTEROP_STRESS_LIFECYCLE_CYCLES = [Environment]::GetEnvironmentVariable('SMS_INTEROP_STRESS_LIFECYCLE_CYCLES', 'Process')
}

try {
    [Environment]::SetEnvironmentVariable('SMS_CPP_AGENT', $nativeAgent, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_PYTHON_EXECUTABLE', $installedPython, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_PYTHONPATH', $pythonPackageRoot, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_PYTHON_CHECKPOINT_LIBRARY', $pythonCheckpointLibrary, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_RUN_INTEROP_STRESS', '0', 'Process')
    [Environment]::SetEnvironmentVariable('SMS_INTEROP_STRESS_VALUES', $StressValueCount.ToString([Globalization.CultureInfo]::InvariantCulture), 'Process')
    [Environment]::SetEnvironmentVariable('SMS_INTEROP_STRESS_LIFECYCLE_CYCLES', $StressLifecycleCycleCount.ToString([Globalization.CultureInfo]::InvariantCulture), 'Process')

    Write-Host 'Running the normal C# / C++ / Python interoperability suite.'
    Invoke-Checked $dotnet 'test' $testProject '-c' $Configuration '--no-build' '--filter' 'FullyQualifiedName!~StressInteropTests'
    if ($Stress) {
        [Environment]::SetEnvironmentVariable('SMS_RUN_INTEROP_STRESS', '1', 'Process')
        Write-Host "Running stress validation ($StressValueCount values per ordered pair; $StressLifecycleCycleCount mixed lifecycle cycles)."
        Invoke-Checked $dotnet 'test' $testProject '-c' $Configuration '--no-build' '--filter' 'FullyQualifiedName~StressInteropTests'
    }
}
finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

$artifactCandidates = [Collections.Generic.List[string]]::new()
$artifactCandidates.Add($nativeAgent)
$artifactCandidates.Add($pythonCheckpointLibrary)
$artifactCandidates.Add(([string]$nativePythonLibrary).Trim())
foreach ($rootPath in @($installPath, (Join-Path $pythonPackageRoot 'shared_memory_store'))) {
    if (Test-Path -LiteralPath $rootPath -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force)) {
            $artifactCandidates.Add($file.FullName)
        }
    }
}
if (Test-Path -LiteralPath $pythonArtifactsPath -PathType Container) {
    foreach ($file in @(Get-ChildItem -LiteralPath $pythonArtifactsPath -Recurse -File -Force |
        Where-Object { $_.Extension -in @('.whl', '.gz') })) {
        $artifactCandidates.Add($file.FullName)
    }
}
Write-InteroperabilityEvidence -Mode host -ArtifactPaths @($artifactCandidates)
Write-Host 'Host C# / C++ / Python interoperability validation passed.'
