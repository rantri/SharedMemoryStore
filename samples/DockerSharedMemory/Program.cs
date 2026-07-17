using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharedMemoryStore;

var mode = args.Length > 0 ? args[0] : "all";
var storeName = Environment.GetEnvironmentVariable("SMS_STORE_NAME");
if (string.IsNullOrWhiteSpace(storeName))
{
    storeName = "sms-docker-shared-memory";
}

var exitCode = mode.ToLowerInvariant() switch
{
    "writer" => await RunWriterAsync(storeName),
    "verifier" => await RunVerifierAsync(storeName),
    "isolated-profile" => RunIsolatedProfileCheck(storeName),
    "reservation" => RunReservationWorkflow(storeName, OpenMode.CreateOrOpen),
    "segmented-publish" => RunSegmentedPublishWorkflow(storeName, OpenMode.CreateOrOpen),
    "recovery" => RunRecoveryWorkflow(storeName, expectedLeaseRecoveries: 0, expectedReservationRecoveries: 0),
    "advanced" => RunAdvancedWorkflow(storeName),
    "abrupt-lease-owner" => RunAbruptLeaseOwner(storeName),
    "abrupt-reservation-owner" => RunAbruptReservationOwner(storeName),
    "recovery-verifier" => await RunRecoveryVerifierAsync(storeName),
    "contention-holder" => await RunContentionHolderAsync(storeName),
    "contention-verifier" => await RunContentionVerifierAsync(storeName),
    "disposal-race" => await RunDisposalRaceWorkflow(storeName),
    "all" => RunAllLocalWorkflows(storeName),
    _ => Usage()
};

return exitCode;

static SharedMemoryStoreOptions Options(string storeName, OpenMode mode)
{
    return SharedMemoryStoreOptions.Create(
        storeName,
        slotCount: 8,
        maxValueBytes: 256,
        maxDescriptorBytes: 32,
        maxKeyBytes: 32,
        leaseRecordCount: 8,
        participantRecordCount: 16,
        openMode: mode,
        enableLeaseRecovery: true);
}

static async Task<int> RunWriterAsync(string storeName)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"writer open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 10;
    }

    using (store)
    {
        var publish = store.TryPublish([1], [10, 11, 12], [1]);
        Console.WriteLine($"writer publish: {publish}");
        if (publish != StoreStatus.Success && publish != StoreStatus.DuplicateKey)
        {
            return 11;
        }

        var holdSeconds = ReadIntEnvironment("SMS_HOLD_SECONDS", 45);
        Console.WriteLine($"writer holding store for {holdSeconds} seconds");
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    }

    return 0;
}

static async Task<int> RunVerifierAsync(string storeName)
{
    var (open, store) = await OpenExistingWithRetryAsync(storeName);
    Console.WriteLine($"verifier open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 20;
    }

    using (store)
    {
        var acquire = store.TryAcquire([1], out var lease);
        Console.WriteLine($"verifier acquire: {acquire}");
        if (acquire != StoreStatus.Success)
        {
            return 21;
        }

        ReadOnlySpan<byte> expected = [10, 11, 12];
        if (!lease.ValueSpan.SequenceEqual(expected))
        {
            lease.Dispose();
            return 22;
        }

        var removeWhileLeased = store.TryRemove([1]);
        Console.WriteLine($"verifier remove while leased: {removeWhileLeased}");
        if (removeWhileLeased != StoreStatus.RemovePending)
        {
            lease.Dispose();
            return 23;
        }

        var release = lease.Release();
        Console.WriteLine($"verifier release: {release}");
        if (release != StoreStatus.Success)
        {
            return 24;
        }

        var republish = store.TryPublish([1], [13, 14, 15], [2]);
        Console.WriteLine($"verifier republish after release: {republish}");
        if (republish != StoreStatus.Success)
        {
            return 25;
        }

        var removeAfterRepublish = store.TryRemove([1]);
        Console.WriteLine($"verifier remove after republish: {removeAfterRepublish}");
        if (removeAfterRepublish != StoreStatus.Success)
        {
            return 26;
        }

        var churn = ReadIntEnvironment("SMS_CHURN_CYCLES", 10_000);
        for (var i = 0; i < churn; i++)
        {
            var key = BitConverter.GetBytes(i);
            var publish = store.TryPublish(key, [42], [2]);
            if (publish != StoreStatus.Success)
            {
                Console.WriteLine($"churn publish failed at {i}: {publish}");
                return 27;
            }

            var read = store.TryAcquire(key, out var churnLease);
            if (read != StoreStatus.Success)
            {
                Console.WriteLine($"churn acquire failed at {i}: {read}");
                return 28;
            }

            churnLease.Dispose();
            var remove = store.TryRemove(key);
            if (remove != StoreStatus.Success)
            {
                Console.WriteLine($"churn remove failed at {i}: {remove}");
                return 29;
            }
        }

        var diagnostics = store.GetDiagnostics();
        Console.WriteLine($"verifier diagnostics failures: {diagnostics.LastFailureStatus}");
    }

    Console.WriteLine("docker shared memory validation passed");
    return 0;
}

