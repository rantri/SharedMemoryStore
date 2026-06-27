param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$rootPath = (Resolve-Path $Root).Path
$failures = [System.Collections.Generic.List[string]]::new()

$requiredRootFiles = @(
    "README.md",
    "LICENSE",
    "CHANGELOG.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    "SECURITY.md",
    "SUPPORT.md"
)

$requiredGithubFiles = @(
    ".github/ISSUE_TEMPLATE/bug_report.yml",
    ".github/ISSUE_TEMPLATE/documentation.yml",
    ".github/ISSUE_TEMPLATE/feature_request.yml",
    ".github/ISSUE_TEMPLATE/config.yml",
    ".github/pull_request_template.md"
)

$requiredGuideFiles = @(
    "docs/index.md",
    "docs/getting-started.md",
    "docs/usage.md",
    "docs/errors.md",
    "docs/diagnostics.md",
    "docs/lifecycle.md",
    "docs/packaging.md",
    "docs/portability.md",
    "docs/performance.md",
    "docs/examples.md",
    "docs/releases.md"
)

$requiredSampleFiles = @(
    "samples/BasicUsage/README.md",
    "samples/FrameValue/README.md"
)

$contractFiles = @(
    "specs/001-frame-memory-store/contracts/public-api.md",
    "specs/001-frame-memory-store/contracts/error-taxonomy.md",
    "specs/001-frame-memory-store/contracts/shared-memory-layout.md"
)

$allRequiredFiles = $requiredRootFiles + $requiredGithubFiles + $requiredGuideFiles + $requiredSampleFiles
$publicDocumentationFiles = $allRequiredFiles + @("samples/BasicUsage/Program.cs", "samples/FrameValue/Program.cs")

function Add-Failure {
    param([string]$Message)
    $failures.Add($Message) | Out-Null
}

