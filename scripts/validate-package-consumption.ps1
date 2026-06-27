param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $root "artifacts"
$packageDir = Join-Path $root "artifacts/package"
$consumerDir = Join-Path $root "artifacts/consumer-smoke"

function Assert-UnderArtifactRoot {
    param([string]$Path)

    $fullArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside artifacts: $fullPath"
    }
}

function Invoke-DotNet {
    dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $args failed with exit code $LASTEXITCODE"
    }
}

if (Test-Path -LiteralPath $packageDir) {
    Assert-UnderArtifactRoot $packageDir
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}

if (Test-Path -LiteralPath $consumerDir) {
    Assert-UnderArtifactRoot $consumerDir
    Remove-Item -LiteralPath $consumerDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
Invoke-DotNet pack (Join-Path $root "src/SharedMemoryStore/SharedMemoryStore.csproj") -c $Configuration -o $packageDir
Invoke-DotNet new console -f net10.0 -n SharedMemoryStore.ConsumerSmoke -o $consumerDir
Invoke-DotNet add (Join-Path $consumerDir "SharedMemoryStore.ConsumerSmoke.csproj") package SharedMemoryStore --source $packageDir

$program = @'
using Store = SharedMemoryStore.SharedMemoryStore;
using SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = $"sms-consumer-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = 32,
    MaxDescriptorBytes = 8,
    MaxKeyBytes = 8,
    LeaseRecordCount = 2,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 32, 8, 8, 2)
};

var open = Store.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    return 1;
}

using (store)
{
    if (store.TryPublish([1], [2, 3, 4], [9]) != StoreStatus.Success) return 2;
    if (store.TryAcquire([1], out var lease) != StoreStatus.Success) return 3;
    if (!new byte[] { 2, 3, 4 }.AsSpan().SequenceEqual(lease.ValueSpan)) return 4;
    if (lease.Release() != StoreStatus.Success) return 5;
    if (store.TryRemove([1]) != StoreStatus.Success) return 6;
    if (store.TryPublish([1], [5]) != StoreStatus.Success) return 7;
}

if (store.TryPublish([1], [6]) != StoreStatus.StoreDisposed) return 8;

return 0;
'@

Set-Content -LiteralPath (Join-Path $consumerDir "Program.cs") -Value $program -Encoding UTF8
Invoke-DotNet run --project (Join-Path $consumerDir "SharedMemoryStore.ConsumerSmoke.csproj") -c $Configuration