static int RunIsolatedProfileCheck(string storeName)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.OpenExisting), out var store);
    store?.Dispose();
    Console.WriteLine($"isolated open: {open}");
    return open == StoreOpenStatus.NotFound
        || open == StoreOpenStatus.UnsupportedPlatform
        || open == StoreOpenStatus.AccessDenied
        || open == StoreOpenStatus.MappingFailed
        ? 0
        : 30;
}

static int RunReservationWorkflow(string storeName, OpenMode mode)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, mode), out var store);
    Console.WriteLine($"reservation open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 40;
    }

    using (store)
    {
        var status = store.TryReserve([2], 3, [7], out var reservation);
        Console.WriteLine($"reservation reserve: {status}");
        if (status != StoreStatus.Success)
        {
            return 41;
        }

        reservation.GetSpan(3).Fill(9);
        status = reservation.Advance(3);
        Console.WriteLine($"reservation advance: {status}");
        if (status != StoreStatus.Success)
        {
            return 42;
        }

        status = reservation.Commit();
        Console.WriteLine($"reservation commit: {status}");
        return status == StoreStatus.Success ? 0 : 43;
    }
}

static int RunSegmentedPublishWorkflow(string storeName, OpenMode mode)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, mode), out var store);
    Console.WriteLine($"segmented open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 50;
    }

    using (store)
    {
        var status = store.TryPublishSegments([3], new ReadOnlySequence<byte>([1, 2, 3, 4]), [8], out var copied);
        Console.WriteLine($"segmented publish: {status}");
        Console.WriteLine($"segmented copied: {copied}");
        return status == StoreStatus.Success && copied == 4 ? 0 : 51;
    }
}

static int RunRecoveryWorkflow(string storeName, int expectedLeaseRecoveries, int expectedReservationRecoveries)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"recovery open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 60;
    }

    using (store)
    {
        var status = store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var leaseReport);
        Console.WriteLine($"lease recovery: {status}; scanned={leaseReport.ScannedRecordCount}; recovered={leaseReport.RecoveredLeaseCount}; active={leaseReport.ActiveLeaseCount}");
        if (status != StoreStatus.Success || leaseReport.RecoveredLeaseCount < expectedLeaseRecoveries)
        {
            return 61;
        }

        status = store.TryRecoverReservations(new ReservationRecoveryOptions(false), out var reservationReport);
        Console.WriteLine($"reservation recovery: {status}; scanned={reservationReport.ScannedReservationCount}; recovered={reservationReport.RecoveredReservationCount}; active={reservationReport.ActiveReservationCount}");
        return status == StoreStatus.Success && reservationReport.RecoveredReservationCount >= expectedReservationRecoveries ? 0 : 62;
    }
}

static int RunAdvancedWorkflow(string storeName)
{
    var reservation = RunReservationWorkflow(storeName + "-reservation", OpenMode.CreateOrOpen);
    if (reservation != 0)
    {
        return reservation;
    }

    var segmented = RunSegmentedPublishWorkflow(storeName + "-segmented", OpenMode.CreateOrOpen);
    if (segmented != 0)
    {
        return segmented;
    }

    var recovery = RunRecoveryWorkflow(storeName + "-recovery", expectedLeaseRecoveries: 0, expectedReservationRecoveries: 0);
    if (recovery != 0)
    {
        return recovery;
    }

    var diagnostics = RunDiagnosticsWorkflow(storeName + "-diagnostics");
    if (diagnostics != 0)
    {
        return diagnostics;
    }

    Console.WriteLine("docker advanced workflow validation passed");
    return 0;
}

