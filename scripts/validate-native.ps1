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
$requestedBuildPath = if ([IO.Path]::IsPathRooted($BuildDirectory)) {
    [IO.Path]::GetFullPath($BuildDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $BuildDirectory))
}
$requestedInstallPath = if ([IO.Path]::IsPathRooted($InstallDirectory)) {
    [IO.Path]::GetFullPath($InstallDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $InstallDirectory))
}

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

function Reset-RepositoryScratchDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description)

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $normalizedRoot = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if (-not $target.StartsWith($rootPrefix, $comparison)) {
        throw "$Description must be a scratch directory inside the repository: '$target'."
    }

    $relativeTarget = [IO.Path]::GetRelativePath($normalizedRoot, $target).Replace('\', '/')
    $git = (Get-Command git -ErrorAction Stop).Source
    & $git -C $normalizedRoot check-ignore --quiet --no-index -- $relativeTarget 2>$null
    $ignoreExitCode = $LASTEXITCODE
    if ($ignoreExitCode -eq 1) {
        throw "$Description must be covered by repository ignore rules: '$relativeTarget'."
    }
    if ($ignoreExitCode -ne 0) {
        throw "Could not verify repository ignore coverage for $Description '$relativeTarget'."
    }
    $protectedEntries = @(
        & $git -C $normalizedRoot --literal-pathspecs ls-files --cached --others --exclude-standard -- $relativeTarget 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not prove that $Description '$relativeTarget' contains only ignored scratch output."
    }
    if ($protectedEntries.Count -ne 0) {
        throw "Refusing to reset $Description '$relativeTarget' because it contains tracked or nonignored entry '$($protectedEntries[0])'."
    }

    $ancestor = [IO.Path]::GetDirectoryName($target)
    while (-not (Test-Path -LiteralPath $ancestor)) {
        $parent = [IO.Directory]::GetParent($ancestor)
        if ($null -eq $parent) {
            throw "Repository root was not reached while checking $Description '$target'."
        }
        $ancestor = $parent.FullName
    }
    while ($true) {
        $ancestorItem = Get-Item -LiteralPath $ancestor -Force
        if (-not $ancestorItem.PSIsContainer `
            -or ($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to reset $Description through non-directory or linked ancestor '$ancestor'."
        }
        if ($ancestor.Equals($normalizedRoot, $comparison)) {
            break
        }
        if (-not $ancestor.StartsWith($rootPrefix, $comparison)) {
            throw "$Description ancestor escaped the repository while checking '$target'."
        }
        $parent = [IO.Directory]::GetParent($ancestor)
        if ($null -eq $parent) {
            throw "Repository root was not reached while checking $Description '$target'."
        }
        $ancestor = $parent.FullName
    }

    $item = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -or (Test-Path -LiteralPath $target)) {
        if ($null -eq $item) {
            throw "Refusing to reset an uninspectable $Description '$target'."
        }
        if (-not $item.PSIsContainer `
            -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to reset non-directory or linked $Description '$target'."
        }
        $linkedDescendants = @(Get-ChildItem -LiteralPath $target -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Sort-Object { $_.FullName.Length } -Descending)
        foreach ($linkedDescendant in $linkedDescendants) {
            Remove-Item -LiteralPath $linkedDescendant.FullName -Force
            if (Test-Path -LiteralPath $linkedDescendant.FullName) {
                throw "Could not unlink $Description entry '$($linkedDescendant.FullName)'."
            }
        }
        Remove-Item -LiteralPath $target -Recurse -Force
        $remainingItem = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if ($null -ne $remainingItem -or (Test-Path -LiteralPath $target)) {
            throw "$Description remained after guarded reset: '$target'."
        }
    }

    return (New-Item -ItemType Directory -Path $target -Force).FullName
}

$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$buildPrefix = $requestedBuildPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$installPrefix = $requestedInstallPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($requestedBuildPath.Equals($requestedInstallPath, $comparison) `
    -or $requestedBuildPath.StartsWith($installPrefix, $comparison) `
    -or $requestedInstallPath.StartsWith($buildPrefix, $comparison)) {
    throw 'Native build and install scratch directories must be separate and non-nested.'
}

$buildPath = Reset-RepositoryScratchDirectory $requestedBuildPath 'native build directory'
$installPath = Reset-RepositoryScratchDirectory $requestedInstallPath 'native install directory'

$cmakeCommand = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
$ctestName = if ($IsWindows) { 'ctest.exe' } else { 'ctest' }
$ctestCommand = Join-Path (Split-Path -Parent $cmakeCommand) $ctestName
if (-not (Test-Path -LiteralPath $ctestCommand)) {
    $ctestCommand = (Get-Command $ctestName -ErrorAction Stop).Source
}

$configureArguments = @(
    '-S', $repositoryRoot,
    '-B', $buildPath,
    "-DCMAKE_BUILD_TYPE=$Configuration",
    '-DSMS_BUILD_TESTS=ON',
    '-DSMS_BUILD_SAMPLES=ON',
    '-DSMS_BUILD_STATIC=ON'
)

Invoke-Checked $cmakeCommand @configureArguments
$multiConfig = Test-MultiConfigBuild $buildPath
$buildArguments = @('--build', $buildPath)
if ($multiConfig) {
    $buildArguments += @('--config', $Configuration)
}
$buildArguments += '--parallel'
Invoke-Checked $cmakeCommand @buildArguments

if (-not $SkipTests) {
    $testArguments = @('--test-dir', $buildPath)
    if ($multiConfig) {
        $testArguments += @('-C', $Configuration)
    }
    $testArguments += '--output-on-failure'
    Invoke-Checked $ctestCommand @testArguments
}

$installArguments = @('--install', $buildPath)
if ($multiConfig) {
    $installArguments += @('--config', $Configuration)
}
$installArguments += @('--prefix', $installPath)
Invoke-Checked $cmakeCommand @installArguments

if (-not $SkipConsumer) {
    $consumerBuild = Join-Path $buildPath 'package-consumer'
    $consumerArguments = @(
        '-S', (Join-Path $repositoryRoot 'tests/cpp/package_consumer'),
        '-B', $consumerBuild,
        "-DCMAKE_BUILD_TYPE=$Configuration",
        "-DCMAKE_PREFIX_PATH=$installPath"
    )

    Invoke-Checked $cmakeCommand @consumerArguments
    $consumerIsMultiConfig = Test-MultiConfigBuild $consumerBuild
    $consumerBuildArguments = @('--build', $consumerBuild)
    if ($consumerIsMultiConfig) {
        $consumerBuildArguments += @('--config', $Configuration)
    }
    $consumerBuildArguments += '--parallel'
    Invoke-Checked $cmakeCommand @consumerBuildArguments
    $consumerName = if ($IsWindows) { 'shared_memory_store_package_consumer.exe' } else { 'shared_memory_store_package_consumer' }
    $consumerPath = if ($consumerIsMultiConfig) {
        Join-Path (Join-Path $consumerBuild $Configuration) $consumerName
    }
    else {
        Join-Path $consumerBuild $consumerName
    }
    if (-not (Test-Path -LiteralPath $consumerPath -PathType Leaf)) {
        throw 'The installed-package consumer executable was not produced.'
    }

    Invoke-Checked $consumerPath
}

Write-Host 'Native SharedMemoryStore validation passed.'
