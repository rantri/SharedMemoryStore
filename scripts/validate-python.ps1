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
$requestedWorkPath = if ([IO.Path]::IsPathRooted($ArtifactsDirectory)) {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArtifactsDirectory))
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

function Test-MultiConfigBuild {
    param([Parameter(Mandatory)][string]$BuildPath)

    $cachePath = Join-Path $BuildPath 'CMakeCache.txt'
    $entry = Get-Content -LiteralPath $cachePath |
        Where-Object { $_.StartsWith('CMAKE_CONFIGURATION_TYPES:', [StringComparison]::Ordinal) } |
        Select-Object -First 1
    if ($null -eq $entry) {
        return $false
    }

    $separator = $entry.IndexOf('=')
    return $separator -ge 0 -and -not [string]::IsNullOrWhiteSpace($entry.Substring($separator + 1))
}

function Reset-ArtifactScratchDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $normalizedRoot = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    $normalizedArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $artifactsPrefix = $normalizedArtifactsRoot + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if (-not $target.StartsWith($artifactsPrefix, $comparison)) {
        throw "Python validation output must stay below '$normalizedArtifactsRoot'; received '$target'."
    }

    $relativeTarget = [IO.Path]::GetRelativePath($normalizedRoot, $target).Replace('\', '/')
    $git = (Get-Command git -ErrorAction Stop).Source
    & $git -C $normalizedRoot check-ignore --quiet --no-index -- $relativeTarget 2>$null
    $ignoreExitCode = $LASTEXITCODE
    if ($ignoreExitCode -eq 1) {
        throw "Python validation scratch path must be covered by repository ignore rules: '$relativeTarget'."
    }
    if ($ignoreExitCode -ne 0) {
        throw "Could not verify repository ignore coverage for Python validation scratch path '$relativeTarget'."
    }
    $protectedEntries = @(
        & $git -C $normalizedRoot --literal-pathspecs ls-files --cached --others --exclude-standard -- $relativeTarget 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not prove that Python validation scratch path '$relativeTarget' contains only ignored output."
    }
    if ($protectedEntries.Count -ne 0) {
        throw "Refusing to reset Python validation scratch path '$relativeTarget' because it contains tracked or nonignored entry '$($protectedEntries[0])'."
    }

    $ancestor = [IO.Path]::GetDirectoryName($target)
    while (-not (Test-Path -LiteralPath $ancestor)) {
        $parent = [IO.Directory]::GetParent($ancestor)
        if ($null -eq $parent) {
            throw "Repository root was not reached while checking Python validation scratch path '$target'."
        }
        $ancestor = $parent.FullName
    }
    while ($true) {
        $ancestorItem = Get-Item -LiteralPath $ancestor -Force
        if (-not $ancestorItem.PSIsContainer `
            -or ($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to reset Python validation through non-directory or linked ancestor '$ancestor'."
        }
        if ($ancestor.Equals($normalizedRoot, $comparison)) {
            break
        }
        if (-not $ancestor.StartsWith($rootPrefix, $comparison)) {
            throw "Python validation ancestor escaped the repository while checking '$target'."
        }
        $parent = [IO.Directory]::GetParent($ancestor)
        if ($null -eq $parent) {
            throw "Repository root was not reached while checking Python validation scratch path '$target'."
        }
        $ancestor = $parent.FullName
    }

    $item = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -or (Test-Path -LiteralPath $target)) {
        if ($null -eq $item) {
            throw "Refusing to reset an uninspectable Python validation scratch path '$target'."
        }
        if (-not $item.PSIsContainer `
            -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to reset non-directory or linked Python validation scratch path '$target'."
        }
        $linkedDescendants = @(Get-ChildItem -LiteralPath $target -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Sort-Object { $_.FullName.Length } -Descending)
        foreach ($linkedDescendant in $linkedDescendants) {
            Remove-Item -LiteralPath $linkedDescendant.FullName -Force
            if (Test-Path -LiteralPath $linkedDescendant.FullName) {
                throw "Could not unlink Python validation entry '$($linkedDescendant.FullName)'."
            }
        }
        Remove-Item -LiteralPath $target -Recurse -Force
        $remainingItem = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if ($null -ne $remainingItem -or (Test-Path -LiteralPath $target)) {
            throw "Python validation scratch path remained after guarded reset: '$target'."
        }
    }

    return (New-Item -ItemType Directory -Path $target -Force).FullName
}

