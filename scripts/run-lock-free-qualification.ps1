[CmdletBinding()]
param(
    [ValidateSet('pr', 'nightly', 'release')]
    [string]$Tier = 'pr',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/lock-free-qualification',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$EvidenceRunId = '',
    [switch]$SkipPerformance,
    [switch]$SkipOsValidation,
    [string[]]$AdditionalOsEvidence = @(),
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
        throw 'Executable qualification could not determine repository cleanliness.'
    }
    if ($earlyStatus.Count -ne 0) {
        throw 'Executable qualification requires a clean working tree.'
    }
}
$runStartedUtc = [DateTimeOffset]::UtcNow
$configPath = Join-Path $root 'specs/009-lock-free-publish-read/qualification-config.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$selected = $config.tiers.$Tier
if ($null -eq $selected) {
    throw "Qualification tier '$Tier' is absent from $configPath."
}
$churnTestSourceRelativePath = 'tests/SharedMemoryStore.IntegrationTests/LockFreeChurnIntegrationTests.cs'
$churnTestNamespace = 'SharedMemoryStore.IntegrationTests'
$churnTestClass = 'LockFreeChurnIntegrationTests'
$churnQualificationTestMethod = 'CollisionHeavyMultiProcessRemoveReuseRestoresCapacityAndKeepsLateLatencyBounded'
$churnQualificationTestNameFragment = "$churnTestClass.$churnQualificationTestMethod"

$outputRoot = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not ($outputRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Qualification output must remain below '$allowedRoot'."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$runId = if ([string]::IsNullOrWhiteSpace($EvidenceRunId)) {
    (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + $Tier + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}
else {
    $EvidenceRunId
}
$runRoot = Join-Path $outputRoot $runId
if (Test-Path -LiteralPath $runRoot) {
    throw "Refusing to reuse qualification evidence directory '$runRoot'."
}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$results = [Collections.Generic.List[object]]::new()
$notQualifiedReasons = [Collections.Generic.List[string]]::new()
$acceptedOsEvidence = [Collections.Generic.List[object]]::new()
$overallStatus = 'running'
$failureMessage = $null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$powershell = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName

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
        commit = Invoke-TextCommand $git @('-C', $root, 'rev-parse', 'HEAD')
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

    foreach ($property in @('commit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
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
    foreach ($property in @('commit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
        if ([string]$Start[$property] -ne [string]$End[$property]) {
            throw "Repository provenance changed during qualification: '$property' start='$($Start[$property])' completion='$($End[$property])'."
        }
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
        $Minimum = [int64]::MinValue,
        $Maximum = [int64]::MaxValue)

    # Command-mode arguments can preserve a static-member token as text. Only
    # these exact internal bound tokens are accepted; evidence values remain
    # required to be real JSON numbers below.
    $minimumBound = if (Test-IsIntegerNumber $Minimum) {
        [Convert]::ToInt64($Minimum, [Globalization.CultureInfo]::InvariantCulture)
    }
    elseif ([string]$Minimum -eq '[int64]::MinValue') {
        [int64]::MinValue
    }
    else {
        throw "$Context.$Property received an invalid internal minimum bound '$Minimum'."
    }
    $maximumBound = if (Test-IsIntegerNumber $Maximum) {
        [Convert]::ToInt64($Maximum, [Globalization.CultureInfo]::InvariantCulture)
    }
    elseif ([string]$Maximum -eq '[int32]::MaxValue') {
        [int32]::MaxValue
    }
    elseif ([string]$Maximum -eq '[int64]::MaxValue') {
        [int64]::MaxValue
    }
    else {
        throw "$Context.$Property received an invalid internal maximum bound '$Maximum'."
    }

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if (-not (Test-IsIntegerNumber $value)) {
        throw "$Context.$Property must be an integer JSON number, actual type '$($value.GetType().FullName)'."
    }
    try {
        $converted = [Convert]::ToInt64($value, [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Context.$Property is outside signed 64-bit range."
    }
    if ($converted -lt $minimumBound -or $converted -gt $maximumBound) {
        throw "$Context.$Property=$converted is outside [$minimumBound,$maximumBound]."
    }
    return $converted
}

function Get-StrictDouble {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context,
        $Minimum = -[double]::MaxValue,
        $Maximum = [double]::MaxValue,
        [switch]$Positive)

    $minimumBound = if (Test-IsNumericValue $Minimum) {
        [Convert]::ToDouble($Minimum, [Globalization.CultureInfo]::InvariantCulture)
    }
    elseif ([string]$Minimum -eq '-[double]::MaxValue') {
        -[double]::MaxValue
    }
    else {
        throw "$Context.$Property received an invalid internal minimum bound '$Minimum'."
    }
    $maximumBound = if (Test-IsNumericValue $Maximum) {
        [Convert]::ToDouble($Maximum, [Globalization.CultureInfo]::InvariantCulture)
    }
    elseif ([string]$Maximum -eq '[double]::MaxValue') {
        [double]::MaxValue
    }
    else {
        throw "$Context.$Property received an invalid internal maximum bound '$Maximum'."
    }

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if (-not (Test-IsNumericValue $value)) {
        throw "$Context.$Property must be a numeric JSON value, actual type '$($value.GetType().FullName)'."
    }
    $converted = [Convert]::ToDouble($value, [Globalization.CultureInfo]::InvariantCulture)
    if (-not [double]::IsFinite($converted) -or $converted -lt $minimumBound -or $converted -gt $maximumBound `
        -or ($Positive -and $converted -le 0)) {
        throw "$Context.$Property=$converted is not a valid finite value."
    }
    return $converted
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

function Get-StrictString {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string]$Context,
        [switch]$AllowEmpty)

    $value = Get-RequiredPropertyValue $Object $Property $Context
    if ($value -isnot [string] -or (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($value))) {
        throw "$Context.$Property must be a JSON string$(if ($AllowEmpty) { '' } else { ' with content' })."
    }
    return [string]$value
}

function Get-CanonicalCheckpointCatalog {
    $catalogPath = Join-Path $root 'src/SharedMemoryStore/LockFree/LockFreeCheckpoint.cs'
    $source = Get-Content -LiteralPath $catalogPath -Raw
    $enumMatch = [regex]::Match(
        $source,
        '(?s)internal\s+enum\s+LockFreeCheckpointId\s*\{(?<body>.*?)\}')
    if (-not $enumMatch.Success) {
        throw "Cannot parse LockFreeCheckpointId from '$catalogPath'."
    }

    $idByName = @{}
    foreach ($match in [regex]::Matches(
        $enumMatch.Groups['body'].Value,
        '(?m)^\s*(?<name>[A-Za-z][A-Za-z0-9_]*)\s*=\s*(?<id>[0-9]+)\s*,?\s*$')) {
        $name = $match.Groups['name'].Value
        $id = [int]$match.Groups['id'].Value
        if ($idByName.ContainsKey($name)) {
            throw "Checkpoint enum contains duplicate name '$name'."
        }
        $idByName[$name] = $id
    }

    $catalogMatches = [regex]::Matches(
        $source,
        '(?s)(?<position>Before|After)\(\s*' +
            'LockFreeCheckpointId\.(?<name>[A-Za-z][A-Za-z0-9_]*)\s*,\s*' +
            'LockFreeCheckpointFamily\.(?<family>[A-Za-z][A-Za-z0-9_]*)\s*,\s*' +
            'LockFreePauseClassification\.(?<pause>[A-Za-z][A-Za-z0-9_]*)\s*,\s*' +
            'LockFreeCrashClassification\.(?<crash>[A-Za-z][A-Za-z0-9_]*)\s*,\s*' +
            'LockFreeRaceClassification\.(?<race>[A-Za-z][A-Za-z0-9_]*)\s*,\s*' +
            '(?:orderingPoint:\s*(?<ordering>true|false)\s*,\s*)?"')
    if ($idByName.Count -eq 0 -or $catalogMatches.Count -ne $idByName.Count) {
        throw "Checkpoint catalog must classify every enum member exactly once; enum=$($idByName.Count), catalog=$($catalogMatches.Count)."
    }

    $entries = foreach ($match in $catalogMatches) {
        $name = $match.Groups['name'].Value
        if (-not $idByName.ContainsKey($name)) {
            throw "Checkpoint catalog references unknown enum member '$name'."
        }
        [pscustomobject][ordered]@{
            id = [int]$idByName[$name]
            name = $name
            family = $match.Groups['family'].Value
            position = $match.Groups['position'].Value
            pause = $match.Groups['pause'].Value
            crash = $match.Groups['crash'].Value
            race = $match.Groups['race'].Value
            isPublicOrderingPoint = $match.Groups['ordering'].Success -and
                $match.Groups['ordering'].Value -ceq 'true'
        }
    }
    $ordered = @($entries | Sort-Object id)
    if (@($ordered | Group-Object id | Where-Object Count -ne 1).Count -ne 0 `
        -or @($ordered | Group-Object name | Where-Object Count -ne 1).Count -ne 0) {
        throw 'Checkpoint catalog contains a duplicate or omits a canonical identifier.'
    }
    for ($index = 0; $index -lt $ordered.Count; $index++) {
        if ([int]$ordered[$index].id -ne ($index + 1)) {
            throw "Checkpoint identifiers must remain append-only and contiguous; index=$index id=$($ordered[$index].id)."
        }
    }
    return $ordered
}

$checkpointCatalog = @(Get-CanonicalCheckpointCatalog)
$canonicalSuspensionCheckpointIds = @($checkpointCatalog |
    Where-Object { $_.family -notin @('Participant', 'Disposal') } |
    ForEach-Object { [int]$_.id })

function Get-TestedAssemblyManifest {
    [xml]$solution = Get-Content -LiteralPath (Join-Path $root 'SharedMemoryStore.slnx') -Raw
    $projectPaths = @($solution.SelectNodes("//*[local-name()='Project']") | ForEach-Object { [string]$_.Path })
    if ($projectPaths.Count -eq 0) {
        throw 'Solution does not expose any project paths for assembly provenance.'
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

function Assert-AssemblyManifestStable {
    param(
        [Parameter(Mandatory)][object[]]$Start,
        [Parameter(Mandatory)][object[]]$End)

    $startCanonical = @($Start | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" }) -join "`n"
    $endCanonical = @($End | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($startCanonical) -or $startCanonical -ne $endCanonical) {
        throw 'Tested assembly manifest changed after the clean build or while qualification was running.'
    }
}

function Get-TestedAssemblyHash {
    param([Parameter(Mandatory)][string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/')
    $assemblyRows = @($testedAssemblyManifest | Where-Object {
        ([string]$_.path).Replace('\', '/') -ceq $normalized
    })
    if ($assemblyRows.Count -ne 1) {
        throw "Fresh-build assembly manifest lacks unique path '$RelativePath'."
    }
    $hash = [string]$assemblyRows[0].sha256
    if ($hash -notmatch '^[0-9A-F]{64}$') {
        throw "Fresh-build assembly manifest lacks unique path '$RelativePath'."
    }
    return $hash
}

function Invoke-BoundedStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [hashtable]$Environment = @{},
        [int[]]$AllowedExitCodes = @(0)
    )

    $stdoutPath = Join-Path $runRoot ($Name + '.stdout.log')
    $stderrPath = Join-Path $runRoot ($Name + '.stderr.log')
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
    foreach ($entry in $Environment.GetEnumerator()) {
        $start.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $startedUtc = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "Could not start qualification step '$Name'."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timeoutMilliseconds = [int64]$selected.stepTimeoutSeconds * 1000
    $completed = $process.WaitForExit([int][Math]::Min([int]::MaxValue, $timeoutMilliseconds))
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
        "[qualification-runner] stdout unavailable: $streamDrainDetail"
    }
    $stderr = if ($stderrTask.IsCompletedSuccessfully) {
        $stderrTask.GetAwaiter().GetResult()
    }
    else {
        "[qualification-runner] stderr unavailable: $streamDrainDetail"
    }
    if (-not $terminationSucceeded) {
        $stderr += [Environment]::NewLine + "[qualification-runner] $terminationDetail"
    }
    [IO.File]::WriteAllText($stdoutPath, $stdout)
    [IO.File]::WriteAllText($stderrPath, $stderr)
    $stopwatch.Stop()

    $hasExited = $false
    try {
        $hasExited = $process.HasExited
    }
    catch {
        $hasExited = $false
    }
    $exitCode = if ($hasExited -and $completed) { $process.ExitCode } else { -1 }
    $executionFailed = -not $completed -or -not $terminationSucceeded -or -not $streamsDrained `
        -or $exitCode -notin $AllowedExitCodes

    $result = [ordered]@{
        name = $Name
        command = $FileName + ' ' + ($Arguments -join ' ')
        startedUtc = $startedUtc
        elapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        timeoutSeconds = [int]$selected.stepTimeoutSeconds
        timedOut = -not $completed
        exitCode = $exitCode
        environment = [ordered]@{} + $Environment
        stdout = [IO.Path]::GetRelativePath($root, $stdoutPath)
        stderr = [IO.Path]::GetRelativePath($root, $stderrPath)
        stdoutSha256 = Get-FileSha256 $stdoutPath
        stderrSha256 = Get-FileSha256 $stderrPath
        status = if ($executionFailed) {
            'failed'
        }
        elseif ($exitCode -eq 0) {
            'passed'
        }
        else {
            'not-qualified'
        }
        qualification = 'execution-only'
        validation = @()
    }
    $results.Add([pscustomobject]$result)
    $process.Dispose()
    if ($executionFailed) {
        throw "Qualification step '$Name' failed; see '$stdoutPath' and '$stderrPath'."
    }
}

function Add-EvidenceResult {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('passed', 'failed', 'not-qualified', 'smoke-only')][string]$Status,
        [Parameter(Mandatory)][string]$Qualification,
        [Parameter(Mandatory)][string[]]$Evidence,
        [string[]]$Artifacts = @())

    $results.Add([pscustomobject][ordered]@{
        name = $Name
        command = $null
        startedUtc = [DateTimeOffset]::UtcNow
        elapsedSeconds = 0
        timeoutSeconds = 0
        timedOut = $false
        exitCode = $null
        environment = [ordered]@{}
        stdout = $null
        stderr = $null
        stdoutSha256 = $null
        stderrSha256 = $null
        status = $Status
        qualification = $Qualification
        validation = $Evidence
        artifacts = $Artifacts
    })
}

function Get-StepResult {
    param([Parameter(Mandatory)][string]$Name)

    $matches = @($results | Where-Object { $_.name -eq $Name })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one result for qualification step '$Name'."
    }

    return $matches[0]
}

function Set-StepValidation {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('passed', 'failed', 'not-qualified', 'smoke-only')][string]$Status,
        [Parameter(Mandatory)][string]$Qualification,
        [Parameter(Mandatory)][string[]]$Evidence
    )

    $result = Get-StepResult $Name
    $result.status = $Status
    $result.qualification = $Qualification
    $result.validation = $Evidence
}

function Fail-StepValidation {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Message
    )

    Set-StepValidation $Name 'failed' 'validation-failed' @($Message)
    throw "Qualification evidence '$Name' failed validation: $Message"
}

function Mark-StepNotQualified {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Reason,
        [string[]]$Evidence = @()
    )

    Set-StepValidation $Name 'not-qualified' 'environment-not-qualified' (@($Reason) + $Evidence)
    $notQualifiedReasons.Add("$Name`: $Reason")
}

function Assert-ExactStringSet {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][object[]]$Actual,
        [Parameter(Mandatory)][string[]]$Expected)

    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Actual) {
        if ($value -isnot [string] -or -not $actualSet.Add([string]$value)) {
            throw "Qualification config '$Name' contains a nonstring or duplicate value."
        }
    }
    $expectedSet = [Collections.Generic.HashSet[string]]::new($Expected, [StringComparer]::Ordinal)
    if ($Actual.Count -ne $Expected.Count -or -not $actualSet.SetEquals($expectedSet)) {
        throw "Qualification config '$Name' must be exactly [$($Expected -join ', ')], actual [$($Actual -join ', ')]."
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

function Assert-RequiredBenchmarkHardwareMetadata {
    param(
        [Parameter(Mandatory)]$Environment,
        [Parameter(Mandatory)][string]$Context)

    $logicalCount = Get-StrictInt64 $Environment 'logicalProcessorCount' $Context 1 ([int32]::MaxValue)
    [void](Get-StrictInt64 $Environment 'physicalCoreCount' $Context 1 $logicalCount)
    [void](Get-StrictInt64 $Environment 'totalMemoryBytes' $Context 1048576 ([int64]::MaxValue))
    $processorModel = Get-StrictString $Environment 'processorModel' $Context
    $processorIdentifier = Get-StrictString $Environment 'processorIdentifier' $Context
    foreach ($value in @($processorModel, $processorIdentifier)) {
        if ($value.Trim() -match '^(?i:(?:unknown|unavailable|not[- ]available|n/?a)(?:\s+(?:cpu|processor|model))?)$') {
            throw "$Context contains unknown processor-model evidence '$value'."
        }
    }
}

function Assert-ExactBenchmarkStoreDimensions {
    param(
        [Parameter(Mandatory)]$Dimensions,
        [Parameter(Mandatory)][string]$Context,
        [Parameter(Mandatory)][int64]$SlotCount,
        [Parameter(Mandatory)][int64]$MaxValueBytes,
        [Parameter(Mandatory)][int64]$MaxDescriptorBytes,
        [Parameter(Mandatory)][int64]$MaxKeyBytes,
        [Parameter(Mandatory)][int64]$LeaseRecordCount,
        [Parameter(Mandatory)][int64]$LockFreeParticipantRecordCount)

    $expected = [ordered]@{
        slotCount = $SlotCount
        maxValueBytes = $MaxValueBytes
        maxDescriptorBytes = $MaxDescriptorBytes
        maxKeyBytes = $MaxKeyBytes
        leaseRecordCount = $LeaseRecordCount
        lockFreeParticipantRecordCount = $LockFreeParticipantRecordCount
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if ((Get-StrictInt64 $Dimensions $entry.Key $Context 0 ([int32]::MaxValue)) -ne $entry.Value) {
            throw "$Context.$($entry.Key) does not match the exact benchmark store topology."
        }
    }
}

function Assert-BenchmarkScenarioStoreDimensions {
    param(
        [Parameter(Mandatory)]$Configuration,
        [Parameter(Mandatory)][string[]]$ExpectedScenarios,
        [Parameter(Mandatory)][string]$Context)

    $allDimensions = Get-RequiredPropertyValue $Configuration 'scenarioStoreDimensions' $Context
    Assert-ExactStringSet "$Context scenarioStoreDimensions" `
        @($allDimensions.PSObject.Properties.Name) $ExpectedScenarios
    foreach ($scenario in $ExpectedScenarios) {
        $expected = switch ($scenario) {
            { $_ -cin @('acquire-release', 'publish-remove') } { @(32, 8, 0, 8, 64, 64); break }
            { $_ -cin @('same-key-read', 'distributed-key-read') } { @(256, 256, 0, 8, 64, 64); break }
            { $_ -cin @('broker-directed', 'large-ingest') } {
                @(256, (Get-StrictInt64 $Configuration 'largeFrameBytes' $Context 1 ([int32]::MaxValue)), 16, 8, 64, 64)
                break
            }
            'mixed-churn' { @(768, 256, 16, 8, 128, 64); break }
            'sticky-overflow-miss' {
                @((Get-StrictInt64 $Configuration 'stickyOverflowSlotCount' $Context 1 ([int32]::MaxValue)), 1, 0, 8, 64, 64)
                break
            }
            default { throw "$Context has no store-dimension contract for scenario '$scenario'." }
        }
        Assert-ExactBenchmarkStoreDimensions `
            $allDimensions.$scenario `
            "$Context scenarioStoreDimensions.$scenario" `
            $expected[0] $expected[1] $expected[2] $expected[3] $expected[4] $expected[5]
    }
}

function Get-Sc017SourceTransitionCount {
    $sourcePath = Join-Path $root 'tests/SharedMemoryStore.UnitTests/LockFreeDirectoryGenerationStressTests.cs'
    $source = Get-Content -LiteralPath $sourcePath -Raw
    $matches = [regex]::Matches(
        $source,
        '(?m)^\s*private const int QualificationTransitionCount = (?<count>[1-9][0-9]*);\s*$')
    if ($matches.Count -ne 1) {
        throw 'The SC-017 test must declare exactly one machine-readable QualificationTransitionCount.'
    }

    return [Convert]::ToInt64(
        $matches[0].Groups['count'].Value,
        [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-Sc017TierConfiguration {
    param(
        [Parameter(Mandatory)]$QualificationConfig,
        [Parameter(Mandatory)][int64]$TransitionCount)

    $tiers = Get-RequiredPropertyValue $QualificationConfig 'tiers' 'qualification config'
    foreach ($tierName in @('pr', 'nightly', 'release')) {
        $tierConfig = Get-RequiredPropertyValue $tiers $tierName 'qualification config tiers'
        $configured = Get-StrictInt64 $tierConfig 'directoryGenerationStressRepetitions' `
            "qualification tier '$tierName'" 1 [int64]::MaxValue
        if ($configured -lt $TransitionCount) {
            throw "Qualification tier '$tierName' directoryGenerationStressRepetitions must be at least the source-owned SC-017 transition count $TransitionCount; actual=$configured."
        }
    }
}

function Invoke-Sc017ConfigurationVerifierSelfTest {
    $transitionCount = Get-Sc017SourceTransitionCount
    Assert-Sc017TierConfiguration $config $transitionCount

    $tampered = $config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $tampered.tiers.pr.directoryGenerationStressRepetitions = $transitionCount - 1
    $rejected = $false
    try {
        Assert-Sc017TierConfiguration $tampered $transitionCount
    }
    catch {
        $rejected = $_.Exception.Message -like '*must be at least the source-owned SC-017 transition count*'
    }
    if (-not $rejected) {
        throw "SC-017 configuration verifier self-test accepted $($transitionCount - 1) repetitions for $transitionCount transitions."
    }

    return 2
}

function Get-ChurnQualificationTestContract {
    param(
        [Parameter(Mandatory)]$QualificationConfig,
        [AllowEmptyString()][string]$SourceOverride)

    $assertions = @(Get-RequiredPropertyValue $QualificationConfig `
        'requiredLeakAssertions' 'qualification config')
    if ($assertions.Count -ne 3) {
        throw 'Qualification config must contain exactly three required leak assertions.'
    }
    $expectedEvidenceSteps = [ordered]@{
        'slot-owner-count=0' = 'churn'
        'lease-owner-count=0' = 'churn'
        'unreferenced-stale-participant-count=0' = 'recovery'
    }
    foreach ($expected in $expectedEvidenceSteps.GetEnumerator()) {
        $matches = @($assertions | Where-Object {
            [string]$_.id -ceq $expected.Key
        })
        if ($matches.Count -ne 1 `
            -or [string]$matches[0].evidenceStep -cne [string]$expected.Value) {
            throw "Leak assertion '$($expected.Key)' must map exactly once to evidence step '$($expected.Value)'."
        }
    }

    $churnAssertions = @($assertions | Where-Object {
        [string]$_.evidenceStep -ceq 'churn'
    })
    if ($churnAssertions.Count -ne 2) {
        throw "Qualification config must map exactly two owner/leak assertions to the churn evidence step; actual=$($churnAssertions.Count)."
    }

    $distinctMappings = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($assertion in $churnAssertions) {
        $mapping = Get-StrictString $assertion 'testNameContains' `
            "qualification leak assertion '$($assertion.id)'"
        [void]$distinctMappings.Add($mapping)
    }
    if ($distinctMappings.Count -ne 1) {
        throw 'Every churn owner/leak assertion must map to one identical test method.'
    }

    $mapping = @($distinctMappings)[0]
    $match = [regex]::Match(
        $mapping,
        '^(?<class>[A-Za-z_][A-Za-z0-9_]*)\.(?<method>[A-Za-z_][A-Za-z0-9_]*)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups['class'].Value -cne $churnTestClass) {
        throw "Configured churn evidence test '$mapping' must name one method on LockFreeChurnIntegrationTests."
    }
    if ($mapping -cne $churnQualificationTestNameFragment) {
        throw "Configured churn evidence test must remain the SC-016 collision workload '$churnQualificationTestNameFragment'; actual='$mapping'."
    }

    $sourcePath = Join-Path $root $churnTestSourceRelativePath
    $source = if ($PSBoundParameters.ContainsKey('SourceOverride')) {
        $SourceOverride
    }
    else {
        Get-Content -LiteralPath $sourcePath -Raw
    }
    $method = $match.Groups['method'].Value
    $namespacePattern = '(?m)^[ \t]*namespace[ \t]+' +
        [regex]::Escape($churnTestNamespace) + ';[ \t]*$'
    $classPattern = '(?m)^[ \t]*public[ \t]+sealed[ \t]+class[ \t]+' +
        [regex]::Escape($churnTestClass) + '[ \t]*$'
    $methodDeclarationPattern = '^[ \t]*\[Fact\][ \t]*\r?\n' +
        '(?:[ \t]*\[[^\r\n]+\][ \t]*\r?\n)*' +
        '[ \t]*public[ \t]+(?:async[ \t]+)?(?:void|Task(?:<[^>\r\n]+>)?)[ \t]+' +
        [regex]::Escape($method) + '[ \t]*\('
    $methodPattern = '(?ms)' + $methodDeclarationPattern
    $sourceContractPattern = '(?ms)' +
        '^[ \t]*namespace[ \t]+' + [regex]::Escape($churnTestNamespace) + ';[ \t]*\r?\n' +
        '[ \t\r\n]*' +
        '^[ \t]*public[ \t]+sealed[ \t]+class[ \t]+' +
        [regex]::Escape($churnTestClass) + '[ \t]*\r?\n[ \t]*\{[ \t]*\r?\n' +
        '(?:(?!^}[ \t]*$)(?!^[^\r\n]*\b(?:class|struct|record|interface|enum)\b).)*?' +
        $methodDeclarationPattern
    if (([regex]::Matches($source, $namespacePattern).Count -ne 1) `
        -or ([regex]::Matches($source, $classPattern).Count -ne 1) `
        -or ([regex]::Matches($source, $methodPattern).Count -ne 1) `
        -or ([regex]::Matches($source, $sourceContractPattern).Count -ne 1)) {
        throw "Configured churn evidence FQN '$churnTestNamespace.$mapping' must identify the exact namespace, class, and one [Fact] in $churnTestSourceRelativePath."
    }

    return [pscustomobject]@{
        testNameFragment = $mapping
        fullyQualifiedName = "$churnTestNamespace.$mapping"
        sourcePath = $churnTestSourceRelativePath
    }
}

function Assert-QualificationConfiguration {
    Assert-KnownProvenance $repositoryProvenance 'start'
    if (-not $ValidateOnly -and $repositoryProvenance.workingTreeState -ne 'clean') {
        throw 'Executable qualification requires a clean working tree.'
    }
    if ((Get-StrictInt64 $config 'schemaVersion' 'qualification config' 5 5) -ne 5) {
        throw "Qualification config schemaVersion must be 5."
    }
    if ($Configuration -ne 'Release' -and -not $ValidateOnly) {
        throw "Qualification evidence must be built in Release; '$Configuration' is diagnostic-only."
    }

    $positiveProperties = @(
        'stepTimeoutSeconds',
        'checkerHistoryRepetitionsPerFamily',
        'productionHistoryCountPerFamily',
        'productionRaceRepetitionsPerFamily',
        'churnCycles',
        'recoveryCases',
        'performanceWarmupSeconds',
        'performanceDurationSeconds',
        'performanceDurationBoundGraceSeconds',
        'performanceTrials',
        'mixedOperations',
        'largeFrames',
        'largeFrameBytes',
        'suspensionBaselineSeconds',
        'suspensionPauseSeconds',
        'suspensionWarmupSeconds',
        'directoryGenerationStressRepetitions')
    foreach ($property in $positiveProperties) {
        [void](Get-StrictInt64 $selected $property "qualification tier '$Tier'" 1 [int64]::MaxValue)
    }
    $sc017TransitionCount = Get-Sc017SourceTransitionCount
    Assert-Sc017TierConfiguration $config $sc017TransitionCount
    [void](Get-StrictInt64 $config 'seed' 'qualification config' 0 [int32]::MaxValue)
    [void](Get-StrictDouble $config 'suspensionMinimumHealthyThroughputRatio' 'qualification config' 0 1 -Positive)
    $expectedMode = if ($Tier -eq 'release') { 'full' } else { 'all' }
    $modeValue = Get-RequiredPropertyValue $selected 'performanceMode' "qualification tier '$Tier'"
    if ($modeValue -isnot [string] -or $modeValue -ne $expectedMode) {
        throw "Qualification tier '$Tier' must use performanceMode '$expectedMode'."
    }
    if ((Get-StrictInt64 $selected 'performanceDurationBoundGraceSeconds' `
        "qualification tier '$Tier'" 60 60) -ne 60) {
        throw "Qualification tier '$Tier' must use exactly 60 seconds of duration-bound watchdog grace."
    }

    if ((Get-StrictInt64 $config 'boundedOperationSlackMilliseconds' 'qualification config' 250 250) -ne 250) {
        throw 'boundedOperationSlackMilliseconds must remain the contracted 250 ms.'
    }
    $waitPolicySource = Get-Content -LiteralPath (Join-Path $root 'tests/SharedMemoryStore.IntegrationTests/LockFreeWaitPolicyMatrixIntegrationTests.cs') -Raw
    $slackPattern = 'CompletionAllowance\s*=\s*TimeSpan\.FromMilliseconds\(' +
        [regex]::Escape([string][int]$config.boundedOperationSlackMilliseconds) + '\)'
    if ($waitPolicySource -notmatch $slackPattern) {
        throw 'The bounded-operation slack setting is not enforced by LockFreeWaitPolicyMatrixIntegrationTests.'
    }

    Assert-ExactStringSet 'platforms' @($config.platforms) @('windows-x64', 'linux-x64')
    Assert-ExactStringSet 'requiredLeakAssertions' @($config.requiredLeakAssertions | ForEach-Object id) @(
        'slot-owner-count=0',
        'lease-owner-count=0',
        'unreferenced-stale-participant-count=0')
    foreach ($assertion in @($config.requiredLeakAssertions)) {
        if ([string]::IsNullOrWhiteSpace([string]$assertion.testNameContains) `
            -or [string]::IsNullOrWhiteSpace([string]$assertion.assertedState) `
            -or [string]$assertion.evidenceStep -notin @('churn', 'recovery')) {
            throw "Leak assertion '$($assertion.id)' lacks an executable test mapping."
        }
    }
    [void](Get-ChurnQualificationTestContract $config)

    Assert-ExactStringSet 'referenceModelFamilies' @($config.completionEvidence.referenceModelFamilies) @(
        'publish-publish', 'publish-reserve', 'reserve-reserve', 'commit-acquire',
        'acquire-remove', 'release-reclaim',
        'recovery-live-action', 'disposal-operation', 'participant-capacity',
        'value-capacity', 'lease-capacity', 'cancellation', 'stale-token')
    Assert-ExactStringSet 'productionHistoryFamilies' @($config.completionEvidence.productionHistoryFamilies) @(
        'publish-publish', 'publish-reserve', 'reserve-reserve', 'commit-acquire',
        'acquire-remove', 'release-reclaim', 'recovery-live-lease', 'disposal-operation')
    Assert-ExactStringSet 'productionRaceFamilies' @($config.completionEvidence.productionRaceFamilies) @(
        'publish-publish', 'publish-reserve', 'reserve-reserve', 'commit-acquire',
        'acquire-remove', 'release-reclaim', 'recovery-live-lease', 'disposal-operation')
    Assert-ExactStringSet 'performance profiles' @($config.performanceMatrix.profiles) @('Legacy', 'LockFree')
    Assert-ExactStringSet 'count-bound performance profiles' @($config.performanceMatrix.countBoundProfiles) @('LockFree')
    Assert-ExactStringSet 'lock-free-only performance scenarios' @($config.performanceMatrix.lockFreeOnlyScenarios) @('sticky-overflow-miss')
    $linuxTiny = Get-RequiredPropertyValue $config 'linuxTinyPerformance' 'qualification config'
    $expectedLinuxTinyProperties = @(
        'mode', 'profiles', 'scenarios', 'processCounts', 'syncKeysPerWorker',
        'syncMaximumWorkerCount', 'syncCanonicalBucketCount', 'syncKeyCatalogSha256',
        'syncKeyCanonicalBucketAssignments', 'minimumThroughputRatio',
        'maximumUncontendedP99Ratio', 'maximumScaleP99Ratio',
        'maximumP99Microseconds', 'maximumStallMicroseconds')
    if ((@($linuxTiny.PSObject.Properties.Name) -join ',') -cne ($expectedLinuxTinyProperties -join ',')) {
        throw "linuxTinyPerformance properties must be exactly [$($expectedLinuxTinyProperties -join ', ')]."
    }
    if ((Get-StrictString $linuxTiny 'mode' 'qualification config linuxTinyPerformance') -cne 'sync') {
        throw 'linuxTinyPerformance.mode must be sync.'
    }
    Assert-ExactStringSet 'linuxTinyPerformance profiles' @($linuxTiny.profiles) @('Legacy', 'LockFree')
    Assert-ExactStringSet 'linuxTinyPerformance scenarios' @($linuxTiny.scenarios) @('acquire-release', 'publish-remove')
    [void](Assert-LinuxTinySyncTopology $linuxTiny 'qualification config linuxTinyPerformance')
    $linuxProcessCounts = @($linuxTiny.processCounts)
    if ($linuxProcessCounts.Count -ne 2 `
        -or -not (Test-IsIntegerNumber $linuxProcessCounts[0]) -or [int64]$linuxProcessCounts[0] -ne 1 `
        -or -not (Test-IsIntegerNumber $linuxProcessCounts[1]) -or [int64]$linuxProcessCounts[1] -ne 8 `
        -or (Get-StrictDouble $linuxTiny 'minimumThroughputRatio' 'qualification config linuxTinyPerformance' 1 1) -ne 1 `
        -or (Get-StrictDouble $linuxTiny 'maximumUncontendedP99Ratio' 'qualification config linuxTinyPerformance' 1 1) -ne 1 `
        -or (Get-StrictDouble $linuxTiny 'maximumScaleP99Ratio' 'qualification config linuxTinyPerformance' 3 3) -ne 3 `
        -or (Get-StrictDouble $linuxTiny 'maximumP99Microseconds' 'qualification config linuxTinyPerformance' 10 10) -ne 10 `
        -or (Get-StrictDouble $linuxTiny 'maximumStallMicroseconds' 'qualification config linuxTinyPerformance' 10000 10000) -ne 10000) {
        throw 'linuxTinyPerformance must require process counts [1,8], LF1/Legacy1 p99<=1, LF8/Legacy8 throughput>=1, LF8/LF1 p99<=3, LF8 p99<=10us, and every raw lock-free stall<=10000us.'
    }
    if ((Get-StrictInt64 $config.tiers.release 'performanceWarmupSeconds' 'qualification config release tier' 10 10) -ne 10 `
        -or (Get-StrictInt64 $config.tiers.release 'performanceDurationSeconds' 'qualification config release tier' 60 60) -ne 60 `
        -or (Get-StrictInt64 $config.tiers.release 'performanceDurationBoundGraceSeconds' 'qualification config release tier' 60 60) -ne 60 `
        -or (Get-StrictInt64 $config.tiers.release 'performanceTrials' 'qualification config release tier' 3 3) -ne 3) {
        throw 'The Linux tiny release gate requires exactly 10s warmup, 60s measurement, 60s watchdog grace, and three trials.'
    }

    $contractCounts = [ordered]@{
        'acquire-release' = @(1, 2, 4, 8, 12)
        'publish-remove' = @(1, 2, 4, 8, 12)
        'same-key-read' = @(1, 2, 4, 6, 8, 12)
        'distributed-key-read' = @(1, 2, 4, 6, 8, 12)
        'broker-directed' = @(1, 12)
        'mixed-churn' = @(12)
        'large-ingest' = @(1, 12)
        'sticky-overflow-miss' = @(1)
    }
    foreach ($entry in $contractCounts.GetEnumerator()) {
        $source = if ($config.performanceMatrix.shortScenarios.PSObject.Properties.Name -contains $entry.Key) {
            $config.performanceMatrix.shortScenarios.$($entry.Key)
        }
        else {
            $config.performanceMatrix.releaseOnlyScenarios.$($entry.Key)
        }
        $actual = @($source | ForEach-Object {
            if (-not (Test-IsIntegerNumber $_)) {
                throw "Performance matrix '$($entry.Key)' contains a non-integer process count."
            }
            [Convert]::ToInt32($_, [Globalization.CultureInfo]::InvariantCulture)
        })
        if (($actual -join ',') -ne (@($entry.Value) -join ',')) {
            throw "Performance matrix '$($entry.Key)' does not match the contracted process counts."
        }
    }

    $configuredCheckpointIds = @($config.suspensionCheckpointIds | ForEach-Object {
        if (-not (Test-IsIntegerNumber $_)) {
            throw 'suspensionCheckpointIds contains a non-integer value.'
        }
        [Convert]::ToInt32($_, [Globalization.CultureInfo]::InvariantCulture)
    })
    if (($configuredCheckpointIds -join ',') -cne ($canonicalSuspensionCheckpointIds -join ',') `
        -or @($configuredCheckpointIds | Sort-Object -Unique).Count -ne $canonicalSuspensionCheckpointIds.Count) {
        throw 'suspensionCheckpointIds must exactly match the source-derived non-Participant/non-Disposal checkpoint catalog.'
    }
    if ((Get-StrictInt64 $selected 'recoveryCases' "qualification tier '$Tier'" 1 [int32]::MaxValue) `
        -lt $checkpointCatalog.Count) {
        throw "Qualification tier '$Tier' must execute at least one recovery case for each of the $($checkpointCatalog.Count) canonical checkpoints."
    }

    if ($Tier -eq 'release') {
        $releaseMinimums = [ordered]@{
            checkerHistoryRepetitionsPerFamily = 10000
            productionHistoryCountPerFamily = 16
            productionRaceRepetitionsPerFamily = 1000000
            churnCycles = 100000000
            recoveryCases = 10000
            performanceWarmupSeconds = 10
            performanceDurationSeconds = 60
            performanceDurationBoundGraceSeconds = 60
            performanceTrials = 3
            mixedOperations = 100000000
            largeFrames = 100000
            largeFrameBytes = 1363148
            suspensionBaselineSeconds = 10
            suspensionPauseSeconds = 30
            suspensionWarmupSeconds = 10
            directoryGenerationStressRepetitions = 1000000
        }
        foreach ($minimum in $releaseMinimums.GetEnumerator()) {
            $actualMinimum = Get-StrictInt64 $selected $minimum.Key "release qualification tier" 1 [int64]::MaxValue
            if ($actualMinimum -lt [int64]$minimum.Value) {
                throw "Release '$($minimum.Key)' must be at least $($minimum.Value)."
            }
        }
    }
}

function Get-TrxResults {
    param([Parameter(Mandatory)][string]$Directory)

    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -Filter '*.trx' -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) {
        throw "No TRX files were produced below '$Directory'."
    }
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        [xml]$document = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($node in @($document.SelectNodes("//*[local-name()='UnitTestResult']"))) {
            $rows.Add([pscustomobject]@{
                testName = [string]$node.testName
                outcome = [string]$node.outcome
                file = [IO.Path]::GetRelativePath($root, $file.FullName)
            })
        }
    }
    return @($rows)
}

function Assert-TrxStepEvidence {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Directory,
        [int]$ExpectedPassedCount = -1,
        [string[]]$RequiredTestNameContains = @())

    $rows = @(Get-TrxResults $Directory)
    $passed = @($rows | Where-Object { $_.outcome -ceq 'Passed' })
    $nonPassed = @($rows | Where-Object { $_.outcome -cne 'Passed' })
    if ($nonPassed.Count -ne 0 -or $passed.Count -eq 0) {
        $outcomes = @($rows | Group-Object outcome | Sort-Object Name | ForEach-Object {
            "$($_.Name)=$($_.Count)"
        }) -join ', '
        Fail-StepValidation $Step "TRX evidence must contain only Passed rows; passed=$($passed.Count), nonPassed=$($nonPassed.Count), outcomes=[$outcomes]."
    }
    if ($ExpectedPassedCount -ge 0 -and $passed.Count -ne $ExpectedPassedCount) {
        Fail-StepValidation $Step "TRX evidence has $($passed.Count) passed rows; expected exactly $ExpectedPassedCount."
    }
    foreach ($fragment in $RequiredTestNameContains) {
        if (@($passed | Where-Object { $_.testName -like "*$fragment*" }).Count -eq 0) {
            Fail-StepValidation $Step "TRX evidence lacks a passed test containing '$fragment'."
        }
    }
    Set-StepValidation $Step 'passed' 'trx-test-execution-proven' @(
        "passed=$($passed.Count)",
        'nonPassed=0',
        "trxFiles=$(@($rows.file | Sort-Object -Unique).Count)")
    return $passed
}

