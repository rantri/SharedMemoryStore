[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$RunId,
    [string]$QualificationRoot = 'artifacts/010-qualification',
    [string]$CodeReviewPath = '',
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$qualificationRootPath = if ([IO.Path]::IsPathFullyQualified($QualificationRoot)) {
    [IO.Path]::GetFullPath($QualificationRoot)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $QualificationRoot))
}
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ($qualificationRootPath + [IO.Path]::DirectorySeparatorChar).StartsWith(
        $artifactsPrefix,
        $comparison)) {
    throw "QualificationRoot must remain below '$artifactsRoot'."
}

$contractRevision = 1
$tiers = @('pr', 'nightly', 'release')
$platforms = @('windows-x64', 'linux-x64')
$criterionMap = [ordered]@{
    'SC-001' = [pscustomobject]@{ row = 'ordered-pair-3x3-lifecycle'; evidence = @('dual-platform-os-evidence') }
    'SC-002' = [pscustomobject]@{ row = 'canonical-conformance-all-runtimes'; evidence = @('contract', 'raw-visibility') }
    'SC-003' = [pscustomobject]@{ row = 'mixed-runtime-million-operation-stress'; evidence = @('sync-probe', 'dual-platform-os-evidence') }
    'SC-004' = [pscustomobject]@{ row = 'cross-runtime-ten-thousand-crash-recovery'; evidence = @('recovery', 'dual-platform-os-evidence') }
    'SC-005' = [pscustomobject]@{ row = 'complete-transition-pause-reuse-million'; evidence = @('directory-generation-stress', 'participant-suspension', 'recovery') }
    'SC-006' = [pscustomobject]@{ row = 'finite-wait-envelope'; evidence = @('wait-policy') }
    'SC-007' = [pscustomobject]@{ row = 'dual-platform-zero-hot-os-locks'; evidence = @('dual-platform-os-evidence') }
    'SC-008' = [pscustomobject]@{ row = 'twelve-reader-pending-removal'; evidence = @('sync-probe', 'dual-platform-os-evidence') }
    'SC-009' = [pscustomobject]@{ row = 'all-distribution-clean-consumers'; evidence = @('package-consumption', 'dual-platform-os-evidence') }
    'SC-010' = [pscustomobject]@{ row = 'full-dual-platform-release-suite'; evidence = @('full-test-suite', 'dual-platform-os-evidence', 'completion-integrity') }
    'SC-011' = [pscustomobject]@{ row = 'one-current-protocol-static-inspection'; evidence = @('contract', 'package-consumption') }
    'SC-012' = [pscustomobject]@{ row = 'retired-store-migration-and-fail-closed'; evidence = @('contract', 'full-test-suite') }
    'SC-013' = [pscustomobject]@{ row = 'dual-platform-absolute-performance'; evidence = @('sync-probe', 'dual-platform-os-evidence') }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-FileRow {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RelativeTo)

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Qualification evidence cannot be a reparse point: '$Path'."
    }
    return [pscustomobject][ordered]@{
        path = [IO.Path]::GetRelativePath($RelativeTo, $item.FullName).Replace('\', '/')
        length = [int64]$item.Length
        sha256 = Get-FileSha256 $item.FullName
    }
}

function Assert-ExactProvenance {
    param(
        [Parameter(Mandatory)]$Start,
        [Parameter(Mandatory)]$Completion,
        [Parameter(Mandatory)][string]$Context)

    foreach ($property in @('commit', 'headTree', 'workingTreeState', 'statusSha256', 'sourceManifestSha256')) {
        $startValue = [string]$Start.$property
        $completionValue = [string]$Completion.$property
        if ([string]::IsNullOrWhiteSpace($startValue) `
            -or $startValue -eq 'unknown' `
            -or $startValue -cne $completionValue) {
            throw "$Context has unknown or unstable provenance property '$property'."
        }
    }
    if ([string]$Start.workingTreeState -cne 'clean') {
        throw "$Context is not bound to a clean working tree."
    }
}

