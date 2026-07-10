param(
    [ValidateSet("Supported", "Isolated", "Advanced", "Recovery", "Contention", "DisposalRace", "CleanConsumer", "All")]
    [string]$Profile = "All",
    [string]$Configuration = "Release",
    [switch]$SkipComposeBuild
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $root "artifacts"
$sampleProject = Join-Path $root "samples/DockerSharedMemory/DockerSharedMemory.csproj"
$supportedCompose = Join-Path $root "samples/DockerSharedMemory/docker-compose.yml"
$isolatedCompose = Join-Path $root "samples/DockerSharedMemory/docker-compose.isolated.yml"

function Invoke-CommandChecked {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage failed with exit code $LASTEXITCODE."
    }
}

function Assert-UnderArtifactRoot {
    param([string]$Path)

    $fullArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside artifacts: $fullPath"
    }
}

function Test-DockerAvailable {
    & docker --version *> $null
    return $LASTEXITCODE -eq 0
}

function Test-SelectedProfile {
    param([string]$Name)

    return $Profile -eq $Name -or $Profile -eq "All"
}

function Invoke-Compose {
    param(
        [string]$ComposeFile,
        [string]$ExitCodeFrom,
        [string[]]$Services
    )

    $projectName = "smsdocker-" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
    $upArgs = @("compose", "-p", $projectName, "-f", $ComposeFile, "up", "--abort-on-container-exit", "--exit-code-from", $ExitCodeFrom)
    if (-not $SkipComposeBuild) {
        $upArgs += "--build"
    }

    $upArgs += $Services

    try {
        Invoke-CommandChecked "docker" $upArgs "docker compose up"
    }
    finally {
        & docker compose -p $projectName -f $ComposeFile down --volumes
    }
}

function Invoke-DockerCleanConsumerValidation {
    $consumerDir = Join-Path $artifactRoot "docker-consumer"
    $packageDir = Join-Path $consumerDir "local-packages"
    $projectPath = Join-Path $consumerDir "SharedMemoryStore.DockerConsumer.csproj"
    $programPath = Join-Path $consumerDir "Program.cs"
    $dockerfilePath = Join-Path $consumerDir "Dockerfile"
    $imageName = "sharedmemorystore-docker-consumer:local"

    if (Test-Path -LiteralPath $consumerDir) {
        Assert-UnderArtifactRoot $consumerDir
        Remove-Item -LiteralPath $consumerDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    Invoke-CommandChecked "dotnet" @("pack", (Join-Path $root "src/SharedMemoryStore/SharedMemoryStore.csproj"), "-c", $Configuration, "-o", $packageDir) "pack Docker consumer package"

    $project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharedMemoryStore" Version="1.0.1" />
  </ItemGroup>
</Project>
'@
    Set-Content -LiteralPath $projectPath -Value $project -Encoding UTF8

    $program = @'
using System.Buffers;
using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    $"sms-docker-consumer-{Guid.NewGuid():N}",
    slotCount: 4,
    maxValueBytes: 64,
    maxDescriptorBytes: 8,
    maxKeyBytes: 8,
    leaseRecordCount: 4,
    openMode: OpenMode.CreateOrOpen,
    enableLeaseRecovery: true);

var open = MemoryStore.TryCreateOrOpen(options, out var store);
Console.WriteLine($"consumer open: {open}");
if (open != StoreOpenStatus.Success || store is null) return 1;

using (store)
{
    var publish = store.TryPublish([1], [2, 3, 4], [9]);
    Console.WriteLine($"consumer publish: {publish}");
    if (publish != StoreStatus.Success) return 2;

    var acquire = store.TryAcquire([1], out var lease);
    Console.WriteLine($"consumer acquire: {acquire}");
    if (acquire != StoreStatus.Success) return 3;
    if (!new byte[] { 2, 3, 4 }.AsSpan().SequenceEqual(lease.ValueSpan)) return 4;

    var removeWhileLeased = store.TryRemove([1]);
    Console.WriteLine($"consumer remove while leased: {removeWhileLeased}");
    if (removeWhileLeased != StoreStatus.RemovePending) return 5;

    var release = lease.Release();
    Console.WriteLine($"consumer release: {release}");
    if (release != StoreStatus.Success) return 6;

    var republish = store.TryPublish([1], [5, 6], [8]);
    Console.WriteLine($"consumer republish: {republish}");
    if (republish != StoreStatus.Success) return 7;
    if (store.TryRemove([1]) != StoreStatus.Success) return 8;

    var reserve = store.TryReserve([2], 3, [7], out var reservation);
    Console.WriteLine($"consumer reserve: {reserve}");
    if (reserve != StoreStatus.Success) return 9;
    reservation.GetSpan(3).Fill(7);
    if (reservation.Advance(3) != StoreStatus.Success) return 10;
    if (reservation.Commit() != StoreStatus.Success) return 11;
    if (store.TryRemove([2]) != StoreStatus.Success) return 12;

    var segmented = store.TryPublishSegments([3], new ReadOnlySequence<byte>([8, 9, 10]), [6], out var copied);
    Console.WriteLine($"consumer segmented: {segmented}; copied={copied}");
    if (segmented != StoreStatus.Success || copied != 3) return 13;
    if (store.TryRemove([3]) != StoreStatus.Success) return 14;

    var recovery = store.TryRecoverReservations(new ReservationRecoveryOptions(false), out var recoveryReport);
    Console.WriteLine($"consumer reservation recovery: {recovery}; scanned={recoveryReport.ScannedReservationCount}");
    if (recovery != StoreStatus.Success) return 15;

    var diagnostics = store.TryGetDiagnostics(StoreWaitOptions.Default, out var snapshot);
    Console.WriteLine($"consumer diagnostics: {diagnostics}; free={snapshot.FreeSlotCount}");
    if (diagnostics != StoreStatus.Success) return 16;
}

var disposed = store.TryPublish([4], [4]);
Console.WriteLine($"consumer disposed publish: {disposed}");
if (disposed != StoreStatus.StoreDisposed) return 17;

Console.WriteLine("docker clean consumer validation passed");
return 0;
'@
    Set-Content -LiteralPath $programPath -Value $program -Encoding UTF8

    $dockerfile = @'
FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /consumer
COPY local-packages/ ./local-packages/
COPY SharedMemoryStore.DockerConsumer.csproj ./
RUN dotnet restore SharedMemoryStore.DockerConsumer.csproj --source ./local-packages
COPY Program.cs ./
RUN dotnet build SharedMemoryStore.DockerConsumer.csproj -c Release --no-restore
ENTRYPOINT ["dotnet", "run", "--project", "SharedMemoryStore.DockerConsumer.csproj", "-c", "Release", "--no-build"]
'@
    Set-Content -LiteralPath $dockerfilePath -Value $dockerfile -Encoding UTF8

    Invoke-CommandChecked "docker" @("build", "-t", $imageName, $consumerDir) "docker clean consumer build"
    Invoke-CommandChecked "docker" @("run", "--rm", "--ipc=shareable", "--shm-size=128m", $imageName) "docker clean consumer run"
}