function Assert-ExactPassedTrxRows {
    param(
        [Parameter(Mandatory)][object[]]$Rows,
        [Parameter(Mandatory)][string[]]$ExpectedTestNames)

    if ($ExpectedTestNames.Count -eq 0) {
        throw 'Exact TRX verification requires at least one expected test name.'
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($testName in $ExpectedTestNames) {
        if ([string]::IsNullOrWhiteSpace($testName) -or -not $expected.Add($testName)) {
            throw 'Exact TRX verification contains an empty or duplicate expected test name.'
        }
    }

    $passed = @($Rows | Where-Object { [string]$_.outcome -ceq 'Passed' })
    $nonPassed = @($Rows | Where-Object { [string]$_.outcome -cne 'Passed' })
    if ($nonPassed.Count -ne 0) {
        $outcomes = @($Rows | Group-Object outcome | Sort-Object Name | ForEach-Object {
            "$($_.Name)=$($_.Count)"
        }) -join ', '
        throw "TRX evidence must contain only Passed rows; outcomes=[$outcomes]."
    }
    if ($passed.Count -ne $ExpectedTestNames.Count) {
        throw "TRX evidence has $($passed.Count) passed rows; expected exactly $($ExpectedTestNames.Count)."
    }

    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $passed) {
        $testName = [string]$row.testName
        if ([string]::IsNullOrWhiteSpace($testName) -or -not $actual.Add($testName)) {
            throw "TRX evidence contains an empty or duplicate passed test name '$testName'."
        }
    }
    if (-not $actual.SetEquals($expected)) {
        throw "TRX evidence test names must be exactly [$($ExpectedTestNames -join ', ')]; actual [$(@($passed.testName) -join ', ')]."
    }

    return $passed
}

function Assert-ExactTrxStepEvidence {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$ExpectedTestNames)

    $rows = @(Get-TrxResults $Directory)
    try {
        $passed = @(Assert-ExactPassedTrxRows $rows $ExpectedTestNames)
    }
    catch {
        Fail-StepValidation $Step $_.Exception.Message
    }
    Set-StepValidation $Step 'passed' 'exact-trx-test-execution-proven' @(
        "passed=$($passed.Count)",
        'nonPassed=0',
        "trxFiles=$(@($rows.file | Sort-Object -Unique).Count)",
        "exactTestNames=$($ExpectedTestNames -join ',')")
    return $passed
}

function Invoke-ChurnQualificationVerifierSelfTest {
    $contract = Get-ChurnQualificationTestContract $config
    $validRow = [pscustomobject]@{
        testName = $contract.fullyQualifiedName
        outcome = 'Passed'
        file = 'synthetic.trx'
    }
    [void](Assert-ExactPassedTrxRows @($validRow) @($contract.fullyQualifiedName))
    $assertions = 1

    $differentMappings = $config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $leaseMappings = @($differentMappings.requiredLeakAssertions | Where-Object {
        [string]$_.id -ceq 'lease-owner-count=0' -and [string]$_.evidenceStep -ceq 'churn'
    })
    if ($leaseMappings.Count -ne 1) {
        throw 'Churn qualification verifier self-test could not locate the configured lease-owner mapping.'
    }
    $leaseMappings[0].testNameContains =
        'LockFreeChurnIntegrationTests.BenchmarkFixedKeysSurviveRepeatedEightProcessPublishRemoveChurn'
    $mappingRejected = $false
    try {
        [void](Get-ChurnQualificationTestContract $differentMappings)
    }
    catch {
        $mappingRejected = $_.Exception.Message -like '*must map to one identical test method*'
    }
    if (-not $mappingRejected) {
        throw 'Churn qualification verifier self-test accepted two distinct churn leak-evidence mappings.'
    }
    $assertions++

    $missingMapping = $config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $missingMapping.requiredLeakAssertions = @(
        $missingMapping.requiredLeakAssertions | Where-Object {
            [string]$_.id -cne 'lease-owner-count=0'
        })
    $missingMappingRejected = $false
    try {
        [void](Get-ChurnQualificationTestContract $missingMapping)
    }
    catch {
        $missingMappingRejected = $_.Exception.Message -like '*must contain exactly three required leak assertions*'
    }
    if (-not $missingMappingRejected) {
        throw 'Churn qualification verifier self-test accepted a missing churn leak-evidence mapping.'
    }
    $assertions++

    $swappedEvidenceSteps = $config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $leaseMapping = @($swappedEvidenceSteps.requiredLeakAssertions | Where-Object {
        [string]$_.id -ceq 'lease-owner-count=0'
    })
    $participantMapping = @($swappedEvidenceSteps.requiredLeakAssertions | Where-Object {
        [string]$_.id -ceq 'unreferenced-stale-participant-count=0'
    })
    if ($leaseMapping.Count -ne 1 -or $participantMapping.Count -ne 1) {
        throw 'Churn qualification verifier self-test could not locate exact role-swap mappings.'
    }
    $leaseMapping[0].evidenceStep = 'recovery'
    $participantMapping[0].evidenceStep = 'churn'
    $roleSwapRejected = $false
    try {
        [void](Get-ChurnQualificationTestContract $swappedEvidenceSteps)
    }
    catch {
        $roleSwapRejected = $_.Exception.Message -like '*must map exactly once to evidence step*'
    }
    if (-not $roleSwapRejected) {
        throw 'Churn qualification verifier self-test accepted swapped leak-evidence roles.'
    }
    $assertions++

    $wrongExistingTarget = $config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    foreach ($assertion in @($wrongExistingTarget.requiredLeakAssertions | Where-Object {
        [string]$_.evidenceStep -ceq 'churn'
    })) {
        $assertion.testNameContains =
            'LockFreeChurnIntegrationTests.BenchmarkFixedKeysSurviveRepeatedEightProcessPublishRemoveChurn'
    }
    $semanticTargetRejected = $false
    try {
        [void](Get-ChurnQualificationTestContract $wrongExistingTarget)
    }
    catch {
        $semanticTargetRejected = $_.Exception.Message -like '*must remain the SC-016 collision workload*'
    }
    if (-not $semanticTargetRejected) {
        throw 'Churn qualification verifier self-test accepted the fixed-key sibling as configured SC-016 evidence.'
    }
    $assertions++

    $source = Get-Content -LiteralPath (Join-Path $root $churnTestSourceRelativePath) -Raw
    $sourceWithoutDirectMethod = $source.Replace(
        $churnQualificationTestMethod,
        'MissingConfiguredChurnEvidenceMethod',
        [StringComparison]::Ordinal)
    $outerClassClose = $sourceWithoutDirectMethod.LastIndexOf('}')
    if ($outerClassClose -lt 0) {
        throw 'Churn qualification verifier self-test source has no outer class terminator.'
    }
    $nestedMethodSource = $sourceWithoutDirectMethod.Insert(
        $outerClassClose,
        "    private sealed class NestedChurnEvidence`r`n    {`r`n" +
            "        [Fact]`r`n        public void $churnQualificationTestMethod()`r`n" +
            "        {`r`n        }`r`n    }`r`n")
    $sourceNegativeCases = @(
        [pscustomobject]@{
            name = 'missing-method'
            source = $source.Replace(
                $churnQualificationTestMethod,
                'MissingConfiguredChurnEvidenceMethod',
                [StringComparison]::Ordinal)
        },
        [pscustomobject]@{
            name = 'wrong-namespace'
            source = $source.Replace(
                "namespace $churnTestNamespace;",
                "namespace $churnTestNamespace.Wrong;",
                [StringComparison]::Ordinal)
        },
        [pscustomobject]@{
            name = 'wrong-class'
            source = $source.Replace(
                "public sealed class $churnTestClass",
                "public sealed class WrongChurnIntegrationTests",
                [StringComparison]::Ordinal)
        },
        [pscustomobject]@{
            name = 'method-outside-configured-class'
            source = $source.Replace(
                    $churnQualificationTestMethod,
                    'MissingConfiguredChurnEvidenceMethod',
                    [StringComparison]::Ordinal) +
                "`r`n`r`npublic sealed class WrongChurnEvidenceContainer`r`n{`r`n" +
                "    [Fact]`r`n    public void $churnQualificationTestMethod()`r`n    {`r`n    }`r`n}`r`n"
        },
        [pscustomobject]@{
            name = 'configured-class-nested-under-another-type'
            source = $source.Replace(
                    "public sealed class $churnTestClass",
                    "public sealed class OuterChurnContainer`r`n{`r`n    public sealed class $churnTestClass",
                    [StringComparison]::Ordinal) +
                "`r`n}`r`n"
        },
        [pscustomobject]@{
            name = 'configured-method-nested-under-another-type'
            source = $nestedMethodSource
        })
    foreach ($case in $sourceNegativeCases) {
        $sourceRejected = $false
        try {
            [void](Get-ChurnQualificationTestContract $config -SourceOverride $case.source)
        }
        catch {
            $sourceRejected = $_.Exception.Message -match 'must identify the exact namespace, class, and one \[Fact\]'
        }
        if (-not $sourceRejected) {
            throw "Churn qualification verifier self-test accepted invalid source case '$($case.name)'."
        }
        $assertions++
    }

    $siblingRow = [pscustomobject]@{
        testName = "$churnTestNamespace.LockFreeChurnIntegrationTests.BenchmarkFixedKeysSurviveRepeatedEightProcessPublishRemoveChurn"
        outcome = 'Passed'
        file = 'synthetic.trx'
    }
    $wrongRow = [pscustomobject]@{
        testName = "$churnTestNamespace.LockFreeChurnIntegrationTests.WrongChurnEvidence"
        outcome = 'Passed'
        file = 'synthetic.trx'
    }
    $nonPassedRow = [pscustomobject]@{
        testName = $contract.fullyQualifiedName
        outcome = 'Failed'
        file = 'synthetic.trx'
    }
    $negativeCases = @(
        [pscustomobject]@{ name = 'missing'; rows = @() },
        [pscustomobject]@{ name = 'extra-sibling'; rows = @($validRow, $siblingRow) },
        [pscustomobject]@{ name = 'wrong-only'; rows = @($wrongRow) },
        [pscustomobject]@{ name = 'duplicate'; rows = @($validRow, $validRow) },
        [pscustomobject]@{ name = 'non-passed'; rows = @($nonPassedRow) })
    foreach ($case in $negativeCases) {
        $rejected = $false
        try {
            [void](Assert-ExactPassedTrxRows @($case.rows) @($contract.fullyQualifiedName))
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Churn exact-TRX verifier self-test accepted invalid case '$($case.name)'."
        }
        $assertions++
    }

    $trxSelfTestRoot = Join-Path $runRoot 'churn-trx-verifier-self-test'
    New-Item -ItemType Directory -Path $trxSelfTestRoot | Out-Null
    $validTrxPath = Join-Path $trxSelfTestRoot 'valid.trx'
    $extraTrxPath = Join-Path $trxSelfTestRoot 'extra.trx'
    [IO.File]::WriteAllText(
        $validTrxPath,
        ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="{0}" outcome="Passed" /></Results></TestRun>' `
            -f $contract.fullyQualifiedName))
    $parsedRows = @(Get-TrxResults $trxSelfTestRoot)
    [void](Assert-ExactPassedTrxRows $parsedRows @($contract.fullyQualifiedName))
    $assertions++

    [IO.File]::WriteAllText(
        $extraTrxPath,
        ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="{0}" outcome="Passed" /></Results></TestRun>' `
            -f $siblingRow.testName))
    $temporaryStepName = 'self-test-churn-exact-trx-wrapper-negative'
    Add-EvidenceResult $temporaryStepName 'passed' 'self-test-setup' @('pending negative assertion')
    $temporaryStep = Get-StepResult $temporaryStepName
    $wrapperRejected = $false
    try {
        [void](Assert-ExactTrxStepEvidence `
            $temporaryStepName $trxSelfTestRoot @($contract.fullyQualifiedName))
    }
    catch {
        $wrapperRejected = $_.Exception.Message -like '*TRX evidence has 2 passed rows; expected exactly 1*' `
            -and $temporaryStep.status -ceq 'failed' `
            -and $temporaryStep.qualification -ceq 'validation-failed'
    }
    finally {
        [void]$results.Remove($temporaryStep)
    }
    if (-not $wrapperRejected) {
        throw 'Churn exact-TRX verifier self-test did not reject and record an XML-parsed extra sibling row.'
    }
    $assertions++

    return $assertions
}

function Get-FamilyCompletionSeed {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Family)

    if ($Step -eq 'reference-model-histories') {
        return [int64](Get-StrictInt64 $config 'seed' 'qualification config' 0 [int32]::MaxValue)
    }

    $ordinal = switch ($Family) {
        'publish-publish' { 1 }
        'publish-reserve' { 2 }
        'reserve-reserve' { 3 }
        'commit-acquire' { 4 }
        'acquire-remove' { 5 }
        'release-reclaim' { 6 }
        'recovery-live-lease' { 7 }
        'disposal-operation' { 8 }
        default { throw "No derived-seed ordinal is defined for '$Family'." }
    }
    $rootSeed = Get-StrictInt64 $config 'seed' 'qualification config' 0 [int32]::MaxValue
    $unsigned = (([uint64][uint32][int32]$rootSeed) +
        ([uint64]$ordinal * [uint64]2654435769)) -band [uint64]4294967295
    if ($unsigned -ge [uint64]2147483648) {
        return [int64]$unsigned - [int64]4294967296
    }

    return [int64]$unsigned
}

function Get-UniqueFamilyMarkerLine {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$Family)

    $pattern = '^[\t ]*family=' + [regex]::Escape($Family) + '(?:[\t ]|$)'
    $lines = @($Text -split "\r?\n" | Where-Object { $_ -match $pattern })
    if ($lines.Count -ne 1) {
        throw "Expected exactly one marker line for family '$Family'; found $($lines.Count)."
    }

    return [string]$lines[0].Trim()
}

function Assert-ExactFamilyMarkerSet {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string[]]$Families)

    $expected = [Collections.Generic.HashSet[string]]::new($Families, [StringComparer]::Ordinal)
    $markerLines = @($Text -split "\r?\n" | Where-Object { $_ -match '^[\t ]*family=(?<family>[a-z0-9-]+)(?:[\t ]|$)' })
    if ($markerLines.Count -ne $Families.Count) {
        throw "Expected exactly $($Families.Count) family marker lines; found $($markerLines.Count)."
    }

    foreach ($line in $markerLines) {
        $match = [regex]::Match($line, '^[\t ]*family=(?<family>[a-z0-9-]+)(?:[\t ]|$)')
        if (-not $match.Success -or -not $expected.Contains($match.Groups['family'].Value)) {
            throw "Unexpected family marker line: '$($line.Trim())'."
        }
    }
}

function Assert-FamilyCompletionMarkerText {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$Field,
        [Parameter(Mandatory)][int64]$ExpectedCount,
        [Parameter(Mandatory)][string[]]$Families)

    Assert-ExactFamilyMarkerSet $Text $Families
    foreach ($family in $Families) {
        $expectedSeed = Get-FamilyCompletionSeed $Step $family
        $line = Get-UniqueFamilyMarkerLine $Text $family
        $pattern = '^family=' + [regex]::Escape($family) +
            '\s+seed=' + [regex]::Escape([string]$expectedSeed) + '\s+' +
            [regex]::Escape($Field) + '=' + [regex]::Escape([string]$ExpectedCount) +
            '(?:\s+\S.*)?$'
        if ($line -notmatch $pattern) {
            throw "Family '$family' does not have the exact seed=$expectedSeed $Field=$ExpectedCount marker prefix."
        }
    }
}

function ConvertTo-MarkerInt64 {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Context)

    [int64]$parsed = 0
    if (-not [int64]::TryParse(
            $Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "Marker field '$Context' is not a nonnegative Int64: '$Value'."
    }

    return $parsed
}

function Assert-ProductionRaceMarkerText {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][int64]$ExpectedCount,
        [Parameter(Mandatory)][string[]]$Families)

    Assert-ExactFamilyMarkerSet $Text $Families
    $recoverySuccesses = $null
    $recoveryBusy = $null
    $liveActiveWitnesses = $null
    $operationWins = $null
    $disposalWins = $null

    foreach ($family in $Families) {
        $expectedSeed = Get-FamilyCompletionSeed 'production-race-stress' $family
        $line = Get-UniqueFamilyMarkerLine $Text $family
        $common = '^family=' + [regex]::Escape($family) +
            '\s+seed=' + [regex]::Escape([string]$expectedSeed) +
            '\s+completed=' + [regex]::Escape([string]$ExpectedCount) +
            '\s+productionOperationRaces=' + [regex]::Escape([string]$ExpectedCount)

        if ($family -eq 'recovery-live-lease') {
            $pattern = $common +
                '\s+control=persistent-two-phase-barrier' +
                '\s+recoverySuccesses=(?<successes>[0-9]+)' +
                '\s+recoveryBusy=(?<busy>[0-9]+)' +
                '\s+liveActiveWitnesses=(?<witnesses>[0-9]+)$'
            $match = [regex]::Match($line, $pattern)
            if (-not $match.Success) {
                throw "Family '$family' lacks its exact production race and recovery witness marker."
            }

            $recoverySuccesses = ConvertTo-MarkerInt64 $match.Groups['successes'].Value "$family.recoverySuccesses"
            $recoveryBusy = ConvertTo-MarkerInt64 $match.Groups['busy'].Value "$family.recoveryBusy"
            $liveActiveWitnesses = ConvertTo-MarkerInt64 $match.Groups['witnesses'].Value "$family.liveActiveWitnesses"
            if ($recoverySuccesses -le 0 `
                -or $recoveryBusy -gt $ExpectedCount `
                -or $recoverySuccesses -ne ($ExpectedCount - $recoveryBusy) `
                -or $liveActiveWitnesses -ne $recoverySuccesses) {
                throw "Family '$family' has invalid recovery witnesses: successes=$recoverySuccesses, busy=$recoveryBusy, liveActiveWitnesses=$liveActiveWitnesses, expected=$ExpectedCount."
            }
        }
        elseif ($family -eq 'disposal-operation') {
            $pattern = $common +
                '\s+disposeCalls=' + [regex]::Escape([string]$ExpectedCount) +
                '\s+freshHandles=' + [regex]::Escape([string]$ExpectedCount) +
                '\s+operationWins=(?<operationWins>[0-9]+)' +
                '\s+disposalWins=(?<disposalWins>[0-9]+)' +
                '\s+control=persistent-two-phase-barrier$'
            $match = [regex]::Match($line, $pattern)
            if (-not $match.Success) {
                throw "Family '$family' lacks its exact per-repetition disposal race marker."
            }

            $operationWins = ConvertTo-MarkerInt64 $match.Groups['operationWins'].Value "$family.operationWins"
            $disposalWins = ConvertTo-MarkerInt64 $match.Groups['disposalWins'].Value "$family.disposalWins"
            if ($operationWins -le 0 `
                -or $disposalWins -le 0 `
                -or $disposalWins -gt $ExpectedCount `
                -or $operationWins -ne ($ExpectedCount - $disposalWins)) {
                throw "Family '$family' has invalid disposal order witnesses: operationWins=$operationWins, disposalWins=$disposalWins, expected=$ExpectedCount."
            }
        }
        else {
            $pattern = $common + '\s+control=persistent-two-phase-barrier$'
            if ($line -notmatch $pattern) {
                throw "Family '$family' lacks its exact productionOperationRaces=$ExpectedCount marker."
            }
        }
    }

    return [pscustomobject][ordered]@{
        recoverySuccesses = $recoverySuccesses
        recoveryBusy = $recoveryBusy
        liveActiveWitnesses = $liveActiveWitnesses
        operationWins = $operationWins
        disposalWins = $disposalWins
    }
}

function Invoke-ProductionRaceMarkerParserSelfTest {
    $families = @($config.completionEvidence.productionRaceFamilies)
    [int64]$count = 17
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($family in $families) {
        $seed = Get-FamilyCompletionSeed 'production-race-stress' $family
        $common = "family=$family seed=$seed completed=$count productionOperationRaces=$count"
        if ($family -eq 'recovery-live-lease') {
            $lines.Add("$common control=persistent-two-phase-barrier recoverySuccesses=9 recoveryBusy=8 liveActiveWitnesses=9")
        }
        elseif ($family -eq 'disposal-operation') {
            $lines.Add("$common disposeCalls=$count freshHandles=$count operationWins=7 disposalWins=10 control=persistent-two-phase-barrier")
        }
        else {
            $lines.Add("$common control=persistent-two-phase-barrier")
        }
    }

    $validText = $lines -join "`n"
    $valid = Assert-ProductionRaceMarkerText $validText $count $families
    if ($valid.recoverySuccesses -ne 9 `
        -or $valid.liveActiveWitnesses -ne 9 `
        -or $valid.operationWins -ne 7 `
        -or $valid.disposalWins -ne 10) {
        throw 'Production race marker parser self-test did not preserve the validated witness counts.'
    }

    $invalidCases = [Collections.Generic.List[object]]::new()
    $invalidCases.Add([pscustomobject]@{
        name = 'duplicate-family'
        text = $validText + "`n" + $lines[0]
    })

    $wrongRaceCount = @($lines | ForEach-Object { $_ })
    $wrongRaceCount[0] = $wrongRaceCount[0].Replace('productionOperationRaces=17', 'productionOperationRaces=16')
    $invalidCases.Add([pscustomobject]@{ name = 'wrong-production-count'; text = $wrongRaceCount -join "`n" })

    $wrongRecoveryWitness = @($lines | ForEach-Object { $_ })
    $recoveryIndex = [Array]::IndexOf($families, 'recovery-live-lease')
    $wrongRecoveryWitness[$recoveryIndex] = $wrongRecoveryWitness[$recoveryIndex].Replace(
        'liveActiveWitnesses=9',
        'liveActiveWitnesses=8')
    $invalidCases.Add([pscustomobject]@{ name = 'wrong-recovery-witness'; text = $wrongRecoveryWitness -join "`n" })

    $wrongRecoveryTotal = @($lines | ForEach-Object { $_ })
    $wrongRecoveryTotal[$recoveryIndex] = $wrongRecoveryTotal[$recoveryIndex].Replace('recoveryBusy=8', 'recoveryBusy=7')
    $invalidCases.Add([pscustomobject]@{ name = 'wrong-recovery-total'; text = $wrongRecoveryTotal -join "`n" })

    $wrongDisposeCalls = @($lines | ForEach-Object { $_ })
    $disposalIndex = [Array]::IndexOf($families, 'disposal-operation')
    $wrongDisposeCalls[$disposalIndex] = $wrongDisposeCalls[$disposalIndex].Replace('disposeCalls=17', 'disposeCalls=16')
    $invalidCases.Add([pscustomobject]@{ name = 'wrong-dispose-calls'; text = $wrongDisposeCalls -join "`n" })

    $missingDisposalOrdering = @($lines | ForEach-Object { $_ })
    $missingDisposalOrdering[$disposalIndex] = $missingDisposalOrdering[$disposalIndex].Replace(
        'operationWins=7 disposalWins=10',
        'operationWins=0 disposalWins=17')
    $invalidCases.Add([pscustomobject]@{ name = 'missing-disposal-ordering'; text = $missingDisposalOrdering -join "`n" })

    $wrongSeed = @($lines | ForEach-Object { $_ })
    $wrongSeed[0] = $wrongSeed[0] -replace 'seed=-?[0-9]+', 'seed=0'
    $invalidCases.Add([pscustomobject]@{ name = 'wrong-derived-seed'; text = $wrongSeed -join "`n" })

    foreach ($case in $invalidCases) {
        $rejected = $false
        try {
            $null = Assert-ProductionRaceMarkerText $case.text $count $families
        }
        catch {
            $rejected = $true
        }

        if (-not $rejected) {
            throw "Production race marker parser self-test accepted invalid case '$($case.name)'."
        }
    }

    return 1 + $invalidCases.Count
}