function Assert-PlatformEvidenceManifest {
    param(
        [Parameter(Mandatory)][string]$PlatformRoot,
        [Parameter(Mandatory)]$Summary,
        [Parameter(Mandatory)][string]$Context)

    $summaryPath = [IO.Path]::GetFullPath((Join-Path $PlatformRoot 'summary.json'))
    $prefix = $PlatformRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $recorded = [Collections.Generic.Dictionary[string, object]]::new($pathComparer)
    foreach ($row in @($Summary.evidenceManifest)) {
        $recordedPath = [string]$row.path
        if ([string]::IsNullOrWhiteSpace($recordedPath)) {
            throw "$Context has an empty evidence-manifest path."
        }
        $fullPath = if ([IO.Path]::IsPathFullyQualified($recordedPath)) {
            [IO.Path]::GetFullPath($recordedPath)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $repositoryRoot $recordedPath))
        }
        if (-not $fullPath.StartsWith($prefix, $comparison) `
            -or $fullPath.Equals($summaryPath, $comparison) `
            -or -not $recorded.TryAdd($fullPath, $row) `
            -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "$Context has an out-of-root, duplicate, summary, or missing evidence path '$recordedPath'."
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 `
            -or [int64]$row.length -ne [int64]$item.Length `
            -or [string]$row.sha256 -cnotmatch '^[0-9A-F]{64}$' `
            -or [string]$row.sha256 -cne (Get-FileSha256 $fullPath)) {
            throw "$Context evidence integrity failed for '$recordedPath'."
        }
    }

    $actual = @(Get-ChildItem -LiteralPath $PlatformRoot -Recurse -File | Where-Object {
        -not $_.FullName.Equals($summaryPath, $comparison)
    })
    if ($actual.Count -ne $recorded.Count) {
        throw "$Context evidence manifest has $($recorded.Count) rows for $($actual.Count) files."
    }
    foreach ($file in $actual) {
        if (-not $recorded.ContainsKey($file.FullName)) {
            throw "$Context evidence manifest omits '$($file.FullName)'."
        }
    }
}