function New-UnrelatedRunDirectory {
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $normalizedRoot = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $temporaryPrefix = $temporaryRoot + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $temporaryRoot (
        'shared-memory-store-python-validation-' + [Guid]::NewGuid().ToString('N'))))

    if (-not $candidate.StartsWith($temporaryPrefix, $comparison)) {
        throw "Python validation unrelated run directory escaped the OS temporary root: '$candidate'."
    }
    if ($candidate.Equals($normalizedRoot, $comparison) -or $candidate.StartsWith($rootPrefix, $comparison)) {
        throw "Python validation unrelated run directory must be outside the repository: '$candidate'."
    }

    return (New-Item -ItemType Directory -Path $candidate).FullName
}

function Remove-UnrelatedRunDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $temporaryPrefix = $temporaryRoot + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if (-not $target.StartsWith($temporaryPrefix, $comparison)) {
        throw "Refusing to remove unrelated run directory outside the OS temporary root: '$target'."
    }

    $item = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }
    if (-not $item.PSIsContainer `
        -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove non-directory or linked unrelated run path '$target'."
    }
    Remove-Item -LiteralPath $target -Recurse -Force
    if (Test-Path -LiteralPath $target) {
        throw "Python validation unrelated run directory remained after cleanup: '$target'."
    }
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

$workPath = Reset-ArtifactScratchDirectory $requestedWorkPath
$nativeBuildPath = Join-Path $workPath 'native-build'
$stagedSourcePath = Join-Path $workPath 'source-package'
$buildEnvironmentPath = Join-Path $workPath 'build-environment'
$wheelEnvironmentPath = Join-Path $workPath 'wheel-environment'
$distributionPath = Join-Path $workPath 'dist'
New-Item -ItemType Directory -Path $stagedSourcePath, $distributionPath -Force | Out-Null

$cmake = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
$python = (Get-Command $PythonExecutable -ErrorAction Stop).Source
$nativeLibraryName = if ($IsWindows) { 'shared_memory_store.dll' } else { 'libshared_memory_store.so' }
$expectedPythonFiles = @('__init__.py', '_native.py', 'enums.py', 'store.py')

$configureArguments = @(
    '-S', $repositoryRoot,
    '-B', $nativeBuildPath,
    "-DCMAKE_BUILD_TYPE=$Configuration",
    '-DSMS_BUILD_TESTS=OFF',
    '-DSMS_BUILD_SAMPLES=OFF',
    '-DSMS_INSTALL=ON',
    '-DSMS_PYTHON_INSTALL_DIR=shared_memory_store'
)

Invoke-Checked $cmake @configureArguments
$multiConfig = Test-MultiConfigBuild $nativeBuildPath
$buildArguments = @('--build', $nativeBuildPath)
if ($multiConfig) {
    $buildArguments += @('--config', $Configuration)
}
$buildArguments += '--parallel'
Invoke-Checked $cmake @buildArguments
$installArguments = @('--install', $nativeBuildPath)
if ($multiConfig) {
    $installArguments += @('--config', $Configuration)
}
$installArguments += @('--component', 'Python', '--prefix', $stagedSourcePath)
Invoke-Checked $cmake @installArguments

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
$unrelatedRunPath = New-UnrelatedRunDirectory
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
    Remove-UnrelatedRunDirectory $unrelatedRunPath
}

Write-Host "Python SharedMemoryStore validation passed for $($wheels[0].Name)."