function Join-Root {
    param([string]$RelativePath)
    return Join-Path $rootPath ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

function Read-Text {
    param([string]$RelativePath)
    return Get-Content -Raw -LiteralPath (Join-Root $RelativePath)
}

function Assert-FileExists {
    param([string]$RelativePath)

    if (-not (Test-Path -LiteralPath (Join-Root $RelativePath) -PathType Leaf)) {
        Add-Failure "Missing required file: $RelativePath"
    }
}

function Assert-Contains {
    param(
        [string]$RelativePath,
        [string]$Needle,
        [string]$Reason
    )

    if (-not (Test-Path -LiteralPath (Join-Root $RelativePath) -PathType Leaf)) {
        Add-Failure "Cannot check missing file: $RelativePath ($Reason)"
        return
    }

    $content = Read-Text $RelativePath
    if (-not $content.Contains($Needle, [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "$RelativePath does not contain '$Needle' ($Reason)"
    }
}

function Assert-NoPlaceholders {
    param([string[]]$RelativePaths)

    $placeholderPatterns = @(
        "\bTODO\b",
        "\bTBD\b",
        "NEEDS CLARIFICATION",
        "\[[A-Z][A-Z _-]+\](?!\()"
    )

    foreach ($relativePath in $RelativePaths) {
        $path = Join-Root $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $lines = Get-Content -LiteralPath $path
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($pattern in $placeholderPatterns) {
                if ($lines[$i] -cmatch $pattern) {
                    Add-Failure "Unresolved placeholder in $relativePath line $($i + 1): $($Matches[0])"
                }
            }
        }
    }
}

function Test-ExternalOrAnchorLink {
    param([string]$Target)

    return $Target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or $Target.StartsWith("#")
}

function Assert-MarkdownLinksResolve {
    param([string[]]$RelativePaths)

    $linkPattern = '(?<!\!)\[[^\]]+\]\(([^)\s]+)(?:\s+"[^"]*")?\)'

    foreach ($relativePath in $RelativePaths) {
        $path = Join-Root $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $content = Read-Text $relativePath
        $matches = [regex]::Matches($content, $linkPattern)
        foreach ($match in $matches) {
            $target = $match.Groups[1].Value.Trim()
            if ([string]::IsNullOrWhiteSpace($target) -or (Test-ExternalOrAnchorLink $target)) {
                continue
            }

            $withoutFragment = ($target -split '#', 2)[0]
            $withoutQuery = ($withoutFragment -split '\?', 2)[0]
            if ([string]::IsNullOrWhiteSpace($withoutQuery)) {
                continue
            }

            try {
                $decoded = [System.Uri]::UnescapeDataString($withoutQuery)
            }
            catch {
                $decoded = $withoutQuery
            }

            $baseDirectory = Split-Path -Parent $path
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $baseDirectory ($decoded -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
            if (-not $resolved.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                Add-Failure "Link in $relativePath points outside repository: $target"
                continue
            }

            if (-not (Test-Path -LiteralPath $resolved)) {
                Add-Failure "Broken relative link in ${relativePath}: $target"
            }
        }
    }
}

function Assert-PackageMetadata {
    $projectPath = Join-Root "src/SharedMemoryStore/SharedMemoryStore.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        Add-Failure "Missing package project: src/SharedMemoryStore/SharedMemoryStore.csproj"
        return
    }

    [xml]$project = Get-Content -Raw -LiteralPath $projectPath
    $propertyGroup = $project.Project.PropertyGroup | Select-Object -First 1
    $expected = @{
        TargetFramework = "net10.0"
        PackageId = "SharedMemoryStore"
        Version = "0.1.0"
        Description = "A bounded named shared-memory key-value store for opaque binary values."
        PackageLicenseExpression = "MIT"
        PackageReadmeFile = "README.md"
        RepositoryType = "git"
    }

    foreach ($name in $expected.Keys) {
        if ($propertyGroup.$name -ne $expected[$name]) {
            Add-Failure "Package metadata mismatch for $name. Expected '$($expected[$name])', found '$($propertyGroup.$name)'."
        }
    }

    foreach ($tag in @("shared-memory", "memory-mapped-file", "zero-copy", "library")) {
        if (-not ($propertyGroup.PackageTags -like "*$tag*")) {
            Add-Failure "PackageTags missing '$tag'."
        }
    }

    if ([string]::IsNullOrWhiteSpace($propertyGroup.PackageReleaseNotes)) {
        Add-Failure "PackageReleaseNotes must be populated."
    }

    $readmeItem = $project.Project.ItemGroup.None | Where-Object {
        $_.Include -eq "..\..\README.md" -and $_.Pack -eq "true" -and $_.PackagePath -eq "\"
    }
    if (-not $readmeItem) {
        Add-Failure "Package project must pack README.md at the package root."
    }

    Assert-Contains "README.md" "SharedMemoryStore" "package README identity"
    Assert-Contains "README.md" "0.1.0" "package version alignment"
    Assert-Contains "README.md" "net10.0" "target framework alignment"
    Assert-Contains "README.md" "MIT" "license alignment"
    Assert-Contains "LICENSE" "MIT License" "license metadata alignment"
    Assert-Contains "CHANGELOG.md" "0.1.0" "release notes alignment"
    Assert-Contains "docs/packaging.md" "PackageId" "package documentation notes"
    Assert-Contains "docs/packaging.md" "PackageReleaseNotes" "package release notes documentation"
}

function Assert-RequiredLinks {
    foreach ($path in @(
        "docs/getting-started.md",
        "docs/usage.md",
        "specs/001-frame-memory-store/contracts/public-api.md",
        "docs/examples.md",
        "docs/lifecycle.md",
        "docs/packaging.md",
        "SUPPORT.md",
        "SECURITY.md",
        "CONTRIBUTING.md",
        ".github/ISSUE_TEMPLATE/bug_report.yml",
        ".github/ISSUE_TEMPLATE/documentation.yml",
        ".github/ISSUE_TEMPLATE/feature_request.yml",
        ".github/pull_request_template.md",
        "LICENSE",
        "CHANGELOG.md",
        "docs/releases.md"
    )) {
        Assert-Contains "README.md" $path "README entry-point reachability"
    }

    foreach ($path in $requiredRootFiles + $requiredGithubFiles + $requiredGuideFiles + $requiredSampleFiles + $contractFiles) {
        if ($path -eq "docs/index.md") {
            continue
        }

        $indexNeedle = if ($path.StartsWith("docs/")) {
            Split-Path -Leaf $path
        } else {
            $path
        }

        Assert-Contains "docs/index.md" $indexNeedle "documentation index reachability"
    }

    $contractCoverage = @{
        "docs/lifecycle.md" = @("specs/001-frame-memory-store/contracts/public-api.md", "specs/001-frame-memory-store/contracts/shared-memory-layout.md", "specs/001-frame-memory-store/contracts/error-taxonomy.md")
        "docs/errors.md" = @("specs/001-frame-memory-store/contracts/error-taxonomy.md")
        "docs/diagnostics.md" = @("specs/001-frame-memory-store/contracts/public-api.md", "specs/001-frame-memory-store/contracts/error-taxonomy.md")
        "docs/portability.md" = @("specs/001-frame-memory-store/contracts/shared-memory-layout.md")
        "docs/performance.md" = @("specs/001-frame-memory-store/contracts/public-api.md", "specs/001-frame-memory-store/contracts/error-taxonomy.md")
        "docs/examples.md" = @("specs/001-frame-memory-store/contracts/public-api.md", "specs/001-frame-memory-store/contracts/shared-memory-layout.md")
    }

    foreach ($doc in $contractCoverage.Keys) {
        foreach ($contract in $contractCoverage[$doc]) {
            Assert-Contains $doc $contract "contract traceability"
        }
    }

    foreach ($path in @("CONTRIBUTING.md", "CODE_OF_CONDUCT.md", ".github/ISSUE_TEMPLATE/bug_report.yml", ".github/ISSUE_TEMPLATE/documentation.yml", ".github/ISSUE_TEMPLATE/feature_request.yml", ".github/pull_request_template.md")) {
        Assert-FileExists $path
    }

    Assert-Contains "CONTRIBUTING.md" "CODE_OF_CONDUCT.md" "contributor conduct path"
    Assert-Contains "CONTRIBUTING.md" "SECURITY.md" "security disclosure path"
    Assert-Contains "CONTRIBUTING.md" "SUPPORT.md" "support path"
    Assert-Contains "CONTRIBUTING.md" ".github/ISSUE_TEMPLATE/bug_report.yml" "bug issue template path"
    Assert-Contains "CONTRIBUTING.md" ".github/ISSUE_TEMPLATE/documentation.yml" "documentation issue template path"
    Assert-Contains "CONTRIBUTING.md" ".github/ISSUE_TEMPLATE/feature_request.yml" "feature issue template path"
    Assert-Contains "CONTRIBUTING.md" ".github/pull_request_template.md" "pull request guidance"

    foreach ($path in @("SUPPORT.md", "SECURITY.md", "CHANGELOG.md", "docs/releases.md", "docs/packaging.md")) {
        Assert-FileExists $path
    }

    Assert-Contains "docs/releases.md" "PackageReleaseNotes" "release readiness"
    Assert-Contains "docs/releases.md" "SECURITY.md" "release security check"
    Assert-Contains "docs/releases.md" "SUPPORT.md" "release support check"
    Assert-Contains "docs/releases.md" "CHANGELOG.md" "release changelog check"
}

Write-Host "Validating documentation inventory..."
foreach ($relativePath in $allRequiredFiles + $contractFiles) {
    Assert-FileExists $relativePath
}

Write-Host "Scanning public documentation for unresolved placeholders..."
Assert-NoPlaceholders $publicDocumentationFiles

Write-Host "Validating relative Markdown links..."
Assert-MarkdownLinksResolve ($requiredRootFiles + $requiredGuideFiles + $requiredSampleFiles)

Write-Host "Checking package metadata and package-facing documentation alignment..."
Assert-PackageMetadata

Write-Host "Checking required reader, contributor, contract, and release links..."
Assert-RequiredLinks

if ($failures.Count -gt 0) {
    Write-Error ("Documentation validation failed:`n - " + ($failures -join "`n - "))
    exit 1
}

Write-Host "Documentation validation passed."