function Assert-FamilyCompletionMarkers {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Field,
        [Parameter(Mandatory)][int64]$ExpectedCount,
        [Parameter(Mandatory)][string[]]$Families,
        [Parameter(Mandatory)][string]$Qualification)

    $stepResult = Get-StepResult $Step
    $stdout = Get-Content -LiteralPath (Join-Path $root $stepResult.stdout) -Raw
    try {
        Assert-FamilyCompletionMarkerText $Step $stdout $Field $ExpectedCount $Families
    }
    catch {
        Fail-StepValidation $Step $_.Exception.Message
    }
    Set-StepValidation $Step 'passed' $Qualification @(
        "families=$($Families.Count)",
        "$Field=$ExpectedCount",
        "total=$([int64]$Families.Count * $ExpectedCount)",
        "rootSeed=$([int]$config.seed)")
}

function Assert-ProductionRaceEvidence {
    param(
        [Parameter(Mandatory)][int64]$ExpectedCount,
        [Parameter(Mandatory)][string[]]$Families)

    $step = 'production-race-stress'
    $stepResult = Get-StepResult $step
    $stdout = Get-Content -LiteralPath (Join-Path $root $stepResult.stdout) -Raw
    try {
        $witnesses = Assert-ProductionRaceMarkerText $stdout $ExpectedCount $Families
    }
    catch {
        Fail-StepValidation $step $_.Exception.Message
    }

    Set-StepValidation $step 'passed' 'sc011-production-race-count-and-witnesses-proven' @(
        "families=$($Families.Count)",
        "completedPerFamily=$ExpectedCount",
        "productionOperationRacesPerFamily=$ExpectedCount",
        "total=$([int64]$Families.Count * $ExpectedCount)",
        'familyMarkers=exactly-one-per-configured-family',
        "recoverySuccesses=$($witnesses.recoverySuccesses)",
        "recoveryBusy=$($witnesses.recoveryBusy)",
        "liveActiveWitnesses=$($witnesses.liveActiveWitnesses)",
        "disposalOperationWins=$($witnesses.operationWins)",
        "disposalWins=$($witnesses.disposalWins)",
        "rootSeed=$([int]$config.seed)")
}

function Assert-FullSuiteEvidence {
    param([Parameter(Mandatory)][string]$TrxDirectory)

    Assert-TrxStepEvidence 'full-test-suite' $TrxDirectory | Out-Null
    $result = Get-StepResult 'full-test-suite'
    $result.qualification = 'full-solution-trx-passed'
}

function Assert-OwnerLeakEvidence {
    param([Parameter(Mandatory)][hashtable]$TrxDirectories)

    $evidence = [Collections.Generic.List[string]]::new()
    foreach ($assertion in @($config.requiredLeakAssertions)) {
        $step = [string]$assertion.evidenceStep
        if (-not $TrxDirectories.ContainsKey($step)) {
            throw "Leak assertion '$($assertion.id)' names unavailable evidence step '$step'."
        }
        $passed = @(Get-TrxResults $TrxDirectories[$step] | Where-Object outcome -eq 'Passed')
        $matching = @($passed | Where-Object { $_.testName -like "*$($assertion.testNameContains)*" })
        if ($matching.Count -eq 0) {
            throw "Leak assertion '$($assertion.id)' has no passing executable test in '$step'."
        }
        $evidence.Add("$($assertion.id); step=$step; passedRows=$($matching.Count); test=$($assertion.testNameContains); state=$($assertion.assertedState)")
    }
    Add-EvidenceResult 'owner-leak-assertions' 'passed' 'configured-owner-leak-claims-executed' @($evidence) @(
        $TrxDirectories.Values | ForEach-Object { [IO.Path]::GetRelativePath($root, [string]$_) })
}

function Assert-RecoveryCheckpointEvidence {
    param(
        [Parameter(Mandatory)][object[]]$PassedRows,
        [Parameter(Mandatory)][int64]$ExpectedCases)

    $testPrefix = 'SharedMemoryStore.IntegrationTests.LockFreeCrashRecoveryIntegrationTests.' +
        'EveryCanonicalCheckpointCanBeKilledRecoveredAndFilledToCapacity'
    $parsed = [Collections.Generic.List[object]]::new()
    foreach ($row in $PassedRows) {
        $match = [regex]::Match(
            [string]$row.testName,
            '^' + [regex]::Escape($testPrefix) +
                '\(caseIndex:\s*(?<case>[0-9]+),\s*checkpointValue:\s*(?<checkpoint>[0-9]+)\)$')
        if (-not $match.Success) {
            Fail-StepValidation 'recovery' "Recovery TRX row has an unparseable identity: '$($row.testName)'."
        }
        $caseIndex = [int64]::Parse($match.Groups['case'].Value, [Globalization.CultureInfo]::InvariantCulture)
        $checkpointId = [int]::Parse($match.Groups['checkpoint'].Value, [Globalization.CultureInfo]::InvariantCulture)
        if ($caseIndex -lt 0 -or $caseIndex -ge $ExpectedCases) {
            Fail-StepValidation 'recovery' "Recovery TRX case index $caseIndex is outside [0,$($ExpectedCases - 1)]."
        }
        $expectedCheckpointId = [int]$checkpointCatalog[[int]($caseIndex % $checkpointCatalog.Count)].id
        if ($checkpointId -ne $expectedCheckpointId) {
            Fail-StepValidation 'recovery' "Recovery case $caseIndex executed checkpoint $checkpointId; expected canonical checkpoint $expectedCheckpointId."
        }
        $parsed.Add([pscustomobject]@{ caseIndex = $caseIndex; checkpointId = $checkpointId })
    }

    $caseIndexes = @($parsed | ForEach-Object caseIndex | Sort-Object)
    if ($parsed.Count -ne $ExpectedCases `
        -or @($caseIndexes | Sort-Object -Unique).Count -ne $ExpectedCases) {
        Fail-StepValidation 'recovery' 'Recovery TRX evidence omitted or duplicated a configured case index.'
    }
    for ($index = 0; $index -lt $caseIndexes.Count; $index++) {
        if ([int64]$caseIndexes[$index] -ne [int64]$index) {
            Fail-StepValidation 'recovery' "Recovery TRX case-index set is not contiguous at index $index."
        }
    }
    $actualCheckpointSet = @($parsed | ForEach-Object checkpointId | Sort-Object -Unique)
    $expectedCheckpointSet = @($checkpointCatalog | ForEach-Object { [int]$_.id })
    if (($actualCheckpointSet -join ',') -cne ($expectedCheckpointSet -join ',')) {
        Fail-StepValidation 'recovery' 'Recovery TRX evidence does not cover the exact source-derived checkpoint catalog.'
    }

    $checkpointSetDigest = Get-StringSha256 (
        ($expectedCheckpointSet | ForEach-Object { '{0:D3}' -f $_ }) -join "`n")
    $result = Get-StepResult 'recovery'
    $result.validation = @($result.validation) + @(
        "configuredCases=$ExpectedCases",
        "catalogCheckpointCount=$($checkpointCatalog.Count)",
        "checkpointSetDigest=$checkpointSetDigest")
}

function Get-ExpectedReleaseOsRows {
    param([Parameter(Mandatory)][string]$PlatformId)

    $allNames = @(
        'self-test-architecture', 'self-test-atomic', 'self-test-raw',
        'self-test-no-lock', 'self-test-crash', 'self-test-release-tests',
        'self-test-interop', 'self-test-samples', 'self-test-pack',
        'dotnet-info', 'clean', 'restore', 'build',
        'architecture', 'atomic', 'raw', 'no-lock-held', 'no-lock-linux-strace',
        'linux-tiny-performance',
        'crash-checkpoint-kill', 'crash-linux-sigstop', 'crash-linux-docker-pause',
        'release-tests', 'native', 'python', 'docker', 'sample-6', 'sample-12', 'pack')
    $requirements = [ordered]@{}
    foreach ($name in $allNames) {
        $requirements[$name] = $true
    }
    $requirements['crash-linux-docker-pause'] = $false
    if ($PlatformId -eq 'windows-x64') {
        $requirements['no-lock-linux-strace'] = $false
        $requirements['crash-linux-sigstop'] = $false
        $requirements['linux-tiny-performance'] = $false
    }
    elseif ($PlatformId -ne 'linux-x64') {
        throw "Release OS row contract does not support '$PlatformId'."
    }
    return $requirements
}

function Get-OsAssemblyHash {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$EvidencePath)

    $assemblyMatches = @($Report.testedAssemblies | Where-Object {
        ([string]$_.path).Replace('\', '/') -ceq $RelativePath.Replace('\', '/')
    })
    if ($assemblyMatches.Count -ne 1 -or [string]$assemblyMatches[0].sha256 -notmatch '^[0-9A-F]{64}$') {
        throw "OS evidence '$EvidencePath' does not uniquely bind tested assembly '$RelativePath'."
    }
    return [string]($assemblyMatches[0].sha256)
}

function Assert-OsDerivedDouble {
    param(
        [Parameter(Mandatory)][double]$Actual,
        [Parameter(Mandatory)][double]$Expected,
        [Parameter(Mandatory)][string]$Context)

    $tolerance = [Math]::Max(0.000000001, [Math]::Abs($Expected) * 0.000000000001)
    if (-not [double]::IsFinite($Actual) -or -not [double]::IsFinite($Expected) `
        -or [Math]::Abs($Actual - $Expected) -gt $tolerance) {
        throw "$Context is not reproducible from the raw OS performance evidence."
    }
}

function Assert-LinuxTinyOsPerformanceEvidence {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string]$EvidencePath)

    $row = @($Report.results | Where-Object { [string]$_.name -ceq 'linux-tiny-performance' })
    if ($row.Count -ne 1 -or -not (Get-StrictBoolean $row[0] 'required' 'Linux tiny performance OS row') `
        -or [string]$row[0].status -cne 'pass') {
        throw "Linux OS evidence '$EvidencePath' lacks its required passing linux-tiny-performance row."
    }
    $expectedCommandTokens = @(
        'SharedMemoryStore.SyncProbe.csproj', '--mode sync', '--profile both',
        '--scenario acquire-release,publish-remove', '--process-counts 1,8',
        '--warmup 10', '--duration 60', '--trials 3')
    foreach ($token in $expectedCommandTokens) {
        if ([string]$row[0].command -cnotlike "*$token*") {
            throw "Linux tiny performance command is missing exact token '$token'."
        }
    }

    $performance = Get-RequiredPropertyValue $row[0] 'performanceEvidence' 'Linux tiny performance OS row'
    if ((Get-StrictInt64 $performance 'schemaVersion' 'Linux tiny performance row evidence' 1 1) -ne 1) {
        throw 'Linux tiny performance row evidence schema must be 1.'
    }
    $expectedRawPath = [IO.Path]::GetFullPath((Join-Path `
        (Join-Path (Split-Path -Parent $EvidencePath) ([IO.Path]::GetFileNameWithoutExtension($EvidencePath) + '.evidence')) `
        'linux-tiny-performance.json'))
    $rawPathValue = Get-StrictString $performance 'reportPath' 'Linux tiny performance row evidence'
    $actualRawPath = if ([IO.Path]::IsPathFullyQualified($rawPathValue)) {
        [IO.Path]::GetFullPath($rawPathValue)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $root $rawPathValue))
    }
    $pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $actualRawPath.Equals($expectedRawPath, $pathComparison) `
        -or -not (Test-Path -LiteralPath $actualRawPath -PathType Leaf) `
        -or (Get-StrictString $performance 'reportSha256' 'Linux tiny performance row evidence') -cne
            (Get-FileSha256 $actualRawPath)) {
        throw "Linux tiny performance row does not bind the exact sibling raw report '$expectedRawPath'."
    }
    $manifestMatches = @($Report.evidenceManifest | Where-Object {
        $manifestPath = if ([IO.Path]::IsPathFullyQualified([string]$_.path)) {
            [IO.Path]::GetFullPath([string]$_.path)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $root ([string]$_.path)))
        }
        $manifestPath.Equals($actualRawPath, $pathComparison) `
            -and [string]$_.sha256 -ceq (Get-FileSha256 $actualRawPath)
    })
    if ($manifestMatches.Count -ne 1) {
        throw 'Linux tiny performance raw JSON is not uniquely bound by the OS evidence manifest.'
    }

    $raw = Get-Content -LiteralPath $actualRawPath -Raw | ConvertFrom-Json -Depth 30
    if ((Get-StrictInt64 $raw 'schemaVersion' 'Linux tiny performance raw report' 8 8) -ne 8 `
        -or (Get-StrictInt64 $raw 'minimumCompatibleSchemaVersion' 'Linux tiny performance raw report' 8 8) -ne 8) {
        throw 'Linux tiny performance raw report must be exact schema 8/minimum-compatible 8.'
    }
    [void](Get-StrictString $raw 'schemaCompatibility' 'Linux tiny performance raw report')
    $environment = Get-RequiredPropertyValue $raw 'environment' 'Linux tiny performance raw report'
    $osHost = Get-RequiredPropertyValue $Report 'host' 'Linux OS evidence report'
    if ((Get-StrictString $environment 'repositoryCommit' 'Linux tiny performance environment') -cne
            [string]$Report.provenance.repositoryCommit `
        -or (Get-StrictString $environment 'repositoryWorkingTreeState' 'Linux tiny performance environment') -cne 'clean' `
        -or (Get-StrictString $Report 'platform' 'Linux OS evidence report') -cne 'linux' `
        -or (Get-StrictString $Report 'architecture' 'Linux OS evidence report') -cne 'x64' `
        -or (Get-StrictString $environment 'operatingSystem' 'Linux tiny performance environment') -cne
            (Get-StrictString $osHost 'operatingSystem' 'Linux OS host evidence') `
        -or (Get-StrictString $environment 'operatingSystemArchitecture' 'Linux tiny performance environment') -cne
            (Get-StrictString $osHost 'operatingSystemArchitecture' 'Linux OS host evidence') `
        -or (Get-StrictString $environment 'operatingSystemArchitecture' 'Linux tiny performance environment') -cne 'X64' `
        -or (Get-StrictString $environment 'processArchitecture' 'Linux tiny performance environment') -cne
            (Get-StrictString $osHost 'processArchitecture' 'Linux OS host evidence') `
        -or (Get-StrictString $environment 'processArchitecture' 'Linux tiny performance environment') -cne 'X64' `
        -or (Get-StrictInt64 $environment 'logicalProcessorCount' 'Linux tiny performance environment' 8 [int32]::MaxValue) -ne
            (Get-StrictInt64 $osHost 'logicalProcessorCount' 'Linux OS host evidence' 8 [int32]::MaxValue)) {
        throw 'Linux tiny performance raw environment does not match its clean Linux OS evidence report.'
    }
    foreach ($property in @('framework', 'runtimeVersion', 'processorIdentifier')) {
        [void](Get-StrictString $environment $property 'Linux tiny performance environment')
    }
    Assert-RequiredBenchmarkHardwareMetadata $environment 'Linux tiny performance environment'
    [void](Get-StrictBoolean $environment 'serverGarbageCollection' 'Linux tiny performance environment')
    [void](Get-StrictInt64 $environment 'stopwatchFrequency' 'Linux tiny performance environment' 1 [int64]::MaxValue)
    $probeAssembly = "benchmarks/SharedMemoryStore.SyncProbe/bin/$($Report.configuration)/net10.0/SharedMemoryStore.SyncProbe.dll"
    $storeAssembly = "benchmarks/SharedMemoryStore.SyncProbe/bin/$($Report.configuration)/net10.0/SharedMemoryStore.dll"
    $actualProbeHash = Get-StrictString $environment 'probeAssemblySha256' 'Linux tiny performance environment'
    $actualStoreHash = Get-StrictString $environment 'sharedMemoryStoreAssemblySha256' 'Linux tiny performance environment'
    $expectedProbeHash = $(Get-OsAssemblyHash -Report $Report -RelativePath $probeAssembly -EvidencePath $EvidencePath)
    $expectedStoreHash = $(Get-OsAssemblyHash -Report $Report -RelativePath $storeAssembly -EvidencePath $EvidencePath)
    if ($actualProbeHash -cne $expectedProbeHash -or $actualStoreHash -cne $expectedStoreHash) {
        throw "Linux tiny performance raw assembly hashes do not match the OS tested-assembly manifest (probe=$actualProbeHash/$expectedProbeHash; store=$actualStoreHash/$expectedStoreHash)."
    }

    $configuration = Get-RequiredPropertyValue $raw 'configuration' 'Linux tiny performance raw report'
    if ((Get-StrictString $configuration 'mode' 'Linux tiny performance configuration') -cne 'sync' `
        -or (Get-StrictInt64 $configuration 'durationSeconds' 'Linux tiny performance configuration' 60 60) -ne 60 `
        -or (Get-StrictInt64 $configuration 'durationBoundGraceSeconds' 'Linux tiny performance configuration' 60 60) -ne 60 `
        -or (Get-StrictInt64 $configuration 'warmupSeconds' 'Linux tiny performance configuration' 10 10) -ne 10 `
        -or (Get-StrictInt64 $configuration 'warmupCycles' 'Linux tiny performance configuration' 0 0) -ne 0 `
        -or (Get-StrictInt64 $configuration 'samplingInterval' 'Linux tiny performance configuration' 64 64) -ne 64 `
        -or (Get-StrictInt64 $configuration 'maxLatencySamplesPerWorker' 'Linux tiny performance configuration' 65536 65536) -ne 65536 `
        -or (Get-StrictInt64 $configuration 'trials' 'Linux tiny performance configuration' 3 3) -ne 3 `
        -or -not (Get-StrictBoolean $configuration 'affinityRequested' 'Linux tiny performance configuration')) {
        throw 'Linux tiny performance raw configuration is not the exact 10s/60s/3-trial affinity workload.'
    }
    [void](Assert-LinuxTinySyncTopology $configuration 'Linux tiny performance configuration')
    Assert-BenchmarkScenarioStoreDimensions `
        $configuration @('acquire-release', 'publish-remove') 'Linux tiny performance configuration'
    if ((@($configuration.profiles) -join ',') -cne 'Legacy,LockFree' `
        -or (@($configuration.countBoundProfiles) -join ',') -cne 'LockFree' `
        -or (@($configuration.scenarios) -join ',') -cne 'acquire-release,publish-remove' `
        -or (@($configuration.scenarioProcessCounts.PSObject.Properties.Name) -join ',') -cne
            'acquire-release,publish-remove') {
        throw 'Linux tiny performance raw profile/scenario matrix is not exact.'
    }
    foreach ($scenario in @('acquire-release', 'publish-remove')) {
        $counts = @($configuration.scenarioProcessCounts.$scenario)
        if ($counts.Count -ne 2 `
            -or -not (Test-IsIntegerNumber $counts[0]) -or [int64]$counts[0] -ne 1 `
            -or -not (Test-IsIntegerNumber $counts[1]) -or [int64]$counts[1] -ne 8) {
            throw "Linux tiny performance '$scenario' process-count matrix must be exactly [1,8]."
        }
    }

    $runs = @($raw.runs)
    $summaries = @($raw.summary)
    if ($runs.Count -ne 24 -or $summaries.Count -ne 8) {
        throw "Linux tiny performance raw matrix must contain 24 runs and 8 summaries, actual=$($runs.Count)/$($summaries.Count)."
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
        $context = "Linux tiny run $($run.profile)/$($run.scenario)/$($run.processCount)/trial-$($run.trial)"
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
            -or (Get-StrictInt64 $run 'operationTarget' $context 0 0) -ne 0 `
            -or (Get-StrictInt64 $run 'frameTarget' $context 0 0) -ne 0 `
            -or (Get-StrictBoolean $run 'oversubscribed' $context)) {
            throw "$context is not a correctness-clean qualification measurement."
        }
        $readerCount = Get-StrictInt64 $run 'readerProcessCount' $context 0 $processCount
        $publisherCount = Get-StrictInt64 $run 'publisherProcessCount' $context 0 $processCount
        $observerCount = Get-StrictInt64 $run 'observerProcessCount' $context 0 0
        if (($scenario -ceq 'acquire-release' -and ($readerCount -ne $processCount -or $publisherCount -ne 0)) `
            -or ($scenario -ceq 'publish-remove' -and ($readerCount -ne 0 -or $publisherCount -ne $processCount)) `
            -or $observerCount -ne 0) {
            throw "$context has the wrong process-role topology."
        }
        $cycles = Get-StrictInt64 $run 'cycles' $context 1 [int64]::MaxValue
        $operations = Get-StrictInt64 $run 'operations' $context 1 [int64]::MaxValue
        if ([decimal]$operations -lt ([decimal]2 * [decimal]$cycles)) {
            throw "$context has fewer than the two recorded store operations required per completed cycle."
        }
        $measuredSeconds = Get-StrictDouble $run 'measuredSeconds' $context 60 [double]::MaxValue
        $wallSeconds = Get-StrictDouble $run 'wallSeconds' $context $measuredSeconds [double]::MaxValue
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
        Assert-OsDerivedDouble `
            (Get-StrictDouble $run 'apiCallsPerSecond' $context 0 [double]::MaxValue -Positive) `
            ([double]$operations / $measuredSeconds) "$context.apiCallsPerSecond"
        $p50 = Get-StrictDouble $run 'p50Microseconds' $context 0 [double]::MaxValue
        $p95 = Get-StrictDouble $run 'p95Microseconds' $context 0 [double]::MaxValue
        $p99 = Get-StrictDouble $run 'p99Microseconds' $context 0 [double]::MaxValue
        $maximum = Get-StrictDouble $run 'maxMicroseconds' $context 0 [double]::MaxValue
        [void](Get-StrictDouble $run 'earlyP99Microseconds' $context 0 [double]::MaxValue -Positive)
        [void](Get-StrictDouble $run 'lateP99Microseconds' $context 0 [double]::MaxValue -Positive)
        if ($p50 -gt $p95 -or $p95 -gt $p99 -or $p99 -gt $maximum `
            -or ($profile -ceq 'LockFree' -and $maximum -gt
                (Get-StrictDouble $config.linuxTinyPerformance 'maximumStallMicroseconds' `
                    'qualification config linuxTinyPerformance' 10000 10000))) {
            throw "$context violates p99/maximum ordering or the every-run 10000us lock-free stall gate."
        }
        $assigned = @($run.assignedProcessors)
        if ($assigned.Count -ne $processCount `
            -or @($assigned | Sort-Object -Unique).Count -ne $processCount `
            -or (Get-StrictInt64 $run 'affinityAppliedCount' $context $processCount $processCount) -ne $processCount) {
            throw "$context lacks complete unique $processCount-process affinity evidence."
        }
        foreach ($processor in $assigned) {
            if (-not (Test-IsIntegerNumber $processor) `
                -or [int64]$processor -lt 0 `
                -or [int64]$processor -gt 63) {
                throw "$context has a processor assignment outside the probe's 64-bit affinity mask [0,63]."
            }
        }
        $workerCycles = @($run.workerCycles)
        [decimal]$workerCycleTotal = 0
        if ($workerCycles.Count -ne $processCount) {
            throw "$context must contain exactly $processCount worker-cycle rows."
        }
        foreach ($workerCycle in $workerCycles) {
            if (-not (Test-IsIntegerNumber $workerCycle) -or [int64]$workerCycle -lt 0) {
                throw "$context has an invalid worker-cycle row."
            }
            $workerCycleTotal += [decimal]$workerCycle
        }
        if ($workerCycleTotal -ne [decimal]$cycles) { throw "$context worker cycles do not sum to Cycles." }
        [decimal]$statusTotal = 0
        $histogram = Get-RequiredPropertyValue $run 'statusHistogram' $context
        if (@($histogram.PSObject.Properties).Count -eq 0) { throw "$context has an empty status histogram." }
        foreach ($entry in $histogram.PSObject.Properties) {
            if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -lt 0) {
                throw "$context status '$($entry.Name)' is invalid."
            }
            if ($entry.Name -ceq 'Validation.ChecksumMismatch' `
                -or $entry.Name -clike 'CorruptReason.*') {
                throw "$context contains forbidden checksum/corruption evidence '$($entry.Name)'."
            }
            if ($entry.Name -match '^(Acquire|Release|Publish|Remove)\.') { $statusTotal += [decimal]$entry.Value }
        }
        if ($statusTotal -ne [decimal]$operations) { throw "$context status histogram does not sum to Operations." }
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
    foreach ($summary in $summaries) {
        $context = "Linux tiny summary $($summary.profile)/$($summary.scenario)/$($summary.processCount)"
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
        $matching = @($runs | Where-Object {
            [string]$_.profile -ceq $profile -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq $processCount
        })
        if ($matching.Count -ne 3 -or (Get-StrictInt64 $summary 'totalFailures' $context 0 0) -ne 0) {
            throw "$context does not summarize exactly three correctness-clean trials."
        }
        foreach ($pair in @(
            @('medianApiCallsPerSecond', 'apiCallsPerSecond'),
            @('medianP99Microseconds', 'p99Microseconds'),
            @('medianMaxMicroseconds', 'maxMicroseconds'))) {
            $values = [double[]]@($matching | ForEach-Object {
                Get-StrictDouble $_ $pair[1] $context 0 [double]::MaxValue
            })
            Assert-OsDerivedDouble `
                (Get-StrictDouble $summary $pair[0] $context 0 [double]::MaxValue) `
                (Get-MedianValue $values) "$context.$($pair[0])"
        }
        $merged = [ordered]@{}
        foreach ($run in $matching) {
            foreach ($entry in $run.statusHistogram.PSObject.Properties) {
                if (-not $merged.Contains($entry.Name)) { $merged[$entry.Name] = [int64]0 }
                $merged[$entry.Name] = [int64]$merged[$entry.Name] + [int64]$entry.Value
            }
        }
        $summaryHistogram = Get-RequiredPropertyValue $summary 'statusHistogram' $context
        if ((@($summaryHistogram.PSObject.Properties.Name) -join ',') -cne (@($merged.Keys) -join ',')) {
            throw "$context status histogram keys do not match its raw trials."
        }
        foreach ($entry in $summaryHistogram.PSObject.Properties) {
            if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -ne [int64]$merged[$entry.Name]) {
                throw "$context status '$($entry.Name)' is not the raw-trial total."
            }
        }
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
        $legacyOneP99 = Get-StrictDouble $legacyOne 'medianP99Microseconds' "$scenario legacy/1p summary" 0 [double]::MaxValue -Positive
        $lockFreeOneP99 = Get-StrictDouble $lockFreeOne 'medianP99Microseconds' "$scenario lock-free/1p summary" 0 [double]::MaxValue -Positive
        $legacyEightRate = Get-StrictDouble $legacyEight 'medianApiCallsPerSecond' "$scenario legacy/8p summary" 0 [double]::MaxValue -Positive
        $lockFreeEightRate = Get-StrictDouble $lockFreeEight 'medianApiCallsPerSecond' "$scenario lock-free/8p summary" 0 [double]::MaxValue -Positive
        $lockFreeEightP99 = Get-StrictDouble $lockFreeEight 'medianP99Microseconds' "$scenario lock-free/8p summary" 0 [double]::MaxValue -Positive
        $uncontendedP99Ratio = $lockFreeOneP99 / $legacyOneP99
        $throughputRatio = $lockFreeEightRate / $legacyEightRate
        $scaleP99Ratio = $lockFreeEightP99 / $lockFreeOneP99
        if (-not [double]::IsFinite($uncontendedP99Ratio) `
            -or $uncontendedP99Ratio -gt [double]$config.linuxTinyPerformance.maximumUncontendedP99Ratio `
            -or -not [double]::IsFinite($throughputRatio) `
            -or $throughputRatio -lt [double]$config.linuxTinyPerformance.minimumThroughputRatio `
            -or -not [double]::IsFinite($scaleP99Ratio) `
            -or $scaleP99Ratio -gt [double]$config.linuxTinyPerformance.maximumScaleP99Ratio `
            -or $lockFreeEightP99 -gt [double]$config.linuxTinyPerformance.maximumP99Microseconds) {
            throw "Linux tiny performance '$scenario' gate failed: uncontendedP99Ratio=$uncontendedP99Ratio throughputRatio=$throughputRatio scaleP99Ratio=$scaleP99Ratio lockFreeEightP99Microseconds=$lockFreeEightP99."
        }
    }
    $declared = Get-RequiredPropertyValue $performance 'validation' 'Linux tiny performance row evidence'
    $expectedDeclaredProperties = @(
        'schemaVersion', 'runCount', 'summaryCount', 'warmupSeconds', 'durationSeconds',
        'trials', 'processCounts', 'minimumThroughputRatio', 'maximumUncontendedP99Ratio',
        'maximumScaleP99Ratio', 'maximumP99Microseconds', 'maximumStallMicroseconds', 'metrics')
    if ((@($declared.PSObject.Properties.Name) -join ',') -cne ($expectedDeclaredProperties -join ',')) {
        throw 'Linux tiny performance row declared validation has unexpected properties or property order.'
    }
    $declaredProcessCounts = @(Get-RequiredPropertyValue $declared 'processCounts' 'Linux tiny declared validation')
    if ($declaredProcessCounts.Count -ne 2 `
        -or -not (Test-IsIntegerNumber $declaredProcessCounts[0]) -or [int64]$declaredProcessCounts[0] -ne 1 `
        -or -not (Test-IsIntegerNumber $declaredProcessCounts[1]) -or [int64]$declaredProcessCounts[1] -ne 8 `
        -or (Get-StrictInt64 $declared 'schemaVersion' 'Linux tiny declared validation' 2 2) -ne 2 `
        -or (Get-StrictInt64 $declared 'runCount' 'Linux tiny declared validation' 24 24) -ne 24 `
        -or (Get-StrictInt64 $declared 'summaryCount' 'Linux tiny declared validation' 8 8) -ne 8 `
        -or (Get-StrictInt64 $declared 'warmupSeconds' 'Linux tiny declared validation' 10 10) -ne 10 `
        -or (Get-StrictInt64 $declared 'durationSeconds' 'Linux tiny declared validation' 60 60) -ne 60 `
        -or (Get-StrictInt64 $declared 'trials' 'Linux tiny declared validation' 3 3) -ne 3 `
        -or (Get-StrictDouble $declared 'minimumThroughputRatio' 'Linux tiny declared validation' 1 1) -ne 1 `
        -or (Get-StrictDouble $declared 'maximumUncontendedP99Ratio' 'Linux tiny declared validation' 1 1) -ne 1 `
        -or (Get-StrictDouble $declared 'maximumScaleP99Ratio' 'Linux tiny declared validation' 3 3) -ne 3 `
        -or (Get-StrictDouble $declared 'maximumP99Microseconds' 'Linux tiny declared validation' 10 10) -ne 10 `
        -or (Get-StrictDouble $declared 'maximumStallMicroseconds' 'Linux tiny declared validation' 10000 10000) -ne 10000) {
        throw 'Linux tiny performance row declared validation does not match the recomputed gate.'
    }
    $declaredMetrics = @($declared.metrics)
    if ($declaredMetrics.Count -ne 8) {
        throw 'Linux tiny performance row must declare exactly eight recomputable metric rows.'
    }
    $actualDeclaredMetricKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($metric in $declaredMetrics) {
        $metricContext = "Linux tiny declared metric $($metric.profile)/$($metric.scenario)/$($metric.processCount)"
        $profile = Get-StrictString $metric 'profile' $metricContext
        $scenario = Get-StrictString $metric 'scenario' $metricContext
        $metricProcessCount = Get-StrictInt64 $metric 'processCount' $metricContext 1 8
        if ($metricProcessCount -notin @(1, 8)) {
            throw "$metricContext has an unsupported process count."
        }
        $metricKey = "$profile|$scenario|$metricProcessCount"
        if (-not $expectedSummaryKeys.Contains($metricKey) -or -not $actualDeclaredMetricKeys.Add($metricKey)) {
            throw "$metricContext is unexpected or duplicated."
        }
        $matchingSummary = @($summaries | Where-Object {
            [string]$_.profile -ceq $profile -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq $metricProcessCount
        })
        $matchingRuns = @($runs | Where-Object {
            [string]$_.profile -ceq $profile -and [string]$_.scenario -ceq $scenario -and [int64]$_.processCount -eq $metricProcessCount
        })
        if ($matchingSummary.Count -ne 1 -or $matchingRuns.Count -ne 3) {
            throw "$metricContext does not identify one exact recomputed summary tuple."
        }
        Assert-OsDerivedDouble `
            (Get-StrictDouble $metric 'medianApiCallsPerSecond' $metricContext 0 [double]::MaxValue) `
            ([double]$matchingSummary[0].medianApiCallsPerSecond) "$metricContext.medianApiCallsPerSecond"
        Assert-OsDerivedDouble `
            (Get-StrictDouble $metric 'medianP99Microseconds' $metricContext 0 [double]::MaxValue) `
            ([double]$matchingSummary[0].medianP99Microseconds) "$metricContext.medianP99Microseconds"
        Assert-OsDerivedDouble `
            (Get-StrictDouble $metric 'maximumRawStallMicroseconds' $metricContext 0 [double]::MaxValue) `
            ([double](($matchingRuns | Measure-Object maxMicroseconds -Maximum).Maximum)) `
            "$metricContext.maximumRawStallMicroseconds"
    }
    if (-not $actualDeclaredMetricKeys.SetEquals($expectedSummaryKeys)) {
        throw 'Linux tiny performance declared metric tuple set is incomplete.'
    }
    return [IO.Path]::GetRelativePath($root, $actualRawPath)
}

