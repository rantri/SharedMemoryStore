using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.LockFreeAgent;

/// <summary>
/// Cross-process deterministic checkpoint participant. The command deliberately
/// blocks inside the production checkpoint callback so the controller can
/// terminate the process at the exact protocol transition.
/// </summary>
internal static class CheckpointCrashCommands
{
    private const int InvalidArgumentsExitCode = 64;
    private const int OperationFailureExitCode = 66;
    private const int CheckpointNotReachedExitCode = 68;

    internal static int Run(string[] arguments)
    {
        if (!Arguments.TryParse(arguments, out Arguments parsed))
        {
            return InvalidArgumentsExitCode;
        }

        bool suspensionProbe = string.Equals(arguments[0], "checkpoint-pause", StringComparison.Ordinal);
        LockFreeCheckpointEntry target;
        try
        {
            target = LockFreeCheckpointCatalog.Get(parsed.Checkpoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return InvalidArgumentsExitCode;
        }

        var armed = target.Family == LockFreeCheckpointFamily.Participant;
        var reached = 0;
        Action? beforePause = null;
        Action? afterContinue = null;
        CheckpointPreparation? preparation = null;
        LeaseHandle capturedLease = default;
        var checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            preparation?.Observe(entry.Id);
            if (!armed || entry.Id != target.Id || Interlocked.Exchange(ref reached, 1) != 0)
            {
                return;
            }

            // A suspension probe measures healthy-process progress while this
            // participant is stopped. Test-only corruption used to reach a
            // validation checkpoint must therefore be repaired before the
            // controller starts that measurement window. Crash probes retain
            // the injected word so recovery can verify the repair path.
            beforePause?.Invoke();

            var signal = new CheckpointSignal(
                (int)entry.Id,
                entry.Id.ToString(),
                entry.Family.ToString(),
                entry.Position.ToString(),
                entry.Crash.ToString(),
                Environment.ProcessId,
                capturedLease.StoreId,
                capturedLease.ParticipantToken,
                capturedLease.SlotBinding,
                capturedLease.LeaseToken);
            Console.WriteLine("CHECKPOINT " + JsonSerializer.Serialize(signal));
            Console.Out.Flush();

            string? command = Console.ReadLine();
            if (!string.Equals(command, "CONTINUE", StringComparison.Ordinal))
            {
                Environment.Exit(CheckpointNotReachedExitCode);
            }

            afterContinue?.Invoke();
        });

        if (target.Id == LockFreeCheckpointId.ParticipantAfterRecoveryFenceBeforeReferenceScan
            && !CreateDefiniteStaleParticipant(parsed.Options))
        {
            return OperationFailureExitCode;
        }