Write-Host "Validating DockerSharedMemory sample project..."
Invoke-CommandChecked "dotnet" @("run", "--project", $sampleProject, "-c", $Configuration, "--", "all") "local DockerSharedMemory sample"

if (-not (Test-DockerAvailable)) {
    throw "Docker is required for cross-container validation but was not found on PATH."
}

if (Test-SelectedProfile "Supported") {
    Write-Host "Running supported same-host Docker shared-memory profile..."
    Invoke-Compose $supportedCompose "verifier" @("writer", "verifier")
}

if (Test-SelectedProfile "Advanced") {
    Write-Host "Running advanced Docker workflow profile..."
    Invoke-Compose $supportedCompose "advanced" @("writer", "advanced")
}

if (Test-SelectedProfile "Recovery") {
    Write-Host "Running Docker recovery and abrupt-exit profile..."
    Invoke-Compose $supportedCompose "recovery-verifier" @("recovery-keeper", "abrupt-lease-owner", "abrupt-reservation-owner", "recovery-verifier")
}

if (Test-SelectedProfile "Contention") {
    Write-Host "Running Docker contention profile..."
    Invoke-Compose $supportedCompose "contention-verifier" @("writer", "contention-holder", "contention-verifier")
}

if (Test-SelectedProfile "DisposalRace") {
    Write-Host "Running Docker disposal-race profile..."
    Invoke-Compose $supportedCompose "disposal-race" @("disposal-race")
}

if (Test-SelectedProfile "Isolated") {
    Write-Host "Running isolated Docker negative profile..."
    Invoke-Compose $isolatedCompose "isolated-verifier" @("writer", "isolated-verifier")
}

if (Test-SelectedProfile "CleanConsumer") {
    Write-Host "Running Docker clean-consumer package validation..."
    Invoke-DockerCleanConsumerValidation
}

Write-Host "Docker shared-memory validation passed."