function Invoke-LinuxTinyOsPerformanceVerifierSelfTest {
    $reportPath = Join-Path $runRoot 'linux-tiny-os-performance-self-test.json'
    $treeRoot = Join-Path $runRoot 'linux-tiny-os-performance-self-test.evidence'
    New-Item -ItemType Directory -Path $treeRoot | Out-Null
    $rawPath = Join-Path $treeRoot 'linux-tiny-performance.json'
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
                        WorkerCycles = @($workerCycles); OperationTarget = 0; FrameTarget = 0
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
    $raw = [pscustomobject][ordered]@{
        SchemaVersion = 8
        Environment = [pscustomobject][ordered]@{
            RepositoryCommit = 'synthetic'; RepositoryWorkingTreeState = 'clean'
            SharedMemoryStoreAssemblySha256 = ('A' * 64); ProbeAssemblySha256 = ('B' * 64)
            OperatingSystem = 'Ubuntu 24.04 synthetic'; OperatingSystemArchitecture = 'X64'; ProcessArchitecture = 'X64'
            Framework = '.NET synthetic'; RuntimeVersion = 'synthetic'; LogicalProcessorCount = 8
            PhysicalCoreCount = 4; TotalMemoryBytes = 17179869184; ProcessorModel = 'Synthetic CPU'
            ProcessorIdentifier = 'Synthetic CPU'; ServerGarbageCollection = $false; StopwatchFrequency = 10000000
        }
        Configuration = [pscustomobject][ordered]@{
            Mode = 'sync'; DurationSeconds = 60; DurationBoundGraceSeconds = 60
            Trials = 3; Profiles = @('Legacy', 'LockFree')
            CountBoundProfiles = @('LockFree')
            Scenarios = @('acquire-release', 'publish-remove')
            ScenarioProcessCounts = [pscustomobject][ordered]@{
                'acquire-release' = @(1, 8); 'publish-remove' = @(1, 8)
            }
            ScenarioStoreDimensions = [pscustomobject][ordered]@{
                'acquire-release' = [pscustomobject][ordered]@{
                    SlotCount = 32; MaxValueBytes = 8; MaxDescriptorBytes = 0; MaxKeyBytes = 8
                    LeaseRecordCount = 64; LockFreeParticipantRecordCount = 64
                }
                'publish-remove' = [pscustomobject][ordered]@{
                    SlotCount = 32; MaxValueBytes = 8; MaxDescriptorBytes = 0; MaxKeyBytes = 8
                    LeaseRecordCount = 64; LockFreeParticipantRecordCount = 64
                }
            }
            WarmupCycles = 0; WarmupSeconds = 10; AffinityRequested = $true
            SamplingInterval = 64; MaxLatencySamplesPerWorker = 65536
            SyncKeysPerWorker = [int]$config.linuxTinyPerformance.syncKeysPerWorker
            SyncMaximumWorkerCount = [int]$config.linuxTinyPerformance.syncMaximumWorkerCount
            SyncCanonicalBucketCount = [int]$config.linuxTinyPerformance.syncCanonicalBucketCount
            SyncKeyCatalogSha256 = [string]$config.linuxTinyPerformance.syncKeyCatalogSha256
            SyncKeyCanonicalBucketAssignments = @($config.linuxTinyPerformance.syncKeyCanonicalBucketAssignments)
        }
        Runs = @($runs); Summary = @($summaries); MinimumCompatibleSchemaVersion = 8
        SchemaCompatibility = 'synthetic Schema v8 release-runner verifier self-test'
    }
    $raw | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $rawRelativePath = [IO.Path]::GetRelativePath($root, $rawPath)
    $rawManifest = [pscustomobject][ordered]@{
        path = $rawRelativePath
        length = (Get-Item -LiteralPath $rawPath).Length
        sha256 = Get-FileSha256 $rawPath
    }
    $declaredMetrics = @($summaries | ForEach-Object {
        $summary = $_
        $matchingRuns = @($runs | Where-Object {
            [string]$_.Profile -ceq [string]$summary.Profile `
                -and [string]$_.Scenario -ceq [string]$summary.Scenario `
                -and [int64]$_.ProcessCount -eq [int64]$summary.ProcessCount
        })
        [pscustomobject][ordered]@{
            profile = [string]$summary.Profile
            scenario = [string]$summary.Scenario
            processCount = [int64]$summary.ProcessCount
            medianApiCallsPerSecond = [double]$summary.MedianApiCallsPerSecond
            medianP99Microseconds = [double]$summary.MedianP99Microseconds
            maximumRawStallMicroseconds = [double](($matchingRuns | Measure-Object MaxMicroseconds -Maximum).Maximum)
        }
    })
    $osReport = [pscustomobject][ordered]@{
        platform = 'linux'
        architecture = 'x64'
        configuration = 'Release'
        provenance = [pscustomobject][ordered]@{ repositoryCommit = 'synthetic' }
        host = [pscustomobject][ordered]@{
            operatingSystem = 'Ubuntu 24.04 synthetic'
            operatingSystemArchitecture = 'X64'
            processArchitecture = 'X64'
            logicalProcessorCount = 8
        }
        testedAssemblies = @(
            [pscustomobject][ordered]@{
                path = 'benchmarks/SharedMemoryStore.SyncProbe/bin/Release/net10.0/SharedMemoryStore.dll'
                sha256 = ('A' * 64)
            },
            [pscustomobject][ordered]@{
                path = 'benchmarks/SharedMemoryStore.SyncProbe/bin/Release/net10.0/SharedMemoryStore.SyncProbe.dll'
                sha256 = ('B' * 64)
            })
        results = @([pscustomobject][ordered]@{
            name = 'linux-tiny-performance'; required = $true; status = 'pass'
            command = 'dotnet SharedMemoryStore.SyncProbe.csproj --mode sync --profile both --scenario acquire-release,publish-remove --process-counts 1,8 --warmup 10 --duration 60 --trials 3'
            performanceEvidence = [pscustomobject][ordered]@{
                schemaVersion = 1; reportPath = $rawRelativePath; reportSha256 = $rawManifest.sha256
                validation = [pscustomobject][ordered]@{
                    schemaVersion = 2; runCount = 24; summaryCount = 8
                    warmupSeconds = 10; durationSeconds = 60; trials = 3; processCounts = @(1, 8)
                    minimumThroughputRatio = 1.0; maximumUncontendedP99Ratio = 1.0
                    maximumScaleP99Ratio = 3.0; maximumP99Microseconds = 10.0
                    maximumStallMicroseconds = 10000.0; metrics = $declaredMetrics
                }
            }
        })
        evidenceManifest = @($rawManifest)
    }
    [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath)
    $hostMismatch = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $hostMismatch.host.operatingSystem = 'Debian synthetic'
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $hostMismatch $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted a raw report from a different host OS description.'
    }
    [int]$assertions = 2

    $oddMedian = Get-MedianValue ([double[]]@(30.0, 10.0, 20.0))
    $evenMedian = Get-MedianValue ([double[]]@(40.0, 10.0, 30.0, 20.0))
    if ($oddMedian -ne 20.0 -or $evenMedian -ne 25.0) {
        throw 'Linux OS performance verifier self-test did not compute canonical odd/even medians.'
    }
    $assertions++

    $tampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $tamperedLockFreeRun = @($tampered.Runs | Where-Object {
        [string]$_.Profile -ceq 'LockFree' `
            -and [string]$_.Scenario -ceq 'acquire-release' `
            -and [int64]$_.ProcessCount -eq 1
    })[0]
    $tamperedLockFreeRun.MaxMicroseconds = 10001.0
    $tampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted an over-limit raw lock-free stall.'
    }
    $assertions++

    $sampleTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $sampleTamperedOneProcessRun = @($sampleTampered.Runs | Where-Object {
        [int64]$_.ProcessCount -eq 1
    })[0]
    $sampleTamperedOneProcessRun.LateSampleCount = 1023
    $sampleTamperedOneProcessRun.SampleCount = 2047
    $sampleTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted incoherent early/late sample counts.'
    }
    $assertions++

    $assertRawTamperRejected = {
        param(
            [Parameter(Mandatory)]$TamperedRaw,
            [Parameter(Mandatory)]$TamperedOsReport,
            [Parameter(Mandatory)][string]$FailureMessage)

        $TamperedRaw | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
        $tamperedHash = Get-FileSha256 $rawPath
        $TamperedOsReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
        $TamperedOsReport.evidenceManifest[0].sha256 = $tamperedHash
        $TamperedOsReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
        $wasRejected = $false
        try {
            [void](Assert-LinuxTinyOsPerformanceEvidence $TamperedOsReport $reportPath)
        }
        catch {
            $wasRejected = $true
        }
        if (-not $wasRejected) {
            throw $FailureMessage
        }
    }

    $unknownProcessorTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $unknownProcessorTampered.Environment.ProcessorModel = ' Unknown CPU '
    $unknownProcessorOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    & $assertRawTamperRejected $unknownProcessorTampered $unknownProcessorOsReport `
        'Linux OS performance verifier self-test accepted an unknown processor model.'
    $assertions++

    $missingMemoryTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $missingMemoryTampered.Environment.PSObject.Properties.Remove('TotalMemoryBytes')
    $missingMemoryOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    & $assertRawTamperRejected $missingMemoryTampered $missingMemoryOsReport `
        'Linux OS performance verifier self-test accepted missing memory metadata.'
    $assertions++

    $storeDimensionTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $storeDimensionTampered.Configuration.ScenarioStoreDimensions.'acquire-release'.SlotCount = 31
    $storeDimensionOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    & $assertRawTamperRejected $storeDimensionTampered $storeDimensionOsReport `
        'Linux OS performance verifier self-test accepted incorrect store dimensions.'
    $assertions++

    $countPolicyTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $countPolicyTampered.Configuration.CountBoundProfiles = @('Legacy')
    $countPolicyOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    & $assertRawTamperRejected $countPolicyTampered $countPolicyOsReport `
        'Linux OS performance verifier self-test accepted a swapped count-bound profile policy.'
    $assertions++

    $targetTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $targetTampered.Runs[0].OperationTarget = 1
    $targetOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    & $assertRawTamperRejected $targetTampered $targetOsReport `
        'Linux OS performance verifier self-test accepted a count target on a duration-only tiny row.'
    $assertions++

    $durationTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $durationTampered.Runs[0].MeasuredSeconds = 59.999
    $durationOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    & $assertRawTamperRejected $durationTampered $durationOsReport `
        'Linux OS performance verifier self-test accepted a short duration-bound row.'
    $assertions++

    foreach ($scenario in @('acquire-release', 'publish-remove')) {
        $uncontendedTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
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
        $uncontendedOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        @($uncontendedOsReport.results[0].performanceEvidence.validation.metrics | Where-Object {
            [string]$_.profile -ceq 'LockFree' `
                -and [string]$_.scenario -ceq $scenario `
                -and [int64]$_.processCount -eq 1
        })[0].medianP99Microseconds = 6.0
        & $assertRawTamperRejected $uncontendedTampered $uncontendedOsReport `
            "Linux OS performance verifier self-test accepted an over-limit '$scenario' uncontended p99 ratio."
        $assertions++

        $scaleTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
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
        $scaleOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        @($scaleOsReport.results[0].performanceEvidence.validation.metrics | Where-Object {
            [string]$_.profile -ceq 'LockFree' `
                -and [string]$_.scenario -ceq $scenario `
                -and [int64]$_.processCount -eq 1
        })[0].medianP99Microseconds = 2.0
        & $assertRawTamperRejected $scaleTampered $scaleOsReport `
            "Linux OS performance verifier self-test accepted an over-limit '$scenario' scale p99 ratio."
        $assertions++

        $absoluteTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
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
        $absoluteOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        @($absoluteOsReport.results[0].performanceEvidence.validation.metrics | Where-Object {
            [string]$_.profile -ceq 'LockFree' `
                -and [string]$_.scenario -ceq $scenario `
                -and [int64]$_.processCount -eq 8
        })[0].medianP99Microseconds = 11.0
        & $assertRawTamperRejected $absoluteTampered $absoluteOsReport `
            "Linux OS performance verifier self-test accepted an over-limit '$scenario' absolute p99."
        $assertions++

        $throughputTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
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
        $throughputOsReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        @($throughputOsReport.results[0].performanceEvidence.validation.metrics | Where-Object {
            [string]$_.profile -ceq 'LockFree' `
                -and [string]$_.scenario -ceq $scenario `
                -and [int64]$_.processCount -eq 8
        })[0].medianApiCallsPerSecond = 550.0
        & $assertRawTamperRejected $throughputTampered $throughputOsReport `
            "Linux OS performance verifier self-test accepted an under-limit '$scenario' 8-process throughput ratio."
        $assertions++
    }

    $topologyTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $topologyTampered.Configuration.SyncKeyCanonicalBucketAssignments[1] = 1
    $topologyTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted a colliding synchronization-key topology.'
    }
    $assertions++

    $raw | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = Get-FileSha256 $rawPath
    $osReport.evidenceManifest[0].sha256 = Get-FileSha256 $rawPath
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $duplicateMetricReport = $osReport | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $duplicateMetricReport.results[0].performanceEvidence.validation.metrics[1] =
        $duplicateMetricReport.results[0].performanceEvidence.validation.metrics[0]
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $duplicateMetricReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted duplicated declared metric tuples.'
    }
    $assertions++

    $affinityTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $affinityTamperedEightProcessRun = @($affinityTampered.Runs | Where-Object {
        [int64]$_.ProcessCount -eq 8
    })[0]
    $affinityTamperedEightProcessRun.AssignedProcessors = @(64, 65, 66, 67, 68, 69, 70, 71)
    $affinityTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted processor IDs outside its 64-bit affinity mask.'
    }
    $assertions++

    $operationTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $operationTampered.Runs[0].Operations = [int64]$operationTampered.Runs[0].Cycles
    $operationTampered.Runs[0].ApiCallsPerSecond =
        [double]$operationTampered.Runs[0].Operations / [double]$operationTampered.Runs[0].MeasuredSeconds
    $operationTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted fewer than two operations per completed cycle.'
    }
    $assertions++

    $successTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $successTampered.Runs[0].StatusHistogram = [pscustomobject][ordered]@{
        'Acquire.NotFound' = [int64]$successTampered.Runs[0].Operations
    }
    $successTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted a cycle set without exact paired success counts.'
    }
    $assertions++

    $corruptionTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $corruptionTampered.Runs[0].StatusHistogram | Add-Member `
        -NotePropertyName 'Validation.ChecksumMismatch' -NotePropertyValue 1
    $corruptionTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted checksum/corruption evidence.'
    }
    $assertions++

    $corruptReasonTampered = $raw | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $corruptReasonTampered.Runs[0].StatusHistogram | Add-Member `
        -NotePropertyName 'CorruptReason.Forged' -NotePropertyValue 1
    $corruptReasonTampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $tamperedHash = Get-FileSha256 $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = $tamperedHash
    $osReport.evidenceManifest[0].sha256 = $tamperedHash
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $rejected = $false
    try { [void](Assert-LinuxTinyOsPerformanceEvidence $osReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) {
        throw 'Linux OS performance verifier self-test accepted a corruption-reason row.'
    }
    $assertions++

    $raw | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $rawPath
    $osReport.results[0].performanceEvidence.reportSha256 = Get-FileSha256 $rawPath
    $osReport.evidenceManifest[0].sha256 = Get-FileSha256 $rawPath
    $osReport.evidenceManifest[0].length = (Get-Item -LiteralPath $rawPath).Length
    $osReport | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath
    return $assertions
}

function Assert-OsResultCommandEvidence {
    param(
        [Parameter(Mandatory)]$Row,
        [Parameter(Mandatory)][string]$Context)

    $members = @{}
    foreach ($property in @('command', 'stdout', 'stderr', 'stdoutSha256', 'stderrSha256')) {
        $member = $Row.PSObject.Properties[$property]
        if ($null -eq $member) {
            throw "$Context is missing nullable execution-evidence property '$property'."
        }
        $members[$property] = $member.Value
    }

    if ($null -eq $members.command) {
        foreach ($property in @('stdout', 'stderr', 'stdoutSha256', 'stderrSha256')) {
            if ($null -ne $members[$property]) {
                throw "$Context has '$property' evidence without a command."
            }
        }
        return $false
    }

    if ($members.command -isnot [string] -or [string]::IsNullOrWhiteSpace($members.command)) {
        throw "$Context has an empty or non-string command."
    }
    foreach ($stream in @('stdout', 'stderr')) {
        if ($members[$stream] -isnot [string] -or [string]::IsNullOrWhiteSpace($members[$stream])) {
            throw "$Context has an empty or non-string $stream path."
        }
        $digest = $members[$stream + 'Sha256']
        if ($digest -isnot [string] -or $digest -notmatch '^[0-9A-F]{64}$') {
            throw "$Context has no SHA-256-bound $stream evidence."
        }
    }
    return $true
}

function Assert-ExactReleaseOsRows {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string]$PlatformId,
        [Parameter(Mandatory)][string]$EvidencePath)

    if ([string]$Report.command -ne 'all') {
        throw "Release OS evidence '$EvidencePath' must have command=all."
    }
    $expected = Get-ExpectedReleaseOsRows $PlatformId
    $rows = @($Report.results)
    if ($rows.Count -ne $expected.Count) {
        throw "Release OS evidence '$EvidencePath' has $($rows.Count) rows; expected exactly $($expected.Count)."
    }
    $duplicates = @($rows | Group-Object name | Where-Object Count -ne 1)
    if ($duplicates.Count -ne 0) {
        throw "Release OS evidence '$EvidencePath' has missing/duplicate result names: $($duplicates.Name -join ',')."
    }
    foreach ($entry in $expected.GetEnumerator()) {
        $rowMatches = @($rows | Where-Object { [string]$_.name -ceq [string]$entry.Key })
        if ($rowMatches.Count -ne 1) {
            throw "Release OS evidence '$EvidencePath' is missing exact row '$($entry.Key)'."
        }
        $required = Get-StrictBoolean $rowMatches[0] 'required' "OS row '$($entry.Key)'"
        if ($required -ne [bool]$entry.Value) {
            throw "Release OS row '$($entry.Key)' has required=$required; expected $($entry.Value)."
        }
        $status = Get-RequiredPropertyValue $rowMatches[0] 'status' "OS row '$($entry.Key)'"
        if ($status -isnot [string] -or ($required -and $status -ne 'pass') `
            -or (-not $required -and $status -notin @('pass', 'not-qualified'))) {
            throw "Release OS row '$($entry.Key)' has invalid status '$status'."
        }
        [void](Get-StrictDouble $rowMatches[0] 'elapsedSeconds' "OS row '$($entry.Key)'" 0 [double]::MaxValue)
        $timeoutSeconds = Get-StrictInt64 $rowMatches[0] 'timeoutSeconds' "OS row '$($entry.Key)'" 0 [int32]::MaxValue
        $timedOut = Get-StrictBoolean $rowMatches[0] 'timedOut' "OS row '$($entry.Key)'"
        $hasCommandEvidence = Assert-OsResultCommandEvidence $rowMatches[0] "OS row '$($entry.Key)'"
        $requiresCommand = $status -ceq 'pass' `
            -and [string]$entry.Key -cnotlike 'self-test-*'
        if ($timedOut) {
            throw "Release OS row '$($entry.Key)' timed out."
        }
        if ($requiresCommand -and -not $hasCommandEvidence) {
            throw "Release OS passing executable row '$($entry.Key)' cannot omit its bounded command/log evidence."
        }
        if ($hasCommandEvidence) {
            $exitCode = Get-StrictInt64 $rowMatches[0] 'exitCode' "OS row '$($entry.Key)'" `
                ([int64]-1) [int32]::MaxValue
            if ($timeoutSeconds -le 0 -or $exitCode -ne 0) {
                throw "Release OS executable row '$($entry.Key)' lacks bounded successful command/log evidence."
            }
        }
    }
    $unexpected = @($rows | Where-Object { -not $expected.Contains([string]$_.name) })
    if ($unexpected.Count -ne 0) {
        throw "Release OS evidence '$EvidencePath' has unexpected rows: $($unexpected.name -join ',')."
    }

    $cleanRow = @($rows | Where-Object { [string]$_.name -ceq 'clean' })[0]
    $preclean = Get-RequiredPropertyValue $cleanRow 'preclean' "OS evidence '$EvidencePath' clean row"
    if ((Get-StrictInt64 $preclean 'schemaVersion' "OS evidence '$EvidencePath' preclean" 1 1) -ne 1) {
        throw "Release OS evidence '$EvidencePath' has an unsupported pre-clean schema."
    }
    $projectCount = Get-StrictInt64 $preclean 'solutionProjectCount' "OS evidence '$EvidencePath' preclean" 1 [int32]::MaxValue
    $uniqueProjectCount = Get-StrictInt64 $preclean 'uniqueSolutionProjectCount' "OS evidence '$EvidencePath' preclean" 1 [int32]::MaxValue
    $targetCount = Get-StrictInt64 $preclean 'targetCount' "OS evidence '$EvidencePath' preclean" 2 [int32]::MaxValue
    $uniqueTargetCount = Get-StrictInt64 $preclean 'uniqueTargetCount' "OS evidence '$EvidencePath' preclean" 2 [int32]::MaxValue
    $existedBeforeCount = Get-StrictInt64 $preclean 'existedBeforeCount' "OS evidence '$EvidencePath' preclean" 0 $targetCount
    $removedCount = Get-StrictInt64 $preclean 'removedCount' "OS evidence '$EvidencePath' preclean" 0 $targetCount
    $verifiedAbsentCount = Get-StrictInt64 $preclean 'verifiedAbsentCount' "OS evidence '$EvidencePath' preclean" 0 $targetCount
    $protectedFileCount = Get-StrictInt64 $preclean 'protectedFileCount' "OS evidence '$EvidencePath' preclean" 0 [int32]::MaxValue
    if ($uniqueProjectCount -ne $projectCount `
        -or $targetCount -ne (2 * $projectCount) `
        -or $uniqueTargetCount -ne $targetCount `
        -or $existedBeforeCount -ne $removedCount `
        -or $verifiedAbsentCount -ne $targetCount `
        -or $protectedFileCount -ne 0) {
        throw "Release OS evidence '$EvidencePath' lacks exact, unique, protected-file-free pre-clean coverage."
    }
    foreach ($hashProperty in @('solutionProjectSetSha256', 'targetSetSha256', 'reportSha256')) {
        $hash = Get-RequiredPropertyValue $preclean $hashProperty "OS evidence '$EvidencePath' preclean"
        if ($hash -isnot [string] -or $hash -notmatch '^[0-9A-F]{64}$') {
            throw "Release OS evidence '$EvidencePath' preclean.$hashProperty is not a SHA-256 digest."
        }
    }
    $precleanReportPath = Get-RequiredPropertyValue $preclean 'reportPath' "OS evidence '$EvidencePath' preclean"
    if ($precleanReportPath -isnot [string] `
        -or [string]::IsNullOrWhiteSpace($precleanReportPath) `
        -or $precleanReportPath.Replace('\', '/') -notmatch '^artifacts/.+\.evidence/preclean\.json$') {
        throw "Release OS evidence '$EvidencePath' has an invalid pre-clean sidecar path."
    }
    $manifestMatches = @($Report.evidenceManifest | Where-Object {
        ([string]$_.path).Replace('\', '/') -ceq $precleanReportPath.Replace('\', '/') `
            -and [string]$_.sha256 -ceq [string]$preclean.reportSha256
    })
    if ($manifestMatches.Count -ne 1) {
        throw "Release OS evidence '$EvidencePath' does not uniquely manifest its pre-clean sidecar and digest."
    }
    if ($PlatformId -eq 'linux-x64') {
        return Assert-LinuxTinyOsPerformanceEvidence $Report $EvidencePath
    }
    $windowsPerformanceRow = @($rows | Where-Object { [string]$_.name -ceq 'linux-tiny-performance' })
    if ($windowsPerformanceRow.Count -ne 1 `
        -or (Get-StrictBoolean $windowsPerformanceRow[0] 'required' 'Windows optional Linux performance row') `
        -or [string]$windowsPerformanceRow[0].status -cne 'not-qualified' `
        -or $null -ne $windowsPerformanceRow[0].command) {
        throw "Windows OS evidence '$EvidencePath' must retain one optional non-executed Linux performance row."
    }
    return $null
}

function Assert-OsReportProvenance {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string]$EvidencePath)

    foreach ($sectionName in @('provenance', 'completionProvenance')) {
        $section = Get-RequiredPropertyValue $Report $sectionName "OS evidence '$EvidencePath'"
        foreach ($property in @('repositoryCommit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
            $value = Get-RequiredPropertyValue $section $property "OS evidence '$EvidencePath'.$sectionName"
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -eq 'unknown') {
                throw "OS evidence '$EvidencePath'.$sectionName.$property is unknown or invalid."
            }
        }
    }
    foreach ($property in @('repositoryCommit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
        if ([string]$Report.provenance.$property -ne [string]$Report.completionProvenance.$property) {
            throw "OS evidence '$EvidencePath' changed provenance property '$property' during execution."
        }
    }
    $runnerToOsProperty = [ordered]@{
        commit = 'repositoryCommit'
        headTree = 'headTree'
        workingTreeState = 'workingTreeState'
        statusSha256 = 'statusSha256'
        sourceManifestSha256 = 'sourceManifestSha256'
    }
    foreach ($entry in $runnerToOsProperty.GetEnumerator()) {
        if ([string]$Report.provenance.($entry.Value) -ne [string]$repositoryProvenance[$entry.Key]) {
            throw "OS evidence '$EvidencePath' does not match qualification provenance '$($entry.Key)'."
        }
    }
    $assemblies = @($Report.testedAssemblies)
    $completionAssemblies = @($Report.completionTestedAssemblies)
    if ($assemblies.Count -eq 0 -or $completionAssemblies.Count -ne $assemblies.Count `
        -or @($assemblies | Group-Object path | Where-Object Count -ne 1).Count -ne 0 `
        -or @($completionAssemblies | Group-Object path | Where-Object Count -ne 1).Count -ne 0) {
        throw "OS evidence '$EvidencePath' lacks stable unique tested-assembly manifests."
    }
    foreach ($manifest in @($assemblies, $completionAssemblies)) {
        foreach ($assembly in @($manifest)) {
            if ([string]::IsNullOrWhiteSpace([string]$assembly.path) `
                -or [string]$assembly.sha256 -notmatch '^[0-9A-F]{64}$' `
                -or (Get-StrictInt64 $assembly 'length' "OS tested assembly '$($assembly.path)'" 1 [int64]::MaxValue) -le 0) {
                throw "OS evidence '$EvidencePath' has an invalid tested assembly row."
            }
        }
    }
    $startCanonical = @($assemblies | Sort-Object path | ForEach-Object {
        "$($_.path)|$($_.length)|$($_.sha256)"
    }) -join "`n"
    $completionCanonical = @($completionAssemblies | Sort-Object path | ForEach-Object {
        "$($_.path)|$($_.length)|$($_.sha256)"
    }) -join "`n"
    if ($startCanonical -cne $completionCanonical) {
        throw "OS evidence '$EvidencePath' tested assembly manifest changed during execution."
    }
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Context)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $artifactBoundary = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $fullPath.Equals($artifactBoundary, $comparison) `
        -and -not $fullPath.StartsWith($artifactBoundary + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "$Context resolves outside the repository artifacts root."
    }
    $current = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Context traverses reparse point '$current'."
            }
        }
        if ($current.Equals($artifactBoundary, $comparison)) {
            break
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($current, $comparison)) {
            throw "$Context cannot be proven below the artifacts root."
        }
        $current = $parent
    }
}

function Assert-OsEvidenceTree {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string]$EvidencePath)

    $reportPath = [IO.Path]::GetFullPath($EvidencePath)
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "OS evidence report '$reportPath' is missing."
    }
    Assert-NoReparsePath $reportPath "OS evidence report '$reportPath'"
    $evidenceRoot = [IO.Path]::GetFullPath((Join-Path `
        (Split-Path -Parent $reportPath) `
        ([IO.Path]::GetFileNameWithoutExtension($reportPath) + '.evidence')))
    if (-not (Test-Path -LiteralPath $evidenceRoot -PathType Container)) {
        throw "OS evidence '$reportPath' is missing exact sibling evidence root '$evidenceRoot'."
    }
    Assert-NoReparsePath $evidenceRoot "OS evidence root '$evidenceRoot'"

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $evidencePrefix = $evidenceRoot + [IO.Path]::DirectorySeparatorChar
    $manifestRows = @(Get-RequiredPropertyValue $Report 'evidenceManifest' "OS evidence '$reportPath'")
    if ($manifestRows.Count -eq 0) {
        throw "OS evidence '$reportPath' has an empty evidence manifest."
    }
    $manifestByPath = [Collections.Generic.Dictionary[string, object]]::new($pathComparer)
    foreach ($entry in $manifestRows) {
        $manifestPath = Get-StrictString $entry 'path' "OS evidence '$reportPath' manifest row"
        if ([IO.Path]::IsPathFullyQualified($manifestPath)) {
            throw "OS evidence manifest path '$manifestPath' must be repository relative."
        }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $manifestPath))
        if (-not $fullPath.StartsWith($evidencePrefix, $comparison)) {
            throw "OS evidence manifest path '$manifestPath' escapes exact sibling root '$evidenceRoot'."
        }
        $canonicalRepositoryPath = [IO.Path]::GetRelativePath($root, $fullPath).Replace('\', '/')
        if ($manifestPath.Replace('\', '/') -cne $canonicalRepositoryPath) {
            throw "OS evidence manifest path '$manifestPath' is not normalized as '$canonicalRepositoryPath'."
        }
        if (-not $manifestByPath.TryAdd($fullPath, $entry)) {
            throw "OS evidence manifest duplicates normalized path '$manifestPath'."
        }
        Assert-NoReparsePath $fullPath "OS evidence manifest path '$manifestPath'"
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "OS evidence manifest file '$manifestPath' is missing."
        }
        $length = Get-StrictInt64 $entry 'length' "OS evidence manifest '$manifestPath'" 0 [int64]::MaxValue
        $hash = Get-StrictString $entry 'sha256' "OS evidence manifest '$manifestPath'"
        $actual = Get-Item -LiteralPath $fullPath -Force
        if ([int64]$actual.Length -ne $length -or $hash -notmatch '^[0-9A-F]{64}$' `
            -or $hash -cne (Get-FileSha256 $fullPath)) {
            throw "OS evidence manifest length/hash mismatch for '$manifestPath'."
        }
    }

    $allItems = @(Get-ChildItem -LiteralPath $evidenceRoot -Recurse -Force)
    $reparseItems = @($allItems | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($reparseItems.Count -ne 0) {
        throw "OS evidence tree contains reparse entries: $($reparseItems.FullName -join ', ')."
    }
    $actualFiles = @($allItems | Where-Object { -not $_.PSIsContainer })
    $actualPaths = [Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($file in $actualFiles) {
        if (-not $actualPaths.Add([IO.Path]::GetFullPath($file.FullName))) {
            throw "OS evidence tree enumerated duplicate path '$($file.FullName)'."
        }
    }
    if ($actualPaths.Count -ne $manifestByPath.Count `
        -or @($actualPaths | Where-Object { -not $manifestByPath.ContainsKey($_) }).Count -ne 0 `
        -or @($manifestByPath.Keys | Where-Object { -not $actualPaths.Contains($_) }).Count -ne 0) {
        throw "OS evidence '$reportPath' manifest is not the exact actual file set (manifest=$($manifestByPath.Count), actual=$($actualPaths.Count))."
    }

    foreach ($row in @($Report.results)) {
        $statusMember = $row.PSObject.Properties['status']
        $nameMember = $row.PSObject.Properties['name']
        $requiresCommand = $null -ne $statusMember `
            -and [string]$statusMember.Value -ceq 'pass' `
            -and $null -ne $nameMember `
            -and [string]$nameMember.Value -cnotlike 'self-test-*'
        $hasCommandEvidence = Assert-OsResultCommandEvidence $row "OS result row '$($row.name)'"
        if ($requiresCommand -and -not $hasCommandEvidence) {
            throw "OS passing executable row '$($row.name)' cannot omit its command and manifested logs."
        }
        if (-not $hasCommandEvidence) {
            continue
        }
        foreach ($stream in @('stdout', 'stderr')) {
            $streamPath = Get-StrictString $row $stream "OS executable row '$($row.name)'"
            if ([IO.Path]::IsPathFullyQualified($streamPath)) {
                throw "OS executable row '$($row.name)' $stream path must be repository relative."
            }
            $fullStreamPath = [IO.Path]::GetFullPath((Join-Path $root $streamPath))
            if (-not $manifestByPath.ContainsKey($fullStreamPath)) {
                throw "OS executable row '$($row.name)' $stream log is not uniquely present in the exact evidence manifest."
            }
            $declaredHash = Get-StrictString $row ($stream + 'Sha256') "OS executable row '$($row.name)'"
            if ($declaredHash -cne [string]$manifestByPath[$fullStreamPath].sha256 `
                -or $declaredHash -cne (Get-FileSha256 $fullStreamPath)) {
                throw "OS executable row '$($row.name)' $stream hash is not bound to its manifested log."
            }
        }
    }

    $canonicalRows = [Collections.Generic.List[string]]::new()
    foreach ($path in $manifestByPath.Keys) {
        $entry = $manifestByPath[$path]
        $canonicalRows.Add(
            ([IO.Path]::GetRelativePath($evidenceRoot, $path).Replace('\', '/') +
            "|$($entry.length)|$($entry.sha256)"))
    }
    $canonicalRows.Sort([StringComparer]::Ordinal)
    return [pscustomobject][ordered]@{
        reportPath = [IO.Path]::GetRelativePath($root, $reportPath)
        reportSha256 = Get-FileSha256 $reportPath
        evidenceRoot = [IO.Path]::GetRelativePath($root, $evidenceRoot)
        evidenceFileCount = $manifestByPath.Count
        evidenceTreeSha256 = Get-StringSha256 (@($canonicalRows) -join "`n")
    }
}

function Assert-AcceptedOsEvidenceStable {
    [int]$validated = 0
    foreach ($accepted in $acceptedOsEvidence) {
        $reportPath = if ([IO.Path]::IsPathFullyQualified([string]$accepted.reportPath)) {
            [IO.Path]::GetFullPath([string]$accepted.reportPath)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $root ([string]$accepted.reportPath)))
        }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 30
        $current = Assert-OsEvidenceTree $report $reportPath
        if ([string]$current.reportSha256 -cne [string]$accepted.reportSha256 `
            -or [string]$current.evidenceTreeSha256 -cne [string]$accepted.evidenceTreeSha256 `
            -or [int64]$current.evidenceFileCount -ne [int64]$accepted.evidenceFileCount) {
            throw "Accepted OS evidence '$reportPath' changed before qualification completion."
        }
        $validated++
    }
    return $validated
}