        StoreOpenStatus open = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            parsed.Options,
            checkpoint,
            out MemoryStore? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            Console.Error.WriteLine("Open failed: " + open);
            return OperationFailureExitCode;
        }

        using (store)
        {
            if (target.Family != LockFreeCheckpointFamily.Participant)
            {
                StoreStatus capture = store.TryAcquire(parsed.TokenKey, out ValueLease lease);
                if (capture != StoreStatus.Success)
                {
                    Console.Error.WriteLine("Token lease acquire failed: " + capture);
                    return OperationFailureExitCode;
                }

                capturedLease = lease.HandleForEngine;
                armed = true;
            }

            StoreStatus status = ExecuteTarget(
                store,
                target.Id,
                parsed,
                suspensionProbe,
                (id, action) => preparation = new CheckpointPreparation(id, action),
                continuation => beforePause = continuation,
                continuation => afterContinue = continuation);
            if (Volatile.Read(ref reached) == 0)
            {
                Console.Error.WriteLine("Target checkpoint was not reached: " + target.Id);
                return CheckpointNotReachedExitCode;
            }

            bool expectedStoreFull = status == StoreStatus.StoreFull
                && target.Id is LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification
                    or LockFreeCheckpointId.StoreFullAfterExactDoubleCollect;
            if (status is not (StoreStatus.Success
                    or StoreStatus.RemovePending
                    or StoreStatus.DuplicateKey)
                && !expectedStoreFull
                && !(suspensionProbe && status == StoreStatus.StoreBusy))
            {
                Console.Error.WriteLine("Target operation failed after continue: " + status);
                return OperationFailureExitCode;
            }

            Console.WriteLine("OK " + (suspensionProbe ? "checkpoint-pause " : "checkpoint-crash ") + target.Id);
            return 0;
        }
    }

    private static StoreStatus ExecuteTarget(
        MemoryStore store,
        LockFreeCheckpointId checkpoint,
        in Arguments parsed,
        bool suspensionProbe,
        Action<LockFreeCheckpointId, Action> setPreparation,
        Action<Action> setBeforePause,
        Action<Action> setAfterContinue)
    {
        switch (checkpoint)
        {
            case LockFreeCheckpointId.PublishBeforeSlotClaim:
            case LockFreeCheckpointId.PublishAfterCommitPublication:
                return store.TryPublish(parsed.OperationKey, parsed.Value, parsed.Descriptor);

            case LockFreeCheckpointId.ReserveBeforeSlotClaim:
            case LockFreeCheckpointId.ReserveAfterReservationPublication:
            case LockFreeCheckpointId.SlotClaimAfterParticipantRecheck:
                return store.TryReserve(
                    parsed.OperationKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out _);

            case LockFreeCheckpointId.ReserveAfterExistingLookup:
                return store.TryReserve(
                    parsed.ExistingKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out _);

            case LockFreeCheckpointId.CommitBeforePublicationCas:
            case LockFreeCheckpointId.CommitAfterPublicationCas:
            {
                StoreStatus reserve = PrepareReservation(store, parsed.OperationKey, parsed, out ValueReservation reservation);
                return reserve == StoreStatus.Success ? reservation.Commit() : reserve;
            }

            case LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas:
            case LockFreeCheckpointId.AdvanceAfterBytesAdvancedCas:
            {
                StoreStatus reserve = store.TryReserve(
                    parsed.OperationKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out ValueReservation reservation);
                if (reserve != StoreStatus.Success)
                {
                    return reserve;
                }

                parsed.Value.CopyTo(reservation.GetSpan(parsed.Value.Length));
                return reservation.Advance(parsed.Value.Length);
            }

            case LockFreeCheckpointId.AbortBeforeAbortCas:
            case LockFreeCheckpointId.AbortAfterOwnershipReleaseCas:
            case LockFreeCheckpointId.AbortAfterUnlinkCompletion:
            {
                StoreStatus reserve = store.TryReserve(
                    parsed.OperationKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out ValueReservation reservation);
                return reserve == StoreStatus.Success ? reservation.Abort() : reserve;
            }

            case LockFreeCheckpointId.AcquireBeforeLeaseClaimCas:
            case LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup:
            case LockFreeCheckpointId.AcquireAfterPublishedRevalidation:
                return store.TryAcquire(parsed.ExistingKey, out _);

            case LockFreeCheckpointId.ProjectBeforeHandleValidation:
            case LockFreeCheckpointId.ProjectAfterMetadataReadBeforeControlRevalidation:
            case LockFreeCheckpointId.ProjectAfterSpanProjection:
            {
                StoreStatus acquire = store.TryAcquire(parsed.ExistingKey, out ValueLease lease);
                if (acquire != StoreStatus.Success)
                {
                    return acquire;
                }

                _ = lease.ValueSpan.Length;
                return lease.Release();
            }

            case LockFreeCheckpointId.ReleaseBeforeActiveReleaseCas:
            case LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas:
            case LockFreeCheckpointId.ReleaseAfterRecordRecycle:
            {
                StoreStatus acquire = store.TryAcquire(parsed.ExistingKey, out ValueLease lease);
                return acquire == StoreStatus.Success ? lease.Release() : acquire;
            }

            case LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas:
            case LockFreeCheckpointId.RemoveAfterLeaseClassification:
            case LockFreeCheckpointId.ReclaimBeforeOwnershipCas:
            case LockFreeCheckpointId.ReclaimAfterGenerationAdvance:
            case LockFreeCheckpointId.ReclaimAfterLeaseScanBeforeOwnershipCas:
            case LockFreeCheckpointId.DirectoryAfterLocationValidation:
            case LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead:
            case LockFreeCheckpointId.DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance:
            case LockFreeCheckpointId.ReclaimAfterMetadataValidation:
                return store.TryRemove(parsed.ExistingKey);

            case LockFreeCheckpointId.DirectoryBeforeDescriptorPublication:
            case LockFreeCheckpointId.DirectoryAfterDescriptorClear:
            case LockFreeCheckpointId.DirectoryAfterOperationValidation:
            case LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation:
            case LockFreeCheckpointId.DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas:
            case LockFreeCheckpointId.DirectoryAfterLocationPublicationBeforeSourceRevalidation:
            case LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch:
            case LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication:
            case LockFreeCheckpointId.DirectoryAfterInsertCompletionStateValidationBeforeLocationRead:
            case LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck:
            case LockFreeCheckpointId.ReserveAfterDirectoryInsertBeforePendingClassification:
            {
                StoreStatus reserve = store.TryReserve(
                    parsed.OperationKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out ValueReservation reservation);
                if (reserve != StoreStatus.Success)
                {
                    return reserve;
                }

                return reservation.Abort();
            }

            case LockFreeCheckpointId.DirectoryAfterCancelLocationClearBeforeDescriptorRejection:
            {
                // This checkpoint describes a cancellation racing the insert
                // helper, rather than a state reached by a single ordinary
                // reserve/abort call.  Turn the exact reservation Aborting at
                // the helper's freshly revalidated dispatch boundary.  The
                // same production helper then enters CancelInsert and leaves
                // a genuine cross-process crash-recovery state.
                byte[] operationKey = parsed.OperationKey;
                setPreparation(
                    LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch,
                    () => BeginAbortInFlightReservation(store, operationKey));
                StoreStatus reserve = store.TryReserve(
                    parsed.OperationKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out ValueReservation reservation);
                return reserve == StoreStatus.Success ? reservation.Abort() : reserve;
            }

            case LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas:
            case LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication:
            case LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan:
            case LockFreeCheckpointId.DirectoryAfterSpillSummaryClear:
                return ExecuteSpillSummaryTarget(
                    store,
                    checkpoint,
                    parsed.Options.SlotCount,
                    parsed.SpillFirstBucket,
                    parsed.SpillSecondBucket);

            case LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation:
                return ExecuteInvalidReferenceTarget(
                    store,
                    parsed.TokenKey,
                    parsed.Options.SlotCount,
                    parsed.Value,
                    parsed.Descriptor,
                    suspensionProbe,
                    setPreparation,
                    setBeforePause,
                    setAfterContinue);

            case LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification:
            case LockFreeCheckpointId.StoreFullAfterExactDoubleCollect:
                return ExecuteStoreFullTarget(store, checkpoint, parsed, setAfterContinue);

            case LockFreeCheckpointId.DiagnosticsBeforeBoundedScan:
            case LockFreeCheckpointId.DiagnosticsAfterSnapshotAssembly:
                return store.TryGetDiagnostics(out _);

            case LockFreeCheckpointId.RecoveryBeforeOwnerClassification:
            case LockFreeCheckpointId.RecoveryAfterExactRecoveryCas:
            {
                StoreStatus reserve = store.TryReserve(
                    parsed.RecoveryKey,
                    parsed.Value.Length,
                    parsed.Descriptor,
                    out _);
                if (reserve != StoreStatus.Success)
                {
                    return reserve;
                }

                return store.TryRecoverReservations(
                    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: true),
                    out _);
            }

            case LockFreeCheckpointId.ParticipantAfterRecoveryFenceBeforeReferenceScan:
                return store.TryRecoverReservations(
                    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
                    out _);

            case LockFreeCheckpointId.DisposalBeforeLocalGateClose:
            case LockFreeCheckpointId.DisposalAfterParticipantClosingPublication:
            case LockFreeCheckpointId.DisposalAfterParticipantRelease:
            case LockFreeCheckpointId.ParticipantBeforeReclaimGenerationAdvanceCas:
                store.Dispose();
                return StoreStatus.Success;

            case LockFreeCheckpointId.ParticipantBeforeRegisteringCas:
            case LockFreeCheckpointId.ParticipantAfterIdentityKindWrite:
            case LockFreeCheckpointId.ParticipantAfterReservedWrite:
            case LockFreeCheckpointId.ParticipantAfterProcessStartWrite:
            case LockFreeCheckpointId.ParticipantAfterPidNamespaceWrite:
            case LockFreeCheckpointId.ParticipantAfterOpenSequenceWrite:
            case LockFreeCheckpointId.ParticipantAfterActivePublication:
            case LockFreeCheckpointId.ParticipantAfterRegistrationBeforeEngineConstruction:
                return StoreStatus.Success;

            default:
                return StoreStatus.UnknownFailure;
        }
    }

    private static StoreStatus ExecuteInvalidReferenceTarget(
        MemoryStore store,
        byte[] key,
        int slotCount,
        byte[] value,
        byte[] descriptor,
        bool suspensionProbe,
        Action<LockFreeCheckpointId, Action> setPreparation,
        Action<Action> setBeforePause,
        Action<Action> setAfterContinue)
    {
        DirectoryReferenceMutation mutation = DirectoryReferenceMutation.Capture(store, key);
        setPreparation(
            LockFreeCheckpointId.ReserveBeforeSlotClaim,
            mutation.InjectInvalidReference);
        if (suspensionProbe)
        {
            setBeforePause(mutation.RestoreExactReference);
        }

        setAfterContinue(mutation.RestoreExactReference);
        byte[] collisionKey = GenerateBucketPairMate(key, slotCount);
        StoreStatus publish = store.TryPublish(collisionKey, value, descriptor);
        return publish == StoreStatus.Success ? store.TryRemove(collisionKey) : publish;
    }

    private static StoreStatus ExecuteStoreFullTarget(
        MemoryStore store,
        LockFreeCheckpointId checkpoint,
        in Arguments parsed,
        Action<Action> setAfterContinue)
    {
        var resumed = 0;
        setAfterContinue(() => Volatile.Write(ref resumed, 1));
        for (var index = 0; index < parsed.Options.SlotCount; index++)
        {
            byte[] key = CreateStoreFullFillerKey(checkpoint, index);
            StoreStatus publish = store.TryPublish(key, parsed.Value, parsed.Descriptor);
            if (Volatile.Read(ref resumed) != 0)
            {
                if (publish == StoreStatus.Success)
                {
                    return store.TryRemove(key);
                }

                return publish;
            }

            if (publish != StoreStatus.Success)
            {
                return publish;
            }
        }

        return store.TryPublish(parsed.OperationKey, parsed.Value, parsed.Descriptor);
    }

    private static byte[] CreateStoreFullFillerKey(
        LockFreeCheckpointId checkpoint,
        int index) =>
        BitConverter.GetBytes(
            0x6f00_0000_0000_0000UL
            | ((ulong)(byte)checkpoint << 48)
            | checked((uint)(index + 1)));

    private static bool CreateDefiniteStaleParticipant(SharedMemoryStoreOptions options)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(CheckpointCrashCommands).Assembly.Location);
        foreach (string argument in new[]
        {
            "participant-orphan",
            options.Name,
            options.SlotCount.ToString(CultureInfo.InvariantCulture),
            options.MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            options.MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
            options.MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
            options.LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
            options.ParticipantRecordCount.ToString(CultureInfo.InvariantCulture)
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start stale-participant helper.");
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            Console.Error.WriteLine(
                "Stale-participant helper failed: exit="
                + (process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "timeout")
                + " stderr=" + process.StandardError.ReadToEnd());
            return false;
        }

        return true;
    }

    private static StoreStatus ExecuteSpillSummaryTarget(
        MemoryStore store,
        LockFreeCheckpointId checkpoint,
        int slotCount,
        int firstBucket,
        int secondBucket)
    {
        byte[][] keys = GenerateBucketPairCollisions(
            count: 17,
            slotCount: slotCount,
            firstBucket: firstBucket,
            secondBucket: secondBucket);
        for (var index = 0; index < 16; index++)
        {
            StoreStatus seed = store.TryPublish(keys[index], [unchecked((byte)index)]);
            if (seed != StoreStatus.Success)
            {
                return seed;
            }
        }

        StoreStatus publish = store.TryPublish(keys[16], [0xA5]);
        if (publish != StoreStatus.Success
            || checkpoint is LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas
                or LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication)
        {
            return publish;
        }

        return store.TryRemove(keys[16]);
    }

    private static byte[][] GenerateBucketPairCollisions(
        int count,
        int slotCount,
        int firstBucket,
        int secondBucket)
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

            if (first == firstBucket && second == secondBucket)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static byte[] GenerateBucketPairMate(byte[] anchorKey, int slotCount)
    {
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        GetBuckets(StoreKey.Hash(anchorKey), bucketMask, out int anchorFirst, out int anchorSecond);
        for (long candidate = 1; ; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            if (key.AsSpan().SequenceEqual(anchorKey))
            {
                continue;
            }

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

    private static StoreStatus PrepareReservation(
        MemoryStore store,
        byte[] key,
        in Arguments parsed,
        out ValueReservation reservation)
    {
        StoreStatus reserve = store.TryReserve(
            key,
            parsed.Value.Length,
            parsed.Descriptor,
            out reservation);
        if (reserve != StoreStatus.Success)
        {
            return reserve;
        }

        parsed.Value.CopyTo(reservation.GetSpan(parsed.Value.Length));
        return reservation.Advance(parsed.Value.Length);
    }

    private static void BeginAbortInFlightReservation(MemoryStore store, byte[] key)
    {
        object engine = ReadPrivate<object>(store, "_engine");
        LockFreeSlotTable slots = ReadPrivate<LockFreeSlotTable>(engine, "_slots");
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(engine, "_layout");
        ulong storeId = ReadPrivate<ulong>(slots, "_storeId");
        ulong keyHash = StoreKey.Hash(key);
        for (var slotIndex = 0; slotIndex < layout.SlotCount; slotIndex++)
        {
            ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
            long control = AtomicControlWord.LoadAcquire(ref slot.Control);
            if ((unchecked((ulong)control) & 0x7UL) != LockFreeSlotTable.InitializingState
                || Volatile.Read(ref slot.KeyHash) != keyHash
                || Volatile.Read(ref slot.KeyLength) != key.Length)
            {
                continue;
            }

            ulong binding = Volatile.Read(ref slot.DirectoryBinding);
            IndexBinding decoded = IndexBinding.Decode(binding);
            if (decoded.SlotIndex != slotIndex)
            {
                continue;
            }

            var handle = new ReservationHandle(
                storeId,
                unchecked((ulong)control) >> 36,
                binding,
                Volatile.Read(ref slot.ValueLength));
            if (!slots.GetInitializingKeySpan(handle).SequenceEqual(key))
            {
                continue;
            }

            StoreStatus beginAbort = slots.TryBeginAbort(handle);
            if (beginAbort != StoreStatus.Success)
            {
                throw new InvalidOperationException(
                    "Unable to begin the deterministic insert cancellation: " + beginAbort);
            }

            return;
        }

        throw new InvalidOperationException(
            "Unable to locate the in-flight insert before cancellation.");
    }

    private static T ReadPrivate<T>(object owner, string fieldName)
    {
        FieldInfo field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Missing field " + owner.GetType().FullName + "." + fieldName + ".");
        return field.GetValue(owner) is T value
            ? value
            : throw new InvalidOperationException(
                "Unexpected value in " + owner.GetType().FullName + "." + fieldName + ".");
    }

    private sealed class DirectoryReferenceMutation
    {
        private readonly MemoryMappedStoreRegion _region;
        private readonly StoreLayoutV2 _layout;
        private readonly DirectoryLocation _location;
        private readonly ulong _exactBinding;
        private readonly ulong _invalidBinding;

        private DirectoryReferenceMutation(
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

        internal static DirectoryReferenceMutation Capture(MemoryStore store, byte[] key)
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
            if (lookup != StoreStatus.Success)
            {
                throw new InvalidOperationException("Unable to capture directory reference: " + lookup);
            }

            return new DirectoryReferenceMutation(region, layout, location, binding);
        }

        internal void InjectInvalidReference()
        {
            long observed = AtomicControlWord.CompareExchange(
                ref Cell(),
                unchecked((long)_invalidBinding),
                unchecked((long)_exactBinding));
            if (unchecked((ulong)observed) != _exactBinding)
            {
                throw new InvalidOperationException("The directory reference changed before invalid-reference injection.");
            }
        }

        internal void RestoreExactReference()
        {
            long observed = AtomicControlWord.CompareExchange(
                ref Cell(),
                unchecked((long)_exactBinding),
                unchecked((long)_invalidBinding));
            ulong raw = unchecked((ulong)observed);
            if (raw != _invalidBinding && raw != _exactBinding)
            {
                throw new InvalidOperationException("The injected directory reference changed before restoration.");
            }
        }

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

    }

    private sealed class CheckpointPreparation
    {
        private readonly LockFreeCheckpointId _checkpoint;
        private readonly Action _action;
        private int _applied;

        internal CheckpointPreparation(LockFreeCheckpointId checkpoint, Action action)
        {
            _checkpoint = checkpoint;
            _action = action;
        }

        internal void Observe(LockFreeCheckpointId checkpoint)
        {
            if (checkpoint == _checkpoint
                && Interlocked.CompareExchange(ref _applied, 1, 0) == 0)
            {
                _action();
            }
        }
    }

    private readonly record struct CheckpointSignal(
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

    private readonly record struct Arguments(
        SharedMemoryStoreOptions Options,
        LockFreeCheckpointId Checkpoint,
        byte[] TokenKey,
        byte[] ExistingKey,
        byte[] OperationKey,
        byte[] RecoveryKey,
        byte[] Value,
        byte[] Descriptor,
        int SpillFirstBucket,
        int SpillSecondBucket)
    {
        internal static bool TryParse(string[] values, out Arguments parsed)
        {
            parsed = default;
            bool pauseProtocol = values.Length > 0
                && string.Equals(values[0], "checkpoint-pause", StringComparison.Ordinal);
            int expectedLength = pauseProtocol ? 18 : 16;
            int versionIndex = pauseProtocol ? 17 : 15;
            if (values.Length != expectedLength
                || string.IsNullOrWhiteSpace(values[1])
                || !TryPositive(values[2], out int slotCount)
                || !TryPositive(values[3], out int maxValueBytes)
                || !TryNonNegative(values[4], out int maxDescriptorBytes)
                || !TryPositive(values[5], out int maxKeyBytes)
                || !TryPositive(values[6], out int leaseRecordCount)
                || !TryPositive(values[7], out int participantRecordCount)
                || !int.TryParse(values[8], NumberStyles.None, CultureInfo.InvariantCulture, out int checkpointValue)
                || !Enum.IsDefined(typeof(LockFreeCheckpointId), checkpointValue)
                || !TryDecode(values[9], out byte[] tokenKey)
                || !TryDecode(values[10], out byte[] existingKey)
                || !TryDecode(values[11], out byte[] operationKey)
                || !TryDecode(values[12], out byte[] recoveryKey)
                || !TryDecode(values[13], out byte[] value)
                || !TryDecode(values[14], out byte[] descriptor)
                || values[versionIndex] != (pauseProtocol ? "v2" : "v1")
                || tokenKey.Length is 0
                || existingKey.Length is 0
                || operationKey.Length is 0
                || recoveryKey.Length is 0
                || tokenKey.Length > maxKeyBytes
                || existingKey.Length > maxKeyBytes
                || operationKey.Length > maxKeyBytes
                || recoveryKey.Length > maxKeyBytes
                || value.Length > maxValueBytes
                || descriptor.Length > maxDescriptorBytes)
            {
                return false;
            }

            int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
            int primaryBucketCount = primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket;
            int spillFirstBucket = 0;
            int spillSecondBucket = 1;
            if (pauseProtocol
                && (!TryNonNegative(values[15], out spillFirstBucket)
                    || !TryNonNegative(values[16], out spillSecondBucket)
                    || spillFirstBucket == spillSecondBucket
                    || spillFirstBucket >= primaryBucketCount
                    || spillSecondBucket >= primaryBucketCount))
            {
                return false;
            }

            var options = SharedMemoryStoreOptions.CreateLockFree(
                values[1],
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                participantRecordCount,
                OpenMode.OpenExisting,
                enableLeaseRecovery: true);
            parsed = new Arguments(
                options,
                (LockFreeCheckpointId)checkpointValue,
                tokenKey,
                existingKey,
                operationKey,
                recoveryKey,
                value,
                descriptor,
                spillFirstBucket,
                spillSecondBucket);
            return true;
        }

        private static bool TryDecode(string text, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromHexString(text);
                return true;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }

        private static bool TryPositive(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

        private static bool TryNonNegative(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }
}
