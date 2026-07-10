[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory = 'artifacts/native-build',
    [string]$InstallDirectory = 'artifacts/native-install',
    [string]$CMakeExecutable = 'cmake',
    [switch]$SkipTests,
    [switch]$SkipConsumer
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $BuildDirectory))
$installPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $InstallDirectory))

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,
        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

$cmakeCommand = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
$ctestName = if ($IsWindows) { 'ctest.exe' } else { 'ctest' }
$ctestCommand = Join-Path (Split-Path -Parent $cmakeCommand) $ctestName
if (-not (Test-Path -LiteralPath $ctestCommand)) {
    $ctestCommand = (Get-Command $ctestName -ErrorAction Stop).Source
}

$configureArguments = @(
    '-S', $repositoryRoot,
    '-B', $buildPath,
    '-DSMS_BUILD_TESTS=ON',
    '-DSMS_BUILD_SAMPLES=ON',
    '-DSMS_BUILD_STATIC=ON'
)
if (-not $IsWindows) {
    $configureArguments += "-DCMAKE_BUILD_TYPE=$Configuration"
}

Invoke-Checked $cmakeCommand @configureArguments
Invoke-Checked $cmakeCommand '--build' $buildPath '--config' $Configuration '--parallel'

if (-not $SkipTests) {
    Invoke-Checked $ctestCommand '--test-dir' $buildPath '-C' $Configuration '--output-on-failure'
}

Invoke-Checked $cmakeCommand '--install' $buildPath '--config' $Configuration '--prefix' $installPath

if (-not $SkipConsumer) {
    $consumerBuild = Join-Path $buildPath 'package-consumer'
    $consumerArguments = @(
        '-S', (Join-Path $repositoryRoot 'tests/cpp/package_consumer'),
        '-B', $consumerBuild,
        "-DCMAKE_PREFIX_PATH=$installPath"
    )
    if (-not $IsWindows) {
        $consumerArguments += "-DCMAKE_BUILD_TYPE=$Configuration"
    }

    Invoke-Checked $cmakeCommand @consumerArguments
    Invoke-Checked $cmakeCommand '--build' $consumerBuild '--config' $Configuration '--parallel'
    $consumer = Get-ChildItem -LiteralPath $consumerBuild -Recurse -File |
        Where-Object { $_.Name -in @('shared_memory_store_package_consumer', 'shared_memory_store_package_consumer.exe') } |
        Select-Object -First 1
    if ($null -eq $consumer) {
        throw 'The installed-package consumer executable was not produced.'
    }

    Invoke-Checked $consumer.FullName
}

Write-Host 'Native SharedMemoryStore validation passed.'