function Invoke-OsEvidenceManifestVerifierSelfTest {
    $reportPath = Join-Path $runRoot 'os-evidence-manifest-self-test.json'
    $treeRoot = Join-Path $runRoot 'os-evidence-manifest-self-test.evidence'
    New-Item -ItemType Directory -Path $treeRoot | Out-Null
    $stdout = Join-Path $treeRoot 'synthetic.stdout.log'
    $stderr = Join-Path $treeRoot 'synthetic.stderr.log'
    $structural = Join-Path $treeRoot 'structural.json'
    [IO.File]::WriteAllText($stdout, "synthetic stdout`n")
    [IO.File]::WriteAllText($stderr, "synthetic stderr`n")
    [IO.File]::WriteAllText($structural, "{}`n")
    [IO.File]::WriteAllText($reportPath, "{}`n")
    $manifest = @(@($stdout, $stderr, $structural) | ForEach-Object {
        $file = Get-Item -LiteralPath $_
        [pscustomobject][ordered]@{
            path = [IO.Path]::GetRelativePath($root, $file.FullName)
            length = $file.Length
            sha256 = Get-FileSha256 $file.FullName
        }
    })
    $report = [pscustomobject][ordered]@{
        results = @(
            [pscustomobject][ordered]@{
                name = 'synthetic'; status = 'pass'; required = $true; command = 'synthetic command'
                stdout = [IO.Path]::GetRelativePath($root, $stdout)
                stderr = [IO.Path]::GetRelativePath($root, $stderr)
                stdoutSha256 = Get-FileSha256 $stdout
                stderrSha256 = Get-FileSha256 $stderr
            },
            [pscustomobject][ordered]@{
                name = 'self-test-synthetic'; status = 'pass'; required = $true; command = $null
                stdout = $null; stderr = $null; stdoutSha256 = $null; stderrSha256 = $null
            },
            [pscustomobject][ordered]@{
                name = 'optional-synthetic'; status = 'not-qualified'; required = $false; command = $null
                stdout = $null; stderr = $null; stdoutSha256 = $null; stderrSha256 = $null
            })
        evidenceManifest = $manifest
    }
    [void](Assert-OsEvidenceTree $report $reportPath)
    [int]$assertions = 3

    $invalidFields = @(
        @{ Name = 'empty structural command'; Row = 1; Property = 'command'; Value = '' },
        @{ Name = 'whitespace optional command'; Row = 2; Property = 'command'; Value = '   ' },
        @{ Name = 'empty executable stdout'; Row = 0; Property = 'stdout'; Value = '' },
        @{ Name = 'whitespace executable stderr'; Row = 0; Property = 'stderr'; Value = "`t" })
    foreach ($case in $invalidFields) {
        $invalidReport = $report | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $invalidReport.results[$case.Row].($case.Property) = $case.Value
        $rejected = $false
        try { [void](Assert-OsEvidenceTree $invalidReport $reportPath) } catch { $rejected = $true }
        if (-not $rejected) {
            throw "OS evidence verifier self-test accepted $($case.Name) pseudo-evidence."
        }
        $assertions++
    }

    $extra = Join-Path $treeRoot 'unexpected.log'
    [IO.File]::WriteAllText($extra, 'unexpected')
    $rejected = $false
    try { [void](Assert-OsEvidenceTree $report $reportPath) } catch { $rejected = $true }
    Remove-Item -LiteralPath $extra
    if (-not $rejected) { throw 'OS evidence verifier self-test accepted an unmanifested extra file.' }
    $assertions++

    [IO.File]::WriteAllText($stdout, 'tampered')
    $rejected = $false
    try { [void](Assert-OsEvidenceTree $report $reportPath) } catch { $rejected = $true }
    [IO.File]::WriteAllText($stdout, "synthetic stdout`n")
    if (-not $rejected) { throw 'OS evidence verifier self-test accepted a content/hash mismatch.' }
    $assertions++

    $tamperedReport = $report | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $tamperedReport.results[0].stdoutSha256 = ('A' * 64)
    $rejected = $false
    try { [void](Assert-OsEvidenceTree $tamperedReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) { throw 'OS evidence verifier self-test accepted an unbound command log digest.' }
    $assertions++

    $commandlessReport = $report | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $commandlessReport.results[0].command = $null
    $commandlessReport.results[0].name = 'clean'
    $commandlessReport.results[0].stdout = $null
    $commandlessReport.results[0].stderr = $null
    $commandlessReport.results[0].stdoutSha256 = $null
    $commandlessReport.results[0].stderrSha256 = $null
    $commandlessReport.evidenceManifest = @($commandlessReport.evidenceManifest | Where-Object {
        [string]$_.path -notin @(
            [IO.Path]::GetRelativePath($root, $stdout),
            [IO.Path]::GetRelativePath($root, $stderr))
    })
    Remove-Item -LiteralPath $stdout, $stderr
    $rejected = $false
    try { [void](Assert-OsEvidenceTree $commandlessReport $reportPath) } catch { $rejected = $true }
    [IO.File]::WriteAllText($stdout, "synthetic stdout`n")
    [IO.File]::WriteAllText($stderr, "synthetic stderr`n")
    if (-not $rejected) { throw 'OS evidence verifier self-test accepted a passing clean row with command/logs removed consistently.' }
    $assertions++

    $escapedReport = $report | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $escapedReport.evidenceManifest[0].path = 'specs/009-lock-free-publish-read/qualification-config.json'
    $rejected = $false
    try { [void](Assert-OsEvidenceTree $escapedReport $reportPath) } catch { $rejected = $true }
    if (-not $rejected) { throw 'OS evidence verifier self-test accepted an out-of-root manifest path.' }
    $assertions++
    return $assertions
}

function Read-OsEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = if ([IO.Path]::IsPathFullyQualified($Path)) {
        [IO.Path]::GetFullPath($Path)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $root $Path))
    }
    $pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $fullPath.StartsWith($allowedRoot, $pathComparison)) {
        throw "OS evidence '$fullPath' must remain below '$allowedRoot'."
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "OS evidence '$fullPath' does not exist."
    }
    $report = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ((Get-StrictInt64 $report 'schemaVersion' "OS evidence '$fullPath'" 3 3) -ne 3 `
        -or (Get-StrictBoolean $report 'validationOnly' "OS evidence '$fullPath'")) {
        throw "OS evidence '$fullPath' is not an executable exact schema-v3 result."
    }
    $platformValue = Get-RequiredPropertyValue $report 'platform' "OS evidence '$fullPath'"
    $architectureValue = Get-RequiredPropertyValue $report 'architecture' "OS evidence '$fullPath'"
    if ($platformValue -isnot [string] -or $architectureValue -isnot [string]) {
        throw "OS evidence '$fullPath' platform and architecture must be strings."
    }
    $platformId = ($platformValue + '-' + $architectureValue).ToLowerInvariant()
    return [pscustomobject]@{
        path = $fullPath
        relativePath = [IO.Path]::GetRelativePath($root, $fullPath)
        sha256 = Get-FileSha256 $fullPath
        report = $report
        platformId = $platformId
    }
}

function Assert-OsEvidenceSet {
    param(
        [Parameter(Mandatory)][string]$CurrentPath,
        [string[]]$AdditionalPaths = @())

    $paths = @($CurrentPath) + @($AdditionalPaths)
    $evidence = [Collections.Generic.List[object]]::new()
    foreach ($path in $paths) {
        try {
            $item = Read-OsEvidence $path
        }
        catch {
            Add-EvidenceResult 'dual-platform-os-evidence' 'not-qualified' 'invalid-os-evidence' @($_.Exception.Message)
            $notQualifiedReasons.Add("dual-platform-os-evidence: $($_.Exception.Message)")
            return
        }

        $report = $item.report
        try {
            $treeSnapshot = Assert-OsEvidenceTree $report $item.path
            if ([string]$treeSnapshot.reportSha256 -cne [string]$item.sha256) {
                throw "OS evidence '$($item.path)' changed while its exact evidence tree was being validated."
            }
        }
        catch {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'not-qualified' 'invalid-evidence-tree' @(
                $_.Exception.Message) @($item.relativePath)
            $notQualifiedReasons.Add("os-evidence-$($item.platformId): invalid exact evidence tree")
            continue
        }
        try {
            Assert-OsReportProvenance $report $item.path
        }
        catch {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'not-qualified' 'source-provenance-mismatch' @(
                $_.Exception.Message) @($item.relativePath)
            $notQualifiedReasons.Add("os-evidence-$($item.platformId): mismatched source or binary provenance")
            continue
        }
        if ([string]$report.overallStatus -eq 'fail') {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'failed' 'os-validation-failed' @(
                "sha256=$($item.sha256)") @($item.relativePath)
            throw "OS validation failed for $($item.platformId); see '$($item.path)'."
        }
        $performanceArtifact = $null
        if ($Tier -eq 'release') {
            try {
                $performanceArtifact = Assert-ExactReleaseOsRows $report $item.platformId $item.path
            }
            catch {
                Add-EvidenceResult "os-evidence-$($item.platformId)" 'not-qualified' 'invalid-or-partial-os-evidence' @(
                    $_.Exception.Message) @($item.relativePath)
                $notQualifiedReasons.Add("os-evidence-$($item.platformId): invalid or partial command=all evidence")
                continue
            }
        }
        if ([string]$report.configuration -ne 'Release') {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'not-qualified' 'non-release-os-evidence' @(
                "configuration=$($report.configuration)") @($item.relativePath)
            $notQualifiedReasons.Add("os-evidence-$($item.platformId): configuration is not Release")
            continue
        }
        $qualifiedArchitecture = Get-StrictBoolean $report 'qualifiedArchitecture' "OS evidence '$($item.path)'"
        if ([string]$report.overallStatus -ne 'pass' -or -not $qualifiedArchitecture) {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'not-qualified' 'os-validation-not-qualified' @(
                "overallStatus=$($report.overallStatus)",
                "qualifiedArchitecture=$($report.qualifiedArchitecture)",
                "sha256=$($item.sha256)") @($item.relativePath)
            $notQualifiedReasons.Add("os-evidence-$($item.platformId): validation is not qualified")
            continue
        }
        if (@($report.results | Where-Object { $_.required -eq $true -and $_.status -ne 'pass' }).Count -ne 0) {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'failed' 'required-os-row-not-passed' @(
                "sha256=$($item.sha256)") @($item.relativePath)
            throw "OS evidence '$($item.path)' claims pass while a required row is not pass."
        }
        if (@($evidence | Where-Object platformId -eq $item.platformId).Count -ne 0) {
            Add-EvidenceResult "os-evidence-$($item.platformId)-duplicate" 'not-qualified' 'duplicate-platform-evidence' @(
                "path=$($item.relativePath)") @($item.relativePath)
            $notQualifiedReasons.Add("os-evidence-$($item.platformId): duplicate platform evidence")
            continue
        }
        if ((Get-FileSha256 $item.path) -cne [string]$treeSnapshot.reportSha256) {
            Add-EvidenceResult "os-evidence-$($item.platformId)" 'not-qualified' 'os-report-changed-during-validation' @(
                "path=$($item.relativePath)") @($item.relativePath)
            $notQualifiedReasons.Add("os-evidence-$($item.platformId): report changed during validation")
            continue
        }

        $evidence.Add($item)
        $acceptedOsEvidence.Add([pscustomobject][ordered]@{
            platformId = $item.platformId
            reportPath = $treeSnapshot.reportPath
            reportSha256 = $treeSnapshot.reportSha256
            evidenceRoot = $treeSnapshot.evidenceRoot
            evidenceFileCount = $treeSnapshot.evidenceFileCount
            evidenceTreeSha256 = $treeSnapshot.evidenceTreeSha256
        })
        $acceptedArtifacts = @($item.relativePath)
        if (-not [string]::IsNullOrWhiteSpace([string]$performanceArtifact)) {
            $acceptedArtifacts += [string]$performanceArtifact
        }
        Add-EvidenceResult "os-evidence-$($item.platformId)" 'passed' 'same-source-release-os-validation-pass' @(
            "sha256=$($item.sha256)",
            "commit=$($repositoryProvenance.commit)",
            "sourceManifestSha256=$($repositoryProvenance.sourceManifestSha256)",
            "evidenceFiles=$($treeSnapshot.evidenceFileCount)",
            "evidenceTreeSha256=$($treeSnapshot.evidenceTreeSha256)") $acceptedArtifacts
    }

    $requiredPlatforms = if ($Tier -eq 'release') {
        @($config.platforms | ForEach-Object { [string]$_ })
    }
    else {
        @($(if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { 'unsupported' }) + '-' +
            [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())
    }
    $passedPlatforms = @($evidence | ForEach-Object platformId | Sort-Object -Unique)
    $missing = @($requiredPlatforms | Where-Object { $_ -notin $passedPlatforms })
    if ($missing.Count -ne 0) {
        Add-EvidenceResult 'dual-platform-os-evidence' 'not-qualified' 'required-platform-evidence-missing' @(
            "required=$($requiredPlatforms -join ',')",
            "passed=$($passedPlatforms -join ',')",
            "missing=$($missing -join ',')") @($evidence.relativePath)
        $notQualifiedReasons.Add("dual-platform-os-evidence: missing $($missing -join ',')")
        return
    }
    Add-EvidenceResult 'dual-platform-os-evidence' 'passed' $(if ($Tier -eq 'release') { 'windows-and-linux-release-qualified' } else { 'current-platform-smoke-qualified' }) @(
        "platforms=$($passedPlatforms -join ',')",
        "commit=$($repositoryProvenance.commit)",
        "sourceManifestSha256=$($repositoryProvenance.sourceManifestSha256)") @($evidence.relativePath)
}

function Get-EvidenceManifest {
    $summaryPath = Join-Path $runRoot 'summary.json'
    return @(Get-ChildItem -LiteralPath $runRoot -Recurse -File | Where-Object {
        $_.FullName -ne $summaryPath
    } | Sort-Object FullName | ForEach-Object {
        [pscustomobject][ordered]@{
            path = [IO.Path]::GetRelativePath($root, $_.FullName)
            length = $_.Length
            sha256 = Get-FileSha256 $_.FullName
        }
    })
}

function Assert-Sc017Evidence {
    param([int64]$ExpectedRepetitions)

    $step = Get-StepResult 'directory-generation-stress'
    $stdout = Get-Content -LiteralPath (Join-Path $root $step.stdout) -Raw
    $expectedSeedHex = ([uint64](Get-StrictInt64 $config 'seed' 'qualification config' 0 [int32]::MaxValue)).ToString('X16')
    $transitionCount = Get-Sc017SourceTransitionCount
    $startPattern = 'SC017 start: seed=0x' + [regex]::Escape($expectedSeedHex) +
        '; configuredRepetitions=' + [regex]::Escape([string]$ExpectedRepetitions) +
        '; transitionCount=' + [regex]::Escape([string]$transitionCount) +
        '; distribution=quotient-plus-remainder\.'
    if ($stdout -notmatch $startPattern) {
        Fail-StepValidation 'directory-generation-stress' `
            "Missing exact SC017 start evidence for $ExpectedRepetitions repetitions across $transitionCount transitions."
    }

    $transitionMarkers = [regex]::Matches(
        $stdout,
        'SC017 transition=(?<name>[a-z0-9-]+); seed=0x[0-9A-F]{16}; repetitions=(?<repetitions>[1-9][0-9]*); result=pass\.')
    $uniqueNames = @($transitionMarkers | ForEach-Object { $_.Groups['name'].Value } | Sort-Object -Unique)
    [int64]$markerRepetitions = 0
    foreach ($marker in $transitionMarkers) {
        $markerRepetitions += [Convert]::ToInt64(
            $marker.Groups['repetitions'].Value,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($transitionMarkers.Count -ne $transitionCount `
        -or $uniqueNames.Count -ne $transitionCount `
        -or $markerRepetitions -ne $ExpectedRepetitions) {
        Fail-StepValidation 'directory-generation-stress' `
            "SC017 transition evidence must contain exactly $transitionCount unique passing markers whose repetitions sum to $ExpectedRepetitions; markers=$($transitionMarkers.Count), unique=$($uniqueNames.Count), sum=$markerRepetitions."
    }

    $pattern = 'SC017 complete: seed=0x' + [regex]::Escape($expectedSeedHex) + '; executedRepetitions=' +
        [regex]::Escape([string]$ExpectedRepetitions) +
        '; wrongGenerationMutations=0; corruption=0; falseMisses=0; leakedCapacity=0\.'
    if ($stdout -notmatch $pattern) {
        Fail-StepValidation 'directory-generation-stress' `
            "Missing exact SC017 completion evidence for $ExpectedRepetitions repetitions and zero failures/leaks."
    }

    Set-StepValidation 'directory-generation-stress' 'passed' 'sc017-qualified-count-and-correctness' @(
        "executedRepetitions=$ExpectedRepetitions",
        "transitionCount=$transitionCount",
        "uniqueTransitionMarkers=$($uniqueNames.Count)",
        "seed=0x$expectedSeedHex",
        'wrongGenerationMutations=0',
        'corruption=0',
        'falseMisses=0',
        'leakedCapacity=0')
}

function Assert-SuspensionEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $context = 'suspension report'
    $environment = Get-RequiredPropertyValue $report 'environment' $context
    foreach ($property in @(
        'operatingSystem', 'operatingSystemArchitecture', 'processArchitecture',
        'framework', 'runtimeVersion')) {
        [void](Get-StrictString $environment $property "$context.environment")
    }
    if ((Get-StrictString $environment 'processArchitecture' "$context.environment") -ne
        [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() `
        -or (Get-StrictString $environment 'operatingSystemArchitecture' "$context.environment") -ne
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() `
        -or (Get-StrictString $environment 'probeAssemblySha256' "$context.environment") -cne
        (Get-TestedAssemblyHash "benchmarks/SharedMemoryStore.SyncProbe/bin/$Configuration/net10.0/SharedMemoryStore.SyncProbe.dll")) {
        Fail-StepValidation 'participant-suspension' 'Suspension report architecture or probe assembly hash does not match the qualification build.'
    }
    [void](Get-StrictInt64 $environment 'logicalProcessorCount' "$context.environment" 1 [int32]::MaxValue)
    [void](Get-StrictInt64 $environment 'availableProcessorCount' "$context.environment" 1 [int32]::MaxValue)
    [void](Get-StrictInt64 $environment 'stopwatchFrequency' "$context.environment" 1 [int64]::MaxValue)
    $configuration = Get-RequiredPropertyValue $report 'configuration' $context
    $baselineSeconds = Get-StrictInt64 $configuration 'baselineWindowSeconds' "$context.configuration" 1 [int32]::MaxValue
    $pauseSeconds = Get-StrictInt64 $configuration 'suspendedWindowSeconds' "$context.configuration" 1 [int32]::MaxValue
    $warmupSeconds = Get-StrictInt64 $configuration 'warmupSeconds' "$context.configuration" 1 [int32]::MaxValue
    $minimumRatio = Get-StrictDouble $configuration 'minimumThroughputRatio' "$context.configuration" 0 1 -Positive
    $affinityRequested = Get-StrictBoolean $configuration 'affinityRequested' "$context.configuration"
    $affinityPolicy = Get-StrictString $configuration 'affinityPolicy' "$context.configuration"
    $comparisonMethod = Get-StrictString $configuration 'comparisonMethod' "$context.configuration"
    if ($baselineSeconds -ne (Get-StrictInt64 $selected 'suspensionBaselineSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or $pauseSeconds -ne (Get-StrictInt64 $selected 'suspensionPauseSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or $warmupSeconds -ne (Get-StrictInt64 $selected 'suspensionWarmupSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or $minimumRatio -ne (Get-StrictDouble $config 'suspensionMinimumHealthyThroughputRatio' 'qualification config' 0 1 -Positive) `
        -or -not $affinityRequested `
        -or $affinityPolicy -ne 'physical-core-first-then-siblings' `
        -or $comparisonMethod -ne 'Same persistent healthy process set is measured immediately before and while one external participant is blocked inside a production checkpoint.') {
        Fail-StepValidation 'participant-suspension' 'Suspension report configuration does not exactly match the selected tier/configuration.'
    }
    if ((Get-StrictInt64 $report 'schemaVersion' $context 1 1) -ne 1) {
        Fail-StepValidation 'participant-suspension' 'Suspension report schema must be exactly 1.'
    }
    $includedCheckpointCount = Get-StrictInt64 $configuration 'includedCheckpointCount' "$context.configuration" 1 [int32]::MaxValue
    $catalogCheckpointCount = Get-StrictInt64 $configuration 'catalogCheckpointCount' "$context.configuration" 1 [int32]::MaxValue
    $requiredWorkloads = @('distributed-key', 'mixed-churn')
    Assert-ExactStringSet 'suspension workloads' @($configuration.workloads) $requiredWorkloads
    Assert-ExactStringSet 'suspension excluded families' @($configuration.excludedFamilies) @('Participant', 'Disposal')
    $expectedCheckpointIds = @($canonicalSuspensionCheckpointIds)
    $expectedResultCount = $expectedCheckpointIds.Count * $requiredWorkloads.Count
    $reportedRequiredCount = Get-StrictInt64 $report 'requiredResultCount' $context 1 [int32]::MaxValue
    if ($includedCheckpointCount -ne $expectedCheckpointIds.Count `
        -or $catalogCheckpointCount -ne $checkpointCatalog.Count `
        -or $reportedRequiredCount -ne $expectedResultCount) {
        Fail-StepValidation 'participant-suspension' 'Suspension report schema/count is invalid.'
    }
    $resultsRows = @($report.results)
    if ($resultsRows.Count -ne $expectedResultCount) {
        Fail-StepValidation 'participant-suspension' 'Suspension report omitted required checkpoint/workload rows.'
    }
    $expectedPairs = foreach ($checkpointId in $expectedCheckpointIds) {
        foreach ($workload in $requiredWorkloads) {
            '{0:D3}|{1}' -f $checkpointId, $workload
        }
    }
    $actualPairs = foreach ($row in $resultsRows) {
        $checkpointId = Get-StrictInt64 $row 'checkpointId' 'suspension result row' 1 ([int]$checkpointCatalog[-1].id)
        $workload = Get-RequiredPropertyValue $row 'workload' "suspension checkpoint $checkpointId"
        if ($workload -isnot [string] -or $workload -notin $requiredWorkloads) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $checkpointId has invalid workload '$workload'."
        }
        '{0:D3}|{1}' -f $checkpointId, $workload
    }
    $expectedCanonicalPairs = @($expectedPairs | Sort-Object)
    $actualCanonicalPairs = @($actualPairs | Sort-Object)
    $pairDigest = Get-StringSha256 ($actualCanonicalPairs -join "`n")
    $expectedPairDigest = Get-StringSha256 ($expectedCanonicalPairs -join "`n")
    if (($actualCanonicalPairs -join "`n") -cne ($expectedCanonicalPairs -join "`n") `
        -or $pairDigest -ne $expectedPairDigest `
        -or @($actualPairs | Sort-Object -Unique).Count -ne $expectedResultCount) {
        Fail-StepValidation 'participant-suspension' "Suspension checkpoint/workload set differs from the exact canonical set; expectedDigest=$expectedPairDigest actualDigest=$pairDigest."
    }
    $checkpointGroups = @($resultsRows | Group-Object checkpointId)
    if ($checkpointGroups.Count -ne $includedCheckpointCount `
        -or @($checkpointGroups | Where-Object { $_.Count -ne $requiredWorkloads.Count }).Count -ne 0) {
        Fail-StepValidation 'participant-suspension' 'Every included steady-state checkpoint must have exactly one row for each required workload.'
    }
    foreach ($checkpointGroup in $checkpointGroups) {
        $workloads = @($checkpointGroup.Group.workload | Sort-Object -Unique)
        if (($workloads -join ',') -ne (($requiredWorkloads | Sort-Object) -join ',')) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($checkpointGroup.Name) is missing a required workload."
        }
    }
    foreach ($row in $resultsRows) {
        $rowContext = "checkpoint $($row.checkpointId)/$($row.workload)"
        $qualification = Get-StrictString $row 'qualification' $rowContext
        if ($qualification -cnotin @(
            'qualified-pass', 'smoke-pass', 'qualified-fail', 'smoke-fail',
            'not-qualified-capacity-pressure', 'not-qualified-affinity',
            'not-qualified-insufficient-processors')) {
            Fail-StepValidation 'participant-suspension' "$rowContext has unknown qualification '$qualification'."
        }
        $checkpointId = Get-StrictInt64 $row 'checkpointId' $rowContext 1 ([int]$checkpointCatalog[-1].id)
        $catalogEntry = $checkpointCatalog[[int]$checkpointId - 1]
        if ((Get-StrictString $row 'checkpointName' $rowContext) -cne [string]$catalogEntry.name `
            -or (Get-StrictString $row 'checkpointFamily' $rowContext) -cne [string]$catalogEntry.family `
            -or (Get-StrictString $row 'checkpointPosition' $rowContext) -cne [string]$catalogEntry.position `
            -or (Get-StrictString $row 'pauseClassification' $rowContext) -cne [string]$catalogEntry.pause `
            -or (Get-StrictString $row 'crashClassification' $rowContext) -cne [string]$catalogEntry.crash `
            -or (Get-StrictString $row 'raceClassification' $rowContext) -cne [string]$catalogEntry.race `
            -or (Get-StrictBoolean $row 'isPublicOrderingPoint' $rowContext) -ne [bool]$catalogEntry.isPublicOrderingPoint) {
            Fail-StepValidation 'participant-suspension' "$rowContext metadata does not match the source-derived checkpoint catalog."
        }
        [void](Get-StrictBoolean $row 'capacityPermits' $rowContext)
        [void](Get-StrictBoolean $row 'gatePassed' $rowContext)
        [void](Get-StrictBoolean $row 'pausedParticipantAffinityApplied' $rowContext)
        $expectedReaders = if ($row.workload -ceq 'distributed-key') { 6 } else { 12 }
        $expectedWriters = if ($row.workload -ceq 'distributed-key') { 0 } else { 2 }
        $expectedHealthy = $expectedReaders + $expectedWriters
        if ((Get-StrictInt64 $row 'readerProcessCount' $rowContext 0 [int32]::MaxValue) -ne $expectedReaders `
            -or (Get-StrictInt64 $row 'writerProcessCount' $rowContext 0 [int32]::MaxValue) -ne $expectedWriters `
            -or (Get-StrictInt64 $row 'healthyProcessCount' $rowContext 0 [int32]::MaxValue) -ne $expectedHealthy `
            -or (Get-StrictInt64 $row 'requiredProcessorCount' $rowContext 1 [int32]::MaxValue) -ne ($expectedHealthy + 1)) {
            Fail-StepValidation 'participant-suspension' "$rowContext process topology is not the contracted healthy set."
        }
        foreach ($property in @(
            'healthyAffinityAppliedCount', 'availableProcessorCount', 'agentSpillFirstBucket',
            'agentSpillSecondBucket', 'pausedParticipantProcessor', 'pausedParticipantProcessId',
            'pausedParticipantExitCode')) {
            [void](Get-StrictInt64 $row $property $rowContext ([int64]-1) [int32]::MaxValue)
        }
        [void](Get-StrictString $row 'pausedParticipantAffinityStrategy' $rowContext)
        $correctnessErrors = @(Get-RequiredPropertyValue $row 'correctnessErrors' $rowContext)
        foreach ($errorValue in $correctnessErrors) {
            if ($errorValue -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$errorValue)) {
                Fail-StepValidation 'participant-suspension' "$rowContext has an invalid correctness error entry."
            }
        }
        $rowBaselineSeconds = Get-StrictInt64 $row 'baselineWindowSeconds' $rowContext 1 [int32]::MaxValue
        $rowPauseSeconds = Get-StrictInt64 $row 'suspendedWindowSeconds' $rowContext 1 [int32]::MaxValue
        $rowMinimumRatio = Get-StrictDouble $row 'minimumThroughputRatio' $rowContext 0 1 -Positive
        $failureCount = Get-StrictInt64 $row 'correctnessFailureCount' $rowContext 0 [int64]::MaxValue
        if ($correctnessErrors.Count -ne $failureCount) {
            Fail-StepValidation 'participant-suspension' "$rowContext correctnessErrors count does not equal correctnessFailureCount."
        }
        foreach ($rateProperty in @(
            'baselineCompletedCyclesPerSecond', 'suspendedCompletedCyclesPerSecond',
            'baselineApiCallsPerSecond', 'suspendedApiCallsPerSecond', 'throughputRatio')) {
            [void](Get-StrictDouble $row $rateProperty $rowContext 0 [double]::MaxValue)
        }
        foreach ($countProperty in @(
            'baselineAttemptedCycles', 'baselineCompletedCycles', 'baselineApiCalls',
            'suspendedAttemptedCycles', 'suspendedCompletedCycles', 'suspendedApiCalls')) {
            [void](Get-StrictInt64 $row $countProperty $rowContext 0 [int64]::MaxValue)
        }
        if ($rowBaselineSeconds -ne $baselineSeconds `
            -or $rowPauseSeconds -ne $pauseSeconds `
            -or $rowMinimumRatio -ne $minimumRatio `
            -or $failureCount -ne 0) {
            Fail-StepValidation 'participant-suspension' "$rowContext used a different duration/gate or reported correctness failures."
        }
        foreach ($capacityName in @(
            'beforeBaselineCapacity', 'afterBaselineCapacity', 'beforeSuspendedCapacity',
            'afterSuspendedCapacity', 'afterResumeCapacity')) {
            $capacity = Get-RequiredPropertyValue $row $capacityName $rowContext
            foreach ($property in @(
                'freeSlotCount', 'publishedSlotCount', 'activeLeaseCount', 'activeReservationCount',
                'initializingSlotCount', 'reservedSlotCount', 'reclaimingSlotCount', 'freeLeaseCount',
                'claimingLeaseCount', 'recoveringLeaseCount', 'freeParticipantCount',
                'activeParticipantCount', 'registeringParticipantCount', 'closingParticipantCount',
                'recoveringParticipantCount', 'reclaimingParticipantCount', 'storeFullFailures',
                'leaseTableFullFailures', 'contentionBudgetExhaustionCount')) {
                [void](Get-StrictInt64 $capacity $property "$rowContext.$capacityName" 0 [int64]::MaxValue)
            }
        }
    }
    if (@($resultsRows | Where-Object { $_.qualification -like '*-fail' }).Count -ne 0) {
        Fail-StepValidation 'participant-suspension' 'At least one checkpoint had a correctness failure or fell below the 90% smoke/qualification gate.'
    }

    foreach ($row in @($resultsRows | Where-Object { $_.qualification -like '*-pass' })) {
        $rowContext = "checkpoint $($row.checkpointId)/$($row.workload)"
        $capacityPermits = Get-StrictBoolean $row 'capacityPermits' $rowContext
        $gatePassed = Get-StrictBoolean $row 'gatePassed' $rowContext
        if (-not $capacityPermits -or -not $gatePassed) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($row.checkpointId)/$($row.workload) claims pass without capacity and gate evidence."
        }
        $baselineRate = Get-StrictDouble $row 'baselineCompletedCyclesPerSecond' $rowContext 0 [double]::MaxValue -Positive
        $suspendedRate = Get-StrictDouble $row 'suspendedCompletedCyclesPerSecond' $rowContext 0 [double]::MaxValue -Positive
        $throughputRatio = Get-StrictDouble $row 'throughputRatio' $rowContext 0 [double]::MaxValue -Positive
        $rowMinimumRatio = Get-StrictDouble $row 'minimumThroughputRatio' $rowContext 0 1 -Positive
        $calculatedRatio = $suspendedRate / $baselineRate
        if (-not [double]::IsFinite($calculatedRatio) -or [Math]::Abs($calculatedRatio - $throughputRatio) -gt 0.000001) {
            Fail-StepValidation 'participant-suspension' "$rowContext has an invalid or inconsistent throughput denominator/ratio."
        }
        if ($row.qualification -eq 'qualified-pass' `
            -and $throughputRatio -lt $rowMinimumRatio) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($row.checkpointId)/$($row.workload) is below its declared ratio threshold."
        }
        $expectedHealthy = if ($row.workload -eq 'distributed-key') { 6 } elseif ($row.workload -eq 'mixed-churn') { 14 } else { -1 }
        if ((Get-StrictInt64 $row 'healthyProcessCount' $rowContext 0 [int32]::MaxValue) -ne $expectedHealthy `
            -or @($row.baselineWorkers).Count -ne $expectedHealthy `
            -or @($row.suspendedWorkers).Count -ne $expectedHealthy) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($row.checkpointId)/$($row.workload) did not measure the required healthy set."
        }
        foreach ($worker in @($row.baselineWorkers) + @($row.suspendedWorkers)) {
            $workerContext = "$rowContext worker $($worker.workerId)/$($worker.window)"
            $workerRole = Get-StrictString $worker 'role' $workerContext
            $workerWindow = Get-StrictString $worker 'window' $workerContext
            if ($workerRole -cnotin @('reader', 'writer') `
                -or $workerWindow -cnotin @('baseline', 'suspended')) {
                Fail-StepValidation 'participant-suspension' "$workerContext has an invalid role or measurement window."
            }
            [void](Get-StrictInt64 $worker 'workerId' $workerContext 0 [int32]::MaxValue)
            [void](Get-StrictInt64 $worker 'processId' $workerContext 1 [int32]::MaxValue)
            [void](Get-StrictInt64 $worker 'assignedProcessor' $workerContext 0 [int32]::MaxValue)
            [void](Get-StrictInt64 $worker 'attemptedCycles' $workerContext 0 [int64]::MaxValue)
            [void](Get-StrictInt64 $worker 'completedCycles' $workerContext 0 [int64]::MaxValue)
            [void](Get-StrictInt64 $worker 'apiCalls' $workerContext 0 [int64]::MaxValue)
            if ((Get-StrictInt64 $worker 'failures' $workerContext 0 [int64]::MaxValue) -ne 0 `
                -or -not (Get-StrictBoolean $worker 'affinityApplied' $workerContext)) {
                Fail-StepValidation 'participant-suspension' "$workerContext has failures or lacks affinity."
            }
            [void](Get-StrictDouble $worker 'elapsedSeconds' $workerContext 0 [double]::MaxValue -Positive)
            [void](Get-StrictDouble $worker 'completedCyclesPerSecond' $workerContext 0 [double]::MaxValue)
            [void](Get-StrictDouble $worker 'apiCallsPerSecond' $workerContext 0 [double]::MaxValue)
            [void](Get-StrictString $worker 'affinityStrategy' $workerContext)
            foreach ($entry in (Get-RequiredPropertyValue $worker 'statusHistogram' $workerContext).PSObject.Properties) {
                if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -lt 0) {
                    Fail-StepValidation 'participant-suspension' "$workerContext status '$($entry.Name)' is not a nonnegative integer."
                }
            }
        }
        foreach ($capacityName in @('afterBaselineCapacity', 'beforeSuspendedCapacity', 'afterSuspendedCapacity')) {
            $capacity = Get-RequiredPropertyValue $row $capacityName $rowContext
            $freeSlots = Get-StrictInt64 $capacity 'freeSlotCount' "$rowContext.$capacityName" 0 [int32]::MaxValue
            $freeLeases = Get-StrictInt64 $capacity 'freeLeaseCount' "$rowContext.$capacityName" 0 [int32]::MaxValue
            $freeParticipants = Get-StrictInt64 $capacity 'freeParticipantCount' "$rowContext.$capacityName" 0 [int32]::MaxValue
            $storeFullFailures = Get-StrictInt64 $capacity 'storeFullFailures' "$rowContext.$capacityName" 0 [int64]::MaxValue
            $leaseFullFailures = Get-StrictInt64 $capacity 'leaseTableFullFailures' "$rowContext.$capacityName" 0 [int64]::MaxValue
            if ($freeSlots -lt 32 -or $freeLeases -lt ($expectedHealthy + 1) `
                -or $freeParticipants -lt 1 -or $storeFullFailures -ne 0 -or $leaseFullFailures -ne 0) {
                Fail-StepValidation 'participant-suspension' "$rowContext fails the ordinary capacity gate at $capacityName."
            }
        }
        $baselineSet = @($row.baselineWorkers | ForEach-Object { "$($_.role):$($_.workerId):$($_.processId)" } | Sort-Object)
        $suspendedSet = @($row.suspendedWorkers | ForEach-Object { "$($_.role):$($_.workerId):$($_.processId)" } | Sort-Object)
        if (($baselineSet -join ',') -ne ($suspendedSet -join ',')) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($row.checkpointId)/$($row.workload) changed the healthy process set between windows."
        }
        $pausedPid = Get-StrictInt64 $row 'pausedParticipantProcessId' $rowContext 1 [int32]::MaxValue
        if (@($row.baselineWorkers | Where-Object { $_.processId -eq $pausedPid }).Count -ne 0 `
            -or @($row.suspendedWorkers | Where-Object { $_.processId -eq $pausedPid }).Count -ne 0) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($row.checkpointId)/$($row.workload) included the paused actor in healthy throughput."
        }
        if ((Get-StrictInt64 $row 'healthyAffinityAppliedCount' $rowContext 0 [int32]::MaxValue) -ne $expectedHealthy `
            -or -not (Get-StrictBoolean $row 'pausedParticipantAffinityApplied' $rowContext)) {
            Fail-StepValidation 'participant-suspension' "Checkpoint $($row.checkpointId)/$($row.workload) claims pass without complete affinity evidence."
        }
    }

    $qualifiedPassCount = Get-StrictInt64 $report 'qualifiedPassCount' $context 0 $expectedResultCount
    $smokePassCount = Get-StrictInt64 $report 'smokePassCount' $context 0 $expectedResultCount
    $failCount = Get-StrictInt64 $report 'failCount' $context 0 $expectedResultCount
    $notQualifiedCount = Get-StrictInt64 $report 'notQualifiedCount' $context 0 $expectedResultCount
    $allRequiredQualifiedAndPassed = Get-StrictBoolean $report 'allRequiredQualifiedAndPassed' $context
    $actualQualifiedPassCount = @($resultsRows | Where-Object { $_.qualification -ceq 'qualified-pass' }).Count
    $actualSmokePassCount = @($resultsRows | Where-Object { $_.qualification -ceq 'smoke-pass' }).Count
    $actualFailCount = @($resultsRows | Where-Object { $_.qualification -cin @('qualified-fail', 'smoke-fail') }).Count
    $actualNotQualifiedCount = @($resultsRows | Where-Object { $_.qualification -clike 'not-qualified-*' }).Count
    if (($qualifiedPassCount + $smokePassCount + $failCount + $notQualifiedCount) -ne $expectedResultCount `
        -or $qualifiedPassCount -ne $actualQualifiedPassCount `
        -or $smokePassCount -ne $actualSmokePassCount `
        -or $failCount -ne $actualFailCount `
        -or $notQualifiedCount -ne $actualNotQualifiedCount `
        -or $allRequiredQualifiedAndPassed -ne ($actualQualifiedPassCount -eq $expectedResultCount)) {
        Fail-StepValidation 'participant-suspension' 'Suspension aggregate counts/flag do not match the exact result rows.'
    }
    $notQualified = @($resultsRows | Where-Object { $_.qualification -like 'not-qualified-*' })
    if ($notQualified.Count -ne 0) {
        $reasons = @($notQualified | Group-Object qualification | ForEach-Object { "$($_.Name)=$($_.Count)" })
        Mark-StepNotQualified 'participant-suspension' ($reasons -join '; ') @(
            "qualifiedPassCount=$($report.qualifiedPassCount)",
            "requiredResultCount=$($report.requiredResultCount)")
        return
    }

    if ($Tier -eq 'release') {
        if ($pauseSeconds -lt 30 `
            -or $qualifiedPassCount -ne $expectedResultCount `
            -or -not $allRequiredQualifiedAndPassed) {
            Fail-StepValidation 'participant-suspension' 'Release SC005 requires every row to use a 30-second-or-longer pause and be qualified-pass.'
        }
        Set-StepValidation 'participant-suspension' 'passed' 'sc005-qualified' @(
            "results=$($report.requiredResultCount)",
            "minimumRatio=$($report.configuration.minimumThroughputRatio)",
            "baselineSeconds=$($report.configuration.baselineWindowSeconds)",
            "pauseSeconds=$($report.configuration.suspendedWindowSeconds)",
            "checkpointCount=$($expectedCheckpointIds.Count)",
            "pairDigest=$pairDigest")
        return
    }

    if ($pauseSeconds -ge 30 `
        -or $smokePassCount -ne $expectedResultCount) {
        Fail-StepValidation 'participant-suspension' 'PR/nightly SC005 smoke must cover every row as smoke-pass without claiming release qualification.'
    }
    Set-StepValidation 'participant-suspension' 'passed' 'sc005-short-duration-correctness-and-coverage-smoke' @(
        "results=$($report.requiredResultCount)",
        "minimumRatio=$($report.configuration.minimumThroughputRatio)",
        "baselineSeconds=$($report.configuration.baselineWindowSeconds)",
        "pauseSeconds=$($report.configuration.suspendedWindowSeconds)",
        "checkpointCount=$($expectedCheckpointIds.Count)",
        "pairDigest=$pairDigest")
}

function Get-ProbeSummaryRow {
    param($Report, [string]$Profile, [string]$Scenario, [int]$ProcessCount)

    $rows = @($Report.summary | Where-Object {
        $_.profile -ceq $Profile -and $_.scenario -ceq $Scenario -and [int]$_.processCount -eq $ProcessCount
    })
    if ($rows.Count -ne 1) {
        Fail-StepValidation 'sync-probe' "Missing unique probe summary row $Profile/$Scenario/$ProcessCount."
    }
    return $rows[0]
}

function Get-ExpectedProbeTuples {
    $scenarios = [ordered]@{}
    foreach ($property in $config.performanceMatrix.shortScenarios.PSObject.Properties) {
        $scenarios[$property.Name] = @($property.Value | ForEach-Object { [int]$_ })
    }
    if ($Tier -eq 'release') {
        foreach ($property in $config.performanceMatrix.releaseOnlyScenarios.PSObject.Properties) {
            $scenarios[$property.Name] = @($property.Value | ForEach-Object { [int]$_ })
        }
    }

    $tuples = [Collections.Generic.List[object]]::new()
    foreach ($scenario in $scenarios.GetEnumerator()) {
        $profiles = if ($scenario.Key -in @($config.performanceMatrix.lockFreeOnlyScenarios)) {
            @('LockFree')
        }
        else {
            @($config.performanceMatrix.profiles | ForEach-Object { [string]$_ })
        }
        foreach ($profile in $profiles) {
            foreach ($processCount in @($scenario.Value)) {
                $tuples.Add([pscustomobject]@{
                    profile = $profile
                    scenario = $scenario.Key
                    processCount = [int]$processCount
                })
            }
        }
    }
    return @($tuples)
}

function Assert-ExactProbeMatrix {
    param([Parameter(Mandatory)]$Report)

    $expectedTuples = @(Get-ExpectedProbeTuples)
    $expectedRunCount = $expectedTuples.Count * [int]$selected.performanceTrials
    if (@($Report.runs).Count -ne $expectedRunCount) {
        Fail-StepValidation 'sync-probe' "Performance report has $(@($Report.runs).Count) rows; expected exactly $expectedRunCount."
    }
    if (@($Report.summary).Count -ne $expectedTuples.Count) {
        Fail-StepValidation 'sync-probe' "Performance summary has $(@($Report.summary).Count) rows; expected exactly $($expectedTuples.Count)."
    }

    foreach ($tuple in $expectedTuples) {
        $summaryRows = @($Report.summary | Where-Object {
            $_.profile -ceq $tuple.profile `
                -and $_.scenario -ceq $tuple.scenario `
                -and [int]$_.processCount -eq $tuple.processCount
        })
        if ($summaryRows.Count -ne 1) {
            Fail-StepValidation 'sync-probe' "Expected one summary row for $($tuple.profile)/$($tuple.scenario)/$($tuple.processCount)."
        }
        foreach ($trial in (1..([int]$selected.performanceTrials))) {
            $runRows = @($Report.runs | Where-Object {
                $_.profile -ceq $tuple.profile `
                    -and $_.scenario -ceq $tuple.scenario `
                    -and [int]$_.processCount -eq $tuple.processCount `
                    -and [int]$_.trial -eq $trial
            })
            if ($runRows.Count -ne 1) {
                Fail-StepValidation 'sync-probe' "Expected one run for $($tuple.profile)/$($tuple.scenario)/$($tuple.processCount)/trial-$trial."
            }
        }
    }

    foreach ($run in @($Report.runs)) {
        if (@($expectedTuples | Where-Object {
            $_.profile -ceq $run.profile `
                -and $_.scenario -ceq $run.scenario `
                -and [int]$_.processCount -eq [int]$run.processCount
        }).Count -ne 1) {
            Fail-StepValidation 'sync-probe' "Unexpected performance row $($run.profile)/$($run.scenario)/$($run.processCount)/trial-$($run.trial)."
        }
    }
    return $expectedRunCount
}

