[CmdletBinding()]
param(
    [ValidateSet(
        'self-test', 'architecture', 'atomic', 'raw', 'no-lock', 'crash',
        'release-tests', 'interop', 'samples', 'pack', 'all')]
    [string]$Command = 'all',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = '',
    [string]$DockerRuntimeImage = 'mcr.microsoft.com/dotnet/runtime:10.0',
    [ValidateRange(1, 86400)]
    [int]$StepTimeoutSeconds = 21600,
    [switch]$SkipDocker,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$git = (Get-Command git -ErrorAction Stop).Source
if (-not $ValidateOnly) {
    $earlyStatus = @(& $git -C $root `
        '-c' 'core.autocrlf=true' `
        '-c' 'core.safecrlf=false' `
        'status' '--porcelain=v2' '--untracked-files=normal' 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw 'Executable OS qualification could not determine repository cleanliness.'
    }
    if ($earlyStatus.Count -ne 0) {
        throw 'Executable OS qualification requires a clean working tree.'
    }
}
$runStartedUtc = [DateTimeOffset]::UtcNow
$platform = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { 'unsupported' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' +
        "$platform-$architecture-$Command-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputPath = "artifacts/lock-free-os-validation/$runId.json"
}
$resultPath = if ([IO.Path]::IsPathFullyQualified($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
}
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not ($resultPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase))) {
    throw "OS validation output must remain below '$artifactRoot'."
}
if (Test-Path -LiteralPath $resultPath) {
    throw "Refusing to overwrite historical OS validation evidence '$resultPath'."
}
New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null
$evidenceRoot = Join-Path (Split-Path -Parent $resultPath) ([IO.Path]::GetFileNameWithoutExtension($resultPath) + '.evidence')
if (Test-Path -LiteralPath $evidenceRoot) {
    throw "Refusing to overwrite historical OS validation evidence directory '$evidenceRoot'."
}
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null

$results = [Collections.Generic.List[object]]::new()
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$qualifiedArchitecture = $architecture -eq 'x64' -and $platform -in @('windows', 'linux')
$integrationProject = 'tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj'
$contractProject = 'tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj'

function Get-StringSha256 {
    param([AllowEmptyString()][string]$Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Invoke-TextCommand {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments)

    try {
        $output = & $FileName @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) {
            return 'unknown'
        }
        return (($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
    }
    catch {
        return 'unknown'
    }
}

function Get-SourceManifestSha256 {
    $discoveredPaths = @(& $git `
        -c core.autocrlf=true `
        -c core.safecrlf=false `
        -C $root `
        ls-files --cached --others --exclude-standard 2>$null)
    if ($LASTEXITCODE -ne 0) {
        return 'unknown'
    }

    # Source provenance must be identical for the same Git content on Windows
    # and Linux regardless of either host's ambient core.autocrlf setting.
    # Use an ordinal path set/order and an explicit clean-filter policy.
    $pathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($discoveredPath in $discoveredPaths) {
        if (-not [string]::IsNullOrEmpty([string]$discoveredPath)) {
            $null = $pathSet.Add([string]$discoveredPath)
        }
    }
    $orderedPaths = [Collections.Generic.List[string]]::new($pathSet)
    $orderedPaths.Sort([StringComparer]::Ordinal)
    $paths = @($orderedPaths)

    $existing = @($paths | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    $hashes = if ($existing.Count -eq 0) {
        @()
    }
    else {
        @($existing | & $git `
            -c core.autocrlf=true `
            -c core.safecrlf=false `
            -C $root `
            hash-object --stdin-paths 2>$null)
    }
    if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne $existing.Count) {
        return 'unknown'
    }
    $hashByPath = @{}
    for ($index = 0; $index -lt $existing.Count; $index++) {
        $hashByPath[$existing[$index]] = [string]$hashes[$index]
    }
    $entries = foreach ($path in $paths) {
        "$path`0$(if ($hashByPath.ContainsKey($path)) { $hashByPath[$path] } else { 'missing' })"
    }
    return Get-StringSha256 ($entries -join "`n")
}

function Remove-SolutionProjectBuildOutputs {
    param([Parameter(Mandatory)][string]$ReportPath)

    $solutionPath = Join-Path $root 'SharedMemoryStore.slnx'
    [xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
    $projects = @(
        $solution.SelectNodes('//Project[@Path]') |
            ForEach-Object { [string]$_.Path } |
            Where-Object { [IO.Path]::GetExtension($_).Equals('.csproj', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object -Unique)
    if ($projects.Count -eq 0) {
        throw 'SharedMemoryStore.slnx does not contain any C# projects.'
    }

    $repositoryProjects = @(
        & $git -C $root ls-files --cached --others --exclude-standard -- '*.csproj' 2>$null |
            Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0 -or $repositoryProjects.Count -eq 0) {
        throw 'Could not enumerate tracked and nonignored repository project files.'
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $repositoryProjectSet = [Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($repositoryProject in $repositoryProjects) {
        $null = $repositoryProjectSet.Add($repositoryProject.Replace('\', '/'))
    }
    $normalizedProjects = @($projects | ForEach-Object { $_.Replace('\', '/') } | Sort-Object -Unique)
    $solutionProjectSet = [Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($normalizedProjectPath in $normalizedProjects) {
        $null = $solutionProjectSet.Add($normalizedProjectPath)
    }
    $outsideSolution = @($repositoryProjectSet | Where-Object { -not $solutionProjectSet.Contains($_) })
    $missingFromRepository = @($solutionProjectSet | Where-Object { -not $repositoryProjectSet.Contains($_) })
    if ($outsideSolution.Count -ne 0 -or $missingFromRepository.Count -ne 0) {
        throw "SharedMemoryStore.slnx must contain every tracked or nonignored C# project; outsideSolution='$($outsideSolution -join ',')'; missingFromRepository='$($missingFromRepository -join ',')'."
    }

    $normalizedRoot = [IO.Path]::GetFullPath($root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    $removed = 0
    $targetRecords = [Collections.Generic.List[object]]::new()
    foreach ($project in $projects) {
        $normalizedProject = $project.Replace('\', '/')
        if ([IO.Path]::IsPathRooted($project) -or -not $repositoryProjectSet.Contains($normalizedProject)) {
            throw "Solution project is not a tracked or nonignored repository project: '$project'."
        }

        $projectPath = [IO.Path]::GetFullPath((Join-Path $normalizedRoot $project))
        if (-not $projectPath.StartsWith($rootPrefix, $comparison) `
            -or -not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Solution project path is missing or outside the repository: '$projectPath'."
        }

        $projectItem = Get-Item -LiteralPath $projectPath -Force
        if (($projectItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to clean outputs for linked solution project '$projectPath'."
        }

        $projectDirectory = [IO.Path]::GetDirectoryName($projectPath)
        $ancestor = $projectDirectory
        while ($true) {
            $ancestorItem = Get-Item -LiteralPath $ancestor -Force
            if (-not $ancestorItem.PSIsContainer `
                -or ($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to clean through non-directory or linked ancestor '$ancestor'."
            }
            if ($ancestor.Equals($normalizedRoot, $comparison)) {
                break
            }
            if (-not $ancestor.StartsWith($rootPrefix, $comparison)) {
                throw "Project ancestor escaped the repository while checking '$projectPath'."
            }
            $parent = [IO.Directory]::GetParent($ancestor)
            if ($null -eq $parent) {
                throw "Repository root was not reached while checking '$projectPath'."
            }
            $ancestor = $parent.FullName
        }

        foreach ($directoryName in @('bin', 'obj')) {
            $target = [IO.Path]::GetFullPath((Join-Path $projectDirectory $directoryName))
            if (-not $target.StartsWith($rootPrefix, $comparison)) {
                throw "Refusing to clean project output outside the repository: '$target'."
            }

            $relativeTarget = [IO.Path]::GetRelativePath($normalizedRoot, $target).Replace('\', '/')
            $protectedEntries = @(
                & $git -C $normalizedRoot --literal-pathspecs ls-files --cached --others --exclude-standard -- $relativeTarget 2>$null)
            if ($LASTEXITCODE -ne 0) {
                throw "Could not prove that '$relativeTarget' contains only ignored build output."
            }
            if ($protectedEntries.Count -ne 0) {
                throw "Refusing to clean '$relativeTarget' because it contains tracked or nonignored entry '$($protectedEntries[0])'."
            }
            $existed = Test-Path -LiteralPath $target
            if (-not $existed) {
                $targetRecords.Add([pscustomobject][ordered]@{
                    path = $relativeTarget
                    existed = $false
                    removed = $false
                    verifiedAbsent = $false
                    trackedOrNonignoredFiles = 0
                })
                continue
            }

            $item = Get-Item -LiteralPath $target -Force
            if (-not $item.PSIsContainer `
                -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to recursively clean non-directory or linked output path '$target'."
            }
            $linkedDescendants = @(Get-ChildItem -LiteralPath $target -Recurse -Force -ErrorAction Stop |
                Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
                Sort-Object { $_.FullName.Length } -Descending)
            foreach ($linkedDescendant in $linkedDescendants) {
                Remove-Item -LiteralPath $linkedDescendant.FullName -Force
                if (Test-Path -LiteralPath $linkedDescendant.FullName) {
                    throw "Could not unlink project output entry '$($linkedDescendant.FullName)'."
                }
            }
            Remove-Item -LiteralPath $target -Recurse -Force
            if (Test-Path -LiteralPath $target) {
                throw "Project output remained after recursive cleanup: '$target'."
            }
            $targetRecords.Add([pscustomobject][ordered]@{
                path = $relativeTarget
                existed = $true
                removed = $true
                verifiedAbsent = $false
                trackedOrNonignoredFiles = 0
            })
            $removed++
        }
    }

    $stabilized = $false
    for ($stabilizationPass = 1; $stabilizationPass -le 5; $stabilizationPass++) {
        $recleaned = $false
        foreach ($targetRecord in $targetRecords) {
            $absoluteTarget = [IO.Path]::GetFullPath((Join-Path $normalizedRoot $targetRecord.path))
            $remainingItem = Get-Item -LiteralPath $absoluteTarget -Force -ErrorAction SilentlyContinue
            if ($null -eq $remainingItem -and -not (Test-Path -LiteralPath $absoluteTarget)) {
                continue
            }

            $protectedEntries = @(
                & $git -C $normalizedRoot --literal-pathspecs ls-files --cached --others --exclude-standard -- $targetRecord.path 2>$null)
            if ($LASTEXITCODE -ne 0 -or $protectedEntries.Count -ne 0) {
                throw "Refusing to re-clean recreated output '$($targetRecord.path)' without a zero-protected-file proof."
            }
            if ($null -eq $remainingItem `
                -or -not $remainingItem.PSIsContainer `
                -or ($remainingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to re-clean non-directory or linked output '$absoluteTarget'."
            }
            $linkedDescendants = @(Get-ChildItem -LiteralPath $absoluteTarget -Recurse -Force -ErrorAction Stop |
                Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
                Sort-Object { $_.FullName.Length } -Descending)
            foreach ($linkedDescendant in $linkedDescendants) {
                Remove-Item -LiteralPath $linkedDescendant.FullName -Force
            }
            Remove-Item -LiteralPath $absoluteTarget -Recurse -Force
            $targetRecord.existed = $true
            if (-not $targetRecord.removed) {
                $targetRecord.removed = $true
                $removed++
            }
            $recleaned = $true
        }

        Start-Sleep -Milliseconds 100
        $remainingTargets = @($targetRecords | Where-Object {
            $candidate = [IO.Path]::GetFullPath((Join-Path $normalizedRoot $_.path))
            $null -ne (Get-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue) `
                -or (Test-Path -LiteralPath $candidate)
        })
        if ($remainingTargets.Count -eq 0) {
            foreach ($targetRecord in $targetRecords) {
                $targetRecord.verifiedAbsent = $true
            }
            $stabilized = $true
            break
        }
    }
    if (-not $stabilized) {
        throw 'Project outputs did not converge to an absent state after five guarded cleanup passes.'
    }

    $orderedTargets = @($targetRecords | Sort-Object path)
    $expectedTargetCount = $normalizedProjects.Count * 2
    if ($orderedTargets.Count -ne $expectedTargetCount `
        -or @($orderedTargets | ForEach-Object path | Sort-Object -Unique).Count -ne $expectedTargetCount `
        -or @($orderedTargets | Where-Object { -not $_.verifiedAbsent }).Count -ne 0) {
        throw 'Cross-platform pre-clean did not verify every unique solution project output target as absent.'
    }

    if (Test-Path -LiteralPath $ReportPath) {
        throw "Refusing to overwrite pre-clean evidence '$ReportPath'."
    }
    $report = [pscustomobject][ordered]@{
        schemaVersion = 1
        completedUtc = [DateTimeOffset]::UtcNow
        solution = 'SharedMemoryStore.slnx'
        solutionProjects = $normalizedProjects
        solutionProjectCount = $normalizedProjects.Count
        solutionProjectSetSha256 = Get-StringSha256 ($normalizedProjects -join "`n")
        targets = $orderedTargets
        targetCount = $orderedTargets.Count
        targetSetSha256 = Get-StringSha256 (@($orderedTargets | ForEach-Object path) -join "`n")
        existedCount = @($orderedTargets | Where-Object existed).Count
        removedCount = @($orderedTargets | Where-Object removed).Count
        verifiedAbsentCount = @($orderedTargets | Where-Object verifiedAbsent).Count
        trackedOrNonignoredFileCount = [int64](@($orderedTargets | Measure-Object trackedOrNonignoredFiles -Sum).Sum)
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $reportRelativePath = [IO.Path]::GetRelativePath($normalizedRoot, $ReportPath).Replace('\', '/')
    $reportSha256 = Get-FileSha256 $ReportPath
    $summary = [pscustomobject][ordered]@{
        schemaVersion = 1
        solutionProjectCount = $normalizedProjects.Count
        uniqueSolutionProjectCount = $solutionProjectSet.Count
        solutionProjectSetSha256 = $report.solutionProjectSetSha256
        targetCount = $orderedTargets.Count
        uniqueTargetCount = @($orderedTargets | ForEach-Object path | Sort-Object -Unique).Count
        targetSetSha256 = $report.targetSetSha256
        existedBeforeCount = $report.existedCount
        removedCount = $report.removedCount
        verifiedAbsentCount = $report.verifiedAbsentCount
        protectedFileCount = $report.trackedOrNonignoredFileCount
        reportPath = $reportRelativePath
        reportSha256 = $reportSha256
    }

    return [pscustomobject]@{
        SolutionProjectCount = $normalizedProjects.Count
        TargetCount = $orderedTargets.Count
        RemovedDirectoryCount = $removed
        VerifiedAbsentCount = $orderedTargets.Count
        ReportPath = $ReportPath
        ReportSha256 = $reportSha256
        Summary = $summary
    }
}

function Get-RepositoryProvenance {
    $status = Invoke-TextCommand $git @(
        '-c', 'core.autocrlf=true',
        '-c', 'core.safecrlf=false',
        '-C', $root,
        'status', '--porcelain=v2', '--untracked-files=normal')
    return [ordered]@{
        repositoryCommit = Invoke-TextCommand $git @('-C', $root, 'rev-parse', 'HEAD')
        headTree = Invoke-TextCommand $git @('-C', $root, 'rev-parse', 'HEAD^{tree}')
        workingTreeState = if ([string]::IsNullOrWhiteSpace($status)) { 'clean' } elseif ($status -eq 'unknown') { 'unknown' } else { 'dirty' }
        statusSha256 = Get-StringSha256 $status
        sourceManifestSha256 = Get-SourceManifestSha256
    }
}

$repositoryProvenance = Get-RepositoryProvenance
$completionProvenance = $null
$testedAssemblyManifest = @()
$completionAssemblyManifest = @()

function Assert-KnownProvenance {
    param(
        [Parameter(Mandatory)]$Provenance,
        [Parameter(Mandatory)][string]$Context)

    foreach ($property in @('repositoryCommit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
        $value = [string]$Provenance[$property]
        if ([string]::IsNullOrWhiteSpace($value) -or $value -eq 'unknown') {
            throw "$Context provenance property '$property' is unknown."
        }
    }
}

function Assert-ProvenanceStable {
    param(
        [Parameter(Mandatory)]$Start,
        [Parameter(Mandatory)]$End)

    Assert-KnownProvenance $Start 'start'
    Assert-KnownProvenance $End 'completion'
    foreach ($property in @('repositoryCommit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
        if ([string]$Start[$property] -ne [string]$End[$property]) {
            throw "Repository provenance changed during OS validation: '$property'."
        }
    }
}

function Get-TestedAssemblyManifest {
    [xml]$solution = Get-Content -LiteralPath (Join-Path $root 'SharedMemoryStore.slnx') -Raw
    $projectPaths = @($solution.SelectNodes("//*[local-name()='Project']") | ForEach-Object { [string]$_.Path })
    if ($projectPaths.Count -eq 0) {
        throw 'Solution does not expose project paths for assembly provenance.'
    }

    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($projectPath in $projectPaths) {
        $projectDirectory = Split-Path -Parent (Join-Path $root $projectPath)
        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($projectPath) + '.dll'
        $outputDirectory = Join-Path $projectDirectory "bin/$Configuration/net10.0"
        foreach ($fileName in @($assemblyName, 'SharedMemoryStore.dll') | Sort-Object -Unique) {
            $fullPath = Join-Path $outputDirectory $fileName
            if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
                [void]$files.Add([IO.Path]::GetFullPath($fullPath))
            }
            elseif ($fileName -eq $assemblyName) {
                throw "Expected freshly built assembly '$fullPath' is missing."
            }
        }
    }

    return @($files | Sort-Object | ForEach-Object {
        [pscustomobject][ordered]@{
            path = [IO.Path]::GetRelativePath($root, $_)
            length = (Get-Item -LiteralPath $_).Length
            sha256 = Get-FileSha256 $_
        }
    })
}

function Get-TestedAssemblyHash {
    param([Parameter(Mandatory)][string]$RelativePath)

    $matches = @($testedAssemblyManifest | Where-Object {
        ([string]$_.path).Replace('\', '/') -ceq $RelativePath.Replace('\', '/')
    })
    if ($matches.Count -ne 1) {
        throw "Tested assembly manifest does not contain exactly one '$RelativePath' row."
    }
    $hash = [string]$matches[0].sha256
    if ($hash -notmatch '^[0-9A-F]{64}$') {
        throw "Tested assembly '$RelativePath' has an invalid SHA-256 digest."
    }
    return $hash
}

function Assert-AssemblyManifestStable {
    param(
        [Parameter(Mandatory)][object[]]$Start,
        [Parameter(Mandatory)][object[]]$End)

    $startCanonical = @($Start | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" }) -join "`n"
    $endCanonical = @($End | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($startCanonical) -or $startCanonical -cne $endCanonical) {
        throw 'Tested assembly manifest changed after the clean OS-validation build.'
    }
}

function Test-IsIntegerNumber {
    param($Value)

    return $Value -is [byte] -or $Value -is [sbyte] `
        -or $Value -is [int16] -or $Value -is [uint16] `
        -or $Value -is [int32] -or $Value -is [uint32] `
        -or $Value -is [int64] -or $Value -is [uint64]
}

function Test-IsNumericValue {
    param($Value)

    return (Test-IsIntegerNumber $Value) -or $Value -is [single] `
        -or $Value -is [double] -or $Value -is [decimal]
}

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context)

    $member = $Object.PSObject.Properties[$Property]
    if ($null -eq $member -or $null -eq $member.Value) {
        throw "$Context is missing required property '$Property'."
    }
    return $member.Value
}

function Get-StrictInt64 {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context,
        [int64]$Minimum = [int64]::MinValue,
        [int64]$Maximum = [int64]::MaxValue)

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if (-not (Test-IsIntegerNumber $value)) {
        throw "$Context.$Property must be an integer JSON number."
    }
    try {
        $converted = [Convert]::ToInt64($value, [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Context.$Property is outside signed 64-bit range."
    }
    if ($converted -lt $Minimum -or $converted -gt $Maximum) {
        throw "$Context.$Property=$converted is outside [$Minimum,$Maximum]."
    }
    return $converted
}

function Get-StrictDouble {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context,
        [double]$Minimum = -[double]::MaxValue,
        [double]$Maximum = [double]::MaxValue,
        [switch]$Positive)

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if (-not (Test-IsNumericValue $value)) {
        throw "$Context.$Property must be a numeric JSON value."
    }
    $converted = [Convert]::ToDouble($value, [Globalization.CultureInfo]::InvariantCulture)
    if (-not [double]::IsFinite($converted) -or $converted -lt $Minimum -or $converted -gt $Maximum `
        -or ($Positive -and $converted -le 0)) {
        throw "$Context.$Property=$converted is not a valid finite value."
    }
    return $converted
}

function Get-StrictString {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context)

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
        throw "$Context.$Property must be a nonempty JSON string."
    }
    return [string]$value
}

function Get-StrictBoolean {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context)

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if ($value -isnot [bool]) {
        throw "$Context.$Property must be a JSON Boolean."
    }
    return [bool]$value
}

function Assert-ExactStringArray {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Context)

    $actualArray = @($Actual)
    if ($actualArray.Count -ne $Expected.Count) {
        throw "$Context must contain exactly [$($Expected -join ', ')]."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actualArray[$index] -isnot [string] -or [string]$actualArray[$index] -cne $Expected[$index]) {
            throw "$Context must contain exactly [$($Expected -join ', ')] in canonical order."
        }
    }
}

function Get-MedianValue {
    param([Parameter(Mandatory)][double[]]$Values)

    if ($Values.Count -eq 0) {
        throw 'Cannot compute a median for an empty evidence set.'
    }
    $sorted = @($Values | Sort-Object)
    if (($sorted.Count % 2) -eq 0) {
        return ([double]$sorted[$sorted.Count / 2 - 1] + [double]$sorted[$sorted.Count / 2]) / 2.0
    }
    return [double]$sorted[[int]($sorted.Count / 2)]
}

function Assert-DerivedDouble {
    param(
        [Parameter(Mandatory)][double]$Actual,
        [Parameter(Mandatory)][double]$Expected,
        [Parameter(Mandatory)][string]$Context)

    $tolerance = [Math]::Max(0.000000001, [Math]::Abs($Expected) * 0.000000000001)
    if (-not [double]::IsFinite($Actual) -or -not [double]::IsFinite($Expected) `
        -or [Math]::Abs($Actual - $Expected) -gt $tolerance) {
        throw "$Context is not reproducible from the raw evidence."
    }
}

function Assert-LinuxTinySyncTopology {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Context)

    if ((Get-StrictInt64 $Object 'syncKeysPerWorker' $Context 2 2) -ne 2 `
        -or (Get-StrictInt64 $Object 'syncMaximumWorkerCount' $Context 12 12) -ne 12 `
        -or (Get-StrictInt64 $Object 'syncCanonicalBucketCount' $Context 16 16) -ne 16) {
        throw "$Context does not describe the exact two-key/12-worker/16-bucket synchronization topology."
    }

    $digest = Get-StrictString $Object 'syncKeyCatalogSha256' $Context
    if ($digest -cne '9A7E93EB1382F2665155971C64C10D4C29039916CD9E314DB72B9906549656D2' `
        -or $digest -cnotmatch '^[0-9A-F]{64}$') {
        throw "$Context has the wrong deterministic synchronization-key catalog digest."
    }

    $assignments = @(Get-RequiredPropertyValue $Object 'syncKeyCanonicalBucketAssignments' $Context)
    if ($assignments.Count -ne 24) {
        throw "$Context must contain exactly 24 synchronization-key bucket assignments."
    }
    for ($index = 0; $index -lt $assignments.Count; $index++) {
        [int64]$expected = [Math]::Floor($index / 2)
        if (-not (Test-IsIntegerNumber $assignments[$index]) `
            -or [int64]$assignments[$index] -ne $expected) {
            throw "$Context synchronization-key bucket assignment $index must be $expected."
        }
    }
}

function Assert-LinuxTinyPerformanceConfiguration {
    param([Parameter(Mandatory)]$Config)

    $tiny = Get-RequiredPropertyValue $Config 'linuxTinyPerformance' 'qualification config'
    $expectedProperties = @(
        'mode', 'profiles', 'scenarios', 'processCounts', 'syncKeysPerWorker',
        'syncMaximumWorkerCount', 'syncCanonicalBucketCount', 'syncKeyCatalogSha256',
        'syncKeyCanonicalBucketAssignments', 'minimumThroughputRatio',
        'maximumUncontendedP99Ratio', 'maximumScaleP99Ratio',
        'maximumP99Microseconds', 'maximumStallMicroseconds')
    $actualProperties = @($tiny.PSObject.Properties.Name)
    if (($actualProperties -join ',') -cne ($expectedProperties -join ',')) {
        throw "qualification config linuxTinyPerformance properties must be exactly [$($expectedProperties -join ', ')]."
    }
    if ((Get-StrictString $tiny 'mode' 'qualification config linuxTinyPerformance') -cne 'sync') {
        throw 'qualification config linuxTinyPerformance.mode must be sync.'
    }
    Assert-ExactStringArray $tiny.profiles @('Legacy', 'LockFree') 'qualification config linuxTinyPerformance.profiles'
    Assert-ExactStringArray $tiny.scenarios @('acquire-release', 'publish-remove') 'qualification config linuxTinyPerformance.scenarios'
    $counts = @(Get-RequiredPropertyValue $tiny 'processCounts' 'qualification config linuxTinyPerformance')
    if ($counts.Count -ne 2 `
        -or -not (Test-IsIntegerNumber $counts[0]) -or [int64]$counts[0] -ne 1 `
        -or -not (Test-IsIntegerNumber $counts[1]) -or [int64]$counts[1] -ne 8) {
        throw 'qualification config linuxTinyPerformance.processCounts must be exactly [1, 8].'
    }
    [void](Assert-LinuxTinySyncTopology $tiny 'qualification config linuxTinyPerformance')
    if ((Get-StrictDouble $tiny 'minimumThroughputRatio' 'qualification config linuxTinyPerformance' 1 1) -ne 1 `
        -or (Get-StrictDouble $tiny 'maximumUncontendedP99Ratio' 'qualification config linuxTinyPerformance' 1 1) -ne 1 `
        -or (Get-StrictDouble $tiny 'maximumScaleP99Ratio' 'qualification config linuxTinyPerformance' 3 3) -ne 3 `
        -or (Get-StrictDouble $tiny 'maximumP99Microseconds' 'qualification config linuxTinyPerformance' 10 10) -ne 10 `
        -or (Get-StrictDouble $tiny 'maximumStallMicroseconds' 'qualification config linuxTinyPerformance' 10000 10000) -ne 10000) {
        throw 'qualification config linuxTinyPerformance gates must remain LF1/Legacy1 p99<=1, LF8/Legacy8 throughput>=1, LF8/LF1 p99<=3, LF8 p99<=10us, and every LF raw stall<=10000us.'
    }
    $release = Get-RequiredPropertyValue (Get-RequiredPropertyValue $Config 'tiers' 'qualification config') 'release' 'qualification config tiers'
    if ((Get-StrictInt64 $release 'performanceWarmupSeconds' 'qualification config release' 10 10) -ne 10 `
        -or (Get-StrictInt64 $release 'performanceDurationSeconds' 'qualification config release' 60 60) -ne 60 `
        -or (Get-StrictInt64 $release 'performanceTrials' 'qualification config release' 3 3) -ne 3) {
        throw 'Linux tiny release performance requires exactly 10s warmup, 60s measurement, and three trials.'
    }
    return $tiny
}

function Test-LinuxTinyHostTuple {
    param(
        [Parameter(Mandatory)]$Environment,
        [Parameter(Mandatory)][string]$ExpectedRepositoryCommit,
        [Parameter(Mandatory)][string]$ExpectedOperatingSystem,
        [Parameter(Mandatory)][string]$ExpectedOperatingSystemArchitecture,
        [Parameter(Mandatory)][string]$ExpectedProcessArchitecture,
        [Parameter(Mandatory)][int64]$ExpectedLogicalProcessorCount,
        [Parameter(Mandatory)][bool]$LinuxHost)

    return $LinuxHost `
        -and [string]$Environment.repositoryCommit -ceq $ExpectedRepositoryCommit `
        -and [string]$Environment.repositoryWorkingTreeState -ceq 'clean' `
        -and [string]$Environment.operatingSystem -ceq $ExpectedOperatingSystem `
        -and [string]$Environment.operatingSystemArchitecture -ceq $ExpectedOperatingSystemArchitecture `
        -and [string]$Environment.processArchitecture -ceq $ExpectedProcessArchitecture `
        -and [int64]$Environment.logicalProcessorCount -eq $ExpectedLogicalProcessorCount
}

function Assert-LinuxTinyPerformanceReport {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)]$TinyConfig,
        [Parameter(Mandatory)]$ReleaseConfig,
        [switch]$SkipEnvironmentBinding)

    if ((Get-StrictInt64 $Report 'schemaVersion' 'Linux tiny performance report' 6 6) -ne 6 `
        -or (Get-StrictInt64 $Report 'minimumCompatibleSchemaVersion' 'Linux tiny performance report' 3 3) -ne 3) {
        throw 'Linux tiny performance report must be schema 6 with minimum-compatible schema 3.'
    }
    [void](Get-StrictString $Report 'schemaCompatibility' 'Linux tiny performance report')
    $environment = Get-RequiredPropertyValue $Report 'environment' 'Linux tiny performance report'
    foreach ($property in @(
        'repositoryCommit', 'repositoryWorkingTreeState', 'sharedMemoryStoreAssemblySha256',
        'probeAssemblySha256', 'operatingSystem', 'operatingSystemArchitecture',
        'processArchitecture', 'framework', 'runtimeVersion', 'processorIdentifier')) {
        [void](Get-StrictString $environment $property 'Linux tiny performance environment')
    }
    [void](Get-StrictInt64 $environment 'logicalProcessorCount' 'Linux tiny performance environment' 1 ([int32]::MaxValue))
    [void](Get-StrictInt64 $environment 'stopwatchFrequency' 'Linux tiny performance environment' 1 ([int64]::MaxValue))
    [void](Get-StrictBoolean $environment 'serverGarbageCollection' 'Linux tiny performance environment')
    if (-not $SkipEnvironmentBinding) {
        if (-not (Test-LinuxTinyHostTuple `
            $environment `
            ([string]$repositoryProvenance.repositoryCommit) `
            ([Runtime.InteropServices.RuntimeInformation]::OSDescription) `
            ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) `
            ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()) `
            ([Environment]::ProcessorCount) `
            ([bool]$IsLinux))) {
            throw 'Linux tiny performance environment does not match this clean Linux qualification host.'
        }
        $probePath = "benchmarks/SharedMemoryStore.SyncProbe/bin/$Configuration/net10.0/SharedMemoryStore.SyncProbe.dll"
        $storePath = "benchmarks/SharedMemoryStore.SyncProbe/bin/$Configuration/net10.0/SharedMemoryStore.dll"
        if ([string]$environment.probeAssemblySha256 -cne (Get-TestedAssemblyHash $probePath) `
            -or [string]$environment.sharedMemoryStoreAssemblySha256 -cne (Get-TestedAssemblyHash $storePath)) {
            throw 'Linux tiny performance assembly hashes do not match the fresh tested-assembly manifest.'
        }
    }

    $configuration = Get-RequiredPropertyValue $Report 'configuration' 'Linux tiny performance report'
    if ((Get-StrictString $configuration 'mode' 'Linux tiny performance configuration') -cne 'sync' `
        -or (Get-StrictInt64 $configuration 'warmupSeconds' 'Linux tiny performance configuration' 10 10) -ne
            (Get-StrictInt64 $ReleaseConfig 'performanceWarmupSeconds' 'qualification config release' 10 10) `
        -or (Get-StrictInt64 $configuration 'durationSeconds' 'Linux tiny performance configuration' 60 60) -ne
            (Get-StrictInt64 $ReleaseConfig 'performanceDurationSeconds' 'qualification config release' 60 60) `
        -or (Get-StrictInt64 $configuration 'trials' 'Linux tiny performance configuration' 3 3) -ne
            (Get-StrictInt64 $ReleaseConfig 'performanceTrials' 'qualification config release' 3 3) `
        -or (Get-StrictInt64 $configuration 'warmupCycles' 'Linux tiny performance configuration' 0 0) -ne 0 `
        -or (Get-StrictInt64 $configuration 'samplingInterval' 'Linux tiny performance configuration' 64 64) -ne 64 `
        -or (Get-StrictInt64 $configuration 'maxLatencySamplesPerWorker' 'Linux tiny performance configuration' 65536 65536) -ne 65536 `
        -or -not (Get-StrictBoolean $configuration 'affinityRequested' 'Linux tiny performance configuration')) {
        throw 'Linux tiny performance report configuration does not match the exact release workload.'
    }
    [void](Assert-LinuxTinySyncTopology $configuration 'Linux tiny performance configuration')
    Assert-ExactStringArray $configuration.profiles @('Legacy', 'LockFree') 'Linux tiny performance report profiles'
    Assert-ExactStringArray $configuration.scenarios @('acquire-release', 'publish-remove') 'Linux tiny performance report scenarios'
    $scenarioCounts = Get-RequiredPropertyValue $configuration 'scenarioProcessCounts' 'Linux tiny performance configuration'
    if ((@($scenarioCounts.PSObject.Properties.Name) -join ',') -cne 'acquire-release,publish-remove') {
        throw 'Linux tiny performance scenarioProcessCounts has unexpected keys or order.'
    }
    foreach ($scenario in @('acquire-release', 'publish-remove')) {
        $values = @($scenarioCounts.$scenario)
        if ($values.Count -ne 2 `
            -or -not (Test-IsIntegerNumber $values[0]) -or [int64]$values[0] -ne 1 `
            -or -not (Test-IsIntegerNumber $values[1]) -or [int64]$values[1] -ne 8) {
            throw "Linux tiny performance scenario '$scenario' must contain exactly process counts [1, 8]."
        }
    }

    $runs = @($Report.runs)
    $summaries = @($Report.summary)
    if ($runs.Count -ne 24 -or $summaries.Count -ne 8) {
        throw "Linux tiny performance matrix must contain exactly 24 raw runs and 8 summaries; actual=$($runs.Count)/$($summaries.Count)."
    }
    $expectedRunKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedSummaryKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($profile in @('Legacy', 'LockFree')) {
        foreach ($scenario in @('acquire-release', 'publish-remove')) {
            foreach ($processCount in @(1, 8)) {
                [void]$expectedSummaryKeys.Add("$profile|$scenario|$processCount")
                foreach ($trial in 1..3) {
                    [void]$expectedRunKeys.Add("$profile|$scenario|$processCount|$trial")
                }
            }
        }
    }
    $actualRunKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($run in $runs) {
        $context = "Linux tiny performance run $($run.profile)/$($run.scenario)/$($run.processCount)/trial-$($run.trial)"
        $profile = Get-StrictString $run 'profile' $context
        $scenario = Get-StrictString $run 'scenario' $context
        $processCount = Get-StrictInt64 $run 'processCount' $context 1 8
        if ($processCount -notin @(1, 8)) {
            throw "$context has an unsupported process count."
        }
        $trial = Get-StrictInt64 $run 'trial' $context 1 3
        $key = "$profile|$scenario|$processCount|$trial"
        if (-not $expectedRunKeys.Contains($key) -or -not $actualRunKeys.Add($key)) {
            throw "$context is unexpected or duplicated."
        }
        if ((Get-StrictString $run 'qualification' $context) -cne 'qualification-measurement' `
            -or (Get-StrictInt64 $run 'failures' $context 0 0) -ne 0 `
            -or (Get-StrictBoolean $run 'oversubscribed' $context)) {
            throw "$context is not a correctness-clean, non-oversubscribed qualification measurement."
        }
        $readerCount = Get-StrictInt64 $run 'readerProcessCount' $context 0 $processCount
        $publisherCount = Get-StrictInt64 $run 'publisherProcessCount' $context 0 $processCount
        $observerCount = Get-StrictInt64 $run 'observerProcessCount' $context 0 0
        if (($scenario -ceq 'acquire-release' -and ($readerCount -ne $processCount -or $publisherCount -ne 0)) `
            -or ($scenario -ceq 'publish-remove' -and ($readerCount -ne 0 -or $publisherCount -ne $processCount)) `
            -or $observerCount -ne 0) {
            throw "$context has the wrong process-role topology."
        }
        $cycles = Get-StrictInt64 $run 'cycles' $context 1 ([int64]::MaxValue)
        $operations = Get-StrictInt64 $run 'operations' $context 1 ([int64]::MaxValue)
        if ([decimal]$operations -lt ([decimal]2 * [decimal]$cycles)) {
            throw "$context has fewer than the two recorded store operations required per completed cycle."
        }
        $measuredSeconds = Get-StrictDouble $run 'measuredSeconds' $context `
            ([double]$ReleaseConfig.performanceDurationSeconds) ([double]::MaxValue)
        $wallSeconds = Get-StrictDouble $run 'wallSeconds' $context $measuredSeconds ([double]::MaxValue)
        [void]$wallSeconds
        [int64]$minimumWindowSamples = [int64]$processCount * 1024
        [int64]$maximumWindowSamples = [int64]$processCount * 32768
        [int64]$minimumTotalSamples = $minimumWindowSamples * 2
        [int64]$maximumTotalSamples = $maximumWindowSamples * 2
        $earlySampleCount = Get-StrictInt64 $run 'earlySampleCount' $context $minimumWindowSamples $maximumWindowSamples
        $lateSampleCount = Get-StrictInt64 $run 'lateSampleCount' $context $minimumWindowSamples $maximumWindowSamples
        $sampleCount = Get-StrictInt64 $run 'sampleCount' $context $minimumTotalSamples $maximumTotalSamples
        if ($sampleCount -ne ($earlySampleCount + $lateSampleCount) -or $sampleCount -gt $cycles) {
            throw "$context sampleCount must equal its early/late windows and cannot exceed completed cycles."
        }
        $apiCallsPerSecond = Get-StrictDouble $run 'apiCallsPerSecond' $context 0 ([double]::MaxValue) -Positive
        Assert-DerivedDouble $apiCallsPerSecond ([double]$operations / $measuredSeconds) "$context.apiCallsPerSecond"
        $p50 = Get-StrictDouble $run 'p50Microseconds' $context 0 ([double]::MaxValue)
        $p95 = Get-StrictDouble $run 'p95Microseconds' $context 0 ([double]::MaxValue)
        $p99 = Get-StrictDouble $run 'p99Microseconds' $context 0 ([double]::MaxValue)
        $maximum = Get-StrictDouble $run 'maxMicroseconds' $context 0 ([double]::MaxValue)
        [void](Get-StrictDouble $run 'earlyP99Microseconds' $context 0 ([double]::MaxValue) -Positive)
        [void](Get-StrictDouble $run 'lateP99Microseconds' $context 0 ([double]::MaxValue) -Positive)
        if ($p50 -gt $p95 -or $p95 -gt $p99 -or $p99 -gt $maximum) {
            throw "$context latency percentiles/maximum are not monotonic."
        }
        if ($profile -ceq 'LockFree' -and $maximum -gt
            (Get-StrictDouble $TinyConfig 'maximumStallMicroseconds' 'qualification config linuxTinyPerformance' 10000 10000)) {
            throw "$context exceeds the every-run 10000us maximum-stall gate: $maximum."
        }
        $assignedProcessors = @(Get-RequiredPropertyValue $run 'assignedProcessors' $context)
        if ($assignedProcessors.Count -ne $processCount `
            -or @($assignedProcessors | Sort-Object -Unique).Count -ne $processCount `
            -or (Get-StrictInt64 $run 'affinityAppliedCount' $context $processCount $processCount) -ne $processCount) {
            throw "$context lacks complete unique $processCount-process affinity evidence."
        }
        foreach ($processor in $assignedProcessors) {
            if (-not (Test-IsIntegerNumber $processor) `
                -or [int64]$processor -lt 0 `
                -or [int64]$processor -gt 63) {
                throw "$context has a processor assignment outside the probe's 64-bit affinity mask [0,63]."
            }
        }
        $workerCycles = @(Get-RequiredPropertyValue $run 'workerCycles' $context)
        if ($workerCycles.Count -ne $processCount) {
            throw "$context must contain exactly $processCount worker-cycle rows."
        }
        [decimal]$cycleTotal = 0
        foreach ($workerCycle in $workerCycles) {
            if (-not (Test-IsIntegerNumber $workerCycle) -or [int64]$workerCycle -lt 0) {
                throw "$context has an invalid worker-cycle count."
            }
            $cycleTotal += [decimal]$workerCycle
        }
        if ($cycleTotal -ne [decimal]$cycles) {
            throw "$context worker cycles do not sum to Cycles."
        }
        [decimal]$statusTotal = 0
        $histogram = Get-RequiredPropertyValue $run 'statusHistogram' $context
        if (@($histogram.PSObject.Properties).Count -eq 0) {
            throw "$context has an empty status histogram."
        }
        foreach ($entry in $histogram.PSObject.Properties) {
            if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -lt 0) {
                throw "$context status '$($entry.Name)' is not a nonnegative integer."
            }
            if ($entry.Name -ceq 'Validation.ChecksumMismatch' `
                -or $entry.Name -clike 'CorruptReason.*') {
                throw "$context contains forbidden checksum/corruption evidence '$($entry.Name)'."
            }
            if ($entry.Name -match '^(Acquire|Release|Publish|Remove)\.') {
                $statusTotal += [decimal]$entry.Value
            }
        }
        if ($statusTotal -ne [decimal]$operations) {
            throw "$context operation-status histogram does not sum to Operations."
        }
        $requiredSuccesses = if ($scenario -ceq 'acquire-release') {
            @('Acquire.Success', 'Release.Success')
        }
        else {
            @('Publish.Success', 'Remove.Success')
        }
        foreach ($successName in $requiredSuccesses) {
            $successRows = @($histogram.PSObject.Properties | Where-Object { $_.Name -ceq $successName })
            if ($successRows.Count -ne 1 `
                -or -not (Test-IsIntegerNumber $successRows[0].Value) `
                -or [int64]$successRows[0].Value -ne $cycles) {
                throw "$context must contain exactly $cycles '$successName' operations, one per completed cycle."
            }
        }
    }
    if (-not $actualRunKeys.SetEquals($expectedRunKeys)) {
        throw 'Linux tiny performance raw run tuple set is incomplete.'
    }

    $actualSummaryKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $metrics = [Collections.Generic.List[object]]::new()
    foreach ($summary in $summaries) {
        $context = "Linux tiny performance summary $($summary.profile)/$($summary.scenario)/$($summary.processCount)"
        $profile = Get-StrictString $summary 'profile' $context
        $scenario = Get-StrictString $summary 'scenario' $context
        $processCount = Get-StrictInt64 $summary 'processCount' $context 1 8
        if ($processCount -notin @(1, 8)) {
            throw "$context has an unsupported process count."
        }
        $key = "$profile|$scenario|$processCount"
        if (-not $expectedSummaryKeys.Contains($key) -or -not $actualSummaryKeys.Add($key)) {
            throw "$context is unexpected or duplicated."
        }
        $matchingRuns = @($runs | Where-Object {
            [string]$_.profile -ceq $profile -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq $processCount
        })
        if ($matchingRuns.Count -ne 3) {
            throw "$context does not summarize exactly three raw trials."
        }
        if ((Get-StrictInt64 $summary 'totalFailures' $context 0 0) -ne 0) {
            throw "$context has correctness failures."
        }
        foreach ($pair in @(
            @('medianApiCallsPerSecond', 'apiCallsPerSecond'),
            @('medianP99Microseconds', 'p99Microseconds'),
            @('medianMaxMicroseconds', 'maxMicroseconds'))) {
            $rawValues = [double[]]@($matchingRuns | ForEach-Object {
                Get-StrictDouble $_ $pair[1] $context 0 ([double]::MaxValue)
            })
            Assert-DerivedDouble `
                (Get-StrictDouble $summary $pair[0] $context 0 ([double]::MaxValue)) `
                (Get-MedianValue $rawValues) "$context.$($pair[0])"
        }
        $merged = [ordered]@{}
        foreach ($run in $matchingRuns) {
            foreach ($entry in $run.statusHistogram.PSObject.Properties) {
                if (-not $merged.Contains($entry.Name)) { $merged[$entry.Name] = [int64]0 }
                $merged[$entry.Name] = [int64]$merged[$entry.Name] + [int64]$entry.Value
            }
        }
        $summaryHistogram = Get-RequiredPropertyValue $summary 'statusHistogram' $context
        if ((@($summaryHistogram.PSObject.Properties.Name) -join ',') -cne (@($merged.Keys) -join ',')) {
            throw "$context status histogram keys do not match the raw trials."
        }
        foreach ($entry in $summaryHistogram.PSObject.Properties) {
            if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -ne [int64]$merged[$entry.Name]) {
                throw "$context status '$($entry.Name)' is not the raw-trial total."
            }
        }
        $metrics.Add([pscustomobject][ordered]@{
            profile = $profile
            scenario = $scenario
            processCount = $processCount
            medianApiCallsPerSecond = [double]$summary.medianApiCallsPerSecond
            medianP99Microseconds = [double]$summary.medianP99Microseconds
            maximumRawStallMicroseconds = [double](($matchingRuns | Measure-Object maxMicroseconds -Maximum).Maximum)
        })
    }
    if (-not $actualSummaryKeys.SetEquals($expectedSummaryKeys)) {
        throw 'Linux tiny performance summary tuple set is incomplete.'
    }
    foreach ($scenario in @('acquire-release', 'publish-remove')) {
        $legacyOne = @($summaries | Where-Object {
            [string]$_.profile -ceq 'Legacy' -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq 1
        })[0]
        $lockFreeOne = @($summaries | Where-Object {
            [string]$_.profile -ceq 'LockFree' -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq 1
        })[0]
        $legacyEight = @($summaries | Where-Object {
            [string]$_.profile -ceq 'Legacy' -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq 8
        })[0]
        $lockFreeEight = @($summaries | Where-Object {
            [string]$_.profile -ceq 'LockFree' -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq 8
        })[0]
        $legacyOneP99 = Get-StrictDouble $legacyOne 'medianP99Microseconds' "$scenario legacy/1p summary" 0 ([double]::MaxValue) -Positive
        $lockFreeOneP99 = Get-StrictDouble $lockFreeOne 'medianP99Microseconds' "$scenario lock-free/1p summary" 0 ([double]::MaxValue) -Positive
        $legacyEightRate = Get-StrictDouble $legacyEight 'medianApiCallsPerSecond' "$scenario legacy/8p summary" 0 ([double]::MaxValue) -Positive
        $lockFreeEightRate = Get-StrictDouble $lockFreeEight 'medianApiCallsPerSecond' "$scenario lock-free/8p summary" 0 ([double]::MaxValue) -Positive
        $lockFreeEightP99 = Get-StrictDouble $lockFreeEight 'medianP99Microseconds' "$scenario lock-free/8p summary" 0 ([double]::MaxValue) -Positive
        $uncontendedP99Ratio = $lockFreeOneP99 / $legacyOneP99
        $throughputRatio = $lockFreeEightRate / $legacyEightRate
        $scaleP99Ratio = $lockFreeEightP99 / $lockFreeOneP99
        if (-not [double]::IsFinite($uncontendedP99Ratio) `
            -or $uncontendedP99Ratio -gt [double]$TinyConfig.maximumUncontendedP99Ratio `
            -or -not [double]::IsFinite($throughputRatio) `
            -or $throughputRatio -lt [double]$TinyConfig.minimumThroughputRatio `
            -or -not [double]::IsFinite($scaleP99Ratio) `
            -or $scaleP99Ratio -gt [double]$TinyConfig.maximumScaleP99Ratio `
            -or $lockFreeEightP99 -gt [double]$TinyConfig.maximumP99Microseconds) {
            throw "Linux tiny performance '$scenario' gate failed: uncontendedP99Ratio=$uncontendedP99Ratio throughputRatio=$throughputRatio scaleP99Ratio=$scaleP99Ratio lockFreeEightP99Microseconds=$lockFreeEightP99."
        }
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 2
        runCount = 24
        summaryCount = 8
        warmupSeconds = 10
        durationSeconds = 60
        trials = 3
        processCounts = @(1, 8)
        minimumThroughputRatio = [double]$TinyConfig.minimumThroughputRatio
        maximumUncontendedP99Ratio = [double]$TinyConfig.maximumUncontendedP99Ratio
        maximumScaleP99Ratio = [double]$TinyConfig.maximumScaleP99Ratio
        maximumP99Microseconds = [double]$TinyConfig.maximumP99Microseconds
        maximumStallMicroseconds = [double]$TinyConfig.maximumStallMicroseconds
        metrics = @($metrics)
    }
}

function Invoke-LinuxTinyPerformanceParserSelfTest {
    param(
        [Parameter(Mandatory)]$TinyConfig,
        [Parameter(Mandatory)]$ReleaseConfig)

    $runs = [Collections.Generic.List[object]]::new()
    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($profile in @('Legacy', 'LockFree')) {
        foreach ($scenario in @('acquire-release', 'publish-remove')) {
            foreach ($processCount in @(1, 8)) {
                $api = if ($profile -ceq 'Legacy') { 1000.0 } else { 1100.0 }
                $p99 = if ($processCount -eq 1) {
                    if ($profile -ceq 'Legacy') { 5.0 } else { 4.0 }
                }
                else {
                    if ($profile -ceq 'Legacy') { 3.0 } else { 8.0 }
                }
                $maximum = if ($profile -ceq 'Legacy') { 500.0 } else { 9000.0 }
                [int64]$cycles = if ($profile -ceq 'Legacy') { 30000 } else { 33000 }
                [int64]$operations = $cycles * 2
                [int64]$workerCycle = $cycles / $processCount
                [int64]$windowSamples = [int64]$processCount * 1024
                $workerCycles = @()
                for ($worker = 0; $worker -lt $processCount; $worker++) {
                    $workerCycles += $workerCycle
                }
                $histogram = if ($scenario -ceq 'acquire-release') {
                    [pscustomobject][ordered]@{ 'Acquire.Success' = $cycles; 'Release.Success' = $cycles }
                }
                else {
                    [pscustomobject][ordered]@{ 'Publish.Success' = $cycles; 'Remove.Success' = $cycles }
                }
                foreach ($trial in 1..3) {
                    $runs.Add([pscustomobject][ordered]@{
                        Profile = $profile; Scenario = $scenario; ProcessCount = $processCount; Trial = $trial
                        ReaderProcessCount = $(if ($scenario -ceq 'acquire-release') { $processCount } else { 0 })
                        PublisherProcessCount = $(if ($scenario -ceq 'publish-remove') { $processCount } else { 0 })
                        ObserverProcessCount = 0; Cycles = $cycles; Operations = $operations
                        ApiCallsPerSecond = $api; P50Microseconds = 1.0; P95Microseconds = 2.0
                        P99Microseconds = $p99; MaxMicroseconds = $maximum
                        EarlyP99Microseconds = $p99; LateP99Microseconds = $p99
                        Failures = 0; MeasuredSeconds = 60.0; WallSeconds = 70.0
                        EarlySampleCount = $windowSamples; LateSampleCount = $windowSamples
                        SampleCount = ($windowSamples * 2); AffinityAppliedCount = $processCount
                        AssignedProcessors = @(0..($processCount - 1)); Oversubscribed = $false
                        Qualification = 'qualification-measurement'; StatusHistogram = $histogram
                        WorkerCycles = @($workerCycles)
                    })
                }
                $summaryHistogram = if ($scenario -ceq 'acquire-release') {
                    [pscustomobject][ordered]@{
                        'Acquire.Success' = ($cycles * 3); 'Release.Success' = ($cycles * 3)
                    }
                }
                else {
                    [pscustomobject][ordered]@{
                        'Publish.Success' = ($cycles * 3); 'Remove.Success' = ($cycles * 3)
                    }
                }
                $summaries.Add([pscustomobject][ordered]@{
                    Profile = $profile; Scenario = $scenario; ProcessCount = $processCount
                    MedianApiCallsPerSecond = $api; MedianP99Microseconds = $p99
                    MedianMaxMicroseconds = $maximum; TotalFailures = 0; StatusHistogram = $summaryHistogram
                })
            }
        }
    }
    $report = [pscustomobject][ordered]@{
        SchemaVersion = 6
        Environment = [pscustomobject][ordered]@{
            RepositoryCommit = 'synthetic'; RepositoryWorkingTreeState = 'clean'
            SharedMemoryStoreAssemblySha256 = ('A' * 64); ProbeAssemblySha256 = ('B' * 64)
            OperatingSystem = 'Ubuntu 24.04 synthetic'; OperatingSystemArchitecture = 'X64'; ProcessArchitecture = 'X64'
            Framework = '.NET synthetic'; RuntimeVersion = 'synthetic'; LogicalProcessorCount = 8
            ProcessorIdentifier = 'synthetic'; ServerGarbageCollection = $false; StopwatchFrequency = 10000000
        }
        Configuration = [pscustomobject][ordered]@{
            Mode = 'sync'; DurationSeconds = 60; Trials = 3; Profiles = @('Legacy', 'LockFree')
            Scenarios = @('acquire-release', 'publish-remove')
            ScenarioProcessCounts = [pscustomobject][ordered]@{
                'acquire-release' = @(1, 8); 'publish-remove' = @(1, 8)
            }
            WarmupCycles = 0; WarmupSeconds = 10; AffinityRequested = $true
            SamplingInterval = 64; MaxLatencySamplesPerWorker = 65536
            SyncKeysPerWorker = [int]$TinyConfig.syncKeysPerWorker
            SyncMaximumWorkerCount = [int]$TinyConfig.syncMaximumWorkerCount
            SyncCanonicalBucketCount = [int]$TinyConfig.syncCanonicalBucketCount
            SyncKeyCatalogSha256 = [string]$TinyConfig.syncKeyCatalogSha256
            SyncKeyCanonicalBucketAssignments = @($TinyConfig.syncKeyCanonicalBucketAssignments)
        }
        Runs = @($runs); Summary = @($summaries); MinimumCompatibleSchemaVersion = 3
        SchemaCompatibility = 'synthetic schema-v6 parser self-test'
    }
    [void](Assert-LinuxTinyPerformanceReport $report $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    if (-not (Test-LinuxTinyHostTuple `
        $report.Environment `
        'synthetic' `
        'Ubuntu 24.04 synthetic' `
        'X64' `
        'X64' `
        8 `
        $true)) {
        throw 'Linux tiny performance host-tuple self-test rejected an exact distro description without the word Linux.'
    }
    if (Test-LinuxTinyHostTuple `
        $report.Environment `
        'synthetic' `
        'Debian synthetic' `
        'X64' `
        'X64' `
        8 `
        $true) {
        throw 'Linux tiny performance host-tuple self-test accepted a different OS description.'
    }
    [int]$assertions = 3

    $tampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $tamperedLockFreeRun = @($tampered.Runs | Where-Object {
        [string]$_.Profile -ceq 'LockFree' `
            -and [string]$_.Scenario -ceq 'acquire-release' `
            -and [int64]$_.ProcessCount -eq 1
    })[0]
    $tamperedLockFreeRun.MaxMicroseconds = 10001.0
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $tampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted an over-limit raw lock-free stall.'
    }
    $assertions++

    $sampleTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $sampleTamperedOneProcessRun = @($sampleTampered.Runs | Where-Object {
        [int64]$_.ProcessCount -eq 1
    })[0]
    $sampleTamperedOneProcessRun.LateSampleCount = 1023
    $sampleTamperedOneProcessRun.SampleCount = 2047
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $sampleTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted incoherent early/late sample counts.'
    }
    $assertions++

    foreach ($scenario in @('acquire-release', 'publish-remove')) {
        $uncontendedTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        foreach ($run in @($uncontendedTampered.Runs | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 1
        })) {
            $run.P99Microseconds = 6.0
            $run.EarlyP99Microseconds = 6.0
            $run.LateP99Microseconds = 6.0
        }
        @($uncontendedTampered.Summary | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 1
        })[0].MedianP99Microseconds = 6.0
        $rejected = $false
        try {
            [void](Assert-LinuxTinyPerformanceReport $uncontendedTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Linux tiny performance parser self-test accepted an over-limit '$scenario' uncontended p99 ratio."
        }
        $assertions++

        $scaleTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        foreach ($run in @($scaleTampered.Runs | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 1
        })) {
            $run.P99Microseconds = 2.0
            $run.EarlyP99Microseconds = 2.0
            $run.LateP99Microseconds = 2.0
        }
        @($scaleTampered.Summary | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 1
        })[0].MedianP99Microseconds = 2.0
        $rejected = $false
        try {
            [void](Assert-LinuxTinyPerformanceReport $scaleTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Linux tiny performance parser self-test accepted an over-limit '$scenario' scale p99 ratio."
        }
        $assertions++

        $absoluteTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        foreach ($run in @($absoluteTampered.Runs | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 8
        })) {
            $run.P99Microseconds = 11.0
            $run.EarlyP99Microseconds = 11.0
            $run.LateP99Microseconds = 11.0
        }
        @($absoluteTampered.Summary | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 8
        })[0].MedianP99Microseconds = 11.0
        $rejected = $false
        try {
            [void](Assert-LinuxTinyPerformanceReport $absoluteTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Linux tiny performance parser self-test accepted an over-limit '$scenario' absolute p99."
        }
        $assertions++

        $throughputTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        foreach ($run in @($throughputTampered.Runs | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 8
        })) {
            $run.MeasuredSeconds = 120.0
            $run.WallSeconds = 130.0
            $run.ApiCallsPerSecond = [double]$run.Operations / 120.0
        }
        @($throughputTampered.Summary | Where-Object {
            [string]$_.Profile -ceq 'LockFree' `
                -and [string]$_.Scenario -ceq $scenario `
                -and [int64]$_.ProcessCount -eq 8
        })[0].MedianApiCallsPerSecond = 550.0
        $rejected = $false
        try {
            [void](Assert-LinuxTinyPerformanceReport $throughputTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Linux tiny performance parser self-test accepted an under-limit '$scenario' 8-process throughput ratio."
        }
        $assertions++
    }

    $topologyTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $topologyTampered.Configuration.SyncKeyCanonicalBucketAssignments[1] = 1
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $topologyTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted a colliding synchronization-key topology.'
    }
    $assertions++

    $affinityTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $affinityTamperedEightProcessRun = @($affinityTampered.Runs | Where-Object {
        [int64]$_.ProcessCount -eq 8
    })[0]
    $affinityTamperedEightProcessRun.AssignedProcessors = @(64, 65, 66, 67, 68, 69, 70, 71)
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $affinityTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted processor IDs outside its 64-bit affinity mask.'
    }
    $assertions++

    $operationTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $operationTampered.Runs[0].Operations = [int64]$operationTampered.Runs[0].Cycles
    $operationTampered.Runs[0].ApiCallsPerSecond =
        [double]$operationTampered.Runs[0].Operations / [double]$operationTampered.Runs[0].MeasuredSeconds
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $operationTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted fewer than two operations per completed cycle.'
    }
    $assertions++

    $successTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $successTampered.Runs[0].StatusHistogram = [pscustomobject][ordered]@{
        'Acquire.NotFound' = [int64]$successTampered.Runs[0].Operations
    }
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $successTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted a cycle set without exact paired success counts.'
    }
    $assertions++

    $corruptionTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $corruptionTampered.Runs[0].StatusHistogram | Add-Member `
        -NotePropertyName 'Validation.ChecksumMismatch' -NotePropertyValue 1
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $corruptionTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted checksum/corruption evidence.'
    }
    $assertions++

    $corruptReasonTampered = $report | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $corruptReasonTampered.Runs[0].StatusHistogram | Add-Member `
        -NotePropertyName 'CorruptReason.Forged' -NotePropertyValue 1
    $rejected = $false
    try {
        [void](Assert-LinuxTinyPerformanceReport $corruptReasonTampered $TinyConfig $ReleaseConfig -SkipEnvironmentBinding)
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Linux tiny performance parser self-test accepted a corruption-reason row.'
    }
    $assertions++
    return $assertions
}

# Each externally selectable validation has a structural self-test. This makes
# a stale path/filter addition fail the cheap `self-test` command rather than
# being discovered only in a release qualification job.
$definitions = [ordered]@{
    'architecture' = @(
        'tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj',
        'tests/SharedMemoryStore.ContractTests/LockFreeProfileApiContractTests.cs',
        'tests/SharedMemoryStore.ContractTests/LockFreeLayoutContractTests.cs')
    'atomic' = @(
        'tests/SharedMemoryStore.IntegrationTests/MappedAtomicLitmusIntegrationTests.cs',
        'tests/SharedMemoryStore.LockFreeAgent/Program.cs')
    'raw' = @(
        'tests/SharedMemoryStore.IntegrationTests/LockFreeRawVisibilityIntegrationTests.cs',
        'tests/SharedMemoryStore.LockFreeAgent/RawVisibilityCommands.cs')
    'no-lock' = @(
        'tests/SharedMemoryStore.IntegrationTests/LockFreeNoOperationLockIntegrationTests.cs',
        'tests/SharedMemoryStore.IntegrationTests/LockFreeOsTraceIntegrationTests.cs',
        'tests/SharedMemoryStore.LockFreeAgent/SteadyNoLockCommands.cs')
    'crash' = @(
        'tests/SharedMemoryStore.IntegrationTests/LockFreeCrashRecoveryIntegrationTests.cs',
        'tests/SharedMemoryStore.IntegrationTests/LockFreeOsTraceIntegrationTests.cs',
        'tests/SharedMemoryStore.LockFreeAgent/CheckpointCrashCommands.cs')
    'release-tests' = @(
        'SharedMemoryStore.slnx',
        'benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj')
    'interop' = @(
        'scripts/validate-native.ps1',
        'scripts/validate-python.ps1',
        'scripts/validate-docker-shared-memory.ps1')
    'samples' = @('samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj')
    'pack' = @('src/SharedMemoryStore/SharedMemoryStore.csproj')
}

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('pass', 'fail', 'not-qualified')][string]$Status,
        [Parameter(Mandatory)][string]$Detail,
        [bool]$Required = $true,
        [string]$CommandLine = $null,
        [string]$Stdout = $null,
        [string]$Stderr = $null,
        [Nullable[int]]$ExitCode = $null,
        [double]$ElapsedSeconds = 0,
        [int]$TimeoutSeconds = 0,
        [bool]$TimedOut = $false,
        [Nullable[DateTimeOffset]]$StartedUtc = $null)

    $results.Add([pscustomobject][ordered]@{
        name = $Name
        status = $Status
        required = $Required
        detail = $Detail
        command = $CommandLine
        startedUtc = $StartedUtc
        exitCode = $ExitCode
        elapsedSeconds = $ElapsedSeconds
        timeoutSeconds = $TimeoutSeconds
        timedOut = $TimedOut
        stdout = $Stdout
        stderr = $Stderr
        stdoutSha256 = if ($null -ne $Stdout) { Get-FileSha256 (Join-Path $root $Stdout) } else { $null }
        stderrSha256 = if ($null -ne $Stderr) { Get-FileSha256 (Join-Path $root $Stderr) } else { $null }
    })
}

function Test-Definition {
    param([Parameter(Mandatory)][string]$Name)

    if (-not $definitions.Contains($Name)) {
        throw "No structural self-test is registered for '$Name'."
    }

    $missing = @($definitions[$Name] | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
    if ($missing.Count -ne 0) {
        throw "Validation '$Name' has missing inputs: $($missing -join ', ')."
    }

    Add-Result "self-test-$Name" 'pass' ($definitions[$Name] -join ',')
}

function Invoke-Required {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [bool]$Required = $true)

    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    $stdoutPath = Join-Path $evidenceRoot ($safeName + '.stdout.log')
    $stderrPath = Join-Path $evidenceRoot ($safeName + '.stderr.log')
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $startedUtc = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) {
            throw "Could not start required validation '$Name'."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = $process.WaitForExit([int]([int64]$StepTimeoutSeconds * 1000))
        $terminationSucceeded = $true
        $terminationDetail = $null
        if (-not $completed) {
            try {
                $process.Kill($true)
            }
            catch {
                $terminationSucceeded = $false
                $terminationDetail = "process-tree kill failed: $($_.Exception.Message)"
            }
            if ($terminationSucceeded -and -not $process.WaitForExit(30000)) {
                $terminationSucceeded = $false
                $terminationDetail = 'process tree did not terminate within 30 seconds of Kill'
            }
        }

        $streamsDrained = $false
        $streamDrainDetail = $null
        try {
            $streamsDrained = [Threading.Tasks.Task]::WaitAll(
                [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask),
                30000)
        }
        catch {
            $streamDrainDetail = "redirected-stream drain failed: $($_.Exception.GetBaseException().Message)"
        }
        if (-not $streamsDrained -and [string]::IsNullOrWhiteSpace($streamDrainDetail)) {
            $streamDrainDetail = 'redirected streams did not close within 30 seconds'
        }
        $stdout = if ($stdoutTask.IsCompletedSuccessfully) {
            $stdoutTask.GetAwaiter().GetResult()
        }
        else {
            "[os-validator] stdout unavailable: $streamDrainDetail"
        }
        $stderr = if ($stderrTask.IsCompletedSuccessfully) {
            $stderrTask.GetAwaiter().GetResult()
        }
        else {
            "[os-validator] stderr unavailable: $streamDrainDetail"
        }
        if (-not $terminationSucceeded) {
            $stderr += [Environment]::NewLine + "[os-validator] $terminationDetail"
        }
        [IO.File]::WriteAllText($stdoutPath, $stdout)
        [IO.File]::WriteAllText($stderrPath, $stderr)
        $stopwatch.Stop()

        $relativeStdout = [IO.Path]::GetRelativePath($root, $stdoutPath)
        $relativeStderr = [IO.Path]::GetRelativePath($root, $stderrPath)
        $commandLine = $FileName + ' ' + ($Arguments -join ' ')
        $hasExited = $false
        try {
            $hasExited = $process.HasExited
        }
        catch {
            $hasExited = $false
        }
        $exitCode = if ($hasExited -and $completed) { $process.ExitCode } else { -1 }
        $executionSucceeded = $completed -and $terminationSucceeded -and $streamsDrained -and $exitCode -eq 0
        if (-not $executionSucceeded) {
            $failureDetail = if (-not $completed) {
                "timeout=$StepTimeoutSeconds seconds; $($(if ($terminationSucceeded) { 'process tree killed' } else { $terminationDetail }))"
            }
            elseif (-not $streamsDrained) {
                $streamDrainDetail
            }
            else {
                "exit=$exitCode"
            }
            Add-Result -Name $Name -Status 'fail' `
                -Detail $failureDetail `
                -Required $Required -CommandLine $commandLine -Stdout $relativeStdout -Stderr $relativeStderr `
                -ExitCode $exitCode -ElapsedSeconds $stopwatch.Elapsed.TotalSeconds `
                -TimeoutSeconds $StepTimeoutSeconds -TimedOut (-not $completed) -StartedUtc $startedUtc
            if (-not $completed) {
                throw "Validation '$Name' timed out after $StepTimeoutSeconds seconds; $failureDetail."
            }
            if (-not $streamsDrained) {
                throw "Validation '$Name' could not prove complete redirected output: $streamDrainDetail."
            }
            throw "Validation '$Name' failed with exit code $exitCode."
        }
        Add-Result -Name $Name -Status 'pass' -Detail 'command completed successfully' `
            -Required $Required -CommandLine $commandLine -Stdout $relativeStdout -Stderr $relativeStderr `
            -ExitCode $exitCode -ElapsedSeconds $stopwatch.Elapsed.TotalSeconds `
            -TimeoutSeconds $StepTimeoutSeconds -TimedOut $false -StartedUtc $startedUtc
    }
    finally {
        $process.Dispose()
    }
}

function Test-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [string[]]$Arguments = @(),
        [int]$TimeoutMilliseconds = 30000)

    try {
        $resolved = (Get-Command $FileName -ErrorAction Stop).Source
        $start = [Diagnostics.ProcessStartInfo]::new($resolved)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $Arguments) {
            $start.ArgumentList.Add($argument)
        }

        $process = [Diagnostics.Process]::Start($start)
        if ($null -eq $process) {
            return $false
        }
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutMilliseconds)) {
                try {
                    $process.Kill($true)
                }
                catch {
                    return $false
                }
                if (-not $process.WaitForExit(30000)) {
                    return $false
                }
                try {
                    [void][Threading.Tasks.Task]::WaitAll(
                        [Threading.Tasks.Task[]]@($stdout, $stderr),
                        5000)
                }
                catch {
                    return $false
                }
                return $false
            }
            if (-not [Threading.Tasks.Task]::WaitAll(
                    [Threading.Tasks.Task[]]@($stdout, $stderr),
                    30000)) {
                return $false
            }
            if (-not $stdout.IsCompletedSuccessfully -or -not $stderr.IsCompletedSuccessfully) {
                return $false
            }
            $stdout.GetAwaiter().GetResult() | Out-Null
            $stderr.GetAwaiter().GetResult() | Out-Null
            return $process.ExitCode -eq 0
        }
        finally {
            $process.Dispose()
        }
    }
    catch {
        return $false
    }
}

function Get-LinuxProcessStartIdentity {
    param([Parameter(Mandatory)][int]$ProcessId)

    if (-not $IsLinux -or $ProcessId -le 0) {
        return $null
    }

    try {
        $stat = Get-Content -LiteralPath "/proc/$ProcessId/stat" -Raw
        $commandEnd = $stat.LastIndexOf(')')
        if ($commandEnd -lt 0 -or $commandEnd + 2 -ge $stat.Length) {
            return $null
        }

        $fields = @($stat.Substring($commandEnd + 2).Split(
                ' ',
                [StringSplitOptions]::RemoveEmptyEntries))
        if ($fields.Count -le 19 -or $fields[19] -notmatch '^\d+$') {
            return $null
        }

        return [string]$fields[19]
    }
    catch {
        return $null
    }
}

function Test-DockerHostPidIdentityVisible {
    param([Parameter(Mandatory)][string]$Image)

    $startIdentity = Get-LinuxProcessStartIdentity $PID
    if ([string]::IsNullOrWhiteSpace($startIdentity)) {
        return $false
    }

    # Linux region ownership is fenced by both PID and /proc field 22. Merely
    # finding a reused numeric PID inside an unrelated namespace is not proof
    # that a container can safely join the existing owner sidecar.
    $probe = 'pid="$1"; expected="$2"; line=$(cat "/proc/$pid/stat") || exit 21; rest=${line##*) }; set -- $rest; shift 19 || exit 22; [ "$1" = "$expected" ]'
    return Test-NativeCommand 'docker' @(
        'run', '--rm', '--pid=host',
        $Image,
        'sh', '-c', $probe, 'sms-owner-identity-probe',
        [string]$PID, $startIdentity)
}

function Invoke-OptionalScript {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Prerequisites,
        [Parameter(Mandatory)][string]$ScriptPath,
        [string[]]$Arguments = @())

    $missing = @($Prerequisites | Where-Object { $null -eq (Get-Command $_ -ErrorAction SilentlyContinue) })
    if ($missing.Count -ne 0) {
        Add-Result $Name 'not-qualified' "missing prerequisite: $($missing -join ',')"
        return
    }

    Invoke-Required $Name $pwsh (@('-NoProfile', '-File', $ScriptPath) + $Arguments)
}

function Test-Selected {
    param([Parameter(Mandatory)][string]$Name)
    return $Command -eq 'all' -or $Command -eq $Name
}

function Assert-ExactAllResultShape {
    if ($Command -ne 'all' -or -not $qualifiedArchitecture) {
        return
    }
    $names = @(
        'self-test-architecture', 'self-test-atomic', 'self-test-raw',
        'self-test-no-lock', 'self-test-crash', 'self-test-release-tests',
        'self-test-interop', 'self-test-samples', 'self-test-pack',
        'dotnet-info', 'clean', 'restore', 'build',
        'architecture', 'atomic', 'raw', 'no-lock-held', 'no-lock-linux-strace',
        'linux-tiny-performance',
        'crash-checkpoint-kill', 'crash-linux-sigstop', 'crash-linux-docker-pause',
        'release-tests', 'native', 'python', 'docker', 'sample-6', 'sample-12', 'pack')
    $requiredByName = [ordered]@{}
    foreach ($name in $names) {
        $requiredByName[$name] = $true
    }
    $requiredByName['crash-linux-docker-pause'] = $false
    if ($platform -eq 'windows' -and $architecture -eq 'x64') {
        $requiredByName['no-lock-linux-strace'] = $false
        $requiredByName['crash-linux-sigstop'] = $false
        $requiredByName['linux-tiny-performance'] = $false
    }

    if ($results.Count -ne $requiredByName.Count `
        -or @($results | Group-Object name | Where-Object Count -ne 1).Count -ne 0) {
        throw "command=all must emit exactly $($requiredByName.Count) unique result rows."
    }
    foreach ($entry in $requiredByName.GetEnumerator()) {
        $rows = @($results | Where-Object { [string]$_.name -ceq [string]$entry.Key })
        if ($rows.Count -ne 1 -or [bool]$rows[0].required -ne [bool]$entry.Value) {
            throw "command=all row '$($entry.Key)' is missing, duplicated, or has the wrong required flag."
        }
    }
}

function Invoke-DotNetTest {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Filter,
        [bool]$Required = $true)

    $trxDirectory = Join-Path $evidenceRoot ('trx/' + ($Name -replace '[^A-Za-z0-9_.-]', '-'))
    New-Item -ItemType Directory -Path $trxDirectory -Force | Out-Null
    Invoke-Required $Name $dotnet @(
        'test', $Project, '-c', $Configuration, '--nologo', '--no-build', '--no-restore',
        '--filter', $Filter, '--logger', 'trx', '--results-directory', $trxDirectory) $Required
    Assert-TrxPassed $Name $trxDirectory
}

function Assert-TrxPassed {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Directory)

    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -Filter '*.trx' -File -ErrorAction SilentlyContinue)
    $total = 0
    $passed = 0
    $outcomes = @{}
    foreach ($file in $files) {
        [xml]$document = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($node in @($document.SelectNodes("//*[local-name()='UnitTestResult']"))) {
            $outcome = [string]$node.outcome
            $total++
            if (-not $outcomes.ContainsKey($outcome)) {
                $outcomes[$outcome] = 0
            }
            $outcomes[$outcome]++
            if ($outcome -ceq 'Passed') {
                $passed++
            }
        }
    }
    $nonPassed = $total - $passed
    if ($files.Count -eq 0 -or $passed -eq 0 -or $nonPassed -ne 0) {
        $outcomeDetail = @($outcomes.GetEnumerator() | Sort-Object Key | ForEach-Object {
            "$($_.Key)=$($_.Value)"
        }) -join ', '
        throw "Validation '$Name' TRX proof is invalid: files=$($files.Count), passed=$passed, nonPassed=$nonPassed, outcomes=[$outcomeDetail]."
    }
    $result = @($results | Where-Object name -eq $Name | Select-Object -Last 1)
    if ($result.Count -ne 1) {
        throw "Validation '$Name' has no unique executable result to attach TRX evidence."
    }
    $result[0].detail = "TRX passed=$passed nonPassed=0 files=$($files.Count)"
}

function Add-NotQualifiedPlatform {
    param([Parameter(Mandatory)][string[]]$Names)
    foreach ($name in $Names) {
        Add-Result $name 'not-qualified' "$platform-$architecture; layout v2 requires Windows/Linux x64"
    }
}

try {
    Assert-KnownProvenance $repositoryProvenance 'start'
    if (-not $ValidateOnly -and $repositoryProvenance.workingTreeState -ne 'clean') {
        throw 'Executable OS qualification requires a clean working tree.'
    }
    $definitionNames = if ($Command -in @('self-test', 'all')) {
        @($definitions.Keys)
    }
    else {
        @($Command)
    }
    foreach ($name in $definitionNames) {
        Test-Definition $name
    }

    $qualificationConfig = Join-Path $root 'specs/009-lock-free-publish-read/qualification-config.json'
    if (-not (Test-Path -LiteralPath $qualificationConfig)) {
        throw 'OS validation qualification config is missing.'
    }
    $parsedConfig = Get-Content -LiteralPath $qualificationConfig -Raw | ConvertFrom-Json
    if (-not (Test-IsIntegerNumber $parsedConfig.schemaVersion) `
        -or [int64]$parsedConfig.schemaVersion -ne 4) {
        throw 'OS validation requires qualification config schema 4.'
    }
    $linuxTinyConfig = Assert-LinuxTinyPerformanceConfiguration $parsedConfig
    $releaseConfig = Get-RequiredPropertyValue `
        (Get-RequiredPropertyValue $parsedConfig 'tiers' 'qualification config') `
        'release' 'qualification config tiers'

    if ($ValidateOnly) {
        $performanceParserAssertions = Invoke-LinuxTinyPerformanceParserSelfTest $linuxTinyConfig $releaseConfig
        Add-Result 'validation-plan' 'pass' `
            "configuration and structural inputs validated; linuxTinyPerformanceParserAssertions=$performanceParserAssertions; no restore, build, tests, interop, sample, performance workload, or pack executed"
        $completionProvenance = Get-RepositoryProvenance
        Assert-ProvenanceStable $repositoryProvenance $completionProvenance
    }
    else {
        Invoke-Required 'dotnet-info' $dotnet @('--info')
        $preclean = Remove-SolutionProjectBuildOutputs (Join-Path $evidenceRoot 'preclean.json')
        Invoke-Required 'clean' $dotnet @(
            'clean', 'SharedMemoryStore.slnx', '-c', $Configuration, '--nologo', '--disable-build-servers')
        $cleanResult = @($results | Where-Object name -eq 'clean')
        if ($cleanResult.Count -ne 1) {
            throw 'OS validation did not record exactly one clean result.'
        }
        $cleanResult[0] | Add-Member -NotePropertyName preclean -NotePropertyValue $preclean.Summary
        $cleanResult[0].detail += "; solutionProjects=$($preclean.SolutionProjectCount); outputTargets=$($preclean.TargetCount); precleanedOutputDirectories=$($preclean.RemovedDirectoryCount); verifiedAbsent=$($preclean.VerifiedAbsentCount); precleanReport=$([IO.Path]::GetRelativePath($root, $preclean.ReportPath)); precleanReportSha256=$($preclean.ReportSha256)"
        Invoke-Required 'restore' $dotnet @('restore', 'SharedMemoryStore.slnx', '--nologo', '--disable-build-servers')
        Invoke-Required 'build' $dotnet @(
            'build', 'SharedMemoryStore.slnx', '-c', $Configuration, '--nologo', '--no-restore', '--disable-build-servers')
        $testedAssemblyManifest = @(Get-TestedAssemblyManifest)

    if ($Command -eq 'all') {
        if (-not $IsLinux) {
            Add-Result 'linux-tiny-performance' 'not-qualified' `
                'Linux-only SC-006 tiny-operation release matrix; optional on Windows' $false
        }
        elseif (-not $qualifiedArchitecture) {
            Add-Result 'linux-tiny-performance' 'not-qualified' `
                "$platform-$architecture; Linux tiny performance requires Linux x64"
        }
        elseif ([Environment]::ProcessorCount -lt 8) {
            Add-Result 'linux-tiny-performance' 'not-qualified' `
                "Linux tiny performance requires at least 8 logical processors for complete affinity; actual=$([Environment]::ProcessorCount)"
        }
        else {
            $performancePath = Join-Path $evidenceRoot 'linux-tiny-performance.json'
            Invoke-Required 'linux-tiny-performance' $dotnet @(
                'run', '-c', $Configuration, '--no-build', '--no-restore',
                '--project', 'benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj', '--',
                '--mode', [string]$linuxTinyConfig.mode,
                '--profile', 'both',
                '--scenario', 'acquire-release,publish-remove',
                '--process-counts', '1,8',
                '--warmup', [string]$releaseConfig.performanceWarmupSeconds,
                '--duration', [string]$releaseConfig.performanceDurationSeconds,
                '--trials', [string]$releaseConfig.performanceTrials,
                '--output', $performancePath)
            $performanceRow = @($results | Where-Object name -eq 'linux-tiny-performance')
            if ($performanceRow.Count -ne 1) {
                throw 'Linux tiny performance command did not emit exactly one result row.'
            }
            try {
                if (-not (Test-Path -LiteralPath $performancePath -PathType Leaf)) {
                    throw 'Linux tiny performance command did not emit its raw JSON report.'
                }
                $performanceReport = Get-Content -LiteralPath $performancePath -Raw | ConvertFrom-Json -Depth 30
                $performanceValidation = Assert-LinuxTinyPerformanceReport `
                    $performanceReport $linuxTinyConfig $releaseConfig
                $relativePerformancePath = [IO.Path]::GetRelativePath($root, $performancePath)
                $performanceRow[0].detail =
                    'schema6 exact 2-profile x 2-scenario x (1,8)-process x 3-trial Linux matrix passed; ' +
                    'LF1 p99<=Legacy1, LF8 throughput>=Legacy8, LF8/LF1 p99<=3, LF8 p99<=10us, ' +
                    'and every lock-free raw MaxMicroseconds<=10000'
                $performanceRow[0] | Add-Member -NotePropertyName performanceEvidence -NotePropertyValue `
                    ([pscustomobject][ordered]@{
                        schemaVersion = 1
                        reportPath = $relativePerformancePath
                        reportSha256 = Get-FileSha256 $performancePath
                        validation = $performanceValidation
                    })
            }
            catch {
                $performanceRow[0].status = 'fail'
                $performanceRow[0].detail = $_.Exception.Message
                throw
            }
        }
    }

    if ($Command -eq 'self-test') {
        Invoke-DotNetTest 'self-test-trace-classifier' $integrationProject 'FullyQualifiedName=SharedMemoryStore.IntegrationTests.LockFreeOsTraceIntegrationTests.TraceClassifierSeparatesColdAndMarkedStoreLockCalls|FullyQualifiedName=SharedMemoryStore.IntegrationTests.LockFreeOsTraceIntegrationTests.DockerCheckpointPrefixSharesTheHostPidNamespace'
    }

    if (Test-Selected 'architecture') {
        if (-not $qualifiedArchitecture) {
            Add-NotQualifiedPlatform @('architecture')
        }
        else {
            Invoke-DotNetTest 'architecture' $contractProject 'FullyQualifiedName~LockFreeProfileApiContractTests|FullyQualifiedName~LockFreeLayoutContractTests'
        }
    }

    if (Test-Selected 'atomic') {
        if (-not $qualifiedArchitecture) {
            Add-NotQualifiedPlatform @('atomic')
        }
        else {
            Invoke-DotNetTest 'atomic' $integrationProject 'FullyQualifiedName~MappedAtomicLitmusIntegrationTests'
        }
    }

    if (Test-Selected 'raw') {
        if (-not $qualifiedArchitecture) {
            Add-NotQualifiedPlatform @('raw')
        }
        else {
            Invoke-DotNetTest 'raw' $integrationProject 'FullyQualifiedName~LockFreeRawVisibilityIntegrationTests'
        }
    }

    if (Test-Selected 'no-lock') {
        if (-not $qualifiedArchitecture) {
            Add-NotQualifiedPlatform @('no-lock-held', 'no-lock-linux-strace')
        }
        else {
            Invoke-DotNetTest 'no-lock-held' $integrationProject 'FullyQualifiedName~LockFreeNoOperationLockIntegrationTests'

            if (-not $IsLinux) {
                Add-Result 'no-lock-linux-strace' 'not-qualified' 'not applicable on this non-Linux platform; held-lock evidence is reported separately' $false
            }
            elseif (-not (Test-NativeCommand 'strace' @('--version'))) {
                Add-Result 'no-lock-linux-strace' 'not-qualified' 'strace command unavailable or unusable'
            }
            else {
                Invoke-DotNetTest 'no-lock-linux-strace' $integrationProject 'FullyQualifiedName=SharedMemoryStore.IntegrationTests.LockFreeOsTraceIntegrationTests.LinuxMarkedSteadyIntervalDoesNotUseStoreOperationLock'
            }
        }
    }

    if (Test-Selected 'crash') {
        if (-not $qualifiedArchitecture) {
            Add-NotQualifiedPlatform @('crash-checkpoint-kill', 'crash-linux-sigstop', 'crash-linux-docker-pause')
        }
        else {
            Invoke-DotNetTest 'crash-checkpoint-kill' $integrationProject 'FullyQualifiedName~LockFreeCrashRecoveryIntegrationTests'

            if (-not $IsLinux) {
                Add-Result 'crash-linux-sigstop' 'not-qualified' 'not applicable on this non-Linux platform' $false
            }
            elseif (-not (Test-NativeCommand 'kill' @('--version'))) {
                Add-Result 'crash-linux-sigstop' 'not-qualified' 'kill command unavailable or unusable'
            }
            else {
                Invoke-DotNetTest 'crash-linux-sigstop' $integrationProject 'FullyQualifiedName=SharedMemoryStore.IntegrationTests.LockFreeOsTraceIntegrationTests.LinuxSigStopAtProtocolCheckpointDoesNotBlockUnrelatedProgress'
            }

            if ($SkipDocker) {
                Add-Result 'crash-linux-docker-pause' 'not-qualified' 'optional Docker checkpoint-pause coverage explicitly skipped' $false
            }
            elseif (-not $IsLinux) {
                Add-Result 'crash-linux-docker-pause' 'not-qualified' 'optional coverage is not applicable on this non-Linux platform' $false
            }
            elseif (-not (Test-NativeCommand 'docker' @('info', '--format', '{{.ServerVersion}}'))) {
                Add-Result 'crash-linux-docker-pause' 'not-qualified' 'optional Docker command or daemon unavailable' $false
            }
            elseif (-not (Test-NativeCommand 'docker' @('image', 'inspect', $DockerRuntimeImage))) {
                Add-Result 'crash-linux-docker-pause' 'not-qualified' "optional runtime image unavailable (not pulled automatically): $DockerRuntimeImage" $false
            }
            elseif (-not (Test-DockerHostPidIdentityVisible $DockerRuntimeImage)) {
                Add-Result 'crash-linux-docker-pause' 'not-qualified' 'optional Docker host PID namespace does not expose the invoking Linux PID with its exact /proc start identity' $false
            }
            else {
                $previousRun = $env:SMS_RUN_LOCK_FREE_DOCKER_PAUSE_VALIDATION
                $previousImage = $env:SMS_LOCK_FREE_DOCKER_IMAGE
                try {
                    $env:SMS_RUN_LOCK_FREE_DOCKER_PAUSE_VALIDATION = '1'
                    $env:SMS_LOCK_FREE_DOCKER_IMAGE = $DockerRuntimeImage
                    Invoke-DotNetTest 'crash-linux-docker-pause' $integrationProject 'FullyQualifiedName=SharedMemoryStore.IntegrationTests.LockFreeOsTraceIntegrationTests.LinuxDockerPauseAtProtocolCheckpointDoesNotBlockUnrelatedProgress' $false
                }
                finally {
                    $env:SMS_RUN_LOCK_FREE_DOCKER_PAUSE_VALIDATION = $previousRun
                    $env:SMS_LOCK_FREE_DOCKER_IMAGE = $previousImage
                }
            }
        }
    }

    if (Test-Selected 'release-tests') {
        $releaseTrx = Join-Path $evidenceRoot 'trx/release-tests'
        New-Item -ItemType Directory -Path $releaseTrx -Force | Out-Null
        Invoke-Required 'release-tests' $dotnet @(
            'test', 'SharedMemoryStore.slnx', '-c', $Configuration, '--nologo', '--no-build', '--no-restore',
            '--logger', 'trx', '--results-directory', $releaseTrx)
        Assert-TrxPassed 'release-tests' $releaseTrx
    }

    if (Test-Selected 'interop') {
        Invoke-OptionalScript 'native' @('cmake') (Join-Path $PSScriptRoot 'validate-native.ps1') @('-Configuration', $Configuration)
        Invoke-OptionalScript 'python' @('python', 'cmake') (Join-Path $PSScriptRoot 'validate-python.ps1') @('-Configuration', $Configuration)

        if ($SkipDocker) {
            Add-Result 'docker' 'not-qualified' 'explicitly skipped'
        }
        elseif (-not (Test-NativeCommand 'docker' @('info', '--format', '{{.ServerVersion}}'))) {
            Add-Result 'docker' 'not-qualified' 'Docker command or daemon unavailable'
        }
        else {
            Invoke-Required 'docker' $pwsh @(
                '-NoProfile', '-File', (Join-Path $PSScriptRoot 'validate-docker-shared-memory.ps1'),
                '-Profile', 'All', '-Configuration', $Configuration)
        }
    }

    if (Test-Selected 'samples') {
        foreach ($workers in @(6, 12)) {
            Invoke-Required "sample-$workers" $dotnet @(
                'run', '-c', $Configuration, '--no-build', '--no-restore',
                '--project', 'samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj', '--',
                '--workers', [string]$workers, '--frames', [string]($workers * 4))
        }
    }

    if (Test-Selected 'pack') {
        Invoke-Required 'pack' $dotnet @(
            'pack', 'src/SharedMemoryStore/SharedMemoryStore.csproj',
            '-c', $Configuration, '--nologo', '--no-build', '--no-restore')
    }
        $completionAssemblyManifest = @(Get-TestedAssemblyManifest)
        Assert-AssemblyManifestStable $testedAssemblyManifest $completionAssemblyManifest
        $completionProvenance = Get-RepositoryProvenance
        Assert-ProvenanceStable $repositoryProvenance $completionProvenance
        Assert-ExactAllResultShape
    }
}
catch {
    Add-Result 'validation-script' 'fail' $_.Exception.Message
    throw
}
finally {
    if ($null -eq $completionProvenance) {
        $completionProvenance = Get-RepositoryProvenance
    }
    if ($testedAssemblyManifest.Count -gt 0 -and $completionAssemblyManifest.Count -eq 0) {
        try {
            $completionAssemblyManifest = @(Get-TestedAssemblyManifest)
        }
        catch {
            $completionAssemblyManifest = @()
        }
    }
    $requiredStatuses = @($results | Where-Object required | ForEach-Object status)
    $overallStatus = if ($ValidateOnly -and $requiredStatuses -notcontains 'fail') {
        'validation-only'
    }
    elseif ($requiredStatuses -contains 'fail') {
        'fail'
    }
    elseif ($requiredStatuses -contains 'not-qualified') {
        'not-qualified'
    }
    elseif ($requiredStatuses.Count -gt 0) {
        'pass'
    }
    else {
        'not-qualified'
    }

    $manifest = @(Get-ChildItem -LiteralPath $evidenceRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject][ordered]@{
            path = [IO.Path]::GetRelativePath($root, $_.FullName)
            length = $_.Length
            sha256 = Get-FileSha256 $_.FullName
        }
    })
    $dotnetInfo = @($results | Where-Object name -eq 'dotnet-info' | Select-Object -First 1)
    [pscustomobject][ordered]@{
        schemaVersion = 3
        validationOnly = [bool]$ValidateOnly
        startedUtc = $runStartedUtc
        completedUtc = [DateTimeOffset]::UtcNow
        command = $Command
        configuration = $Configuration
        platform = $platform
        architecture = $architecture
        qualifiedArchitecture = $qualifiedArchitecture
        overallStatus = $overallStatus
        provenance = $repositoryProvenance
        completionProvenance = $completionProvenance
        testedAssemblies = $testedAssemblyManifest
        completionTestedAssemblies = $completionAssemblyManifest
        host = [ordered]@{
            operatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            operatingSystemArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            framework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
            runtimeVersion = [Environment]::Version.ToString()
            logicalProcessorCount = [Environment]::ProcessorCount
            powershellVersion = $PSVersionTable.PSVersion.ToString()
        }
        toolchain = [ordered]@{
            dotnetPath = $dotnet
            dotnetVersion = Invoke-TextCommand $dotnet @('--version')
            dotnetInfoSha256 = if ($dotnetInfo.Count -eq 1) { $dotnetInfo[0].stdoutSha256 } else { $null }
            powershellPath = $pwsh
            gitPath = $git
            gitVersion = Invoke-TextCommand $git @('--version')
            cmakePath = (Get-Command cmake -ErrorAction SilentlyContinue).Source
            pythonPath = (Get-Command python -ErrorAction SilentlyContinue).Source
            dockerPath = (Get-Command docker -ErrorAction SilentlyContinue).Source
            stracePath = (Get-Command strace -ErrorAction SilentlyContinue).Source
        }
        inputs = [ordered]@{
            script = [IO.Path]::GetRelativePath($root, $PSCommandPath)
            scriptSha256 = Get-FileSha256 $PSCommandPath
            qualificationConfig = [IO.Path]::GetRelativePath($root, $qualificationConfig)
            qualificationConfigSha256 = Get-FileSha256 $qualificationConfig
            solutionSha256 = Get-FileSha256 (Join-Path $root 'SharedMemoryStore.slnx')
        }
        results = $results
        evidenceManifest = $manifest
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath
}

if ($overallStatus -eq 'validation-only') {
    Write-Host "OS validation orchestration '$Command' validated without executing workloads. Evidence: $resultPath"
}
elseif ($overallStatus -eq 'not-qualified') {
    Write-Warning "OS validation '$Command' is NOT QUALIFIED. Evidence: $resultPath"
    exit 2
}
else {
    Write-Host "OS validation '$Command' completed. Evidence: $resultPath"
}
