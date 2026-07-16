param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $root "artifacts"
$packageDir = Join-Path $root "artifacts/package"
$consumerDir = Join-Path $root "artifacts/consumer-smoke"
$consumerCacheDir = Join-Path $root ("artifacts/consumer-smoke-packages-" + [Guid]::NewGuid().ToString('N'))
$previousNuGetPackages = $env:NUGET_PACKAGES

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

try {
    Assert-UnderArtifactRoot $consumerCacheDir
    New-Item -ItemType Directory -Force -Path $consumerCacheDir | Out-Null
    $env:NUGET_PACKAGES = $consumerCacheDir

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

Write-Host "Validating documented first-use workflow and package-surface smoke checks..."
$program = @'
using SharedMemoryStore;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

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

var open = MemoryStore.TryCreateOrOpen(options, out var store);
Console.WriteLine($"open: {open}");
if (open != StoreOpenStatus.Success || store is null)
{
    return 1;
}

using (store)
{
    var publish = store.TryPublish([1], [2, 3, 4], [9]);
    Console.WriteLine($"publish: {publish}");
    if (publish != StoreStatus.Success) return 2;
    var acquire = store.TryAcquire([1], out var lease);
    Console.WriteLine($"acquire: {acquire}");
    if (acquire != StoreStatus.Success) return 3;
    if (!new byte[] { 2, 3, 4 }.AsSpan().SequenceEqual(lease.ValueSpan)) return 4;
    Console.WriteLine($"value length: {lease.ValueLength}");
    var release = lease.Release();
    Console.WriteLine($"release: {release}");
    if (release != StoreStatus.Success) return 5;
    var remove = store.TryRemove([1]);
    Console.WriteLine($"remove: {remove}");
    if (remove != StoreStatus.Success) return 6;
    var frame = CreateLengthPrefixedFrame(new byte[] { 5, 6 });
    var directIngest = await PublishLengthPrefixedFrameAsync(store, new byte[] { 1 }, frame, new byte[] { 8 });
    Console.WriteLine($"direct ingest: {directIngest}");
    if (directIngest != StoreStatus.Success) return 7;
    var readFrame = ReadStoredFrame(store, new byte[] { 1 }, new byte[] { 5, 6 });
    Console.WriteLine($"direct read: {readFrame}");
    if (readFrame != StoreStatus.Success) return 8;
    if (store.TryRemove([1]) != StoreStatus.Success) return 13;

    var segmented = new ReadOnlySequence<byte>(new byte[] { 7, 8, 9 });
    var segmentedPublish = store.TryPublishSegments([1], segmented, [3], out var copied);
    Console.WriteLine($"segmented publish: {segmentedPublish}");
    if (segmentedPublish != StoreStatus.Success) return 14;
    Console.WriteLine($"segmented copied: {copied}");
    if (copied != 3) return 15;
    if (store.TryRemove([1]) != StoreStatus.Success) return 16;
    var recovery = store.TryRecoverReservations(new ReservationRecoveryOptions(false), out var report);
    Console.WriteLine($"reservation recovery: {recovery}");
    if (recovery != StoreStatus.Success) return 17;
    if (report.ScannedReservationCount != 0) return 18;
}

var disposed = store.TryPublish([1], [6]);
Console.WriteLine($"disposed publish: {disposed}");
if (disposed != StoreStatus.StoreDisposed) return 19;

if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
    && RuntimeInformation.ProcessArchitecture == Architecture.X64)
{
    var lockFreeOptions = SharedMemoryStoreOptions.CreateLockFree(
        $"sms-consumer-v2-{Guid.NewGuid():N}",
        slotCount: 2,
        maxValueBytes: 32,
        maxDescriptorBytes: 8,
        maxKeyBytes: 8,
        leaseRecordCount: 2,
        participantRecordCount: 1,
        openMode: OpenMode.CreateNew);
    var lockFreeOpen = MemoryStore.TryCreateOrOpen(lockFreeOptions, out var lockFreeStore);
    Console.WriteLine($"lock-free open: {lockFreeOpen}");
    if (lockFreeOpen != StoreOpenStatus.Success || lockFreeStore is null) return 20;

    using (lockFreeStore)
    {
        if (lockFreeStore.Profile != StoreProfile.LockFree) return 21;
        if (lockFreeStore.ProtocolInfo.LayoutMajorVersion != 2
            || lockFreeStore.ProtocolInfo.LayoutMinorVersion != 0
            || lockFreeStore.ProtocolInfo.ResourceProtocolVersion != 2) return 22;
        if (lockFreeStore.TryPublish([1], [7, 8, 9], [4]) != StoreStatus.Success) return 23;
        if (lockFreeStore.TryAcquire([1], out var lockFreeLease) != StoreStatus.Success) return 24;
        using (lockFreeLease)
        {
            if (!new byte[] { 7, 8, 9 }.AsSpan().SequenceEqual(lockFreeLease.ValueSpan)) return 25;
        }

        if (lockFreeStore.TryRemove([1]) != StoreStatus.Success) return 26;
        var lockFreeOpenExisting = SharedMemoryStoreOptions.CreateLockFree(
            lockFreeOptions.Name,
            slotCount: 2,
            maxValueBytes: 32,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 1,
            openMode: OpenMode.OpenExisting);
        if (MemoryStore.TryCreateOrOpen(lockFreeOpenExisting, out var exhausted)
            != StoreOpenStatus.ParticipantTableFull) return 27;
        exhausted?.Dispose();
    }
}

return 0;

static async Task<StoreStatus> PublishLengthPrefixedFrameAsync(MemoryStore store, byte[] key, byte[] frame, byte[] descriptor)
{
    await using var stream = new MemoryStream(frame);
    var header = new byte[4];
    await stream.ReadExactlyAsync(header);
    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
    var status = store.TryReserve(key, payloadLength, descriptor, out var reservation);
    if (status != StoreStatus.Success) return status;

    while (reservation.RemainingBytes > 0)
    {
        var buffer = new byte[reservation.RemainingBytes];
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            return reservation.Abort();
        }

        var target = reservation.GetSpan(read);
        buffer.AsSpan(0, read).CopyTo(target);
        status = reservation.Advance(read);
        if (status != StoreStatus.Success)
        {
            _ = reservation.Abort();
            return status;
        }
    }

    return reservation.Commit();
}

static StoreStatus ReadStoredFrame(MemoryStore store, byte[] key, byte[] expectedPayload)
{
    var status = store.TryAcquire(key, out var lease);
    if (status != StoreStatus.Success) return status;
    try
    {
        return expectedPayload.AsSpan().SequenceEqual(lease.ValueSpan)
            ? StoreStatus.Success
            : StoreStatus.UnknownFailure;
    }
    finally
    {
        _ = lease.Release();
    }
}

static byte[] CreateLengthPrefixedFrame(byte[] payload)
{
    var frame = new byte[4 + payload.Length];
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
    payload.CopyTo(frame.AsSpan(4));
    return frame;
}
'@

Set-Content -LiteralPath (Join-Path $consumerDir "Program.cs") -Value $program -Encoding UTF8
Invoke-DotNet run --project (Join-Path $consumerDir "SharedMemoryStore.ConsumerSmoke.csproj") -c $Configuration
}
finally {
    if ($null -eq $previousNuGetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNuGetPackages
    }

    if (Test-Path -LiteralPath $consumerCacheDir) {
        Assert-UnderArtifactRoot $consumerCacheDir
        Remove-Item -LiteralPath $consumerCacheDir -Recurse -Force
    }
}