function Assert-AtLeast {
    param([string]$Step, [string]$Gate, [double]$Actual, [double]$Minimum)
    if ($Actual -lt $Minimum) {
        Fail-StepValidation $Step "$Gate failed: actual=$Actual minimum=$Minimum."
    }
}

function Assert-AtMost {
    param([string]$Step, [string]$Gate, [double]$Actual, [double]$Maximum)
    if ($Actual -gt $Maximum) {
        Fail-StepValidation $Step "$Gate failed: actual=$Actual maximum=$Maximum."
    }
}

function Get-NumericArray {
    param(
        [Parameter(Mandatory)]$Values,
        [Parameter(Mandatory)][string]$Context,
        [int]$ExpectedCount = -1)

    $items = @($Values)
    if ($ExpectedCount -ge 0 -and $items.Count -ne $ExpectedCount) {
        throw "$Context contains $($items.Count) values; expected exactly $ExpectedCount."
    }
    $converted = foreach ($value in $items) {
        if (-not (Test-IsNumericValue $value)) {
            throw "$Context contains a nonnumeric value."
        }
        $number = [Convert]::ToDouble($value, [Globalization.CultureInfo]::InvariantCulture)
        if (-not [double]::IsFinite($number) -or $number -lt 0) {
            throw "$Context contains a negative or non-finite value."
        }
        $number
    }
    return @($converted)
}

function Get-Percentile99 {
    param([Parameter(Mandatory)][double[]]$Values)

    if ($Values.Count -eq 0) {
        return 0.0
    }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Clamp([int][Math]::Ceiling($sorted.Count * 0.99) - 1, 0, $sorted.Count - 1)
    return [double]$sorted[$index]
}

