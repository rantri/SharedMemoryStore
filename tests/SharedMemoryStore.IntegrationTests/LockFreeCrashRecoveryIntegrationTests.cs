using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

[Collection("LockFree crash recovery")]
public sealed class LockFreeCrashRecoveryIntegrationTests
{
    private const int SlotCount = 20;
    private const int LeaseRecordCount = 4;
    private const int ParticipantRecordCount = 8;
    private const int MaxValueBytes = 16;
    private const int MaxDescriptorBytes = 4;
    private const int MaxKeyBytes = 8;
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(20);

    public static TheoryData<int, int> CanonicalCheckpoints
    {
        get
        {
            int configuredCases = GetConfiguredRecoveryCases();
            var entries = LockFreeCheckpointCatalog.Entries;
            var data = new TheoryData<int, int>();
            for (var caseIndex = 0; caseIndex < configuredCases; caseIndex++)
            {
                data.Add(caseIndex, (int)entries[caseIndex % entries.Count].Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalCheckpoints))]
    [Trait("Category", "Integration")]
    [Trait("Category", "CrashRecovery")]
    public async Task EveryCanonicalCheckpointCanBeKilledRecoveredAndFilledToCapacity(
        int caseIndex,
        int checkpointValue)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var checkpoint = (LockFreeCheckpointId)checkpointValue;
        LockFreeCheckpointEntry catalogEntry = LockFreeCheckpointCatalog.Get(checkpoint);
        string name = $"sms-v2-crash-{caseIndex:D5}-{checkpointValue:D2}-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = Options(name, OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out MemoryStore? opened));
        using var store = Assert.IsType<MemoryStore>(opened);

        byte[] tokenKey = Key(0xA0, checkpointValue);
        byte[] existingKey = Key(0xB0, checkpointValue);
        byte[] operationKey = checkpoint
            == LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation
                ? GenerateBucketPairMate(tokenKey, SlotCount)
                : Key(0xC0, checkpointValue);
        byte[] recoveryKey = Key(0xD0, checkpointValue);
        byte[] unrelatedKey = checkpoint
            == LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation
                ? GenerateKeyOutsideBuckets(tokenKey, SlotCount)
                : Key(0xE0, checkpointValue);
        byte[] value = [0x31, unchecked((byte)checkpointValue), 0xC7];
        byte[] descriptor = [0xD5, unchecked((byte)(checkpointValue ^ 0x5A))];

        Assert.Equal(StoreStatus.Success, store.TryPublish(tokenKey, [0x71, 0x72], [0x73]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(tokenKey, out ValueLease liveLease));
        ValueLease staleLiveCopy = liveLease;
        DirectoryReferenceRepair? invalidReferenceRepair = checkpoint
            == LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation
                ? DirectoryReferenceRepair.Capture(store, tokenKey)
                : null;

        if (invalidReferenceRepair is not null)
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish(unrelatedKey, [0xE1, 0xE2]));
        }

