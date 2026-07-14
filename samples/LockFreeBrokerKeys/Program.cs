using System.Buffers.Binary;
using System.Threading.Channels;
using SharedMemoryStore;

const int PayloadBytes = 4 * 1024;
int workerCount = ParseOption(args, "--workers", defaultValue: 6);
int frameCount = ParseOption(args, "--frames", defaultValue: 48);
if (workerCount is < 6 or > 12 || frameCount < workerCount)
{
    Console.Error.WriteLine("Use --workers 6..12 and --frames >= workers.");
    return 2;
}

string storeName = $"sms-lock-free-broker-{Guid.NewGuid():N}";
int slotCount = frameCount;
SharedMemoryStoreOptions createOptions = Options(storeName, slotCount, OpenMode.CreateNew);
StoreOpenStatus open = MemoryStore.TryCreateOrOpen(createOptions, out MemoryStore? producer);
if (open != StoreOpenStatus.Success || producer is null)
{
    Console.Error.WriteLine($"Producer open failed: {open}");
    return 3;
}

using (producer)
{
    if (producer.Profile != StoreProfile.LockFree
        || producer.ProtocolInfo.LayoutMajorVersion != 2
        || producer.ProtocolInfo.LayoutMinorVersion != 0
        || producer.ProtocolInfo.ResourceProtocolVersion != 2)
    {
        throw new InvalidOperationException(
            $"Unexpected profile/protocol: {producer.Profile}, {producer.ProtocolInfo}.");
    }

    // These channels stand in for an application-owned broker. The store is
    // still only a key-value store: it neither queues nor assigns work.
    Channel<byte[]> workKeys = Channel.CreateUnbounded<byte[]>();
    Channel<byte[]> observerKeys = Channel.CreateUnbounded<byte[]>();
    var publishedKeys = new byte[frameCount][];
    long workerChecksum = 0;
    long observerChecksum = 0;
    int processed = 0;

    Task[] workers = Enumerable.Range(0, workerCount)
        .Select(workerId => Task.Run(async () =>
        {
            using MemoryStore workerStore = OpenExisting(storeName, slotCount);
            await foreach (byte[] key in workKeys.Reader.ReadAllAsync())
            {
                StoreStatus acquire = workerStore.TryAcquire(key, out ValueLease lease);
                if (acquire != StoreStatus.Success)
                {
                    throw new InvalidOperationException(
                        $"Worker {workerId} could not acquire {Convert.ToHexString(key)}: {acquire}");
                }

                using (lease)
                {
                    Interlocked.Add(ref workerChecksum, Checksum(lease.ValueSpan));
                }

                Interlocked.Increment(ref processed);
            }
        }))
        .ToArray();

    Task observer = Task.Run(async () =>
    {
        using MemoryStore observerStore = OpenExisting(storeName, slotCount);
        await foreach (byte[] key in observerKeys.Reader.ReadAllAsync())
        {
            if (observerStore.TryAcquire(key, out ValueLease lease) == StoreStatus.Success)
            {
                using (lease)
                {
                    Interlocked.Add(ref observerChecksum, Checksum(lease.ValueSpan));
                }
            }
        }
    });

    byte[] descriptor = new byte[sizeof(int)];
    for (var frame = 0; frame < frameCount; frame++)
    {
        byte[] key = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(key, frame);
        publishedKeys[frame] = key;

        BinaryPrimitives.WriteInt32LittleEndian(descriptor, frame);
        StoreStatus reserve = producer.TryReserve(
            key,
            PayloadBytes,
            descriptor,
            out ValueReservation reservation);
        if (reserve != StoreStatus.Success)
        {
            throw new InvalidOperationException($"Reserve {frame} failed: {reserve}");
        }

        using (reservation)
        {
            Span<byte> destination = reservation.GetSpan(PayloadBytes);
            destination.Fill(unchecked((byte)frame));
            StoreStatus advance = reservation.Advance(PayloadBytes);
            StoreStatus commit = advance == StoreStatus.Success
                ? reservation.Commit()
                : advance;
            if (commit != StoreStatus.Success)
            {
                throw new InvalidOperationException($"Commit {frame} failed: {commit}");
            }
        }

        // Broker messages contain keys, never the 4 KiB payload.
        await workKeys.Writer.WriteAsync(key);
        await observerKeys.Writer.WriteAsync(key);
    }

    workKeys.Writer.Complete();
    observerKeys.Writer.Complete();
    await Task.WhenAll(workers.Append(observer));

    // Demonstrate that a non-worker reader protects one exact value while its
    // logical removal remains local to that key.
    using MemoryStore independentReader = OpenExisting(storeName, slotCount);
    StoreStatus held = independentReader.TryAcquire(publishedKeys[0], out ValueLease heldLease);
    if (held != StoreStatus.Success)
    {
        throw new InvalidOperationException($"Independent reader failed: {held}");
    }

    StoreStatus pending = producer.TryRemove(publishedKeys[0]);
    if (pending != StoreStatus.RemovePending || heldLease.Release() != StoreStatus.Success)
    {
        throw new InvalidOperationException($"Expected lease-protected RemovePending, got {pending}.");
    }

    for (var frame = 1; frame < frameCount; frame++)
    {
        StoreStatus remove = producer.TryRemove(publishedKeys[frame]);
        if (remove != StoreStatus.Success)
        {
            throw new InvalidOperationException($"Remove {frame} failed: {remove}");
        }
    }

    var boundedRead = new StoreWaitOptions(TimeSpan.FromMilliseconds(25), CancellationToken.None);
    StoreStatus missing = producer.TryAcquire(
        new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff },
        boundedRead,
        out _);

    _ = producer.TryRecoverLeases(
        new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
        out LeaseRecoveryReport leaseRecovery);
    _ = producer.TryRecoverReservations(
        new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
        out ReservationRecoveryReport reservationRecovery);

    StoreStatus diagnosticsStatus = producer.TryGetDiagnostics(out DiagnosticsSnapshot diagnostics);
    if (diagnosticsStatus != StoreStatus.Success
        || diagnostics.Profile != StoreProfile.LockFree
        || diagnostics.ProtocolInfo.LayoutMajorVersion != 2)
    {
        throw new InvalidOperationException($"Diagnostics failed: {diagnosticsStatus}.");
    }

    Console.WriteLine(
        $"RESULT workers={workerCount} frames={frameCount} processed={processed} "
        + $"workerChecksum={workerChecksum} observerChecksum={observerChecksum} "
        + $"pendingRemove={pending} missing={missing} diagnostics={diagnosticsStatus} "
        + $"profile={producer.Profile} layout={producer.ProtocolInfo.LayoutMajorVersion}."
        + $"{producer.ProtocolInfo.LayoutMinorVersion} "
        + $"recoveredLeases={leaseRecovery.RecoveredLeaseCount} "
        + $"recoveredReservations={reservationRecovery.RecoveredReservationCount}");
}

return 0;

static SharedMemoryStoreOptions Options(string name, int slotCount, OpenMode mode) =>
    SharedMemoryStoreOptions.CreateLockFree(
        name,
        slotCount,
        maxValueBytes: PayloadBytes,
        maxDescriptorBytes: sizeof(int),
        maxKeyBytes: sizeof(long),
        leaseRecordCount: 64,
        participantRecordCount: 32,
        openMode: mode,
        enableLeaseRecovery: true);

static MemoryStore OpenExisting(string name, int slotCount)
{
    StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
        Options(name, slotCount, OpenMode.OpenExisting),
        out MemoryStore? store);
    return status == StoreOpenStatus.Success && store is not null
        ? store
        : throw new InvalidOperationException($"OpenExisting failed: {status}");
}

static int ParseOption(string[] arguments, string name, int defaultValue)
{
    for (var index = 0; index + 1 < arguments.Length; index++)
    {
        if (arguments[index] == name && int.TryParse(arguments[index + 1], out int value))
        {
            return value;
        }
    }

    return defaultValue;
}

static int Checksum(ReadOnlySpan<byte> bytes)
{
    var checksum = 17;
    foreach (byte value in bytes)
    {
        checksum = unchecked((checksum * 31) + value);
    }

    return checksum;
}