function Assert-StickyOverflowEvidence {
    param([Parameter(Mandatory)]$Run)

    $context = "SC018 trial $($Run.trial)"
    $sticky = Get-RequiredPropertyValue $Run 'stickyOverflow' $context
    $churnCycles = Get-StrictInt64 $sticky 'churnCycles' $context 10000 [int32]::MaxValue
    if ((Get-StrictInt64 $sticky 'slotCount' $context 4096 4096) -ne 4096 `
        -or (Get-StrictInt64 $sticky 'primaryBucketCount' $context 2048 2048) -ne 2048 `
        -or (Get-StrictInt64 $sticky 'exactBucketPairCollisionKeyCount' $context 17 17) -ne 17 `
        -or (Get-StrictInt64 $sticky 'collisionCandidatesExamined' $context 17 [int64]::MaxValue) -lt 17 `
        -or $churnCycles -lt 10000 `
        -or (Get-StrictInt64 $sticky 'missingSamplesPerWindow' $context 16384 [int32]::MaxValue) -lt 16384) {
        Fail-StepValidation 'sync-probe' "$context does not use the exact minimum 4,096-slot/10,000-cycle/16,384-sample contract."
    }
    $samplesPerWindow = [int](Get-StrictInt64 $sticky 'missingSamplesPerWindow' $context 16384 [int32]::MaxValue)
    if ((Get-StrictInt64 $Run 'cycles' $context 0 [int64]::MaxValue) -ne $churnCycles `
        -or (Get-StrictInt64 $Run 'sampleCount' $context 0 [int32]::MaxValue) -ne (2 * $samplesPerWindow)) {
        Fail-StepValidation 'sync-probe' "$context run totals do not match its raw churn/sample windows."
    }
    $early = @(Get-NumericArray $sticky.earlyMissingSamplesMicroseconds "$context early samples" $samplesPerWindow)
    $late = @(Get-NumericArray $sticky.lateMissingSamplesMicroseconds "$context late samples" $samplesPerWindow)
    $earlyP99 = Get-StrictDouble $Run 'earlyP99Microseconds' $context 0 [double]::MaxValue -Positive
    $lateP99 = Get-StrictDouble $Run 'lateP99Microseconds' $context 0 [double]::MaxValue -Positive
    $ratio = Get-StrictDouble $Run 'lateToEarlyP99Ratio' $context 0 2 -Positive
    $reportedGate = Get-StrictDouble $sticky 'lateToEarlyP99Gate' $context 2 2 -Positive
    $calculatedEarly = Get-Percentile99 $early
    $calculatedLate = Get-Percentile99 $late
    $calculatedRatio = $calculatedLate / $calculatedEarly
    $tolerance = 0.000000001
    if (-not [double]::IsFinite($calculatedRatio) `
        -or [Math]::Abs($calculatedEarly - $earlyP99) -gt $tolerance `
        -or [Math]::Abs($calculatedLate - $lateP99) -gt $tolerance `
        -or [Math]::Abs($calculatedRatio - $ratio) -gt $tolerance `
        -or $reportedGate -ne 2.0) {
        Fail-StepValidation 'sync-probe' "$context raw latency samples do not reproduce the reported p99 fields."
    }

    $beforeSpill = Get-StrictInt64 $sticky 'spilledBucketCountBeforeChurn' $context 0 [int32]::MaxValue
    $duringSpill = Get-StrictInt64 $sticky 'spilledBucketCountDuringChurn' $context 0 [int32]::MaxValue
    $duringOccupancy = Get-StrictInt64 $sticky 'overflowDirectoryOccupancyDuringChurn' $context 0 [int32]::MaxValue
    $afterFirstSpill = Get-StrictInt64 $sticky 'spilledBucketCountAfterFirstCleanup' $context 0 [int32]::MaxValue
    $afterFirstOccupancy = Get-StrictInt64 $sticky 'overflowDirectoryOccupancyAfterFirstCleanup' $context 0 [int32]::MaxValue
    $afterChurnSpill = Get-StrictInt64 $sticky 'spilledBucketCountAfterChurn' $context 0 [int32]::MaxValue
    $afterChurnOccupancy = Get-StrictInt64 $sticky 'overflowDirectoryOccupancyAfterChurn' $context 0 [int32]::MaxValue
    $scanBeforeCleanup = Get-StrictInt64 $sticky 'overflowScanCountBeforeFirstCleanup' $context 0 [int64]::MaxValue
    $scanAfterCleanup = Get-StrictInt64 $sticky 'overflowScanCountAfterFirstCleanup' $context 0 [int64]::MaxValue
    $maxAfterCleanup = Get-StrictInt64 $sticky 'maxObservedOverflowScanLengthAfterFirstCleanup' $context 0 [int32]::MaxValue
    $scanBeforeLate = Get-StrictInt64 $sticky 'overflowScanCountBeforeLateWindow' $context 0 [int64]::MaxValue
    $scanAfterLate = Get-StrictInt64 $sticky 'overflowScanCountAfterLateWindow' $context 0 [int64]::MaxValue
    $maxScan = Get-StrictInt64 $sticky 'maxObservedOverflowScanLength' $context 0 [int32]::MaxValue
    if ($beforeSpill -ne 0 -or $duringSpill -le 0 -or $duringOccupancy -le 0 `
        -or $afterFirstSpill -ne 0 -or $afterFirstOccupancy -ne 0 `
        -or $afterChurnSpill -ne 0 -or $afterChurnOccupancy -ne 0 `
        -or $scanAfterCleanup -le $scanBeforeCleanup -or $maxAfterCleanup -lt 4096 `
        -or $scanBeforeLate -lt $scanAfterCleanup -or $scanAfterLate -ne $scanBeforeLate `
        -or $maxScan -lt 4096 `
        -or -not (Get-StrictBoolean $sticky 'diagnosticsGatePassed' $context) `
        -or -not (Get-StrictBoolean $sticky 'latencyGatePassed' $context)) {
        Fail-StepValidation 'sync-probe' "$context lacks exact collision, spill, cleanup, scan, or latency-gate evidence."
    }
}

function Assert-ProbeEnvironmentEvidence {
    param([Parameter(Mandatory)]$Report)

    $environment = Get-RequiredPropertyValue $Report 'environment' 'sync probe report'
    $commit = Get-StrictString $environment 'repositoryCommit' 'sync probe environment'
    $workingTreeState = Get-StrictString $environment 'repositoryWorkingTreeState' 'sync probe environment'
    $storeAssemblyHash = Get-StrictString $environment 'sharedMemoryStoreAssemblySha256' 'sync probe environment'
    $probeAssemblyHash = Get-StrictString $environment 'probeAssemblySha256' 'sync probe environment'
    if ($commit -eq 'unknown' -or $commit -ne [string]$repositoryProvenance.commit `
        -or $workingTreeState -eq 'unknown' -or $workingTreeState -ne [string]$repositoryProvenance.workingTreeState) {
        Fail-StepValidation 'sync-probe' 'Performance report source provenance does not match the qualification run.'
    }
    $probePath = "benchmarks/SharedMemoryStore.SyncProbe/bin/$Configuration/net10.0/SharedMemoryStore.SyncProbe.dll"
    $storePath = "benchmarks/SharedMemoryStore.SyncProbe/bin/$Configuration/net10.0/SharedMemoryStore.dll"
    if ($probeAssemblyHash -cne (Get-TestedAssemblyHash $probePath) `
        -or $storeAssemblyHash -cne (Get-TestedAssemblyHash $storePath)) {
        Fail-StepValidation 'sync-probe' 'Performance report assembly hashes do not match the fresh tested-assembly manifest.'
    }

    foreach ($property in @(
        'operatingSystem', 'operatingSystemArchitecture', 'processArchitecture',
        'framework', 'runtimeVersion')) {
        [void](Get-StrictString $environment $property 'sync probe environment')
    }
    try {
        Assert-RequiredBenchmarkHardwareMetadata $environment 'sync probe environment'
    }
    catch {
        Fail-StepValidation 'sync-probe' $_.Exception.Message
    }
    if ((Get-StrictString $environment 'processArchitecture' 'sync probe environment') -ne
        [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() `
        -or (Get-StrictString $environment 'operatingSystemArchitecture' 'sync probe environment') -ne
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        Fail-StepValidation 'sync-probe' 'Performance report architecture does not match the qualification host.'
    }
    $logicalProcessorCount = Get-StrictInt64 $environment 'logicalProcessorCount' `
        'sync probe environment' 1 [int32]::MaxValue
    if ($logicalProcessorCount -ne [Environment]::ProcessorCount) {
        Fail-StepValidation 'sync-probe' 'Performance report logical processor count does not match the qualification host.'
    }
    [void](Get-StrictInt64 $environment 'stopwatchFrequency' 'sync probe environment' 1 [int64]::MaxValue)
    [void](Get-StrictBoolean $environment 'serverGarbageCollection' 'sync probe environment')
}

function Assert-ProbeConfigurationEvidence {
    param([Parameter(Mandatory)]$Report)

    $configuration = Get-RequiredPropertyValue $Report 'configuration' 'sync probe report'
    if ((Get-StrictString $configuration 'mode' 'sync probe configuration') -ne [string]$selected.performanceMode `
        -or (Get-StrictInt64 $configuration 'durationSeconds' 'sync probe configuration' 1 [int32]::MaxValue) -ne
            (Get-StrictInt64 $selected 'performanceDurationSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or (Get-StrictInt64 $configuration 'durationBoundGraceSeconds' 'sync probe configuration' 1 [int32]::MaxValue) -ne
            (Get-StrictInt64 $selected 'performanceDurationBoundGraceSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or (Get-StrictInt64 $configuration 'warmupSeconds' 'sync probe configuration' 0 [int32]::MaxValue) -ne
            (Get-StrictInt64 $selected 'performanceWarmupSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or (Get-StrictInt64 $configuration 'trials' 'sync probe configuration' 1 [int32]::MaxValue) -ne
            (Get-StrictInt64 $selected 'performanceTrials' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or (Get-StrictInt64 $configuration 'largeFrameBytes' 'sync probe configuration' 1 [int32]::MaxValue) -ne
            (Get-StrictInt64 $selected 'largeFrameBytes' "tier '$Tier'" 1 [int32]::MaxValue) `
        -or (Get-StrictInt64 $configuration 'largeFrames' 'sync probe configuration' 1 [int64]::MaxValue) -ne
            (Get-StrictInt64 $selected 'largeFrames' "tier '$Tier'" 1 [int64]::MaxValue) `
        -or (Get-StrictInt64 $configuration 'mixedOperationTarget' 'sync probe configuration' 0 [int64]::MaxValue) -ne
            (Get-StrictInt64 $selected 'mixedOperations' "tier '$Tier'" 1 [int64]::MaxValue) `
        -or -not (Get-StrictBoolean $configuration 'affinityRequested' 'sync probe configuration')) {
        Fail-StepValidation 'sync-probe' 'Performance report configuration does not exactly match the selected tier.'
    }

    foreach ($property in @(
        'readerKeyCount', 'readerPayloadBytes', 'brokerRotatingKeyCount',
        'mixedCollisionKeyCount', 'mixedPrimaryBucketCount', 'samplingInterval',
        'maxLatencySamplesPerWorker', 'brokerObserverSamplingInterval')) {
        [void](Get-StrictInt64 $configuration $property 'sync probe configuration' 1 [int32]::MaxValue)
    }
    [void](Assert-LinuxTinySyncTopology $configuration 'sync probe configuration')
    if ((Get-StrictInt64 $configuration 'warmupCycles' 'sync probe configuration' 0 0) -ne 0) {
        Fail-StepValidation 'sync-probe' 'Performance report must identify time-based warmup with warmupCycles=0.'
    }
    [void](Get-StrictString $configuration 'affinityPolicy' 'sync probe configuration')
    $legacySemantics = Get-StrictString $configuration 'legacyFullPayloadCopiesFieldSemantics' 'sync probe configuration'
    if ($legacySemantics -ne ('Retained for v3-v5 readers. Consult FullPayloadCopyCountIsInstrumented and ' +
        'FullPayloadCopyEvidenceKind before interpreting the value as a measured event count.')) {
        Fail-StepValidation 'sync-probe' 'Performance report does not declare the legacy copy field as non-authoritative.'
    }

    Assert-ExactStringSet 'performance report profiles' @($configuration.profiles) @($config.performanceMatrix.profiles)
    Assert-ExactStringSet 'performance report count-bound profiles' `
        @($configuration.countBoundProfiles) @($config.performanceMatrix.countBoundProfiles)
    $expectedScenarioCounts = [ordered]@{}
    foreach ($property in $config.performanceMatrix.shortScenarios.PSObject.Properties) {
        $expectedScenarioCounts[$property.Name] = @($property.Value | ForEach-Object { [int]$_ })
    }
    if ($Tier -eq 'release') {
        foreach ($property in $config.performanceMatrix.releaseOnlyScenarios.PSObject.Properties) {
            $expectedScenarioCounts[$property.Name] = @($property.Value | ForEach-Object { [int]$_ })
        }
    }
    Assert-ExactStringSet 'performance report scenarios' @($configuration.scenarios) @($expectedScenarioCounts.Keys)
    $actualScenarioCounts = Get-RequiredPropertyValue $configuration 'scenarioProcessCounts' 'sync probe configuration'
    Assert-ExactStringSet 'performance report scenario-count keys' @($actualScenarioCounts.PSObject.Properties.Name) @($expectedScenarioCounts.Keys)
    try {
        Assert-BenchmarkScenarioStoreDimensions `
            $configuration @($expectedScenarioCounts.Keys) 'sync probe configuration'
    }
    catch {
        Fail-StepValidation 'sync-probe' $_.Exception.Message
    }
    foreach ($entry in $expectedScenarioCounts.GetEnumerator()) {
        $actual = @($actualScenarioCounts.($entry.Key) | ForEach-Object {
            if (-not (Test-IsIntegerNumber $_)) {
                Fail-StepValidation 'sync-probe' "Scenario '$($entry.Key)' has a noninteger process count."
            }
            [Convert]::ToInt32($_, [Globalization.CultureInfo]::InvariantCulture)
        })
        if (($actual -join ',') -cne (@($entry.Value) -join ',')) {
            Fail-StepValidation 'sync-probe' "Scenario '$($entry.Key)' process-count matrix is not exact."
        }
    }

    if ($Tier -eq 'release') {
        if ((Get-StrictInt64 $configuration 'stickyOverflowSlotCount' 'sync probe configuration' 4096 4096) -ne 4096 `
            -or (Get-StrictInt64 $configuration 'stickyOverflowChurnCycles' 'sync probe configuration' 10000 [int32]::MaxValue) -lt 10000 `
            -or (Get-StrictInt64 $configuration 'stickyOverflowMissingSamplesPerWindow' 'sync probe configuration' 16384 [int32]::MaxValue) -lt 16384) {
            Fail-StepValidation 'sync-probe' 'Release SC018 configuration is below its exact minimum workload.'
        }
    }
}

function Assert-ProbeDerivedValue {
    param(
        [Parameter(Mandatory)][string]$Context,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][double]$Actual,
        [Parameter(Mandatory)][double]$Expected)

    $tolerance = [Math]::Max(0.000000001, [Math]::Abs($Expected) * 0.000000000001)
    if (-not [double]::IsFinite($Actual) -or -not [double]::IsFinite($Expected) `
        -or [Math]::Abs($Actual - $Expected) -gt $tolerance) {
        Fail-StepValidation 'sync-probe' "$Context.$Property is not reproducible from its raw counters."
    }
}

function Assert-ProbeRunCompletionEvidence {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Context,
        [Parameter(Mandatory)][int64]$DurationSeconds,
        [Parameter(Mandatory)][int64]$MixedOperationTarget,
        [Parameter(Mandatory)][int64]$LargeFrameTarget,
        [Parameter(Mandatory)][string[]]$CountBoundProfiles)

    $profile = Get-StrictString $Run 'profile' $Context
    $scenario = Get-StrictString $Run 'scenario' $Context
    if ($profile -cnotin @('Legacy', 'LockFree')) {
        throw "$Context has noncanonical profile identity '$profile'."
    }
    if ($scenario -cnotin @(
        'acquire-release', 'publish-remove', 'same-key-read', 'distributed-key-read',
        'broker-directed', 'mixed-churn', 'large-ingest', 'sticky-overflow-miss')) {
        throw "$Context has noncanonical scenario identity '$scenario'."
    }
    $operationTarget = Get-StrictInt64 $Run 'operationTarget' $Context 0 [int64]::MaxValue
    $frameTarget = Get-StrictInt64 $Run 'frameTarget' $Context 0 [int64]::MaxValue
    if ($operationTarget -gt 0 -and $frameTarget -gt 0) {
        throw "$Context cannot be both operation-bound and frame-bound."
    }

    $isCountBoundProfile = $profile -cin $CountBoundProfiles
    [int64]$expectedOperationTarget = if ($scenario -ceq 'mixed-churn' -and $isCountBoundProfile) {
        $MixedOperationTarget
    }
    else { 0 }
    [int64]$expectedFrameTarget = if ($scenario -ceq 'large-ingest' -and $isCountBoundProfile) {
        $LargeFrameTarget
    }
    else { 0 }
    if ($operationTarget -ne $expectedOperationTarget -or $frameTarget -ne $expectedFrameTarget) {
        throw "$Context does not match the configured profile-aware count-bound policy."
    }

    if ($scenario -ceq 'sticky-overflow-miss') {
        return
    }

    $operations = Get-StrictInt64 $Run 'operations' $Context 1 [int64]::MaxValue
    $frames = Get-StrictInt64 $Run 'frames' $Context 0 [int64]::MaxValue
    $measuredSeconds = Get-StrictDouble $Run 'measuredSeconds' $Context 0 [double]::MaxValue -Positive
    $earlySamples = Get-StrictInt64 $Run 'earlySampleCount' $Context 1 [int64]::MaxValue
    $lateSamples = Get-StrictInt64 $Run 'lateSampleCount' $Context 1 [int64]::MaxValue
    [void]$earlySamples
    [void]$lateSamples
    if ($operationTarget -gt 0) {
        if ($operations -lt $operationTarget) {
            throw "$Context completed $operations operations below its target $operationTarget."
        }
        return
    }
    if ($frameTarget -gt 0) {
        if ($frames -lt $frameTarget) {
            throw "$Context completed $frames frames below its target $frameTarget."
        }
        return
    }
    if ($measuredSeconds -lt $DurationSeconds) {
        throw "$Context duration-bound row measured $measuredSeconds seconds below $DurationSeconds."
    }
}

function Invoke-ProbeCompletionVerifierSelfTest {
    [int64]$duration = Get-StrictInt64 $selected 'performanceDurationSeconds' "tier '$Tier'" 1 [int32]::MaxValue
    [int64]$mixedTarget = Get-StrictInt64 $selected 'mixedOperations' "tier '$Tier'" 1 [int64]::MaxValue
    [int64]$frameTarget = Get-StrictInt64 $selected 'largeFrames' "tier '$Tier'" 1 [int64]::MaxValue
    [string[]]$countBoundProfiles = @($config.performanceMatrix.countBoundProfiles | ForEach-Object { [string]$_ })
    $legacy = [pscustomobject][ordered]@{
        profile = 'Legacy'; scenario = 'mixed-churn'; operationTarget = 0; frameTarget = 0
        operations = 1; frames = 0; measuredSeconds = [double]$duration
        earlySampleCount = 1; lateSampleCount = 1
    }
    $lockFreeMixed = [pscustomobject][ordered]@{
        profile = 'LockFree'; scenario = 'mixed-churn'; operationTarget = $mixedTarget; frameTarget = 0
        operations = $mixedTarget; frames = 0; measuredSeconds = 1.0
        earlySampleCount = 1; lateSampleCount = 1
    }
    $lockFreeLarge = [pscustomobject][ordered]@{
        profile = 'LockFree'; scenario = 'large-ingest'; operationTarget = 0; frameTarget = $frameTarget
        operations = 1; frames = $frameTarget; measuredSeconds = 1.0
        earlySampleCount = 1; lateSampleCount = 1
    }
    foreach ($row in @($legacy, $lockFreeMixed, $lockFreeLarge)) {
        Assert-ProbeRunCompletionEvidence $row 'completion verifier self-test' `
            $duration $mixedTarget $frameTarget $countBoundProfiles
    }
    [int]$assertions = 3

    $mutations = @(
        @{ message = 'one-below configured mixed target'; apply = {
            param($row) $row.operationTarget = $mixedTarget - 1
        }; source = $lockFreeMixed },
        @{ message = 'one-below completed mixed operations'; apply = {
            param($row) $row.operations = $mixedTarget - 1
        }; source = $lockFreeMixed },
        @{ message = 'Legacy count target inheritance'; apply = {
            param($row) $row.operationTarget = $mixedTarget
        }; source = $legacy },
        @{ message = 'profile target swap'; apply = {
            param($row) $row.operationTarget = 0; $row.frameTarget = $frameTarget
        }; source = $lockFreeMixed },
        @{ message = 'simultaneous operation and frame targets'; apply = {
            param($row) $row.frameTarget = $frameTarget
        }; source = $lockFreeMixed },
        @{ message = 'short duration row'; apply = {
            param($row) $row.measuredSeconds = [double]$duration - 0.001
        }; source = $legacy },
        @{ message = 'missing operation target metadata'; apply = {
            param($row) $row.PSObject.Properties.Remove('operationTarget')
        }; source = $lockFreeMixed },
        @{ message = 'one-below completed frame target'; apply = {
            param($row) $row.frames = $frameTarget - 1
        }; source = $lockFreeLarge },
        @{ message = 'noncanonical profile casing'; apply = {
            param($row) $row.profile = 'lockfree'
        }; source = $lockFreeMixed },
        @{ message = 'noncanonical scenario casing'; apply = {
            param($row) $row.scenario = 'Mixed-Churn'
        }; source = $lockFreeMixed }
    )
    foreach ($mutation in $mutations) {
        $row = $mutation.source | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        & $mutation.apply $row
        $rejected = $false
        try {
            Assert-ProbeRunCompletionEvidence $row 'completion verifier negative self-test' `
                $duration $mixedTarget $frameTarget $countBoundProfiles
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Probe completion verifier accepted $($mutation.message)."
        }
        $assertions++
    }
    return $assertions
}

function Assert-ProbeRowNumericEvidence {
    param([Parameter(Mandatory)]$Report)

    $logicalProcessorCount = Get-StrictInt64 $Report.environment 'logicalProcessorCount' `
        'sync probe environment' 1 [int32]::MaxValue
    foreach ($run in @($Report.runs)) {
        $context = "probe run $($run.profile)/$($run.scenario)/$($run.processCount)/trial-$($run.trial)"
        foreach ($property in @('profile', 'scenario', 'qualification', 'fullPayloadCopyEvidenceKind', 'allocationMeasurementScope')) {
            [void](Get-StrictString $run $property $context)
        }
        $allowedQualifications = if ($run.scenario -ceq 'sticky-overflow-miss') {
            @(
                'qualification-passed-versioned-overflow-cleanup',
                'qualification-failed-overflow-diagnostics',
                'qualification-failed-versioned-overflow-latency')
        }
        else {
            @(
                'qualification-measurement', 'smoke-only',
                'smoke-only-insufficient-warmup', 'not-qualified-oversubscribed')
        }
        if ([string]$run.qualification -cnotin $allowedQualifications) {
            Fail-StepValidation 'sync-probe' "$context has unknown qualification '$($run.qualification)'."
        }
        foreach ($property in @('processCount', 'trial')) {
            [void](Get-StrictInt64 $run $property $context 1 [int32]::MaxValue)
        }
        foreach ($property in @(
            'readerProcessCount', 'publisherProcessCount', 'observerProcessCount', 'cycles',
            'operations', 'frames', 'bytesWritten', 'bytesRead', 'fullPayloadCopies',
            'measuredThreadAllocatedBytes', 'producerStoreOperationAllocatedBytes', 'failures',
            'sampleCount', 'earlySampleCount', 'lateSampleCount', 'affinityAppliedCount',
            'operationTarget', 'frameTarget')) {
            [void](Get-StrictInt64 $run $property $context 0 [int64]::MaxValue)
        }
        try {
            Assert-ProbeRunCompletionEvidence $run $context `
                (Get-StrictInt64 $selected 'performanceDurationSeconds' "tier '$Tier'" 1 [int32]::MaxValue) `
                (Get-StrictInt64 $selected 'mixedOperations' "tier '$Tier'" 1 [int64]::MaxValue) `
                (Get-StrictInt64 $selected 'largeFrames' "tier '$Tier'" 1 [int64]::MaxValue) `
                ([string[]]@($config.performanceMatrix.countBoundProfiles | ForEach-Object { [string]$_ }))
        }
        catch {
            Fail-StepValidation 'sync-probe' $_.Exception.Message
        }
        foreach ($property in @(
            'apiCallsPerSecond', 'p50Microseconds', 'p95Microseconds', 'p99Microseconds',
            'maxMicroseconds', 'earlyP99Microseconds', 'lateP99Microseconds',
            'lateToEarlyP99Ratio', 'framesPerSecond', 'bytesPerSecond', 'fairnessIndex',
            'minWorkerApiCallsPerSecond', 'maxWorkerApiCallsPerSecond',
            'worstWorkerP99Microseconds')) {
            [void](Get-StrictDouble $run $property $context 0 [double]::MaxValue)
        }
        [void](Get-StrictDouble $run 'measuredSeconds' $context 0 [double]::MaxValue -Positive)
        [void](Get-StrictDouble $run 'wallSeconds' $context 0 [double]::MaxValue -Positive)
        $oversubscribed = Get-StrictBoolean $run 'oversubscribed' $context
        [void](Get-StrictBoolean $run 'fullPayloadCopyCountIsInstrumented' $context)
        $processCount = Get-StrictInt64 $run 'processCount' $context 1 [int32]::MaxValue
        $readerCount = Get-StrictInt64 $run 'readerProcessCount' $context 0 [int32]::MaxValue
        $publisherCount = Get-StrictInt64 $run 'publisherProcessCount' $context 0 [int32]::MaxValue
        $observerCount = Get-StrictInt64 $run 'observerProcessCount' $context 0 [int32]::MaxValue
        $expectedTopology = switch ([string]$run.scenario) {
            { $_ -in @('acquire-release', 'same-key-read', 'distributed-key-read') } {
                @($processCount, 0, 0, @('reader')); break
            }
            'publish-remove' { @(0, $processCount, 0, @('publisher')); break }
            'broker-directed' { @($processCount, 1, 1, @('broker-end-to-end')); break }
            'mixed-churn' { @($processCount, 2, 0, @('publisher', 'reader')); break }
            'large-ingest' { @($processCount, 1, 0, @('broker-end-to-end')); break }
            'sticky-overflow-miss' { @(0, 1, 0, @('missing-key')); break }
            default {
                Fail-StepValidation 'sync-probe' "$context has no contracted process topology."
            }
        }
        if ($readerCount -ne [int64]$expectedTopology[0] `
            -or $publisherCount -ne [int64]$expectedTopology[1] `
            -or $observerCount -ne [int64]$expectedTopology[2]) {
            Fail-StepValidation 'sync-probe' "$context process roles do not match the contracted scenario topology."
        }
        $participants = $readerCount + $publisherCount + $observerCount
        if ($participants -le 0 -or $oversubscribed -ne ($participants -gt $logicalProcessorCount)) {
            Fail-StepValidation 'sync-probe' "$context oversubscription flag does not match its raw participant topology."
        }

        $assignedProcessors = @(Get-RequiredPropertyValue $run 'assignedProcessors' $context)
        if ($assignedProcessors.Count -ne $participants) {
            Fail-StepValidation 'sync-probe' "$context has $($assignedProcessors.Count) processor assignments; expected exactly $participants."
        }
        foreach ($processor in $assignedProcessors) {
            if (-not (Test-IsIntegerNumber $processor) -or [int64]$processor -lt -1) {
                Fail-StepValidation 'sync-probe' "$context has a noninteger processor assignment."
            }
        }
        $affinityAppliedCount = Get-StrictInt64 $run 'affinityAppliedCount' $context 0 [int32]::MaxValue
        $assignedCount = @($assignedProcessors | Where-Object { [int64]$_ -ge 0 }).Count
        if ($affinityAppliedCount -ne $assignedCount) {
            Fail-StepValidation 'sync-probe' "$context affinityAppliedCount does not match raw assigned processors."
        }
        if (-not $oversubscribed -and $affinityAppliedCount -eq $participants `
            -and @($assignedProcessors | Sort-Object -Unique).Count -ne $participants) {
            Fail-StepValidation 'sync-probe' "$context affinity-qualified processor assignments are not unique."
        }
        $affinityStrategies = @(Get-RequiredPropertyValue $run 'affinityStrategies' $context)
        if ($affinityStrategies.Count -eq 0 `
            -or @($affinityStrategies | Sort-Object -Unique).Count -ne $affinityStrategies.Count) {
            Fail-StepValidation 'sync-probe' "$context affinity strategies are empty or duplicated."
        }
        foreach ($strategy in $affinityStrategies) {
            if ($strategy -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$strategy)) {
                Fail-StepValidation 'sync-probe' "$context has an invalid affinity strategy."
            }
        }

        $workerCycles = @(Get-RequiredPropertyValue $run 'workerCycles' $context)
        $expectedWorkerCount = if ($run.scenario -cin @('broker-directed', 'large-ingest')) {
            $readerCount
        }
        else {
            $participants
        }
        if ($workerCycles.Count -ne $expectedWorkerCount) {
            Fail-StepValidation 'sync-probe' "$context has $($workerCycles.Count) worker-cycle rows; expected exactly $expectedWorkerCount."
        }
        [decimal]$workerCycleTotal = 0
        foreach ($cycleCount in $workerCycles) {
            if (-not (Test-IsIntegerNumber $cycleCount) -or [int64]$cycleCount -lt 0) {
                Fail-StepValidation 'sync-probe' "$context has an invalid worker-cycle count."
            }
            $workerCycleTotal += [decimal]$cycleCount
        }
        if ($workerCycleTotal -ne [decimal](Get-StrictInt64 $run 'cycles' $context 0 [int64]::MaxValue)) {
            Fail-StepValidation 'sync-probe' "$context worker-cycle rows do not sum to the aggregate cycle count."
        }
        [decimal]$operationStatusTotal = 0
        foreach ($entry in (Get-RequiredPropertyValue $run 'statusHistogram' $context).PSObject.Properties) {
            if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -lt 0) {
                Fail-StepValidation 'sync-probe' "$context status '$($entry.Name)' is not a nonnegative integer."
            }
            if ($entry.Name -match '^(Acquire|Release|Publish|Remove|Reserve|Advance|Commit|RecoverLeases|RecoverReservations)\.') {
                $operationStatusTotal += [decimal]$entry.Value
            }
        }
        $operations = Get-StrictInt64 $run 'operations' $context 0 [int64]::MaxValue
        if ($operationStatusTotal -ne [decimal]$operations) {
            Fail-StepValidation 'sync-probe' "$context operation-status histogram does not sum to operations."
        }
        $sampleCount = Get-StrictInt64 $run 'sampleCount' $context 0 [int64]::MaxValue
        $earlySampleCount = Get-StrictInt64 $run 'earlySampleCount' $context 0 [int64]::MaxValue
        $lateSampleCount = Get-StrictInt64 $run 'lateSampleCount' $context 0 [int64]::MaxValue
        if ($sampleCount -ne ($earlySampleCount + $lateSampleCount)) {
            Fail-StepValidation 'sync-probe' "$context sampleCount does not equal its early/late sample windows."
        }
        $roleLatency = @(Get-RequiredPropertyValue $run 'roleLatency' $context)
        $actualRoles = [Collections.Generic.List[string]]::new()
        [int64]$roleSampleTotal = 0
        foreach ($role in $roleLatency) {
            $roleContext = "$context role $($role.role)"
            $actualRoles.Add((Get-StrictString $role 'role' $roleContext))
            $roleSampleTotal += Get-StrictInt64 $role 'sampleCount' $roleContext 0 [int32]::MaxValue
            foreach ($property in @('earlyP99Microseconds', 'lateP99Microseconds', 'lateToEarlyP99Ratio')) {
                [void](Get-StrictDouble $role $property $roleContext 0 [double]::MaxValue)
            }
        }
        Assert-ExactStringSet "$context role-latency identities" @($actualRoles) @($expectedTopology[3])
        if ($roleSampleTotal -ne (Get-StrictInt64 $run 'sampleCount' $context 0 [int64]::MaxValue)) {
            Fail-StepValidation 'sync-probe' "$context role-latency samples do not sum to sampleCount."
        }

        $measuredSeconds = Get-StrictDouble $run 'measuredSeconds' $context 0 [double]::MaxValue -Positive
        $frames = Get-StrictInt64 $run 'frames' $context 0 [int64]::MaxValue
        $bytesWritten = Get-StrictInt64 $run 'bytesWritten' $context 0 [int64]::MaxValue
        $bytesRead = Get-StrictInt64 $run 'bytesRead' $context 0 [int64]::MaxValue
        Assert-ProbeDerivedValue $context 'apiCallsPerSecond' `
            (Get-StrictDouble $run 'apiCallsPerSecond' $context 0 [double]::MaxValue) `
            ([double]$operations / $measuredSeconds)
        Assert-ProbeDerivedValue $context 'framesPerSecond' `
            (Get-StrictDouble $run 'framesPerSecond' $context 0 [double]::MaxValue) `
            ([double]$frames / $measuredSeconds)
        Assert-ProbeDerivedValue $context 'bytesPerSecond' `
            (Get-StrictDouble $run 'bytesPerSecond' $context 0 [double]::MaxValue) `
            ([double]([decimal]$bytesWritten + [decimal]$bytesRead) / $measuredSeconds)
        $earlyP99 = Get-StrictDouble $run 'earlyP99Microseconds' $context 0 [double]::MaxValue
        $lateP99 = Get-StrictDouble $run 'lateP99Microseconds' $context 0 [double]::MaxValue
        Assert-ProbeDerivedValue $context 'lateToEarlyP99Ratio' `
            (Get-StrictDouble $run 'lateToEarlyP99Ratio' $context 0 [double]::MaxValue) `
            $(if ($earlyP99 -eq 0) { 0.0 } else { $lateP99 / $earlyP99 })
        foreach ($role in $roleLatency) {
            $roleContext = "$context role $($role.role)"
            $roleEarly = Get-StrictDouble $role 'earlyP99Microseconds' $roleContext 0 [double]::MaxValue
            $roleLate = Get-StrictDouble $role 'lateP99Microseconds' $roleContext 0 [double]::MaxValue
            Assert-ProbeDerivedValue $roleContext 'lateToEarlyP99Ratio' `
                (Get-StrictDouble $role 'lateToEarlyP99Ratio' $roleContext 0 [double]::MaxValue) `
                $(if ($roleEarly -eq 0) { 0.0 } else { $roleLate / $roleEarly })
        }
        $fairness = Get-StrictDouble $run 'fairnessIndex' $context 0 1
        $minimumWorkerRate = Get-StrictDouble $run 'minWorkerApiCallsPerSecond' $context 0 [double]::MaxValue
        $maximumWorkerRate = Get-StrictDouble $run 'maxWorkerApiCallsPerSecond' $context 0 [double]::MaxValue
        if ($fairness -gt 1 -or $minimumWorkerRate -gt $maximumWorkerRate) {
            Fail-StepValidation 'sync-probe' "$context fairness or worker-rate bounds are internally inconsistent."
        }

        $stickyProperty = $run.PSObject.Properties['stickyOverflow']
        $hasSticky = $null -ne $stickyProperty -and $null -ne $stickyProperty.Value
        if (($run.scenario -ceq 'sticky-overflow-miss') -ne $hasSticky) {
            Fail-StepValidation 'sync-probe' "$context has inconsistent SC018 raw evidence presence."
        }
        if ($hasSticky) {
            Assert-StickyOverflowEvidence $run
        }
    }

    foreach ($summary in @($Report.summary)) {
        $context = "probe summary $($summary.profile)/$($summary.scenario)/$($summary.processCount)"
        foreach ($property in @('profile', 'scenario')) {
            [void](Get-StrictString $summary $property $context)
        }
        [void](Get-StrictInt64 $summary 'processCount' $context 1 [int32]::MaxValue)
        foreach ($property in @(
            'totalFrames', 'totalBytesWritten', 'totalFullPayloadCopies',
            'totalMeasuredThreadAllocatedBytes', 'totalFailures',
            'totalProducerStoreOperationAllocatedBytes')) {
            [void](Get-StrictInt64 $summary $property $context 0 [int64]::MaxValue)
        }
        foreach ($property in @(
            'medianApiCallsPerSecond', 'medianP50Microseconds', 'medianP95Microseconds',
            'medianP99Microseconds', 'medianMaxMicroseconds', 'medianEarlyP99Microseconds',
            'medianLateP99Microseconds', 'medianLateToEarlyP99Ratio', 'medianFramesPerSecond',
            'medianBytesPerSecond', 'medianFairnessIndex', 'medianWorstWorkerP99Microseconds')) {
            [void](Get-StrictDouble $summary $property $context 0 [double]::MaxValue)
        }
        [void](Get-StrictBoolean $summary 'fullPayloadCopyCountsAreInstrumented' $context)
        foreach ($value in @($summary.fullPayloadCopyEvidenceKinds) + @($summary.allocationMeasurementScopes)) {
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$value)) {
                Fail-StepValidation 'sync-probe' "$context has an invalid evidence-kind or allocation-scope value."
            }
        }
        foreach ($entry in (Get-RequiredPropertyValue $summary 'statusHistogram' $context).PSObject.Properties) {
            if (-not (Test-IsIntegerNumber $entry.Value) -or [int64]$entry.Value -lt 0) {
                Fail-StepValidation 'sync-probe' "$context status '$($entry.Name)' is not a nonnegative integer."
            }
        }
    }
}

function Get-MedianValue {
    param([Parameter(Mandatory)][double[]]$Values)

    if ($Values.Count -eq 0) {
        throw 'Cannot compute a median for an empty evidence set.'
    }
    $sorted = @($Values | Sort-Object)
    $middleIndex = [int][Math]::Floor($sorted.Count / 2.0)
    if (($sorted.Count % 2) -eq 0) {
        return ([double]$sorted[$middleIndex - 1] + [double]$sorted[$middleIndex]) / 2.0
    }
    return [double]$sorted[$middleIndex]
}

function Assert-ProbeSummaryConsistency {
    param([Parameter(Mandatory)]$Report)

    $medianProperties = [ordered]@{
        medianApiCallsPerSecond = 'apiCallsPerSecond'
        medianP50Microseconds = 'p50Microseconds'
        medianP95Microseconds = 'p95Microseconds'
        medianP99Microseconds = 'p99Microseconds'
        medianMaxMicroseconds = 'maxMicroseconds'
        medianEarlyP99Microseconds = 'earlyP99Microseconds'
        medianLateP99Microseconds = 'lateP99Microseconds'
        medianLateToEarlyP99Ratio = 'lateToEarlyP99Ratio'
        medianFramesPerSecond = 'framesPerSecond'
        medianBytesPerSecond = 'bytesPerSecond'
        medianFairnessIndex = 'fairnessIndex'
        medianWorstWorkerP99Microseconds = 'worstWorkerP99Microseconds'
    }
    $totalProperties = [ordered]@{
        totalFrames = 'frames'
        totalBytesWritten = 'bytesWritten'
        totalFullPayloadCopies = 'fullPayloadCopies'
        totalMeasuredThreadAllocatedBytes = 'measuredThreadAllocatedBytes'
        totalFailures = 'failures'
        totalProducerStoreOperationAllocatedBytes = 'producerStoreOperationAllocatedBytes'
    }

    foreach ($summary in @($Report.summary)) {
        $context = "probe summary $($summary.profile)/$($summary.scenario)/$($summary.processCount)"
        $runs = @($Report.runs | Where-Object {
            $_.profile -ceq $summary.profile -and $_.scenario -ceq $summary.scenario `
                -and $_.processCount -eq $summary.processCount
        })
        if ($runs.Count -ne [int]$selected.performanceTrials) {
            Fail-StepValidation 'sync-probe' "$context cannot be reproduced from the configured number of trials."
        }
        foreach ($entry in $medianProperties.GetEnumerator()) {
            $values = [double[]]@($runs | ForEach-Object {
                [double]($_.PSObject.Properties[[string]$entry.Value].Value)
            })
            $expected = Get-MedianValue $values
            $actual = [double]($summary.PSObject.Properties[[string]$entry.Key].Value)
            $tolerance = [Math]::Max(0.000000001, [Math]::Abs($expected) * 0.000000000001)
            if ([Math]::Abs($actual - $expected) -gt $tolerance) {
                Fail-StepValidation 'sync-probe' "$context.$($entry.Key) does not equal the raw-trial median."
            }
        }
        foreach ($entry in $totalProperties.GetEnumerator()) {
            [decimal]$expected = 0
            foreach ($run in $runs) {
                $expected += [decimal]($run.PSObject.Properties[[string]$entry.Value].Value)
            }
            if ([decimal]($summary.PSObject.Properties[[string]$entry.Key].Value) -ne $expected) {
                Fail-StepValidation 'sync-probe' "$context.$($entry.Key) does not equal the raw-trial total."
            }
        }
        $allCopyCountersInstrumented = @($runs | Where-Object { -not $_.fullPayloadCopyCountIsInstrumented }).Count -eq 0
        if ([bool]$summary.fullPayloadCopyCountsAreInstrumented -ne $allCopyCountersInstrumented) {
            Fail-StepValidation 'sync-probe' "$context copy-instrumentation aggregate is inconsistent."
        }
        Assert-ExactStringSet "$context copy evidence kinds" @($summary.fullPayloadCopyEvidenceKinds) `
            @($runs.fullPayloadCopyEvidenceKind | Sort-Object -Unique)
        Assert-ExactStringSet "$context allocation scopes" @($summary.allocationMeasurementScopes) `
            @($runs.allocationMeasurementScope | Sort-Object -Unique)

        $expectedHistogram = @{}
        foreach ($run in $runs) {
            foreach ($property in $run.statusHistogram.PSObject.Properties) {
                if (-not $expectedHistogram.ContainsKey($property.Name)) {
                    $expectedHistogram[$property.Name] = [int64]0
                }
                $expectedHistogram[$property.Name] = [int64]$expectedHistogram[$property.Name] + [int64]$property.Value
            }
        }
        $actualHistogram = $summary.statusHistogram
        Assert-ExactStringSet "$context status histogram keys" @($actualHistogram.PSObject.Properties.Name) @($expectedHistogram.Keys)
        foreach ($name in $expectedHistogram.Keys) {
            if ([int64]($actualHistogram.PSObject.Properties[$name].Value) -ne [int64]$expectedHistogram[$name]) {
                Fail-StepValidation 'sync-probe' "$context status histogram '$name' does not equal the raw-trial total."
            }
        }
    }
}

