[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$ArtifactsDirectory = 'artifacts/python-validation',
    [string]$CMakeExecutable = 'cmake',
    [string]$PythonExecutable = 'python'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$workPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArtifactsDirectory))

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

function Assert-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Validation output must stay below '$artifactsRoot'; received '$fullPath'."
    }

    return $fullPath
}

function Reset-Directory {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = Assert-ArtifactPath $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    return $fullPath
}

function Get-VenvPython {
    param([Parameter(Mandatory)][string]$EnvironmentPath)

    $relative = if ($IsWindows) { 'Scripts/python.exe' } else { 'bin/python' }
    $candidate = Join-Path $EnvironmentPath $relative
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The virtual-environment interpreter was not created: '$candidate'."
    }
    return $candidate
}

function Assert-NativeSet {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][string]$Description
    )

    $nativeFiles = @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object { $_.Extension -in @('.dll', '.so', '.dylib') }
    )
    if ($nativeFiles.Count -ne 1 -or $nativeFiles[0].Name -cne $ExpectedName) {
        $actual = if ($nativeFiles.Count -eq 0) { '<none>' } else { ($nativeFiles.FullName -join ', ') }
        throw "$Description must contain exactly '$ExpectedName' and no opposite-platform native binary; found $actual."
    }
}

function Assert-ArchiveContainsSuffix {
    param(
        [Parameter(Mandatory)][string[]]$Entries,
        [Parameter(Mandatory)][string]$Suffix,
        [Parameter(Mandatory)][string]$Description
    )

    $normalizedSuffix = $Suffix.Replace('\', '/')
    if (-not ($Entries | Where-Object { $_.Replace('\', '/').EndsWith($normalizedSuffix, [StringComparison]::Ordinal) })) {
        throw "$Description is missing required entry '*$normalizedSuffix'."
    }
}

$workPath = Reset-Directory $workPath
$nativeBuildPath = Join-Path $workPath 'native-build'
$stagedSourcePath = Join-Path $workPath 'source-package'
$buildEnvironmentPath = Join-Path $workPath 'build-environment'
$wheelEnvironmentPath = Join-Path $workPath 'wheel-environment'
$distributionPath = Join-Path $workPath 'dist'
$unrelatedRunPath = Join-Path $workPath 'unrelated-run-directory'
New-Item -ItemType Directory -Path $stagedSourcePath, $distributionPath, $unrelatedRunPath -Force | Out-Null

$cmake = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
$python = (Get-Command $PythonExecutable -ErrorAction Stop).Source
$nativeLibraryName = if ($IsWindows) { 'shared_memory_store.dll' } else { 'libshared_memory_store.so' }
$expectedPythonFiles = @('__init__.py', '_native.py', 'enums.py', 'store.py')

$configureArguments = @(
    '-S', $repositoryRoot,
    '-B', $nativeBuildPath,
    '-DSMS_BUILD_TESTS=OFF',
    '-DSMS_BUILD_SAMPLES=OFF',
    '-DSMS_INSTALL=ON',
    '-DSMS_PYTHON_INSTALL_DIR=shared_memory_store'
)
if (-not $IsWindows) {
    $configureArguments += "-DCMAKE_BUILD_TYPE=$Configuration"
}

Invoke-Checked $cmake @configureArguments
Invoke-Checked $cmake '--build' $nativeBuildPath '--config' $Configuration '--parallel'
Invoke-Checked $cmake '--install' $nativeBuildPath '--config' $Configuration '--component' 'Python' '--prefix' $stagedSourcePath

$stagedPackagePath = Join-Path $stagedSourcePath 'shared_memory_store'
foreach ($file in $expectedPythonFiles) {
    $candidate = Join-Path $stagedPackagePath $file
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The staged source package is missing '$candidate'."
    }
}
Assert-NativeSet $stagedPackagePath $nativeLibraryName 'The staged source package'

$savedPythonPath = [Environment]::GetEnvironmentVariable('PYTHONPATH', 'Process')
$savedInstalledGate = [Environment]::GetEnvironmentVariable('SMS_TEST_INSTALLED_PACKAGE', 'Process')
try {
    [Environment]::SetEnvironmentVariable('PYTHONPATH', $stagedSourcePath, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_TEST_INSTALLED_PACKAGE', '0', 'Process')
    Invoke-Checked $python '-m' 'unittest' 'discover' '-s' (Join-Path $repositoryRoot 'tests/python') '-v'
}
finally {
    [Environment]::SetEnvironmentVariable('PYTHONPATH', $savedPythonPath, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_TEST_INSTALLED_PACKAGE', $savedInstalledGate, 'Process')
}

Invoke-Checked $python '-m' 'venv' $buildEnvironmentPath
$buildPython = Get-VenvPython $buildEnvironmentPath
Invoke-Checked $buildPython '-m' 'pip' 'install' '--upgrade' 'pip' 'build'
Invoke-Checked $buildPython '-m' 'build' '--wheel' '--sdist' '--outdir' $distributionPath $repositoryRoot

$wheels = @(Get-ChildItem -LiteralPath $distributionPath -Filter '*.whl' -File)
$sdists = @(Get-ChildItem -LiteralPath $distributionPath -Filter '*.tar.gz' -File)
if ($wheels.Count -ne 1) {
    throw "Expected exactly one wheel in '$distributionPath'; found $($wheels.Count)."
}
if ($sdists.Count -ne 1) {
    throw "Expected exactly one source distribution in '$distributionPath'; found $($sdists.Count)."
}
if ($wheels[0].Name -notmatch '-py3-none-' -or $wheels[0].Name -match '-any\.whl$') {
    throw "The ctypes distribution must be a platform wheel with a generic Python 3 ABI tag: '$($wheels[0].Name)'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($wheels[0].FullName)
try {
    $wheelEntries = @($archive.Entries | ForEach-Object { $_.FullName })
}
finally {
    $archive.Dispose()
}
Assert-ArchiveContainsSuffix $wheelEntries "shared_memory_store/$nativeLibraryName" 'The wheel'
foreach ($file in $expectedPythonFiles) {
    Assert-ArchiveContainsSuffix $wheelEntries "shared_memory_store/$file" 'The wheel'
}
$wheelNativeEntries = @($wheelEntries | Where-Object { $_ -match '\.(dll|so|dylib)$' })
if ($wheelNativeEntries.Count -ne 1 -or -not $wheelNativeEntries[0].EndsWith("/$nativeLibraryName", [StringComparison]::Ordinal)) {
    throw "The wheel must contain exactly one '$nativeLibraryName' native artifact; found $($wheelNativeEntries -join ', ')."
}

$tar = (Get-Command tar -ErrorAction Stop).Source
$sdistEntries = @(& $tar '-tf' $sdists[0].FullName)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect source distribution '$($sdists[0].FullName)'."
}
foreach ($suffix in @(
    'pyproject.toml',
    'CMakeLists.txt',
    'src/cpp/CMakeLists.txt',
    'src/cpp/include/shared_memory_store/c_api.h',
    'src/python/shared_memory_store/__init__.py',
    'src/python/shared_memory_store/_native.py',
    'src/python/shared_memory_store/enums.py',
    'src/python/shared_memory_store/store.py',
    'protocol/compatibility.json',
    'LICENSE',
    'README.md'
)) {
    Assert-ArchiveContainsSuffix $sdistEntries $suffix 'The source distribution'
}
$sdistNativeEntries = @($sdistEntries | Where-Object { $_ -match '\.(dll|so|dylib)$' })
if ($sdistNativeEntries.Count -ne 0) {
    throw "The source distribution must not contain compiled binaries; found $($sdistNativeEntries -join ', ')."
}

# Prove the explicit source distribution contains everything needed to compile a
# wheel, independent of files that happen to exist in the checkout.
$sdistWheelPath = Join-Path $workPath 'sdist-wheel'
New-Item -ItemType Directory -Path $sdistWheelPath -Force | Out-Null
Invoke-Checked $buildPython '-m' 'pip' 'wheel' '--no-deps' '--wheel-dir' $sdistWheelPath $sdists[0].FullName
$sdistWheels = @(Get-ChildItem -LiteralPath $sdistWheelPath -Filter '*.whl' -File)
if ($sdistWheels.Count -ne 1) {
    throw "Building the source distribution should produce exactly one wheel; found $($sdistWheels.Count)."
}
$sdistArchive = [IO.Compression.ZipFile]::OpenRead($sdistWheels[0].FullName)
try {
    $sdistWheelEntries = @($sdistArchive.Entries | ForEach-Object { $_.FullName })
}
finally {
    $sdistArchive.Dispose()
}
$sdistWheelNativeEntries = @($sdistWheelEntries | Where-Object { $_ -match '\.(dll|so|dylib)$' })
if ($sdistWheelNativeEntries.Count -ne 1 -or -not $sdistWheelNativeEntries[0].EndsWith("/$nativeLibraryName", [StringComparison]::Ordinal)) {
    throw "The wheel built from the sdist must contain exactly one '$nativeLibraryName'; found $($sdistWheelNativeEntries -join ', ')."
}
foreach ($file in $expectedPythonFiles) {
    Assert-ArchiveContainsSuffix $sdistWheelEntries "shared_memory_store/$file" 'The wheel built from the source distribution'
}

Invoke-Checked $python '-m' 'venv' $wheelEnvironmentPath
$wheelPython = Get-VenvPython $wheelEnvironmentPath
Invoke-Checked $wheelPython '-m' 'pip' 'install' '--no-deps' $sdistWheels[0].FullName

$savedPythonPath = [Environment]::GetEnvironmentVariable('PYTHONPATH', 'Process')
$savedInstalledGate = [Environment]::GetEnvironmentVariable('SMS_TEST_INSTALLED_PACKAGE', 'Process')
try {
    [Environment]::SetEnvironmentVariable('PYTHONPATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_TEST_INSTALLED_PACKAGE', '1', 'Process')
    Push-Location $unrelatedRunPath
    try {
        Invoke-Checked $wheelPython '-m' 'unittest' 'discover' '-s' (Join-Path $repositoryRoot 'tests/python') '-v'
        Invoke-Checked $wheelPython (Join-Path $repositoryRoot 'samples/PythonBasicUsage/main.py')
    }
    finally {
        Pop-Location
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PYTHONPATH', $savedPythonPath, 'Process')
    [Environment]::SetEnvironmentVariable('SMS_TEST_INSTALLED_PACKAGE', $savedInstalledGate, 'Process')
}

Write-Host "Python SharedMemoryStore validation passed for $($wheels[0].Name)."