static int RunDiagnosticsWorkflow(string storeName)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"diagnostics open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 63;
    }

    using (store)
    {
        var publish = store.TryPublish([9], [9], [9]);
        Console.WriteLine($"diagnostics publish: {publish}");
        if (publish != StoreStatus.Success && publish != StoreStatus.DuplicateKey)
        {
            return 64;
        }

        var status = store.TryGetDiagnostics(StoreWaitOptions.Default, out var diagnostics);
        Console.WriteLine($"diagnostics snapshot: {status}; free={diagnostics.FreeSlotCount}; active={diagnostics.ActiveLeaseCount}");
        return status == StoreStatus.Success ? 0 : 65;
    }
}

static int RunAbruptLeaseOwner(string storeName)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"abrupt lease owner open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 70;
    }

    var publish = store.TryPublish([4], [4], [4]);
    Console.WriteLine($"abrupt lease owner publish: {publish}");
    if (publish != StoreStatus.Success && publish != StoreStatus.DuplicateKey)
    {
        return 71;
    }

    var acquire = store.TryAcquire([4], out _);
    Console.WriteLine($"abrupt lease owner acquire: {acquire}");
    if (acquire != StoreStatus.Success)
    {
        return 72;
    }

    var remove = store.TryRemove([4]);
    Console.WriteLine($"abrupt lease owner remove while leased: {remove}");
    if (remove != StoreStatus.RemovePending)
    {
        return 73;
    }

    Console.WriteLine("abrupt lease owner exiting without releasing");
    Environment.Exit(0);
    return 0;
}

static int RunAbruptReservationOwner(string storeName)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"abrupt reservation owner open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 74;
    }

    var status = store.TryReserve([5], 4, [5], out var reservation);
    Console.WriteLine($"abrupt reservation owner reserve: {status}");
    if (status != StoreStatus.Success)
    {
        return 75;
    }

    reservation.GetSpan(2).Fill(5);
    status = reservation.Advance(2);
    Console.WriteLine($"abrupt reservation owner advance: {status}");
    if (status != StoreStatus.Success)
    {
        return 76;
    }

    Console.WriteLine("abrupt reservation owner exiting without commit or abort");
    Environment.Exit(0);
    return 0;
}

static async Task<int> RunRecoveryVerifierAsync(string storeName)
{
    var sawLeaseRecovery = false;
    var sawReservationRecovery = false;

    for (var attempt = 0; attempt < 60; attempt++)
    {
        var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.OpenExisting), out var store);
        Console.WriteLine($"recovery verifier open attempt {attempt}: {open}");
        if (open == StoreOpenStatus.Success && store is not null)
        {
            using (store)
            {
                var leaseStatus = store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var leaseReport);
                Console.WriteLine($"recovery verifier lease: {leaseStatus}; recovered={leaseReport.RecoveredLeaseCount}; active={leaseReport.ActiveLeaseCount}; unsupported={leaseReport.UnsupportedLeaseCount}");
                if (leaseStatus != StoreStatus.Success)
                {
                    return 80;
                }

                var reservationStatus = store.TryRecoverReservations(new ReservationRecoveryOptions(false), out var reservationReport);
                Console.WriteLine($"recovery verifier reservation: {reservationStatus}; recovered={reservationReport.RecoveredReservationCount}; active={reservationReport.ActiveReservationCount}; unsupported={reservationReport.UnsupportedReservationCount}");
                if (reservationStatus != StoreStatus.Success)
                {
                    return 81;
                }

                sawLeaseRecovery |= leaseReport.RecoveredLeaseCount > 0;
                sawReservationRecovery |= reservationReport.RecoveredReservationCount > 0;
                if (sawLeaseRecovery && sawReservationRecovery)
                {
                    Console.WriteLine("docker recovery validation passed");
                    return 0;
                }
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    return 82;
}

