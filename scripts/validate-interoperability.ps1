[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory = 'artifacts/interop-native',
    [string]$CMakeExecutable = 'cmake',
    [string]$PythonExecutable = 'python',
    [switch]$SkipBuild,
    [switch]$Stress,
    [ValidateRange(1, 100000)]
    [int]$StressValueCount = 1000,
    [ValidateRange(1, 100000)]
    [int]$StressLifecycleCycleCount = 10000,
    [switch]$Docker,
    [switch]$SkipDockerBuild,
    [string]$DockerImage = 'shared-memory-store-interop:local'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $BuildDirectory))
$testProject = Join-Path $repositoryRoot 'tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj'

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
    Write-Host 'Docker C# / C++ / Python interoperability validation passed.'
    return
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$python = (Get-Command $PythonExecutable -ErrorAction Stop).Source

if (-not $SkipBuild) {
    $cmake = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
    $ctestName = if ($IsWindows) { 'ctest.exe' } else { 'ctest' }
    $ctest = Join-Path (Split-Path -Parent $cmake) $ctestName
    if (-not (Test-Path -LiteralPath $ctest -PathType Leaf)) {
        $ctest = (Get-Command $ctestName -ErrorAction Stop).Source
    }

    $configureArguments = @(
        '-S', $repositoryRoot,
        '-B', $buildPath,
        '-DSMS_BUILD_TESTS=ON',
        '-DSMS_BUILD_SAMPLES=OFF',
        '-DSMS_INSTALL=ON',
        '-DSMS_PYTHON_INSTALL_DIR=shared_memory_store'
    )
    if (-not $IsWindows) {
        $configureArguments += "-DCMAKE_BUILD_TYPE=$Configuration"
    }

    Invoke-Checked $cmake @configureArguments
    Invoke-Checked $cmake '--build' $buildPath '--config' $Configuration '--parallel'
    Invoke-Checked $ctest '--test-dir' $buildPath '-C' $Configuration '--output-on-failure'
    Invoke-Checked $cmake '--install' $buildPath '--config' $Configuration '--component' 'Python' '--prefix' (Join-Path $repositoryRoot 'src/python')
    Invoke-Checked $dotnet 'build' $testProject '-c' $Configuration
}

$nativeAgent = Find-NativeAgent $buildPath $Configuration
$nativeLibraryName = if ($IsWindows) { 'shared_memory_store.dll' } else { 'libshared_memory_store.so' }
$nativePythonLibrary = Join-Path $repositoryRoot "src/python/shared_memory_store/$nativeLibraryName"
if (-not (Test-Path -LiteralPath $nativePythonLibrary -PathType Leaf)) {
    throw "The Python package-adjacent native library is missing: '$nativePythonLibrary'."
}

$savedEnvironment = @{
    SMS_CPP_AGENT = [Environment]::GetEnvironmentVariable('SMS_CPP_AGENT', 'Process')
    SMS_PYTHON_EXECUTABLE = [Environment]::GetEnvironmentVariable('SMS_PYTHON_EXECUTABLE', 'Process')
    SMS_RUN_INTEROP_STRESS = [Environment]::GetEnvironmentVariable('SMS_RUN_INTEROP_STRESS', 'Process')
    SMS_INTEROP_STRESS_VALUES = [Environment]::GetEnvironmentVariable('SMS_INTEROP_STRESS_VALUES', 'Process')
    SMS_INTEROP_STRESS_LIFECYCLE_CYCLES = [Environment]::GetEnvironmentVariable('SMS_INTEROP_STRESS_LIFECYCLE_CYCLES', 'Process')
}

try {
    [Environment]::SetEnvironmentVariable('SMS_CPP_AGENT', $nativeAgent, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_PYTHON_EXECUTABLE', $python, 'Process')
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

Write-Host 'Host C# / C++ / Python interoperability validation passed.'