        if (NeedsExistingValue(checkpoint))
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish(existingKey, value, descriptor));
        }

        using Process agent = StartAgent(
            name,
            checkpoint,
            tokenKey,
            existingKey,
            operationKey,
            recoveryKey,
            value,
            descriptor);
        try
        {
            CheckpointSignal signal = await ReadCheckpointAsync(agent, checkpoint);
            Assert.Equal(checkpointValue, signal.Id);
            Assert.Equal(checkpoint.ToString(), signal.Name);
            Assert.Equal(catalogEntry.Family.ToString(), signal.Family);
            Assert.Equal(catalogEntry.Position.ToString(), signal.Position);
            Assert.Equal(catalogEntry.Crash.ToString(), signal.Crash);
            Assert.True(signal.ProcessId > 0);
            Assert.False(agent.HasExited, "The child must remain paused in the production checkpoint callback.");

            invalidReferenceRepair?.AssertInvalidReferenceIsPresent();
            if (IsStoreFullProofCheckpoint(checkpoint))
            {
                DiagnosticsSnapshot saturated = store.GetDiagnostics();
                Assert.Equal(0, saturated.FreeSlotCount);
                Assert.Equal(SlotCount, saturated.PublishedSlotCount);
                Assert.Equal(0, saturated.ActiveReservationCount);
                Assert.Equal(0, saturated.InitializingSlotCount);
                Assert.Equal(0, saturated.ReservedSlotCount);
                Assert.Equal(0, saturated.ReclaimingSlotCount);

                RemoveStoreFullFillers(store, checkpoint);
                DiagnosticsSnapshot paused = store.GetDiagnostics();
                Assert.Equal(SlotCount - 1, paused.FreeSlotCount);
                Assert.Equal(0, paused.ActiveReservationCount);
                Assert.Equal(0, paused.InitializingSlotCount);
                Assert.Equal(0, paused.ReservedSlotCount);
                Assert.Equal(0, paused.ReclaimingSlotCount);
            }

            // A stopped participant cannot own unrelated progress.
            if (invalidReferenceRepair is not null)
            {
                Assert.Equal(StoreStatus.Success, store.TryAcquire(unrelatedKey, out ValueLease unrelated));
                Assert.True(unrelated.ValueSpan.SequenceEqual([(byte)0xE1, (byte)0xE2]));
                Assert.Equal(StoreStatus.Success, unrelated.Release());
            }
            else
            {
                Assert.Equal(StoreStatus.Success, store.TryPublish(unrelatedKey, [0xE1, 0xE2]));
                Assert.Equal(StoreStatus.Success, store.TryAcquire(unrelatedKey, out ValueLease unrelated));
                Assert.True(unrelated.ValueSpan.SequenceEqual([(byte)0xE1, (byte)0xE2]));
                Assert.Equal(StoreStatus.Success, unrelated.Release());
                Assert.Equal(StoreStatus.Success, store.TryRemove(unrelatedKey));
            }

            Kill(agent);
            Assert.True(agent.HasExited, "The deterministic checkpoint participant did not terminate.");
            invalidReferenceRepair?.RestoreExactReference();

            RecoveryEvidence recovery = RecoverStoppedOwners(store);
            Assert.Equal(0, recovery.FailedLeaseRecoveries);
            Assert.Equal(0, recovery.FailedReservationRecoveries);
            Assert.True(recovery.ActiveLeases >= 1, "The controller's live lease must remain classified as active.");
            Assert.True(liveLease.IsValid);
            Assert.True(liveLease.ValueSpan.SequenceEqual([(byte)0x71, (byte)0x72]));
            AssertFullParticipantCapacity(name);

            if (IsSpillSummaryCheckpoint(checkpoint))
            {
                DiagnosticsSnapshot beforeMissingLookup = store.GetDiagnostics();
                Assert.Equal(0, beforeMissingLookup.SpilledBucketCount);
                Assert.Equal(0, beforeMissingLookup.OverflowDirectoryOccupancy);

                byte[] missingSpillKey = GenerateBucketPairCollisions(count: 18, slotCount: SlotCount)[17];
                Assert.Equal(StoreStatus.NotFound, store.TryAcquire(missingSpillKey, out _));

                DiagnosticsSnapshot afterMissingLookup = store.GetDiagnostics();
                Assert.Equal(beforeMissingLookup.OverflowScanCount, afterMissingLookup.OverflowScanCount);
            }

            if (signal.LeaseToken != 0)
            {
                var killedToken = new ValueLease(
                    store,
                    new LeaseHandle(
                        signal.StoreId,
                        signal.ParticipantToken,
                        signal.SlotBinding,
                        signal.LeaseToken));
                Assert.False(killedToken.IsValid);
                Assert.NotEqual(StoreStatus.Success, killedToken.Release());
            }

            Assert.Equal(StoreStatus.Success, liveLease.Release());
            Assert.False(staleLiveCopy.IsValid);

            // Reuse the same lease table after recovery and prove a copied old
            // controller token cannot release a later incarnation either.
            Assert.Equal(StoreStatus.Success, store.TryAcquire(tokenKey, out ValueLease reusedLease));
            Assert.NotEqual(StoreStatus.Success, staleLiveCopy.Release());
            Assert.True(reusedLease.IsValid);
            Assert.Equal(StoreStatus.Success, reusedLease.Release());

            RemoveIfPresent(store, tokenKey);
            RemoveIfPresent(store, existingKey);
            RemoveIfPresent(store, operationKey);
            RemoveIfPresent(store, recoveryKey);
            RemoveIfPresent(store, unrelatedKey);
            if (IsSpillSummaryCheckpoint(checkpoint))
            {
                foreach (byte[] spillKey in GenerateBucketPairCollisions(count: 17, slotCount: SlotCount))
                {
                    RemoveIfPresent(store, spillKey);
                }
            }

            AssertFullSlotAndLeaseCapacity(store, checkpointValue);
        }
        finally
        {
            Kill(agent);
            invalidReferenceRepair?.RestoreExactReference();
            if (IsStoreFullProofCheckpoint(checkpoint))
            {
                RemoveStoreFullFillers(store, checkpoint);
            }
        }
    }

    private static RecoveryEvidence RecoverStoppedOwners(MemoryStore store)
    {
        var recoveredLeases = 0;
        var recoveredReservations = 0;
        var activeLeases = 0;
        var failedLeases = 0;
        var failedReservations = 0;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            StoreStatus leaseStatus = store.TryRecoverLeases(
                new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
                out LeaseRecoveryReport leases);
            StoreStatus reservationStatus = store.TryRecoverReservations(
                new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
                out ReservationRecoveryReport reservations);

            Assert.True(
                leaseStatus is StoreStatus.Success or StoreStatus.StoreBusy,
                "Lease recovery returned " + leaseStatus);
            Assert.True(
                reservationStatus is StoreStatus.Success or StoreStatus.StoreBusy,
                "Reservation recovery returned " + reservationStatus);

            recoveredLeases += leases.RecoveredLeaseCount;
            recoveredReservations += reservations.RecoveredReservationCount;
            activeLeases = Math.Max(activeLeases, leases.ActiveLeaseCount);
            failedLeases += leases.FailedRecoveryCount;
            failedReservations += reservations.FailedRecoveryCount;
            if (leaseStatus == StoreStatus.Success && reservationStatus == StoreStatus.Success)
            {
                return new RecoveryEvidence(
                    recoveredLeases,
                    recoveredReservations,
                    activeLeases,
                    failedLeases,
                    failedReservations);
            }
        }

        throw new Xunit.Sdk.XunitException("Recovery did not converge within eight bounded passes.");
    }

    private static void AssertFullSlotAndLeaseCapacity(MemoryStore store, int checkpointValue)
    {
        var fillKeys = new byte[SlotCount][];
        for (var index = 0; index < SlotCount; index++)
        {
            fillKeys[index] = [0xF1, unchecked((byte)checkpointValue), unchecked((byte)index)];
            Assert.Equal(
                StoreStatus.Success,
                RetryStoreBusy(() => store.TryPublish(fillKeys[index], [unchecked((byte)(0x40 + index))])));
        }

        byte[] overflowKey = [0xF2, unchecked((byte)checkpointValue), 0xFF];
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish(overflowKey, [0xFF]));

        var leases = new ValueLease[LeaseRecordCount];
        for (var index = 0; index < leases.Length; index++)
        {
            Assert.Equal(StoreStatus.Success, store.TryAcquire(fillKeys[0], out leases[index]));
            Assert.True(leases[index].IsValid);
        }

        Assert.Equal(StoreStatus.LeaseTableFull, store.TryAcquire(fillKeys[0], out _));
        foreach (ValueLease lease in leases)
        {
            Assert.Equal(StoreStatus.Success, lease.Release());
        }

        foreach (byte[] key in fillKeys)
        {
            Assert.Equal(StoreStatus.Success, RetryStoreBusy(() => store.TryRemove(key)));
        }
    }

    private static void AssertFullParticipantCapacity(string name)
    {
        var handles = new List<MemoryStore>(ParticipantRecordCount - 1);
        try
        {
            for (var index = 1; index < ParticipantRecordCount; index++)
            {
                Assert.Equal(
                    StoreOpenStatus.Success,
                    MemoryStore.TryCreateOrOpen(Options(name, OpenMode.OpenExisting), out MemoryStore? opened));
                handles.Add(Assert.IsType<MemoryStore>(opened));
            }

            Assert.Equal(
                StoreOpenStatus.ParticipantTableFull,
                MemoryStore.TryCreateOrOpen(Options(name, OpenMode.OpenExisting), out MemoryStore? rejected));
            Assert.Null(rejected);
        }
        finally
        {
            foreach (MemoryStore handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    private static void RemoveIfPresent(MemoryStore store, byte[] key)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            StoreStatus status = store.TryRemove(key);
            if (status is StoreStatus.Success or StoreStatus.NotFound)
            {
                return;
            }

            if (status == StoreStatus.RemovePending)
            {
                _ = store.TryRecoverLeases(new LeaseRecoveryOptions(false), out _);
            }

            Assert.True(
                status is StoreStatus.RemovePending or StoreStatus.StoreBusy,
                "Unexpected cleanup status for " + Convert.ToHexString(key) + ": " + status);
            Thread.Yield();
        }

        throw new Xunit.Sdk.XunitException("Key cleanup did not converge: " + Convert.ToHexString(key));
    }

    private static StoreStatus RetryStoreBusy(Func<StoreStatus> operation)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            StoreStatus status = operation();
            if (status != StoreStatus.StoreBusy)
            {
                return status;
            }

            Thread.Yield();
        }

        return StoreStatus.StoreBusy;
    }

    private static async Task<CheckpointSignal> ReadCheckpointAsync(
        Process process,
        LockFreeCheckpointId expected)
    {
        string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(AgentTimeout);
        if (line is null || !line.StartsWith("CHECKPOINT ", StringComparison.Ordinal))
        {
            string error = await process.StandardError.ReadToEndAsync();
            throw new Xunit.Sdk.XunitException(
                "Agent did not reach " + expected
                + ". exit=" + (process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "running")
                + Environment.NewLine + "stdout=" + line
                + Environment.NewLine + "stderr=" + error);
        }

        CheckpointSignal? signal = JsonSerializer.Deserialize<CheckpointSignal>(line["CHECKPOINT ".Length..]);
        return signal ?? throw new Xunit.Sdk.XunitException("Agent emitted an invalid checkpoint signal: " + line);
    }

    private static Process StartAgent(
        string name,
        LockFreeCheckpointId checkpoint,
        byte[] tokenKey,
        byte[] existingKey,
        byte[] operationKey,
        byte[] recoveryKey,
        byte[] value,
        byte[] descriptor)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(LocateAgentAssembly());
        foreach (string argument in new[]
        {
            "checkpoint-crash",
            name,
            SlotCount.ToString(CultureInfo.InvariantCulture),
            MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
            MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
            LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
            ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
            ((int)checkpoint).ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(tokenKey),
            Convert.ToHexString(existingKey),
            Convert.ToHexString(operationKey),
            Convert.ToHexString(recoveryKey),
            Convert.ToHexString(value),
            Convert.ToHexString(descriptor),
            "v1"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the checkpoint crash agent.");
    }

    private static string LocateAgentAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        string path = Path.Combine(
            root,
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Lock-free checkpoint agent was not built.", path);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            SlotCount,
            MaxValueBytes,
            MaxDescriptorBytes,
            MaxKeyBytes,
            LeaseRecordCount,
            ParticipantRecordCount,
            openMode,
            enableLeaseRecovery: true);

    private static int GetConfiguredRecoveryCases()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_LOCK_FREE_RECOVERY_CASES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return LockFreeCheckpointCatalog.Entries.Count;
        }

        if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int cases)
            || cases is < 1 or > 100_000)
        {
            throw new InvalidOperationException(
                "SMS_LOCK_FREE_RECOVERY_CASES must be an integer between 1 and 100000.");
        }

        return cases;
    }

    private static bool NeedsExistingValue(LockFreeCheckpointId checkpoint) => checkpoint is
        LockFreeCheckpointId.AcquireBeforeLeaseClaimCas
        or LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup
        or LockFreeCheckpointId.AcquireAfterPublishedRevalidation
        or LockFreeCheckpointId.ProjectBeforeHandleValidation
        or LockFreeCheckpointId.ProjectAfterMetadataReadBeforeControlRevalidation
        or LockFreeCheckpointId.ProjectAfterSpanProjection
        or LockFreeCheckpointId.ReleaseBeforeActiveReleaseCas
        or LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas
        or LockFreeCheckpointId.ReleaseAfterRecordRecycle
        or LockFreeCheckpointId.ReserveAfterExistingLookup
        or LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas
        or LockFreeCheckpointId.RemoveAfterLeaseClassification
        or LockFreeCheckpointId.ReclaimBeforeOwnershipCas
        or LockFreeCheckpointId.ReclaimAfterGenerationAdvance
        or LockFreeCheckpointId.ReclaimAfterLeaseScanBeforeOwnershipCas
        or LockFreeCheckpointId.DirectoryAfterLocationValidation
        or LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead
        or LockFreeCheckpointId.DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance
        or LockFreeCheckpointId.ReclaimAfterMetadataValidation;

    private static bool IsSpillSummaryCheckpoint(LockFreeCheckpointId checkpoint) => checkpoint is
        LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas
        or LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication
        or LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan
        or LockFreeCheckpointId.DirectoryAfterSpillSummaryClear;

    private static bool IsStoreFullProofCheckpoint(LockFreeCheckpointId checkpoint) => checkpoint is
        LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification
        or LockFreeCheckpointId.StoreFullAfterExactDoubleCollect;

    private static void RemoveStoreFullFillers(
        MemoryStore store,
        LockFreeCheckpointId checkpoint)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            RemoveIfPresent(store, CreateStoreFullFillerKey(checkpoint, index));
        }
    }

    private static byte[] CreateStoreFullFillerKey(
        LockFreeCheckpointId checkpoint,
        int index) =>
        BitConverter.GetBytes(
            0x6f00_0000_0000_0000UL
            | ((ulong)(byte)checkpoint << 48)
            | checked((uint)(index + 1)));

    private static byte[][] GenerateBucketPairCollisions(int count, int slotCount)
    {
        var keys = new List<byte[]>(count);
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        for (long candidate = 1; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            ulong hash = StoreKey.Hash(key);
            int first = (int)(Mix(hash) & bucketMask);
            int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & bucketMask);
            if (second == first)
            {
                second = (first + 1) & (int)bucketMask;
            }

            if (first == 0 && second == 1)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static byte[] GenerateKeyOutsideBuckets(byte[] excludedKey, int slotCount)
    {
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        GetBuckets(StoreKey.Hash(excludedKey), bucketMask, out int excludedFirst, out int excludedSecond);
        for (long candidate = 1; ; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            GetBuckets(StoreKey.Hash(key), bucketMask, out int first, out int second);
            if (first != excludedFirst
                && first != excludedSecond
                && second != excludedFirst
                && second != excludedSecond)
            {
                return key;
            }
        }
    }

    private static byte[] GenerateBucketPairMate(byte[] anchorKey, int slotCount)
    {
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        GetBuckets(StoreKey.Hash(anchorKey), bucketMask, out int anchorFirst, out int anchorSecond);
        for (long candidate = 1; ; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            GetBuckets(StoreKey.Hash(key), bucketMask, out int first, out int second);
            if (first == anchorFirst && second == anchorSecond)
            {
                return key;
            }
        }
    }

    private static void GetBuckets(ulong hash, uint bucketMask, out int first, out int second)
    {
        first = (int)(Mix(hash) & bucketMask);
        second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & bucketMask);
        if (second == first)
        {
            second = (first + 1) & (int)bucketMask;
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static byte[] Key(byte prefix, int checkpointValue) =>
        [prefix, unchecked((byte)checkpointValue)];

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
            // Unique mappings make best-effort teardown safe after assertion failures.
        }
    }

    private sealed class DirectoryReferenceRepair
    {
        private readonly MemoryMappedStoreRegion _region;
        private readonly StoreLayoutV2 _layout;
        private readonly DirectoryLocation _location;
        private readonly ulong _exactBinding;
        private readonly ulong _invalidBinding;
        private int _repairCompleted;

        private DirectoryReferenceRepair(
            MemoryMappedStoreRegion region,
            StoreLayoutV2 layout,
            DirectoryLocation location,
            ulong exactBinding)
        {
            _region = region;
            _layout = layout;
            _location = location;
            _exactBinding = exactBinding;
            IndexBinding decoded = IndexBinding.Decode(exactBinding);
            _invalidBinding = IndexBinding.Encode(
                decoded.SlotIndex,
                checked(decoded.Generation + 1));
        }

        internal static DirectoryReferenceRepair Capture(MemoryStore store, byte[] key)
        {
            object engine = ReadPrivate<object>(store, "_engine");
            LockFreeKeyDirectory directory = ReadPrivate<LockFreeKeyDirectory>(engine, "_directory");
            MemoryMappedStoreRegion region = ReadPrivate<MemoryMappedStoreRegion>(engine, "_region");
            StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(engine, "_layout");
            StoreStatus lookup = directory.TryLookup(
                key,
                StoreKey.Hash(key),
                out ulong binding,
                out DirectoryLocation location);
            Assert.Equal(StoreStatus.Success, lookup);
            return new DirectoryReferenceRepair(region, layout, location, binding);
        }

        internal void AssertInvalidReferenceIsPresent() =>
            Assert.Equal(_invalidBinding, ReadReference());

        internal void RestoreExactReference()
        {
            if (Interlocked.Exchange(ref _repairCompleted, 1) != 0)
            {
                return;
            }

            long observed = AtomicControlWord.CompareExchange(
                ref Cell(),
                unchecked((long)_exactBinding),
                unchecked((long)_invalidBinding));
            ulong raw = unchecked((ulong)observed);
            Assert.True(
                raw == _invalidBinding || raw == _exactBinding,
                "The injected directory reference changed before controller repair. observed=" + raw);
        }

        private ulong ReadReference() =>
            unchecked((ulong)AtomicControlWord.LoadAcquire(ref Cell()));

        private unsafe ref long Cell()
        {
            long offset = _location.Kind switch
            {
                1 => PrimaryCellOffset(_layout, checked((int)_location.Index)),
                2 => _layout.OverflowDirectoryOffset + (_location.Index * _layout.OverflowStride),
                _ => throw new InvalidOperationException("The captured directory location is invalid.")
            };
            return ref *(long*)(_region.Pointer + offset);
        }

        private static long PrimaryCellOffset(StoreLayoutV2 layout, int absoluteCellIndex)
        {
            int bucket = absoluteCellIndex / LayoutV2Constants.PrimaryLanesPerBucket;
            int lane = absoluteCellIndex % LayoutV2Constants.PrimaryLanesPerBucket;
            return layout.PrimaryDirectoryOffset
                + ((long)bucket * layout.PrimaryBucketStride)
                + 16
                + (lane * sizeof(long));
        }

        private static T ReadPrivate<T>(object owner, string fieldName)
        {
            FieldInfo field = owner.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "Missing field " + owner.GetType().FullName + "." + fieldName + ".");
            return Assert.IsAssignableFrom<T>(field.GetValue(owner));
        }
    }

    private sealed record CheckpointSignal(
        int Id,
        string Name,
        string Family,
        string Position,
        string Crash,
        int ProcessId,
        ulong StoreId,
        ulong ParticipantToken,
        ulong SlotBinding,
        ulong LeaseToken);

    private readonly record struct RecoveryEvidence(
        int RecoveredLeases,
        int RecoveredReservations,
        int ActiveLeases,
        int FailedLeaseRecoveries,
        int FailedReservationRecoveries);
}

[CollectionDefinition("LockFree crash recovery", DisableParallelization = true)]
public sealed class LockFreeCrashRecoveryCollection;