function Assert-SyncProbeEvidence {
    param([Parameter(Mandatory)][string]$Path)

    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ((Get-StrictInt64 $report 'schemaVersion' 'sync probe report' 8 8) -ne 8 `
        -or (Get-StrictInt64 $report 'minimumCompatibleSchemaVersion' 'sync probe report' 8 8) -ne 8 `
        -or (Get-StrictString $report 'schemaCompatibility' 'sync probe report') -notmatch 'Schema v8' `
        -or @($report.runs).Count -eq 0) {
        Fail-StepValidation 'sync-probe' 'Sync probe report must be exact executable schema v8 with nonempty runs.'
    }
    Assert-ProbeEnvironmentEvidence $report
    Assert-ProbeConfigurationEvidence $report
    Assert-ProbeRowNumericEvidence $report
    Assert-ProbeSummaryConsistency $report
    $expectedRunCount = Assert-ExactProbeMatrix $report
    if (@($report.runs | Where-Object { $_.failures -ne 0 }).Count -ne 0 `
        -or @($report.summary | Where-Object { $_.totalFailures -ne 0 }).Count -ne 0) {
        Fail-StepValidation 'sync-probe' 'A performance workload reported correctness failures.'
    }
    if (@($report.runs | Where-Object {
        $null -ne $_.stickyOverflow -and (-not $_.stickyOverflow.diagnosticsGatePassed -or -not $_.stickyOverflow.latencyGatePassed)
    }).Count -ne 0) {
        Fail-StepValidation 'sync-probe' 'SC018 diagnostics or latency gate failed.'
    }
    $notQualified = @($report.runs | Where-Object { $_.qualification -like 'not-qualified-*' })
    if ($notQualified.Count -ne 0) {
        Mark-StepNotQualified 'sync-probe' (($notQualified | Group-Object qualification | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join '; ')
        return
    }
    foreach ($run in @($report.runs)) {
        $participants = [int64]$run.readerProcessCount + [int64]$run.publisherProcessCount + [int64]$run.observerProcessCount
        if ([int64]$run.affinityAppliedCount -ne $participants -or $run.oversubscribed) {
            Mark-StepNotQualified 'sync-probe' "Incomplete affinity or oversubscription in $($run.profile)/$($run.scenario)/$($run.processCount)."
            return
        }
    }

    if ($Tier -ne 'release') {
        Set-StepValidation 'sync-probe' 'passed' 'short-performance-smoke-not-release-qualified' @(
            "durationSeconds=$($selected.performanceDurationSeconds)",
            "trials=$($selected.performanceTrials)",
            "exactRunRows=$expectedRunCount",
            'correctnessFailures=0')
        return
    }

    $ordinary = @($report.runs | Where-Object { $_.scenario -cne 'sticky-overflow-miss' })
    if (@($ordinary | Where-Object { $_.qualification -ne 'qualification-measurement' }).Count -ne 0) {
        Fail-StepValidation 'sync-probe' 'Release rows were smoke-only or lacked the release warmup/duration/frame target.'
    }
    foreach ($scenario in @('same-key-read', 'distributed-key-read')) {
        $one = Get-ProbeSummaryRow $report 'LockFree' $scenario 1
        $six = Get-ProbeSummaryRow $report 'LockFree' $scenario 6
        $twelve = Get-ProbeSummaryRow $report 'LockFree' $scenario 12
        $minimumSix = if ($scenario -eq 'same-key-read') { 4.0 } else { 4.5 }
        $minimumTwelve = if ($scenario -eq 'same-key-read') { 7.0 } else { 8.0 }
        $oneRate = Get-StrictDouble $one 'medianApiCallsPerSecond' "$scenario one-reader summary" 0 [double]::MaxValue -Positive
        $sixRate = Get-StrictDouble $six 'medianApiCallsPerSecond' "$scenario six-reader summary" 0 [double]::MaxValue -Positive
        $twelveRate = Get-StrictDouble $twelve 'medianApiCallsPerSecond' "$scenario twelve-reader summary" 0 [double]::MaxValue -Positive
        Assert-AtLeast 'sync-probe' "${scenario}-6-reader-scaling" `
            ($sixRate / $oneRate) $minimumSix
        Assert-AtLeast 'sync-probe' "${scenario}-12-reader-scaling" `
            ($twelveRate / $oneRate) $minimumTwelve
    }

    $brokerOne = Get-ProbeSummaryRow $report 'LockFree' 'broker-directed' 1
    $brokerTwelve = Get-ProbeSummaryRow $report 'LockFree' 'broker-directed' 12
    $brokerOneRate = Get-StrictDouble $brokerOne 'medianFramesPerSecond' 'broker one-reader summary' 0 [double]::MaxValue -Positive
    $brokerTwelveRate = Get-StrictDouble $brokerTwelve 'medianFramesPerSecond' 'broker twelve-reader summary' 0 [double]::MaxValue -Positive
    Assert-AtLeast 'sync-probe' 'broker-12-reader-publication-rate' `
        ($brokerTwelveRate / $brokerOneRate) 0.8

    foreach ($scenario in @('acquire-release', 'publish-remove')) {
        $legacyEight = Get-ProbeSummaryRow $report 'Legacy' $scenario 8
        $lockFreeEight = Get-ProbeSummaryRow $report 'LockFree' $scenario 8
        $legacyEightRate = Get-StrictDouble $legacyEight 'medianApiCallsPerSecond' "$scenario legacy/8p summary" 0 [double]::MaxValue -Positive
        $lockFreeEightRate = Get-StrictDouble $lockFreeEight 'medianApiCallsPerSecond' "$scenario lock-free/8p summary" 0 [double]::MaxValue -Positive
        $legacyEightP99 = Get-StrictDouble $legacyEight 'medianP99Microseconds' "$scenario legacy/8p summary" 0 [double]::MaxValue -Positive
        $lockFreeEightP99 = Get-StrictDouble $lockFreeEight 'medianP99Microseconds' "$scenario lock-free/8p summary" 0 [double]::MaxValue -Positive
        if ($IsWindows) {
            Assert-AtLeast 'sync-probe' "${scenario}-windows-throughput" `
                ($lockFreeEightRate / $legacyEightRate) 4.0
            Assert-AtMost 'sync-probe' "${scenario}-windows-p99" `
                ($lockFreeEightP99 / $legacyEightP99) 0.2
        }
        elseif ($IsLinux) {
            $legacyOne = Get-ProbeSummaryRow $report 'Legacy' $scenario 1
            $lockFreeOne = Get-ProbeSummaryRow $report 'LockFree' $scenario 1
            $legacyOneP99 = Get-StrictDouble $legacyOne 'medianP99Microseconds' "$scenario legacy/1p summary" 0 [double]::MaxValue -Positive
            $lockFreeOneP99 = Get-StrictDouble $lockFreeOne 'medianP99Microseconds' "$scenario lock-free/1p summary" 0 [double]::MaxValue -Positive
            Assert-AtMost 'sync-probe' "${scenario}-linux-uncontended-p99-ratio" `
                ($lockFreeOneP99 / $legacyOneP99) `
                (Get-StrictDouble $config.linuxTinyPerformance 'maximumUncontendedP99Ratio' `
                    'qualification config linuxTinyPerformance' 1 1)
            Assert-AtLeast 'sync-probe' "${scenario}-linux-8p-throughput-ratio" `
                ($lockFreeEightRate / $legacyEightRate) `
                (Get-StrictDouble $config.linuxTinyPerformance 'minimumThroughputRatio' `
                    'qualification config linuxTinyPerformance' 1 1)
            Assert-AtMost 'sync-probe' "${scenario}-linux-scale-p99-ratio" `
                ($lockFreeEightP99 / $lockFreeOneP99) `
                (Get-StrictDouble $config.linuxTinyPerformance 'maximumScaleP99Ratio' `
                    'qualification config linuxTinyPerformance' 3 3)
            Assert-AtMost 'sync-probe' "${scenario}-linux-8p-p99-us" `
                $lockFreeEightP99 `
                (Get-StrictDouble $config.linuxTinyPerformance 'maximumP99Microseconds' `
                    'qualification config linuxTinyPerformance' 10 10)
            foreach ($run in @($report.runs | Where-Object {
                [string]$_.profile -ceq 'LockFree' `
                    -and [string]$_.scenario -ceq $scenario `
                    -and [int64]$_.processCount -in @(1, 8)
            })) {
                Assert-AtMost 'sync-probe' "${scenario}-linux-max-stall-us-$($run.processCount)p-trial-$($run.trial)" `
                    (Get-StrictDouble $run 'maxMicroseconds' "$scenario lock-free/$($run.processCount)p trial-$($run.trial)" 0 [double]::MaxValue) `
                    (Get-StrictDouble $config.linuxTinyPerformance 'maximumStallMicroseconds' `
                        'qualification config linuxTinyPerformance' 10000 10000)
            }
        }
        else {
            Mark-StepNotQualified 'sync-probe' 'SC006 supports only Windows-x64 and Linux-x64.'
            return
        }
    }

    $zeroCopyRuns = @($report.runs | Where-Object {
        $_.profile -ceq 'LockFree' -and $_.scenario -cin @('broker-directed', 'large-ingest')
    })
    foreach ($run in $zeroCopyRuns) {
        $context = "$($run.scenario)/$($run.processCount)/trial-$($run.trial)"
        if ((Get-StrictBoolean $run 'fullPayloadCopyCountIsInstrumented' $context) `
            -or (Get-StrictInt64 $run 'producerStoreOperationAllocatedBytes' $context 0 0) -ne 0 `
            -or (Get-StrictString $run 'fullPayloadCopyEvidenceKind' $context) -ne
                'structural-direct-reservation-write-and-borrowed-lease-read' `
            -or (Get-StrictString $run 'allocationMeasurementScope' $context) -ne
                'dedicated-producer-and-broker-coordinator-thread-entire-measured-interval') {
            Fail-StepValidation 'sync-probe' "Producer allocation/structural-copy gate failed for $context."
        }
    }
    foreach ($run in @($zeroCopyRuns | Where-Object { $_.scenario -ceq 'large-ingest' })) {
        $frames = Get-StrictInt64 $run 'frames' "large-ingest trial $($run.trial)" 1 [int64]::MaxValue
        Assert-AtLeast 'sync-probe' "large-ingest-frames-$($run.processCount)-trial-$($run.trial)" $frames `
            (Get-StrictInt64 $selected 'largeFrames' "tier '$Tier'" 1 [int64]::MaxValue)
    }

    foreach ($run in @($report.runs | Where-Object { $_.profile -ceq 'LockFree' -and $_.scenario -ceq 'mixed-churn' })) {
        $context = "mixed-churn trial $($run.trial)"
        $operations = Get-StrictInt64 $run 'operations' $context 1 [int64]::MaxValue
        $earlyP99 = Get-StrictDouble $run 'earlyP99Microseconds' $context 0 [double]::MaxValue -Positive
        $lateP99 = Get-StrictDouble $run 'lateP99Microseconds' $context 0 [double]::MaxValue -Positive
        $ratio = Get-StrictDouble $run 'lateToEarlyP99Ratio' $context 0 [double]::MaxValue -Positive
        if ([Math]::Abs(($lateP99 / $earlyP99) - $ratio) -gt 0.000001) {
            Fail-StepValidation 'sync-probe' "$context has an inconsistent late/early p99 ratio."
        }
        Assert-AtLeast 'sync-probe' "mixed-churn-operations-trial-$($run.trial)" `
            $operations (Get-StrictInt64 $selected 'mixedOperations' "tier '$Tier'" 1 [int64]::MaxValue)
        Assert-AtMost 'sync-probe' "mixed-churn-late-early-p99-trial-$($run.trial)" `
            $ratio 2.0
    }

    Set-StepValidation 'sync-probe' 'passed' 'sc002-sc003-sc004-sc006-sc008-sc009-sc016-sc018-qualified' @(
        'correctnessFailures=0',
        'affinity=complete',
        'producerStoreOperationAllocatedBytes=0',
        'copyEvidence=structural-direct-reservation-write-and-borrowed-lease-read',
        'allQualifiedPerformanceThresholds=passed')
}

$commonTest = @('-c', $Configuration, '--nologo', '--no-build', '--no-restore')

try {
    Assert-QualificationConfiguration
    $churnQualificationContract = Get-ChurnQualificationTestContract $config
    Add-EvidenceResult 'configuration-contract' 'passed' 'schema-and-contract-values-validated' @(
        "schemaVersion=$($config.schemaVersion)",
        "tier=$Tier",
        "performanceMode=$($selected.performanceMode)",
        "sc017TransitionCount=$(Get-Sc017SourceTransitionCount)",
        "churnQualificationTest=$($churnQualificationContract.fullyQualifiedName)",
        "boundedOperationSlackMilliseconds=$($config.boundedOperationSlackMilliseconds)",
        "leakAssertions=$(@($config.requiredLeakAssertions).Count)") @([IO.Path]::GetRelativePath($root, $configPath))

    if ($ValidateOnly) {
        $sc017ConfigurationAssertions = Invoke-Sc017ConfigurationVerifierSelfTest
        Add-EvidenceResult 'sc017-configuration-verifier-self-test' 'passed' `
            'source-owned-transition-count-positive-and-negative-cases-passed' @(
                "assertions=$sc017ConfigurationAssertions",
                "sourceTransitionCount=$(Get-Sc017SourceTransitionCount)",
                'all PR/nightly/release tier counts accepted',
                'one-below-source transition count rejected')
        $churnVerifierAssertions = Invoke-ChurnQualificationVerifierSelfTest
        Add-EvidenceResult 'churn-qualification-verifier-self-test' 'passed' `
            'configured-exact-test-and-trx-cardinality-positive-and-negative-cases-passed' @(
                "assertions=$churnVerifierAssertions",
                "exactTest=$($churnQualificationContract.fullyQualifiedName)",
                'two identical semantically pinned mappings and one exact Passed row accepted',
                'distinct/missing/alternate-existing mappings rejected',
                'wrong namespace/class/method source bindings rejected',
                'missing/extra/wrong/duplicate/non-passed rows rejected',
                'XML parsing, wrapper failure recording, and result cleanup exercised') @(
                    [IO.Path]::GetRelativePath($root, (Join-Path $runRoot 'churn-trx-verifier-self-test/valid.trx')),
                    [IO.Path]::GetRelativePath($root, (Join-Path $runRoot 'churn-trx-verifier-self-test/extra.trx')))
        $markerParserAssertions = Invoke-ProductionRaceMarkerParserSelfTest
        Add-EvidenceResult 'production-race-marker-parser-self-test' 'passed' `
            'closed-marker-grammar-positive-and-negative-cases-passed' @(
                "assertions=$markerParserAssertions",
                'valid-eight-family-marker-set=accepted',
                'duplicate/wrong-count/wrong-seed markers=rejected',
                'invalid-recovery/disposal witnesses=rejected')
        $probeCompletionAssertions = Invoke-ProbeCompletionVerifierSelfTest
        Add-EvidenceResult 'sync-probe-completion-verifier-self-test' 'passed' `
            'profile-aware-duration-operation-frame-positive-and-negative-cases-passed' @(
                "assertions=$probeCompletionAssertions",
                'duration-bound Legacy plus count-bound LockFree mixed/large rows accepted',
                'below-target/config-swap/dual-target/short-duration/missing-target cases rejected')
        $osManifestAssertions = Invoke-OsEvidenceManifestVerifierSelfTest
        Add-EvidenceResult 'os-evidence-manifest-verifier-self-test' 'passed' `
            'exact-tree-positive-and-tamper-negative-cases-passed' @(
                "assertions=$osManifestAssertions",
                'exact manifest/file-set/log binding plus null structural/optional rows=accepted',
                'empty/whitespace pseudo-fields plus extra file/content hash/log hash/commandless passing clean row/out-of-root path=rejected') @(
                    [IO.Path]::GetRelativePath($root, (Join-Path $runRoot 'os-evidence-manifest-self-test.json')),
                    [IO.Path]::GetRelativePath($root, (Join-Path $runRoot 'os-evidence-manifest-self-test.evidence')))
        $linuxPerformanceAssertions = Invoke-LinuxTinyOsPerformanceVerifierSelfTest
        Add-EvidenceResult 'linux-tiny-os-performance-verifier-self-test' 'passed' `
            'exact-raw-matrix-positive-and-integrity-negative-cases-passed' @(
                "assertions=$linuxPerformanceAssertions",
                'exact 24-run/8-summary schema/config/tuple/correctness/affinity/median/schema2-metric evidence=accepted',
                'uncontended/throughput/scale/absolute p99 gate breaches plus over-limit stall/duplicate metric/impossible affinity/incoherent cycle/corruption evidence=rejected') @(
                    [IO.Path]::GetRelativePath($root, (Join-Path $runRoot 'linux-tiny-os-performance-self-test.json')),
                    [IO.Path]::GetRelativePath($root, (Join-Path $runRoot 'linux-tiny-os-performance-self-test.evidence/linux-tiny-performance.json')))
        $requiredInputs = @(
            'SharedMemoryStore.slnx',
            'scripts/validate-lock-free-os.ps1',
            'benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj',
            'tests/SharedMemoryStore.LinearizabilityTests/SharedMemoryStore.LinearizabilityTests.csproj',
            'tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj',
            $churnTestSourceRelativePath)
        $missing = @($requiredInputs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
        if ($missing.Count -ne 0) {
            throw "Qualification dry-run inputs are missing: $($missing -join ', ')."
        }
        Add-EvidenceResult 'validation-plan' 'passed' 'configuration-and-structure-only-not-qualification' @(
            'no build, tests, benchmarks, or OS validation executed',
            'exit code 0 validates orchestration structure only',
            "requiredInputs=$($requiredInputs.Count)") @($requiredInputs)
        $completionProvenance = Get-RepositoryProvenance
        Assert-ProvenanceStable $repositoryProvenance $completionProvenance
        $overallStatus = 'validation-only'
    }
    else {
        Invoke-BoundedStep 'dotnet-info' $dotnet @('--info')
        Set-StepValidation 'dotnet-info' 'passed' 'dotnet-toolchain-provisioned' @(
            "dotnet=$dotnet",
            "version=$(Invoke-TextCommand $dotnet @('--version'))")
        $preclean = Remove-SolutionProjectBuildOutputs (Join-Path $runRoot 'preclean.json')
        Invoke-BoundedStep 'clean' $dotnet @(
            'clean', 'SharedMemoryStore.slnx', '-c', $Configuration, '--nologo', '--disable-build-servers')
        (Get-StepResult 'clean') | Add-Member -NotePropertyName preclean -NotePropertyValue $preclean.Summary
        Set-StepValidation 'clean' 'passed' 'solution-output-cleaned-before-qualification-build' @(
            'SharedMemoryStore.slnx',
            "configuration=$Configuration",
            "solutionProjects=$($preclean.SolutionProjectCount)",
            "outputTargets=$($preclean.TargetCount)",
            "precleanedOutputDirectories=$($preclean.RemovedDirectoryCount)",
            "verifiedAbsent=$($preclean.VerifiedAbsentCount)",
            "precleanReport=$([IO.Path]::GetRelativePath($root, $preclean.ReportPath))",
            "precleanReportSha256=$($preclean.ReportSha256)")
        Invoke-BoundedStep 'restore' $dotnet @('restore', 'SharedMemoryStore.slnx', '--nologo', '--disable-build-servers')
        Set-StepValidation 'restore' 'passed' 'explicit-solution-restore-passed' @('SharedMemoryStore.slnx')
        Invoke-BoundedStep 'build' $dotnet @(
            'build', 'SharedMemoryStore.slnx', '-c', $Configuration, '--nologo', '--no-restore', '--disable-build-servers')
        $testedAssemblyManifest = @(Get-TestedAssemblyManifest)
        $testedAssemblyDigest = Get-StringSha256 (@($testedAssemblyManifest | ForEach-Object {
            "$($_.path)|$($_.length)|$($_.sha256)"
        }) -join "`n")
        Set-StepValidation 'build' 'passed' 'fresh-clean-solution-build-and-assembly-manifest' @(
            "assemblies=$($testedAssemblyManifest.Count)",
            "manifestSha256=$testedAssemblyDigest")

        $fullSuiteTrx = Join-Path $runRoot 'trx/full-test-suite'
        New-Item -ItemType Directory -Path $fullSuiteTrx | Out-Null
        Invoke-BoundedStep 'full-test-suite' $dotnet @(
            'test', 'SharedMemoryStore.slnx', '-c', $Configuration, '--nologo', '--no-build', '--no-restore',
            '--logger', 'trx', '--results-directory', $fullSuiteTrx)
        Assert-FullSuiteEvidence $fullSuiteTrx

        Invoke-BoundedStep 'unit-contract' $dotnet (@(
            'test', 'tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj') + $commonTest)
        Invoke-BoundedStep 'directory-generation-stress' $dotnet (@(
            'test', 'tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~SharedMemoryStore.UnitTests.LockFreeDirectoryGenerationStressTests.ConfiguredProductionStressFencesEveryDirectoryMutationTransitionAcrossSlotReuse',
            '--logger', 'console;verbosity=detailed')) @{
                SMS_DIRECTORY_GENERATION_STRESS_REPETITIONS = [int64]$selected.directoryGenerationStressRepetitions
                SMS_DIRECTORY_GENERATION_STRESS_SEED = [int]$config.seed
            }
        Assert-Sc017Evidence ([int64]$selected.directoryGenerationStressRepetitions)
        Invoke-BoundedStep 'contract' $dotnet (@(
            'test', 'tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj') + $commonTest)

        Invoke-BoundedStep 'reference-model-histories' $dotnet (@(
            'test', 'tests/SharedMemoryStore.LinearizabilityTests/SharedMemoryStore.LinearizabilityTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~LockFreeHistoryTests',
            '--logger', 'console;verbosity=detailed')) @{
                SMS_CHECKER_HISTORY_REPETITIONS = [int]$selected.checkerHistoryRepetitionsPerFamily
                SMS_LINEARIZABILITY_SEED = [int]$config.seed
            }
        Assert-FamilyCompletionMarkers `
            'reference-model-histories' `
            'completedCheckerInvocations' `
            ([int64]$selected.checkerHistoryRepetitionsPerFamily) `
            @($config.completionEvidence.referenceModelFamilies) `
            'configured-reference-model-count-proven'

        Invoke-BoundedStep 'production-generated-histories' $dotnet (@(
            'test', 'tests/SharedMemoryStore.LinearizabilityTests/SharedMemoryStore.LinearizabilityTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~ProductionGeneratedHistoryTests',
            '--logger', 'console;verbosity=detailed')) @{
                SMS_PRODUCTION_HISTORY_COUNT = [int]$selected.productionHistoryCountPerFamily
                SMS_PRODUCTION_HISTORY_SEED = [int]$config.seed
            }
        Assert-FamilyCompletionMarkers `
            'production-generated-histories' `
            'completedHistories' `
            ([int64]$selected.productionHistoryCountPerFamily) `
            @($config.completionEvidence.productionHistoryFamilies) `
            'configured-production-history-count-proven'

        Invoke-BoundedStep 'production-race-stress' $dotnet (@(
            'test', 'tests/SharedMemoryStore.LinearizabilityTests/SharedMemoryStore.LinearizabilityTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~ProductionRaceStressTests',
            '--logger', 'console;verbosity=detailed')) @{
                SMS_PRODUCTION_RACE_REPETITIONS = [int]$selected.productionRaceRepetitionsPerFamily
                SMS_PRODUCTION_RACE_SEED = [int]$config.seed
            }
        Assert-ProductionRaceEvidence `
            ([int64]$selected.productionRaceRepetitionsPerFamily) `
            @($config.completionEvidence.productionRaceFamilies)

        $waitTrx = Join-Path $runRoot 'trx/wait-policy'
        New-Item -ItemType Directory -Path $waitTrx | Out-Null
        Invoke-BoundedStep 'wait-policy' $dotnet (@(
            'test', 'tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~LockFreeWaitPolicyMatrixIntegrationTests|FullyQualifiedName~LockFreeNoOperationLockIntegrationTests',
            '--logger', 'trx', '--results-directory', $waitTrx))
        Assert-TrxStepEvidence 'wait-policy' $waitTrx -RequiredTestNameContains @(
            'LockFreeWaitPolicyMatrixIntegrationTests',
            'LockFreeNoOperationLockIntegrationTests') | Out-Null
        $waitResult = Get-StepResult 'wait-policy'
        $waitResult.qualification = 'bounded-wait-plus-250ms-and-no-operation-lock-proven'
        $waitResult.validation = @($waitResult.validation) + "completionAllowanceMilliseconds=$($config.boundedOperationSlackMilliseconds)"

        $churnTrx = Join-Path $runRoot 'trx/churn'
        New-Item -ItemType Directory -Path $churnTrx | Out-Null
        Invoke-BoundedStep 'churn' $dotnet (@(
            'test', 'tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj') + $commonTest + @(
            '--filter', "FullyQualifiedName=$($churnQualificationContract.fullyQualifiedName)",
            '--logger', 'trx', '--results-directory', $churnTrx)) @{
                SMS_LOCK_FREE_CHURN_CYCLES = [int64]$selected.churnCycles
            }
        Assert-ExactTrxStepEvidence 'churn' $churnTrx @(
            $churnQualificationContract.fullyQualifiedName) | Out-Null
        $churnResult = Get-StepResult 'churn'
        $churnResult.qualification = 'configured-churn-and-final-capacity-proof-passed'
        $churnResult.validation = @($churnResult.validation) + @(
            "configuredTotalCycles=$([int64]$selected.churnCycles)",
            "configuredTest=$($churnQualificationContract.fullyQualifiedName)")

        $recoveryTrx = Join-Path $runRoot 'trx/recovery'
        New-Item -ItemType Directory -Path $recoveryTrx | Out-Null
        Invoke-BoundedStep 'recovery' $dotnet (@(
            'test', 'tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~LockFreeCrashRecoveryIntegrationTests',
            '--logger', 'trx', '--results-directory', $recoveryTrx)) @{
                SMS_LOCK_FREE_RECOVERY_CASES = [int]$selected.recoveryCases
            }
        $recoveryPassed = @(Assert-TrxStepEvidence 'recovery' $recoveryTrx ([int]$selected.recoveryCases) @(
            'LockFreeCrashRecoveryIntegrationTests.EveryCanonicalCheckpointCanBeKilledRecoveredAndFilledToCapacity'))
        Assert-RecoveryCheckpointEvidence $recoveryPassed ([int64]$selected.recoveryCases)
        $recoveryResult = Get-StepResult 'recovery'
        $recoveryResult.qualification = 'configured-recovery-case-count-and-capacity-proof-passed'
        $recoveryResult.validation = @($recoveryResult.validation) + "recoveryCases=$([int]$selected.recoveryCases)"
        Assert-OwnerLeakEvidence @{
            churn = $churnTrx
            recovery = $recoveryTrx
        }

        Invoke-BoundedStep 'raw-visibility' $dotnet (@(
            'test', 'tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj') + $commonTest + @(
            '--filter', 'FullyQualifiedName~LockFreeRawVisibilityIntegrationTests'))
        Invoke-BoundedStep 'package-consumption' $powershell @(
            '-NoProfile', '-File', 'scripts/validate-package-consumption.ps1',
            '-Configuration', $Configuration)
        Set-StepValidation 'package-consumption' 'passed' 'isolated-cache-package-consumption-pass' @(
            'pack=passed',
            'legacy-consumer=passed',
            'lock-free-consumer=passed',
            'nuget-cache=isolated-per-run')

        if ($SkipOsValidation) {
            Add-EvidenceResult 'dual-platform-os-evidence' 'not-qualified' 'os-validation-skipped' @(
                'explicitly skipped by -SkipOsValidation')
            $notQualifiedReasons.Add('dual-platform-os-evidence: skipped by -SkipOsValidation')
        }
        else {
            $osOutput = Join-Path $runRoot 'os-validation.json'
            $osRelativeOutput = [IO.Path]::GetRelativePath($root, $osOutput)
            $osCommand = if ($Tier -eq 'release') { 'all' } else { 'self-test' }
            Invoke-BoundedStep `
                -Name 'os-validation-current' `
                -FileName $powershell `
                -Arguments @(
                    '-NoProfile', '-File', 'scripts/validate-lock-free-os.ps1',
                    '-Command', $osCommand,
                    '-Configuration', $Configuration,
                    '-StepTimeoutSeconds', [string]$selected.stepTimeoutSeconds,
                    '-OutputPath', $osRelativeOutput) `
                -AllowedExitCodes @(0, 2)
            $osStep = Get-StepResult 'os-validation-current'
            if ($osStep.exitCode -eq 2) {
                $osStep.status = 'not-qualified'
                $osStep.qualification = 'os-validator-returned-not-qualified'
            }
            else {
                $osStep.status = 'passed'
                $osStep.qualification = 'os-validator-executed'
            }
            $osStep.validation = @(
                "command=$osCommand",
                "report=$osRelativeOutput",
                "reportSha256=$(Get-FileSha256 $osOutput)")
            Assert-OsEvidenceSet $osOutput $AdditionalOsEvidence
        }

        if (-not $SkipPerformance) {
            $benchmarkOutput = Join-Path $runRoot 'sync-probe.json'
            Invoke-BoundedStep 'sync-probe' $dotnet @(
                'run', '-c', $Configuration, '--no-build', '--no-restore',
                '--project', 'benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj', '--',
                '--mode', [string]$selected.performanceMode,
                '--profile', 'both',
                '--count-bound-profiles', 'v2',
                '--warmup', [string]$selected.performanceWarmupSeconds,
                '--duration', [string]$selected.performanceDurationSeconds,
                '--duration-bound-grace', [string]$selected.performanceDurationBoundGraceSeconds,
                '--trials', [string]$selected.performanceTrials,
                '--mixed-operations', [string]$selected.mixedOperations,
                '--large-frames', [string]$selected.largeFrames,
                '--large-frame-bytes', [string]$selected.largeFrameBytes,
                '--repository-commit', [string]$repositoryProvenance.commit,
                '--repository-working-tree-state', [string]$repositoryProvenance.workingTreeState,
                '--output', $benchmarkOutput)
            Assert-SyncProbeEvidence $benchmarkOutput

            $suspensionOutput = Join-Path $runRoot 'participant-suspension.json'
            Invoke-BoundedStep 'participant-suspension' $dotnet @(
                'run', '-c', $Configuration, '--no-build', '--no-restore',
                '--project', 'benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj', '--',
                '--mode', 'suspension',
                '--profile', 'v2',
                '--warmup', [string]$selected.suspensionWarmupSeconds,
                '--suspension-baseline-seconds', [string]$selected.suspensionBaselineSeconds,
                '--suspension-pause-seconds', [string]$selected.suspensionPauseSeconds,
                '--suspension-minimum-ratio', [string]$config.suspensionMinimumHealthyThroughputRatio,
                '--output', $suspensionOutput)
            Assert-SuspensionEvidence $suspensionOutput
        }
        else {
            Add-EvidenceResult 'performance' 'not-qualified' 'performance-skipped' @(
                'explicitly skipped by -SkipPerformance')
            $notQualifiedReasons.Add('performance: skipped by -SkipPerformance')
        }

        $revalidatedOsEvidenceCount = Assert-AcceptedOsEvidenceStable
        $completionAssemblyManifest = @(Get-TestedAssemblyManifest)
        Assert-AssemblyManifestStable $testedAssemblyManifest $completionAssemblyManifest
        $completionProvenance = Get-RepositoryProvenance
        Assert-ProvenanceStable $repositoryProvenance $completionProvenance
        Add-EvidenceResult 'completion-integrity' 'passed' 'source-and-tested-assemblies-stable' @(
            "commit=$($completionProvenance.commit)",
            "sourceManifestSha256=$($completionProvenance.sourceManifestSha256)",
            "testedAssemblyManifestSha256=$testedAssemblyDigest",
            "acceptedOsEvidenceRevalidated=$revalidatedOsEvidenceCount")
        $overallStatus = if ($notQualifiedReasons.Count -eq 0) { 'passed' } else { 'not-qualified' }
    }
}
catch {
    $overallStatus = 'failed'
    $failureMessage = $_.Exception.Message
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
    $dotnetInfo = @($results | Where-Object name -eq 'dotnet-info' | Select-Object -First 1)
    $summary = [ordered]@{
        schemaVersion = 4
        tier = $Tier
        runId = $runId
        validationOnly = [bool]$ValidateOnly
        configuration = $Configuration
        platform = if ($IsWindows) { 'windows-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() } elseif ($IsLinux) { 'linux-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() } else { 'unsupported' }
        overallStatus = $overallStatus
        failure = $failureMessage
        notQualifiedReasons = $notQualifiedReasons
        startedUtc = $runStartedUtc
        completedUtc = [DateTimeOffset]::UtcNow
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
            powershellPath = $powershell
            gitPath = $git
            gitVersion = Invoke-TextCommand $git @('--version')
        }
        inputs = [ordered]@{
            script = [IO.Path]::GetRelativePath($root, $PSCommandPath)
            scriptSha256 = Get-FileSha256 $PSCommandPath
            configuration = [IO.Path]::GetRelativePath($root, $configPath)
            configurationSha256 = Get-FileSha256 $configPath
            solutionSha256 = Get-FileSha256 (Join-Path $root 'SharedMemoryStore.slnx')
        }
        seed = [int]$config.seed
        boundedOperationSlackMilliseconds = [int]$config.boundedOperationSlackMilliseconds
        requiredLeakAssertions = $config.requiredLeakAssertions
        settings = $selected
        acceptedOsEvidence = $acceptedOsEvidence
        results = $results
        evidenceManifest = Get-EvidenceManifest
    }
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json')
}

if ($overallStatus -eq 'validation-only') {
    Write-Host "Qualification orchestration validated without executing workloads. Evidence: $runRoot"
}
elseif ($overallStatus -eq 'not-qualified') {
    Write-Warning "Qualification '$Tier' completed but is NOT QUALIFIED. Evidence: $runRoot"
    exit 2
}
elseif ($Tier -eq 'release') {
    Write-Host "Release qualification gates passed. Evidence: $runRoot"
}
else {
    Write-Host "Qualification tier '$Tier' gates passed; short performance rows are smoke-only, not release qualification. Evidence: $runRoot"
}