static async Task<int> RunContentionHolderAsync(string storeName)
{
    if (!OperatingSystem.IsLinux())
    {
        Console.WriteLine("contention holder requires Linux file locking");
        return 90;
    }

    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"contention holder open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 91;
    }

    using (store)
    {
        var path = LinuxSynchronizationPath(storeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Lock(0, 1);
        try
        {
            Console.WriteLine("contention holder locked synchronization");
            await Task.Delay(TimeSpan.FromSeconds(ReadIntEnvironment("SMS_HOLD_SECONDS", 30)));
        }
        finally
        {
            stream.Unlock(0, 1);
        }
    }

    return 0;
}

static async Task<int> RunContentionVerifierAsync(string storeName)
{
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    var canceledOpen = MemoryStore.TryCreateOrOpen(
        Options(storeName, OpenMode.OpenExisting),
        new StoreWaitOptions(TimeSpan.FromSeconds(10), canceled.Token),
        out var canceledStore);
    canceledStore?.Dispose();
    Console.WriteLine($"contention canceled open: {canceledOpen}");
    if (canceledOpen != StoreOpenStatus.OperationCanceled)
    {
        return 100;
    }

    for (var attempt = 0; attempt < 60; attempt++)
    {
        var noWait = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.OpenExisting), StoreWaitOptions.NoWait, out var noWaitStore);
        noWaitStore?.Dispose();
        Console.WriteLine($"contention no-wait open attempt {attempt}: {noWait}");
        if (noWait == StoreOpenStatus.StoreBusy)
        {
            var elapsed = Stopwatch.StartNew();
            var bounded = MemoryStore.TryCreateOrOpen(
                Options(storeName, OpenMode.OpenExisting),
                new StoreWaitOptions(TimeSpan.FromMilliseconds(100)),
                out var boundedStore);
            elapsed.Stop();
            boundedStore?.Dispose();
            Console.WriteLine($"contention bounded open: {bounded}; elapsed={elapsed.ElapsedMilliseconds}ms");

            if (bounded != StoreOpenStatus.StoreBusy)
            {
                return 101;
            }

            if (elapsed.Elapsed > TimeSpan.FromMilliseconds(350))
            {
                return 102;
            }

            Console.WriteLine("docker contention validation passed");
            return 0;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    return 103;
}

static async Task<int> RunDisposalRaceWorkflow(string storeName)
{
    var open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.CreateOrOpen), out var store);
    Console.WriteLine($"disposal race open: {open}");
    if (open != StoreOpenStatus.Success || store is null)
    {
        return 110;
    }

    AssertStatus(StoreStatus.Success, store.TryPublish([1], [1]));
    AssertStatus(StoreStatus.Success, store.TryAcquire([1], out var heldLease));
    AssertStatus(StoreStatus.Success, store.TryReserve([2], 4, default, out var advanceReservation));
    AssertStatus(StoreStatus.Success, store.TryReserve([3], 1, default, out var commitReservation));
    commitReservation.GetSpan()[0] = 3;
    AssertStatus(StoreStatus.Success, commitReservation.Advance(1));
    AssertStatus(StoreStatus.Success, store.TryReserve([4], 1, default, out var abortReservation));
    AssertStatus(StoreStatus.Success, store.TryReserve([5], 1, default, out var disposeReservation));

    const int raceOperationCount = 25_000;
    var exceptions = new ConcurrentQueue<Exception>();
    var completed = 0;
    using var start = new ManualResetEventSlim();

    var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
    {
        start.Wait();
        while (true)
        {
            var operation = Interlocked.Increment(ref completed);
            if (operation > raceOperationCount)
            {
                return;
            }

            try
            {
                RecordDocumentedOutcome(InvokeRaceOperation(
                    store,
                    worker,
                    operation,
                    heldLease,
                    advanceReservation,
                    commitReservation,
                    abortReservation,
                    disposeReservation));
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        }
    })).ToArray();

    start.Set();
    await Task.WhenAll(workers);

    if (!exceptions.IsEmpty)
    {
        foreach (var exception in exceptions)
        {
            Console.WriteLine(exception);
        }

        return 111;
    }

    Console.WriteLine("docker disposal race validation passed");
    return 0;
}