function Assert-PlatformSummary {
    param(
        [Parameter(Mandatory)][string]$Tier,
        [Parameter(Mandatory)][string]$Platform,
        [Parameter(Mandatory)][string]$Path)

    $context = "$Tier/$Platform summary"
    $summary = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
    if ([int]$summary.schemaVersion -ne 5 `
        -or [int]$summary.contractRevision -ne $contractRevision `
        -or [string]$summary.tier -cne $Tier `
        -or [string]$summary.platform -cne $Platform `
        -or [bool]$summary.validationOnly `
        -or [string]$summary.overallStatus -cne 'passed' `
        -or [int]$summary.controllerExitCode -ne 0 `
        -or @($summary.skips).Count -ne 0) {
        throw "$context is not a passed executable contract-revision-$contractRevision report."
    }
    if ([int64]$summary.completedAtMonotonic -lt [int64]$summary.startedAtMonotonic) {
        throw "$context has a reversed monotonic interval."
    }
    Assert-ExactProvenance $summary.provenance $summary.completionProvenance $context
    foreach ($property in @('sha256', 'layoutSha256', 'resourceNamingSha256')) {
        if ([string]$summary.protocolManifest.$property -cnotmatch '^[0-9A-F]{64}$') {
            throw "$context has an invalid protocol-manifest digest '$property'."
        }
    }
    $results = @($summary.results)
    if ($results.Count -eq 0) {
        throw "$context has no result rows."
    }
    foreach ($result in $results) {
        if (-not [bool]$result.required -or [string]$result.status -cne 'passed') {
            throw "$context has a non-required or non-passing result '$($result.name)'."
        }
    }
    Assert-PlatformEvidenceManifest (Split-Path -Parent $Path) $summary $context
    return $summary
}

function Assert-CodeReview {
    param(
        [Parameter(Mandatory)]$Review,
        [Parameter(Mandatory)]$Provenance)

    if ([int]$Review.schemaVersion -ne 1 `
        -or [int]$Review.contractRevision -ne $contractRevision `
        -or [string]$Review.revision.commit -cne [string]$Provenance.commit `
        -or [string]$Review.revision.sourceManifestSha256 -cne [string]$Provenance.sourceManifestSha256 `
        -or -not [bool]$Review.reviewer.independentFromImplementation `
        -or [string]::IsNullOrWhiteSpace([string]$Review.reviewer.identity) `
        -or [string]$Review.overallStatus -cne 'passed') {
        throw 'The independent review does not bind the exact implementation revision or declare a passing independent reviewer.'
    }
    $unresolved = @($Review.findings | Where-Object {
        [string]$_.status -cne 'resolved' -and [string]$_.severity -cin @('high', 'medium')
    })
    if ($unresolved.Count -ne 0) {
        throw 'The independent review contains unresolved High or Medium findings.'
    }
}

if ($ValidateOnly) {
    $specPath = Join-Path $repositoryRoot 'specs/010-lock-free-only-multilang/spec.md'
    $releaseContractPath = Join-Path $repositoryRoot 'specs/010-lock-free-only-multilang/release-qualification.md'
    $spec = Get-Content -LiteralPath $specPath -Raw
    $releaseContract = Get-Content -LiteralPath $releaseContractPath -Raw
    $criteria = @([regex]::Matches($spec, '(?m)^- \*\*(SC-[0-9]{3})\*\*:') |
        ForEach-Object { $_.Groups[1].Value })
    if (($criteria -join ',') -cne (@($criterionMap.Keys) -join ',')) {
        throw 'The finalizer success-criterion map does not exactly match spec.md.'
    }
    foreach ($entry in $criterionMap.GetEnumerator()) {
        $escapedCriterion = [regex]::Escape([string]$entry.Key)
        $escapedRow = [regex]::Escape([string]$entry.Value.row)
        if ($releaseContract -notmatch "(?m)^\| $escapedCriterion \| ``$escapedRow`` \|") {
            throw "release-qualification.md does not map $($entry.Key) to '$($entry.Value.row)'."
        }
    }
    Write-Host "Qualification finalizer contract validated: $($criterionMap.Count) exact success-criterion rows."
    exit 0
}

$runRoot = [IO.Path]::GetFullPath((Join-Path $qualificationRootPath $RunId))
if (-not (Test-Path -LiteralPath $runRoot -PathType Container)) {
    throw "Qualification run root does not exist: '$runRoot'."
}
$releaseSummaryPath = Join-Path $runRoot 'release/summary.json'
$reviewTargetPath = Join-Path $runRoot 'release/code-review.json'
$manifestPath = Join-Path $runRoot 'manifest.json'
foreach ($reserved in @($releaseSummaryPath, $reviewTargetPath, $manifestPath)) {
    if (Test-Path -LiteralPath $reserved) {
        throw "Refusing to reuse final qualification output '$reserved'."
    }
}
if ([string]::IsNullOrWhiteSpace($CodeReviewPath)) {
    throw 'CodeReviewPath is required for executable finalization.'
}
$reviewSourcePath = if ([IO.Path]::IsPathFullyQualified($CodeReviewPath)) {
    [IO.Path]::GetFullPath($CodeReviewPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $CodeReviewPath))
}
if (-not (Test-Path -LiteralPath $reviewSourcePath -PathType Leaf)) {
    throw "Independent review file does not exist: '$reviewSourcePath'."
}

$summaries = [ordered]@{}
foreach ($tier in $tiers) {
    foreach ($platform in $platforms) {
        $summaryPath = Join-Path $runRoot "$tier/$platform/summary.json"
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
            throw "Required platform summary is missing: '$summaryPath'."
        }
        $summaries["$tier/$platform"] = Assert-PlatformSummary $tier $platform $summaryPath
    }
}
$canonicalProvenance = $summaries['release/windows-x64'].provenance
$canonicalProtocol = $summaries['release/windows-x64'].protocolManifest
foreach ($entry in $summaries.GetEnumerator()) {
    foreach ($property in @('commit', 'headTree', 'statusSha256', 'sourceManifestSha256')) {
        if ([string]$entry.Value.provenance.$property -cne [string]$canonicalProvenance.$property) {
            throw "Platform summary '$($entry.Key)' does not share canonical provenance '$property'."
        }
    }
    foreach ($property in @('sha256', 'layoutSha256', 'resourceNamingSha256')) {
        if ([string]$entry.Value.protocolManifest.$property -cne [string]$canonicalProtocol.$property) {
            throw "Platform summary '$($entry.Key)' does not share protocol digest '$property'."
        }
    }
}

$review = Get-Content -LiteralPath $reviewSourcePath -Raw | ConvertFrom-Json -Depth 100
Assert-CodeReview $review $canonicalProvenance
New-Item -ItemType Directory -Path (Split-Path -Parent $reviewTargetPath) -Force | Out-Null
Copy-Item -LiteralPath $reviewSourcePath -Destination $reviewTargetPath
$review = Get-Content -LiteralPath $reviewTargetPath -Raw | ConvertFrom-Json -Depth 100
Assert-CodeReview $review $canonicalProvenance

$releaseRows = [Collections.Generic.List[object]]::new()
foreach ($criterion in $criterionMap.GetEnumerator()) {
    $evidence = [Collections.Generic.List[object]]::new()
    foreach ($platform in $platforms) {
        $summary = $summaries["release/$platform"]
        foreach ($resultName in @($criterion.Value.evidence)) {
            $matches = @($summary.results | Where-Object { [string]$_.name -ceq $resultName })
            if ($matches.Count -ne 1 -or [string]$matches[0].status -cne 'passed') {
                throw "Release $platform does not contain one passing '$resultName' row for $($criterion.Key)."
            }
            $evidence.Add([pscustomobject][ordered]@{
                platform = $platform
                result = $resultName
                qualification = [string]$matches[0].qualification
            })
        }
    }
    $releaseRows.Add([pscustomobject][ordered]@{
        criterion = [string]$criterion.Key
        name = [string]$criterion.Value.row
        required = $true
        status = 'passed'
        evidence = @($evidence)
    })
}

$platformSummaryRows = foreach ($tier in $tiers) {
    foreach ($platform in $platforms) {
        Get-FileRow (Join-Path $runRoot "$tier/$platform/summary.json") $runRoot
    }
}
$reviewRow = Get-FileRow $reviewTargetPath $runRoot
$rollupStarted = [Diagnostics.Stopwatch]::GetTimestamp()
$rollup = [ordered]@{
    schemaVersion = 1
    contractRevision = $contractRevision
    runId = $RunId
    tier = 'release'
    platform = 'cross-platform'
    validationOnly = $false
    overallStatus = 'passed'
    provenance = $canonicalProvenance
    testedArtifacts = @($summaries['release/windows-x64'].testedArtifacts) +
        @($summaries['release/linux-x64'].testedArtifacts)
    protocolManifest = $canonicalProtocol
    results = @($releaseRows)
    skips = @()
    evidenceManifest = @($platformSummaryRows) + @($reviewRow)
    startedAtMonotonic = $rollupStarted
    completedAtMonotonic = [Diagnostics.Stopwatch]::GetTimestamp()
    controllerExitCode = 0
    platformSummaries = @($platformSummaryRows)
    independentReview = $reviewRow
}
$rollup | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $releaseSummaryPath

$manifestRows = @(Get-ChildItem -LiteralPath $runRoot -Recurse -File | Where-Object {
    -not $_.FullName.Equals($manifestPath, $comparison)
} | Sort-Object FullName | ForEach-Object {
    Get-FileRow $_.FullName $runRoot
})
$manifest = [ordered]@{
    schemaVersion = 1
    contractRevision = $contractRevision
    runId = $RunId
    provenance = $canonicalProvenance
    fileCount = $manifestRows.Count
    files = $manifestRows
}
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath

# Revalidate all final files after serialization and copying.
Assert-CodeReview (Get-Content -LiteralPath $reviewTargetPath -Raw | ConvertFrom-Json -Depth 100) $canonicalProvenance
$writtenRollup = Get-Content -LiteralPath $releaseSummaryPath -Raw | ConvertFrom-Json -Depth 100
if ([string]$writtenRollup.overallStatus -cne 'passed' `
    -or @($writtenRollup.results).Count -ne $criterionMap.Count `
    -or @($writtenRollup.results | Where-Object { [string]$_.status -cne 'passed' }).Count -ne 0) {
    throw 'Serialized release rollup failed completion revalidation.'
}
foreach ($row in @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100).files)) {
    $fullPath = [IO.Path]::GetFullPath((Join-Path $runRoot ([string]$row.path)))
    if (-not $fullPath.StartsWith(
            $runRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
            $comparison) `
        -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf) `
        -or [int64]$row.length -ne (Get-Item -LiteralPath $fullPath).Length `
        -or [string]$row.sha256 -cne (Get-FileSha256 $fullPath)) {
        throw "Final manifest revalidation failed for '$($row.path)'."
    }
}

Write-Host "Cross-platform qualification finalized: $releaseSummaryPath"
Write-Host "Evidence manifest: $manifestPath"
