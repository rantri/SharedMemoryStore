param(
    [string]$Configuration = "Release",
    [switch]$SkipDocker
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path $root "SharedMemoryStore.slnx"
$packageProject = Join-Path $root "src/SharedMemoryStore/SharedMemoryStore.csproj"
$packageOutput = Join-Path $root "artifacts/package"
$sampleProjects = @(
    "samples/BasicUsage/BasicUsage.csproj",
    "samples/FrameValue/FrameValue.csproj",
    "samples/ZeroCopyIngest/ZeroCopyIngest.csproj",
    "samples/HostedServiceIntegration/HostedServiceIntegration.csproj",
    "samples/DockerSharedMemory/DockerSharedMemory.csproj"
)

function Invoke-Checked {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$Description
    )

    Write-Host "==> $Description"
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Invoke-RepositoryScript {
    param([string]$ScriptName)

    $scriptPath = Join-Path $root "scripts/$ScriptName"
    Invoke-Checked "pwsh" @("-NoProfile", "-File", $scriptPath, "-Configuration", $Configuration) $ScriptName
}

Invoke-Checked "dotnet" @("restore", $solution) "restore"
Invoke-Checked "dotnet" @("build", $solution, "-c", $Configuration, "--no-restore") "build"
Invoke-Checked "dotnet" @("test", $solution, "-c", $Configuration, "--no-build") "test"

foreach ($relativeProject in $sampleProjects) {
    $project = Join-Path $root $relativeProject
    $arguments = @("run", "--project", $project, "-c", $Configuration, "--no-build")
    if ($relativeProject -like "*DockerSharedMemory*") {
        $arguments += @("--", "all")
    }

    Invoke-Checked "dotnet" $arguments "sample $relativeProject"
}

Invoke-Checked "pwsh" @("-NoProfile", "-File", (Join-Path $root "scripts/validate-docs.ps1")) "validate docs"
Invoke-RepositoryScript "validate-package-consumption.ps1"
Invoke-Checked "dotnet" @("pack", $packageProject, "-c", $Configuration, "-o", $packageOutput, "--no-build") "pack"

if (-not $SkipDocker) {
    Invoke-Checked "pwsh" @("-NoProfile", "-File", (Join-Path $root "scripts/validate-docker-shared-memory.ps1"), "-Configuration", $Configuration) "validate Docker shared memory"
}

Write-Host "Cross-platform validation passed."