static StoreStatus InvokeRaceOperation(
    MemoryStore store,
    int worker,
    int operation,
    ValueLease heldLease,
    ValueReservation advanceReservation,
    ValueReservation commitReservation,
    ValueReservation abortReservation,
    ValueReservation disposeReservation)
{
    var key = BitConverter.GetBytes(10_000 + operation);
    return ((operation + worker) % 14) switch
    {
        0 => store.TryPublish(key, [(byte)operation]),
        1 => ReserveAndDispose(store, key),
        2 => store.TryAcquire([1], out var lease) == StoreStatus.Success ? lease.Release() : StoreStatus.NotFound,
        3 => store.TryRemove(key),
        4 => store.TryRecoverLeases(new LeaseRecoveryOptions(true), out _),
        5 => store.TryRecoverReservations(new ReservationRecoveryOptions(true), out _),
        6 => ReadDiagnostics(store),
        7 => heldLease.Release(),
        8 => advanceReservation.Advance(0),
        9 => commitReservation.Commit(),
        10 => abortReservation.Abort(),
        11 => DisposeReservation(disposeReservation),
        12 => store.TryPublishSegments(key, new ReadOnlySequence<byte>([(byte)operation]), default, out _),
        _ => DisposeStore(store)
    };
}

static StoreStatus ReserveAndDispose(MemoryStore store, byte[] key)
{
    var status = store.TryReserve(key, 1, default, out var reservation);
    if (status != StoreStatus.Success)
    {
        return status;
    }

    reservation.Dispose();
    return StoreStatus.Success;
}

static StoreStatus ReadDiagnostics(MemoryStore store)
{
    _ = store.GetDiagnostics();
    return StoreStatus.Success;
}

static StoreStatus DisposeReservation(ValueReservation reservation)
{
    reservation.Dispose();
    return StoreStatus.Success;
}

static StoreStatus DisposeStore(MemoryStore store)
{
    store.Dispose();
    return StoreStatus.Success;
}

static void RecordDocumentedOutcome(StoreStatus status)
{
    if (!Enum.IsDefined(status))
    {
        throw new InvalidOperationException("Operation returned an undefined status: " + status);
    }

    if (status == StoreStatus.UnknownFailure)
    {
        throw new InvalidOperationException("Operation returned UnknownFailure during disposal race.");
    }
}

static int RunAllLocalWorkflows(string storeName)
{
    return RunAdvancedWorkflow(storeName);
}

static int Usage()
{
    Console.WriteLine("Usage: DockerSharedMemory [writer|verifier|isolated-profile|reservation|segmented-publish|recovery|advanced|abrupt-lease-owner|abrupt-reservation-owner|recovery-verifier|contention-holder|contention-verifier|disposal-race|all]");
    return 2;
}

static async Task<(StoreOpenStatus Status, MemoryStore? Store)> OpenExistingWithRetryAsync(string storeName)
{
    MemoryStore? store = null;
    StoreOpenStatus open = default;
    for (var attempt = 0; attempt < 60; attempt++)
    {
        open = MemoryStore.TryCreateOrOpen(Options(storeName, OpenMode.OpenExisting), out store);
        if (open == StoreOpenStatus.Success)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    return (open, store);
}

static string LinuxSynchronizationPath(string publicName)
{
    var root = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
    return Path.Combine(root, "SharedMemoryStore", BuildResourceFragment(publicName) + ".lock");
}

static string BuildResourceFragment(string publicName)
{
    var sanitized = new StringBuilder(publicName.Length);
    foreach (var value in publicName)
    {
        sanitized.Append(char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.'
            ? value
            : '_');
    }

    var readable = sanitized.ToString().Trim('_', '.');
    if (readable.Length == 0)
    {
        readable = "store";
    }

    if (readable.Length > 80)
    {
        readable = readable[..80];
    }

    Span<byte> hashBytes = stackalloc byte[32];
    SHA256.HashData(Encoding.UTF8.GetBytes(publicName), hashBytes);
    var hash = Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
    return "sms-" + readable + "-" + hash;
}

static void AssertStatus(StoreStatus expected, StoreStatus actual)
{
    if (actual != expected)
    {
        throw new InvalidOperationException($"Expected {expected} but received {actual}.");
    }
}

static int ReadIntEnvironment(string name, int fallback)
{
    return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0
        ? value
        : fallback;
}
