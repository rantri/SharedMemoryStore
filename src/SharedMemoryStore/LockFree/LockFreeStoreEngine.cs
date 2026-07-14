using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Non-generic construction and pure-policy facade. Ordinary construction is
/// permanently closed over the empty checkpoint strategy; friend-only
/// instrumented construction selects its own closed engine explicitly.
/// </summary>
internal static class LockFreeStoreEngine
{
    internal static StoreOpenStatus TryCreateOrOpenUnderColdGate(
        SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        long waitStartTimestamp,
        MemoryMappedStoreRegion region,
        ISharedStoreSynchronization coldSynchronization,
        RegionOpenDisposition disposition,
        out IStoreEngine? engine)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        StoreOpenStatus status = LockFreeStoreEngine<NoOpLockFreeCheckpoint>.TryCreateOrOpenUnderColdGate(
            options,
            waitOptions,
            waitStartTimestamp,
            region,
            coldSynchronization,
            disposition,
            checkpoint,
            out LockFreeStoreEngine<NoOpLockFreeCheckpoint>? concrete);
        engine = concrete;
        return status;
    }

    internal static StoreStatus NormalizePostLogicalRemoveOutcome(StoreStatus reclaimStatus) =>
        reclaimStatus switch
        {
            StoreStatus.Success => StoreStatus.Success,
            StoreStatus.CorruptStore => StoreStatus.CorruptStore,
            _ => StoreStatus.RemovePending
        };

    /// <summary>
    /// A generation that is still being removed remains a duplicate from a
    /// publisher's point of view. Operational and structural failures are not
    /// lifecycle states and must retain their exact status.
    /// </summary>
    internal static StoreStatus NormalizeExistingGenerationReclaimOutcome(StoreStatus reclaimStatus) =>
        reclaimStatus switch
        {
            StoreStatus.Success => StoreStatus.Success,
            StoreStatus.RemovePending or StoreStatus.NotFound => StoreStatus.DuplicateKey,
            _ => reclaimStatus
        };
}

/// <summary>
/// Layout-v2 engine foundation. This slice owns header compatibility and
/// participant lifetime; data operations are added by the following story
/// phases without changing the public facade.
/// </summary>
internal sealed unsafe class LockFreeStoreEngine<TCheckpoint> : IStoreEngine, ILockFreeCheckpointEmitter
    where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
{
    private readonly MemoryMappedStoreRegion _region;
    private readonly ISharedStoreSynchronization _coldSynchronization;
    private readonly StoreLayoutV2 _layout;
    private readonly LockFreeStoreControl _storeControl;
    private readonly LockFreeParticipantRegistry _participants;
    private readonly LockFreeParticipantRegistry.Registration _registration;
    private readonly LockFreeSlotTable _slots;
    private readonly LockFreeKeyDirectory _directory;
    private readonly LockFreeLeaseRegistry _leases;
    private readonly LockFreeReclaimer _reclaimer;
    private readonly LockFreeRecovery _recovery;
    private readonly LockFreeDiagnostics _diagnostics;
    private readonly LockFreeReservationMemory _reservationMemory;
    private readonly StoreProtocolInfo _protocolInfo;
    private readonly bool _recoveryEnabled;
    private TCheckpoint _checkpoint;
    private int _disposed;

    private LockFreeStoreEngine(
        MemoryMappedStoreRegion region,
        ISharedStoreSynchronization coldSynchronization,
        StoreLayoutV2 layout,
        LockFreeStoreControl storeControl,
        LockFreeParticipantRegistry participants,
        LockFreeParticipantRegistry.Registration registration,
        ulong requiredFeatures,
        ulong optionalFeatures,
        bool recoveryEnabled,
        LockFreeTelemetry telemetry,
        TCheckpoint checkpoint)
    {
        _region = region;
        _coldSynchronization = coldSynchronization;
        _layout = layout;
        _storeControl = storeControl;
        _participants = participants;
        _registration = registration;
        _checkpoint = checkpoint;
        _slots = new LockFreeSlotTable(region, layout, registration, telemetry, storeControl);
        _directory = new LockFreeKeyDirectory(region, layout, telemetry, storeControl);
        _leases = new LockFreeLeaseRegistry(
            region,
            layout,
            registration,
            participants,
            telemetry,
            storeControl);
        _reclaimer = new LockFreeReclaimer(
            layout,
            _slots,
            _directory,
            _leases,
            telemetry,
            storeControl);
        _recovery = new LockFreeRecovery(
            layout,
            _slots,
            _directory,
            participants,
            telemetry,
            storeControl);
        _reservationMemory = new LockFreeReservationMemory(region, layout, _slots);
        _recoveryEnabled = recoveryEnabled;
        _protocolInfo = new StoreProtocolInfo(
            StoreProfile.LockFree,
            LayoutV2Constants.LayoutMajorVersion,
            LayoutV2Constants.LayoutMinorVersion,
            LayoutV2Constants.ResourceProtocolVersion,
            requiredFeatures,
            optionalFeatures);
        _diagnostics = new LockFreeDiagnostics(
            region,
            layout,
            _protocolInfo,
            telemetry,
            storeControl);
    }

    public StoreProfile Profile => StoreProfile.LockFree;

    public StoreProtocolInfo ProtocolInfo => _protocolInfo;

    public StoreStatus RecordFacadeStatus(StoreStatus status) =>
        _diagnostics.RecordStatus(status);

    public DiagnosticsSnapshot CreateDisposedDiagnosticsSnapshot() =>
        _diagnostics.CreateDisposedSnapshot();

    void ILockFreeCheckpointEmitter.ReachCheckpoint(LockFreeCheckpointId checkpoint) => Reach(checkpoint);

    internal static StoreOpenStatus TryCreateOrOpenUnderColdGate(
        SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        long waitStartTimestamp,
        MemoryMappedStoreRegion region,
        ISharedStoreSynchronization coldSynchronization,
        RegionOpenDisposition disposition,
        TCheckpoint checkpoint,
        out LockFreeStoreEngine<TCheckpoint>? engine)
    {
        engine = null;
        LockFreeOperationBudget operationBudget = LockFreeOperationBudget.Start(
            waitOptions,
            waitStartTimestamp);
        if (!LayoutV2Constants.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture))
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        ulong pidNamespaceId = CaptureStorePidNamespaceId();

        StoreLayoutV2 layout;
        try
        {
            layout = StoreLayoutV2.FromOptions(options);
        }
        catch (ArgumentOutOfRangeException)
        {
            return StoreOpenStatus.InvalidOptions;
        }
        catch (OverflowException)
        {
            return StoreOpenStatus.InvalidOptions;
        }

        StoreStatus remainingStatus = operationBudget.TryGetRemainingWaitOptions(out _);
        if (remainingStatus != StoreStatus.Success)
        {
            return ToOpenStatus(remainingStatus);
        }

        try
        {
            if (region.Capacity < LayoutV2Constants.HeaderLength)
            {
                return StoreOpenStatus.IncompatibleLayout;
            }

            ref StoreHeaderV2 header = ref Header(region);
            uint observedMagic = header.Magic;
            bool initialize = disposition == RegionOpenDisposition.CreatedNew;

            if (initialize && options.OpenMode == OpenMode.OpenExisting)
            {
                return StoreOpenStatus.IncompatibleLayout;
            }

            if (!initialize && options.OpenMode == OpenMode.CreateNew)
            {
                return StoreOpenStatus.AlreadyExists;
            }

            if (!initialize && observedMagic == 0)
            {
                // The existing unpublished region may still be owned by an
                // older creator that mapped before taking the named gate. This
                // process cannot prove initialization ownership and therefore
                // must never clear or publish the header.
                return options.OpenMode == OpenMode.CreateOrOpen
                    ? StoreOpenStatus.StoreBusy
                    : StoreOpenStatus.IncompatibleLayout;
            }

            if (initialize)
            {
                if (region.Capacity < layout.TotalBytes || !layout.FitsWithinTotalBytes())
                {
                    return StoreOpenStatus.IncompatibleLayout;
                }

                StoreStatus initialized = InitializeMapping(
                    region,
                    layout,
                    pidNamespaceId,
                    operationBudget);
                if (initialized != StoreStatus.Success)
                {
                    return ToOpenStatus(initialized);
                }
            }
            else
            {
                if (observedMagic != LayoutV2Constants.Magic
                    || region.Capacity < layout.TotalBytes
                    || !layout.MatchesHeader(header))
                {
                    return StoreOpenStatus.IncompatibleLayout;
                }

                long storeControl = Volatile.Read(ref header.Control);
                if (storeControl == LayoutV2Constants.StoreUnsupported)
                {
                    return StoreOpenStatus.UnsupportedPlatform;
                }

                if (storeControl != LayoutV2Constants.StoreReady)
                {
                    return StoreOpenStatus.IncompatibleLayout;
                }

                // Publish the irreversible recovery downgrade only after the
                // complete header and Ready state validate, but before the
                // first Registering CAS. Ordinary KV access remains supported
                // across namespace views.
                StoreOpenStatus namespaceStatus = AdmitPidNamespace(
                    ref header,
                    pidNamespaceId);
                if (namespaceStatus != StoreOpenStatus.Success)
                {
                    return namespaceStatus;
                }
            }

            var controlLatch = new LockFreeStoreControl(region);
            StoreStatus attachedState = controlLatch.Validate();
            if (attachedState != StoreStatus.Success)
            {
                controlLatch.Dispose();
                return attachedState == StoreStatus.UnsupportedPlatform
                    ? StoreOpenStatus.UnsupportedPlatform
                    : StoreOpenStatus.IncompatibleLayout;
            }

            var telemetry = new LockFreeTelemetry();
            var participants = new LockFreeParticipantRegistry(
                region,
                layout,
                telemetry,
                controlLatch);
            StoreOpenStatus registerStatus = participants.TryRegister(
                ref header,
                operationBudget,
                ref checkpoint,
                out var registration);
            if (registerStatus != StoreOpenStatus.Success)
            {
                controlLatch.Dispose();
                return registerStatus;
            }

            attachedState = controlLatch.Validate();
            if (attachedState != StoreStatus.Success)
            {
                participants.RetireUnreferencedRegistration(registration);
                controlLatch.Dispose();
                return attachedState == StoreStatus.UnsupportedPlatform
                    ? StoreOpenStatus.UnsupportedPlatform
                    : StoreOpenStatus.IncompatibleLayout;
            }

            try
            {
                LockFreeCheckpoint.Reach(
                    ref checkpoint,
                    LockFreeCheckpointId.ParticipantAfterRegistrationBeforeEngineConstruction);
                engine = new LockFreeStoreEngine<TCheckpoint>(
                    region,
                    coldSynchronization,
                    layout,
                    controlLatch,
                    participants,
                    registration,
                    header.RequiredFeatures,
                    header.OptionalFeatures,
                    options.EnableLeaseRecovery,
                    telemetry,
                    checkpoint);
                return StoreOpenStatus.Success;
            }
            catch
            {
                // Registration has published Active but no engine escaped and
                // therefore no slot/lease claim can reference its token. Close
                // and retire that exact incarnation before the outer owner
                // disposes the mapping.
                participants.RetireUnreferencedRegistration(registration);
                controlLatch.Dispose();
                throw;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return StoreOpenStatus.AccessDenied;
        }
        catch (Exception)
        {
            return StoreOpenStatus.MappingFailed;
        }
    }

    public StoreStatus TryPublish(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions)
    {
        return RecordOperationStatus(TryPublishCore(key, value, descriptor, waitOptions));
    }

    private StoreStatus TryPublishCore(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions)
    {
        long started = Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        Reach(LockFreeCheckpointId.PublishBeforeSlotClaim);
        StoreStatus status = TryReserveCore(
            key,
            value.Length,
            descriptor,
            waitOptions,
            SlotPublicationIntent.AtomicPublication,
            out var reservation,
            started);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        try
        {
            Span<byte> destination = _reservationMemory.GetSpan(reservation, value.Length);
            if (value.Length != 0 && destination.Length < value.Length)
            {
                _ = AbortReservationCore(reservation);
                return CorruptFrom(nameof(LockFreeStoreEngine));
            }

            StoreStatus copy = LockFreeByteOperations.TryCopy(value, destination, budget);
            if (copy != StoreStatus.Success)
            {
                _ = AbortReservationCore(reservation);
                return copy;
            }

            status = _slots.AdvanceReservation(reservation, value.Length, budget);
            if (status != StoreStatus.Success)
            {
                _ = AbortReservationCore(reservation);
                return status;
            }

            status = CommitReservationCore(reservation, waitOptions, started);
            if (status == StoreStatus.Success)
            {
                Reach(LockFreeCheckpointId.PublishAfterCommitPublication);
            }
            else
            {
                // TryPublish owns this reservation and never exposes its token.
                // A bounded commit may lose its budget immediately before the
                // publication CAS. Publish unowned Aborting, then spend only
                // the post-ownership completion allowance on physical cleanup
                // while preserving the caller-visible timeout/cancel result.
                _ = AbortReservationCore(reservation);
            }

            return status;
        }
        catch
        {
            _ = AbortReservationCore(reservation);
            return StoreStatus.UnknownFailure;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out ReservationHandle reservation)
    {
        return RecordOperationStatus(
            TryReserveCore(
                key,
                payloadLength,
                descriptor,
                waitOptions,
                SlotPublicationIntent.ExplicitReservation,
                out reservation));
    }

    public StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes)
    {
        return RecordOperationStatus(
            TryPublishSegmentsCore(key, payload, descriptor, waitOptions, out copiedBytes));
    }

    private StoreStatus TryPublishSegmentsCore(
        ReadOnlySpan<byte> key,
        ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes)
    {
        long started = Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        copiedBytes = 0;
        long advertisedLength;
        try
        {
            advertisedLength = payload.Length;
        }
        catch (ArgumentOutOfRangeException)
        {
            return StoreStatus.UnknownFailure;
        }
        catch (OverflowException)
        {
            return StoreStatus.UnknownFailure;
        }

        if (advertisedLength < 0)
        {
            return StoreStatus.UnknownFailure;
        }

        if (advertisedLength > int.MaxValue)
        {
            return StoreStatus.ValueTooLarge;
        }

        int payloadLength = (int)advertisedLength;

        StoreStatus status = TryReserveCore(
            key,
            payloadLength,
            descriptor,
            waitOptions,
            SlotPublicationIntent.AtomicPublication,
            out var reservation,
            started);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        try
        {
            Span<byte> destination = _reservationMemory.GetSpan(reservation, payloadLength);
            if (destination.Length < advertisedLength)
            {
                _ = AbortReservationCore(reservation);
                return CorruptHere();
            }

            var segmentIndex = 0;
            foreach (ReadOnlyMemory<byte> segment in payload)
            {
                StoreStatus segmentBound = budget.CheckPeriodic(segmentIndex++);
                if (segmentBound != StoreStatus.Success)
                {
                    _ = AbortReservationCore(reservation);
                    return segmentBound;
                }

                // ReadOnlySequence<T> is extensible, so a caller can supply a
                // malformed sequence whose enumerated segment lengths exceed
                // its advertised Length. That is invalid caller input, not
                // evidence that the shared mapping is corrupt.
                if (copiedBytes > destination.Length ||
                    segment.Length > destination.Length - copiedBytes)
                {
                    _ = AbortReservationCore(reservation);
                    return StoreStatus.UnknownFailure;
                }

                StoreStatus copy = LockFreeByteOperations.TryCopy(
                    segment.Span,
                    destination[(int)copiedBytes..],
                    budget,
                    out int segmentCopiedBytes);
                copiedBytes += segmentCopiedBytes;
                if (copy != StoreStatus.Success)
                {
                    _ = AbortReservationCore(reservation);
                    return copy;
                }
            }

            // The inverse malformed-sequence shape can advertise a Length
            // larger than the bytes its segment chain actually enumerates.
            // Preserve the exact copied prefix and keep this caller failure
            // distinct from persistent mapped corruption.
            if (copiedBytes != advertisedLength)
            {
                _ = AbortReservationCore(reservation);
                return StoreStatus.UnknownFailure;
            }

            status = _slots.AdvanceReservation(reservation, payloadLength, budget);
            if (status != StoreStatus.Success)
            {
                _ = AbortReservationCore(reservation);
                return status;
            }

            status = CommitReservationCore(reservation, waitOptions, started);
            if (status != StoreStatus.Success)
            {
                // The segmented convenience operation owns the reservation;
                // callers cannot clean it after a bounded commit failure.
                _ = AbortReservationCore(reservation);
            }

            return status;
        }
        catch
        {
            _ = AbortReservationCore(reservation);
            return StoreStatus.UnknownFailure;
        }
    }

    public StoreStatus TryAcquire(
        ReadOnlySpan<byte> key,
        StoreWaitOptions waitOptions,
        out LeaseHandle lease)
    {
        return RecordOperationStatus(TryAcquireCore(key, waitOptions, out lease));
    }

    private StoreStatus TryAcquireCore(
        ReadOnlySpan<byte> key,
        StoreWaitOptions waitOptions,
        out LeaseHandle lease)
    {
        long started = Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        lease = default;
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return ready;
        }

        StoreStatus keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
        if (keyStatus != StoreStatus.Success)
        {
            return keyStatus;
        }

        StoreStatus hashStatus = LockFreeByteOperations.TryHash(key, budget, out ulong hash);
        if (hashStatus != StoreStatus.Success)
        {
            return hashStatus;
        }

        StoreStatus lookup = _directory.TryLookup(key, hash, budget, out ulong slotBinding, out _);
        if (lookup != StoreStatus.Success)
        {
            return lookup;
        }

        if (!TryDecodeSlotBinding(slotBinding, out int slotIndex, out long generation))
        {
            return CorruptHere();
        }

        StoreStatus publication = TryIsSlotPublished(slotIndex, generation, out bool isPublished);
        if (publication != StoreStatus.Success)
        {
            return publication;
        }

        if (!isPublished)
        {
            return StoreStatus.NotFound;
        }

        Reach(LockFreeCheckpointId.AcquireBeforeLeaseClaimCas);
        StoreStatus bound = CheckOperationBound(waitOptions, started);
        if (bound != StoreStatus.Success)
        {
            return bound;
        }

        // AcquireSequence is observational metadata, not an ordering or
        // reclamation dependency. Reuse the process-wide monotonic timestamp
        // captured at operation entry instead of bouncing one shared header
        // cache line across every reader process.
        long acquireSequence = Math.Max(1, started);
        StoreStatus claim = _leases.TryClaimAndActivate(
            slotBinding,
            acquireSequence,
            budget,
            ref _checkpoint,
            out lease);
        if (claim != StoreStatus.Success)
        {
            lease = default;
            if (claim != StoreStatus.LeaseTableFull)
            {
                return claim;
            }

            // The lease proof identifies an exact full-table instant, but an
            // acquire capacity result also requires the requested generation
            // to exist at that instant. Revalidate after confirmation. If the
            // exact binding is still Published, its monotonic lifecycle proves
            // that it remained Published through the earlier candidate. A
            // missing/changed generation instead keeps ordinary acquire
            // semantics and cannot leak a stale LeaseTableFull result.
            lookup = _directory.TryLookup(
                key,
                hash,
                budget,
                out ulong fullRevalidatedBinding,
                out _);
            if (lookup != StoreStatus.Success)
            {
                return lookup;
            }

            if (fullRevalidatedBinding != slotBinding)
            {
                return StoreStatus.NotFound;
            }

            publication = TryIsSlotPublished(slotIndex, generation, out isPublished);
            if (publication != StoreStatus.Success)
            {
                return publication;
            }

            return isPublished ? StoreStatus.LeaseTableFull : StoreStatus.NotFound;
        }

        Reach(LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup);
        bound = budget.Check();
        if (bound != StoreStatus.Success)
        {
            _ = _leases.TryRelease(lease, ref _checkpoint);
            lease = default;
            return bound;
        }

        lookup = _directory.TryLookup(key, hash, budget, out ulong revalidatedBinding, out _);
        if (lookup != StoreStatus.Success)
        {
            _ = _leases.TryRelease(lease, ref _checkpoint);
            lease = default;
            return lookup;
        }

        if (revalidatedBinding != slotBinding)
        {
            _ = _leases.TryRelease(lease, ref _checkpoint);
            lease = default;
            return StoreStatus.NotFound;
        }

        publication = TryIsSlotPublished(slotIndex, generation, out isPublished);
        if (publication != StoreStatus.Success)
        {
            _ = _leases.TryRelease(lease, ref _checkpoint);
            lease = default;
            return publication;
        }

        if (!isPublished)
        {
            _ = _leases.TryRelease(lease, ref _checkpoint);
            lease = default;
            return StoreStatus.NotFound;
        }

        Reach(LockFreeCheckpointId.AcquireAfterPublishedRevalidation);
        return StoreStatus.Success;
    }

    public StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions)
    {
        return RecordOperationStatus(TryRemoveCore(key, waitOptions));
    }

    private StoreStatus TryRemoveCore(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions)
    {
        long started = Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return ready;
        }

        StoreStatus keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
        if (keyStatus != StoreStatus.Success)
        {
            return keyStatus;
        }

        StoreStatus hashStatus = LockFreeByteOperations.TryHash(key, budget, out ulong hash);
        if (hashStatus != StoreStatus.Success)
        {
            return hashStatus;
        }

        StoreStatus lookup = _directory.TryLookup(key, hash, budget, out ulong binding, out _);
        if (lookup != StoreStatus.Success)
        {
            return lookup;
        }

        if (!TryDecodeSlotBinding(binding, out int slotIndex, out long generation))
        {
            return CorruptHere();
        }

        Reach(LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas);
        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        if (HasExpired(waitOptions, started))
        {
            return StoreStatus.StoreBusy;
        }

        StoreStatus logicalRemove = _reclaimer.TryLogicalRemove(binding, out _);
        if (logicalRemove != StoreStatus.Success)
        {
            return logicalRemove;
        }

        // NoWait performs the logical ordering point but conservatively leaves
        // the bounded classification/reclaim scan to a helper.
        if (waitOptions.Timeout == TimeSpan.Zero)
        {
            return StoreStatus.RemovePending;
        }

        if (HasExpired(waitOptions, started))
        {
            return StoreStatus.RemovePending;
        }

        Reach(LockFreeCheckpointId.ReclaimBeforeOwnershipCas);
        StoreStatus reclaimed = _reclaimer.TryReclaim(
            binding,
            budget,
            ref _checkpoint,
            reportRemoveClassification: true);
        for (var attempt = 0;
            reclaimed == StoreStatus.StoreBusy && budget.IsInfinite;
            attempt++)
        {
            if (!budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                reclaimed = terminal;
                break;
            }

            reclaimed = _reclaimer.TryReclaim(
                binding,
                budget,
                ref _checkpoint,
                reportRemoveClassification: true);
        }

        if (reclaimed == StoreStatus.Success)
        {
            Reach(LockFreeCheckpointId.ReclaimAfterGenerationAdvance);
            Reach(LockFreeCheckpointId.RemoveAfterLeaseClassification);
        }

        return LockFreeStoreEngine.NormalizePostLogicalRemoveOutcome(reclaimed);
    }

    public StoreStatus TryRecoverLeases(
        LeaseRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out LeaseRecoveryReport report)
    {
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions);
        report = default;
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return RecordOperationStatus(ready);
        }

        StoreStatus status = _recoveryEnabled
            ? _leases.TryRecover(options, budget, _reclaimer, ref _checkpoint, out report)
            : StoreStatus.UnsupportedPlatform;
        if (status == StoreStatus.Success)
        {
            _diagnostics.RecordLeaseRecoveryResults(report);
        }

        return RecordOperationStatus(status);
    }

    public StoreStatus TryRecoverReservations(
        ReservationRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out ReservationRecoveryReport report)
    {
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions);
        report = default;
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return RecordOperationStatus(ready);
        }

        if (!_recoveryEnabled)
        {
            return RecordOperationStatus(StoreStatus.UnsupportedPlatform);
        }

        StoreStatus status = _recovery.TryRecoverReservations(
            options,
            budget,
            ref _checkpoint,
            out report);
        if (status == StoreStatus.Success)
        {
            _diagnostics.RecordReservationRecoveryResults(report);
        }

        return RecordOperationStatus(status);
    }

    public StoreStatus TryGetMetrics(StoreWaitOptions waitOptions, out EngineMetrics metrics)
    {
        long started = Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        metrics = default;
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return RecordOperationStatus(ready);
        }

        _diagnostics.ReachBeforeBoundedScan(ref _checkpoint);
        StoreStatus bound = CheckOperationBound(waitOptions, started);
        if (bound != StoreStatus.Success)
        {
            return RecordOperationStatus(bound);
        }

        StoreStatus status = _diagnostics.TryScanMetricsAfterBoundedPrecheck(
            budget,
            ref _checkpoint,
            out metrics);
        return RecordOperationStatus(status);
    }

    public StoreStatus TryGetDiagnostics(StoreWaitOptions waitOptions, out DiagnosticsSnapshot snapshot)
    {
        long started = Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        snapshot = default;
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return RecordOperationStatus(ready);
        }

        _diagnostics.ReachBeforeBoundedScan(ref _checkpoint);
        StoreStatus bound = CheckOperationBound(waitOptions, started);
        if (bound != StoreStatus.Success)
        {
            return RecordOperationStatus(bound);
        }

        StoreStatus status = _diagnostics.TryCreateSnapshotAfterBoundedPrecheck(
            budget,
            ref _checkpoint,
            out snapshot);
        return RecordOperationStatus(status);
    }

    public bool IsReservationPending(ReservationHandle reservation) =>
        CanProjectMappedState && _slots.IsReservationPending(reservation);

    public int GetReservationBytesWritten(ReservationHandle reservation) =>
        CanProjectMappedState ? _slots.GetBytesAdvanced(reservation) : 0;

    public Span<byte> GetReservationSpan(ReservationHandle reservation, int sizeHint)
    {
        Reach(LockFreeCheckpointId.ProjectBeforeHandleValidation);
        Span<byte> span = CanProjectMappedState
            ? _reservationMemory.GetSpan(reservation, sizeHint)
            : Span<byte>.Empty;
        Reach(LockFreeCheckpointId.ProjectAfterSpanProjection);
        return span;
    }

    public Memory<byte> DangerousGetReservationMemory(ReservationHandle reservation, int sizeHint) =>
        CanProjectMappedState
            ? _reservationMemory.GetMemory(reservation, sizeHint)
            : Memory<byte>.Empty;

    public StoreStatus AdvanceReservation(
        ReservationHandle reservation,
        int byteCount,
        StoreWaitOptions waitOptions)
    {
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions);
        StoreStatus ready = ValidateOperation(waitOptions);
        StoreStatus status = ready == StoreStatus.Success
            ? _slots.AdvanceReservation(reservation, byteCount, budget, ref _checkpoint)
            : ready;
        return RecordOperationStatus(status);
    }

    public StoreStatus CommitReservation(ReservationHandle reservation, StoreWaitOptions waitOptions)
    {
        long started = Stopwatch.GetTimestamp();
        StoreStatus ready = ValidateOperation(waitOptions);
        StoreStatus status = ready == StoreStatus.Success
            ? CommitReservationCore(reservation, waitOptions, started)
            : ready;
        return RecordOperationStatus(status);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public StoreStatus AbortReservation(ReservationHandle reservation, StoreWaitOptions waitOptions)
    {
        long started = Stopwatch.GetTimestamp();
        StoreStatus ready = ValidateOperation(waitOptions);
        StoreStatus status = ready == StoreStatus.Success
            ? AbortReservationCore(reservation, waitOptions, started)
            : ready;
        if (status == StoreStatus.Success)
        {
            _diagnostics.RecordReservationAbort();
        }

        return RecordOperationStatus(status);
    }

    public bool IsLeaseActive(LeaseHandle lease) =>
        TryValidateLeaseProjection(lease, out _, out _, out _, out _, out _);

    public int GetValueLength(LeaseHandle lease) =>
        TryValidateLeaseProjection(lease, out _, out int valueLength, out _, out _, out _)
            ? valueLength
            : 0;

    public int GetDescriptorLength(LeaseHandle lease) =>
        TryValidateLeaseProjection(lease, out _, out _, out int descriptorLength, out _, out _)
            ? descriptorLength
            : 0;

    public ReadOnlySpan<byte> GetValueSpan(LeaseHandle lease)
    {
        Reach(LockFreeCheckpointId.ProjectBeforeHandleValidation);
        if (!TryValidateLeaseProjection(
                lease,
                out _,
                out int valueLength,
                out _,
                out long payloadOffset,
                out _))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        ReadOnlySpan<byte> span = new(_region.Pointer + payloadOffset, valueLength);
        Reach(LockFreeCheckpointId.ProjectAfterSpanProjection);
        return span;
    }

    public ReadOnlySpan<byte> GetDescriptorSpan(LeaseHandle lease)
    {
        if (!TryValidateLeaseProjection(
                lease,
                out _,
                out _,
                out int descriptorLength,
                out _,
                out long descriptorOffset))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return new ReadOnlySpan<byte>(_region.Pointer + descriptorOffset, descriptorLength);
    }

    public StoreStatus ReleaseLease(LeaseHandle lease, StoreWaitOptions waitOptions)
    {
        long started = Stopwatch.GetTimestamp();
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return RecordOperationStatus(ready);
        }

        Reach(LockFreeCheckpointId.ReleaseBeforeActiveReleaseCas);
        StoreStatus bound = CheckOperationBound(waitOptions, started);
        if (bound != StoreStatus.Success)
        {
            return RecordOperationStatus(bound);
        }

        StoreStatus status = _leases.TryRelease(lease, ref _checkpoint);
        if (status == StoreStatus.Success)
        {
            _ = _reclaimer.TryReclaim(
                lease.SlotBinding,
                LockFreeOperationBudget.StartPostOwnershipCleanup(),
                ref _checkpoint);
            Reach(LockFreeCheckpointId.ReleaseAfterRecordRecycle);
        }

        return RecordOperationStatus(status);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_storeControl.Validate() != StoreStatus.Success)
            {
                _reservationMemory.Dispose();
                return;
            }

            ParticipantTransitionResult close = _participants.TryBeginClose(_registration);
            if (close is ParticipantTransitionResult.Succeeded
                or ParticipantTransitionResult.AlreadyCompleted)
            {
                LockFreeOperationBudget cleanupBudget =
                    LockFreeOperationBudget.StartPostOwnershipCleanup();
                try
                {
                    // Closing is published only after the facade lifecycle gate
                    // has drained every entered callback. Other handles can now
                    // recover exact resources even if this disposer pauses.
                    Reach(LockFreeCheckpointId.DisposalAfterParticipantClosingPublication);
                    _reservationMemory.Dispose();
                    CleanupParticipantResources(cleanupBudget);
                }
                finally
                {
                    // Retirement is attempted even if best-effort cleanup or a
                    // test checkpoint throws. A remaining reference/bound leaves
                    // exact Closing for an unrelated recovery caller to finish.
                    _ = _participants.TryRetireClosingRegistration(
                        _registration,
                        cleanupBudget,
                        ref _checkpoint);
                    Reach(LockFreeCheckpointId.DisposalAfterParticipantRelease);
                }
            }
            else
            {
                _reservationMemory.Dispose();
            }
        }
        finally
        {
            try
            {
                _storeControl.Dispose();
            }
            finally
            {
                try
                {
                    // Linux owner cleanup may enter .lifecycle and retire the
                    // final pathname generation. Close the ordinary lock
                    // descriptor before region cleanup can reach that point.
                    _coldSynchronization.Dispose();
                }
                finally
                {
                    _region.Dispose();
                }
            }
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool CanProjectMappedState => !IsDisposed && _storeControl.IsReady;

    private void CleanupParticipantResources(in LockFreeOperationBudget budget)
    {
        _ = _leases.ReleaseParticipantLeases(
            _registration.Token,
            _reclaimer,
            budget,
            ref _checkpoint,
            out _);
        for (var index = 0; index < _layout.SlotCount; index++)
        {
            if (budget.CheckPeriodic(index) != StoreStatus.Success)
            {
                break;
            }

            ref ValueSlotMetadataV2 slot = ref _slots.Slot(index);
            long control = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (_slots.ValidateStructuralControl(control) != StoreStatus.Success)
            {
                break;
            }

            if (ControlParticipant(control) != _registration.Token
                || ControlState(control) is not (
                    LockFreeSlotTable.InitializingState or LockFreeSlotTable.ReservedState))
            {
                continue;
            }

            if (_slots.TryBeginRecoveryAbort(index, control, out var reservation))
            {
                Reach(LockFreeCheckpointId.AbortAfterOwnershipReleaseCas);
                _ = CompleteAbortingReservation(
                    reservation,
                    budget);
            }
        }
    }

    private StoreStatus Unsupported(StoreWaitOptions waitOptions)
    {
        if (IsDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        StoreStatus storeState = _storeControl.Validate();
        if (storeState != StoreStatus.Success)
        {
            return storeState;
        }

        if (!waitOptions.IsValid)
        {
            return StoreStatus.UnknownFailure;
        }

        return waitOptions.CancellationToken.IsCancellationRequested
            ? StoreStatus.OperationCanceled
            : StoreStatus.UnsupportedPlatform;
    }

    private StoreStatus InvalidReservationOrDisposed(StoreWaitOptions waitOptions)
    {
        if (IsDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        StoreStatus storeState = _storeControl.Validate();
        if (storeState != StoreStatus.Success)
        {
            return storeState;
        }

        return waitOptions.CancellationToken.IsCancellationRequested
            ? StoreStatus.OperationCanceled
            : StoreStatus.InvalidReservation;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private StoreStatus TryReserveCore(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        SlotPublicationIntent publicationIntent,
        out ReservationHandle reservation,
        long? operationStarted = null)
    {
        reservation = default;
        long started = operationStarted ?? Stopwatch.GetTimestamp();
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, started);
        StoreStatus ready = ValidateOperation(waitOptions);
        if (ready != StoreStatus.Success)
        {
            return ready;
        }

        StoreStatus input = StoreKey.Validate(key, _layout.MaxKeyBytes);
        if (input != StoreStatus.Success)
        {
            return input;
        }

        if (payloadLength < 0 || payloadLength > _layout.MaxValueBytes)
        {
            return StoreStatus.ValueTooLarge;
        }

        if (descriptor.Length > _layout.MaxDescriptorBytes)
        {
            return StoreStatus.DescriptorTooLarge;
        }

        StoreStatus hashStatus = LockFreeByteOperations.TryHash(key, budget, out ulong hash);
        if (hashStatus != StoreStatus.Success)
        {
            return hashStatus;
        }

        var rejectedCandidateRetry = false;
        var candidateRetryAttempt = 0;
        var capacityRetryAttempt = 0;
    RetryReservation:
        reservation = default;
        StoreStatus lookup = ResolveCreateConflict(key, hash, budget);
        if (lookup != StoreStatus.NotFound)
        {
            return lookup;
        }

        if (rejectedCandidateRetry)
        {
            // A rejected candidate earns one fresh stable conflict
            // resolution, so a real explicit/published winner may still
            // justify DuplicateKey. Absence does not give NoWait an unbounded
            // sequence of new slot claims; every further candidate is an
            // operation-wide contention retry.
            if (!budget.TryContinueAfterContention(
                    candidateRetryAttempt++,
                    out StoreStatus retryTerminal))
            {
                return retryTerminal;
            }

            rejectedCandidateRetry = false;
        }

        Reach(LockFreeCheckpointId.ReserveBeforeSlotClaim);
        StoreStatus claim = _slots.TryClaimReservation(
            hash,
            key.Length,
            descriptor.Length,
            payloadLength,
            publicationIntent,
            budget,
            ref _checkpoint,
            out reservation);
        if (claim == StoreStatus.StoreFull)
        {
            StoreStatus help = _reclaimer.HelpReclaimableSlots(
                budget,
                ref _checkpoint,
                out _);
            if (help != StoreStatus.Success)
            {
                return help;
            }

            // A concurrent reclaimer can advance the last unavailable slot
            // after the claim scan but before the helper scan observes it. In
            // that case reclaimed is zero even though capacity is now free.
            // Always re-probe once after a successful helping pass so that a
            // same-key release/re-publish race cannot leak a transient
            // StoreFull result from the unlink-to-reusable window.
            claim = _slots.TryClaimReservation(
                hash,
                key.Length,
                descriptor.Length,
                payloadLength,
                publicationIntent,
                budget,
                ref _checkpoint,
                out reservation);

            if (claim == StoreStatus.StoreFull)
            {
                // Two exhausted allocation scans plus a helping pass still do
                // not prove simultaneous physical fullness: a free slot can
                // rotate behind both sequential scans. Only the exact local
                // double collect may expose StoreFull publicly. Movement or a
                // competing local proof is ordinary bounded contention.
                StoreStatus proof = _slots.TryProveStoreFull(
                    budget,
                    ref _checkpoint,
                    out bool provenFull);
                if (proof != StoreStatus.Success)
                {
                    return proof;
                }

                if (provenFull)
                {
                    claim = StoreStatus.StoreFull;
                }
                else
                {
                    // A free slot, changing control word, or another local
                    // proof attempt is transient. NoWait terminates here;
                    // finite and infinite callers retry from a fresh key
                    // lookup under their operation-wide budget.
                    if (!budget.TryContinueAfterContention(
                            capacityRetryAttempt++,
                            out StoreStatus capacityTerminal))
                    {
                        return capacityTerminal;
                    }

                    goto RetryReservation;
                }
            }
        }
        if (claim != StoreStatus.Success)
        {
            return claim;
        }

        try
        {
            StoreStatus keyCopy = LockFreeByteOperations.TryCopy(
                key,
                _slots.GetInitializingKeySpan(reservation),
                budget);
            if (keyCopy != StoreStatus.Success)
            {
                _ = _slots.AbortUnboundReservation(reservation, ref _checkpoint);
                reservation = default;
                return keyCopy == StoreStatus.CorruptStore
                    ? CorruptHere()
                    : keyCopy;
            }

            StoreStatus descriptorCopy = LockFreeByteOperations.TryCopy(
                descriptor,
                _slots.GetInitializingDescriptorSpan(reservation),
                budget);
            if (descriptorCopy != StoreStatus.Success)
            {
                _ = _slots.AbortUnboundReservation(reservation, ref _checkpoint);
                reservation = default;
                return descriptorCopy == StoreStatus.CorruptStore
                    ? CorruptHere()
                    : descriptorCopy;
            }

            Reach(LockFreeCheckpointId.DirectoryBeforeDescriptorPublication);

            if (waitOptions.CancellationToken.IsCancellationRequested)
            {
                _ = _slots.AbortUnboundReservation(reservation, ref _checkpoint);
                reservation = default;
                return StoreStatus.OperationCanceled;
            }

            if (HasExpired(waitOptions, started))
            {
                _ = _slots.AbortUnboundReservation(reservation, ref _checkpoint);
                reservation = default;
                return StoreStatus.StoreBusy;
            }

            StoreStatus inserted = _directory.TryInsert(
                key,
                hash,
                reservation.SlotBinding,
                budget,
                ref _checkpoint,
                out _);
            if (inserted != StoreStatus.Success)
            {
                StoreStatus resolvedFailure = ResolveFailedDirectoryInsert(
                    reservation,
                    publicationIntent,
                    inserted,
                    out bool ordered);
                if (ordered)
                {
                    ReachSuccessfulReservationReturn();
                    return StoreStatus.Success;
                }

                reservation = default;
                if (resolvedFailure == StoreStatus.CorruptStore)
                {
                    return CorruptFrom(nameof(LockFreeStoreEngine));
                }

                // PhaseRejected proves only that this candidate lost its
                // insertion attempt. The winner may still be tentative (or
                // may already have aborted), so only a fresh stable resolver
                // is allowed to turn that observation into DuplicateKey.
                if (inserted == StoreStatus.DuplicateKey
                    && resolvedFailure == StoreStatus.DuplicateKey)
                {
                    rejectedCandidateRetry = true;
                    goto RetryReservation;
                }

                return resolvedFailure;
            }

            Reach(LockFreeCheckpointId.ReserveAfterDirectoryInsertBeforePendingClassification);
            StoreStatus reservationState =
                _slots.ClassifyReservationAfterDirectoryInsert(reservation);
            if (reservationState != StoreStatus.Success)
            {
                if (reservationState == StoreStatus.CorruptStore)
                {
                    reservation = default;
                    return CorruptFrom(nameof(LockFreeStoreEngine));
                }

                if (publicationIntent == SlotPublicationIntent.ExplicitReservation)
                {
                    // TryInsert returning Success is itself a witness that a
                    // helper completed Initializing -> Reserved. Normal
                    // recovery preserves a live Active owner; this defensive
                    // branch covers an exact adversarial CAS or a correctly
                    // quiesced administrative recovery that follows ordering.
                    // Neither may turn that ordered reserve into a failure with
                    // no reservation token.
                    if (reservationState is StoreStatus.InvalidReservation
                        or StoreStatus.ReservationAlreadyCompleted)
                    {
                        StoreStatus cleanup =
                            CompleteOrderedReservationRecovery(reservation);
                        if (cleanup == StoreStatus.CorruptStore)
                        {
                            reservation = default;
                            return CorruptFrom(
                                nameof(LockFreeStoreEngine));
                        }
                    }

                    ReachSuccessfulReservationReturn();
                    return StoreStatus.Success;
                }

                _ = _slots.TryBeginAbort(reservation);
                StoreStatus failedCleanup = CompleteAbortingReservation(
                    reservation,
                    LockFreeOperationBudget.StartPostOwnershipCleanup());
                reservation = default;
                return failedCleanup == StoreStatus.CorruptStore
                    ? CorruptFrom(nameof(LockFreeStoreEngine))
                    : reservationState;
            }

            ReachSuccessfulReservationReturn();
            return StoreStatus.Success;
        }
        catch
        {
            StoreStatus resolvedFailure = ResolveFailedDirectoryInsert(
                reservation,
                publicationIntent,
                StoreStatus.UnknownFailure,
                out bool ordered);
            if (ordered)
            {
                ReachSuccessfulReservationReturn();
                return StoreStatus.Success;
            }

            reservation = default;
            return resolvedFailure == StoreStatus.CorruptStore
                ? CorruptFrom(nameof(LockFreeStoreEngine))
                : StoreStatus.UnknownFailure;
        }
    }

    /// <summary>
    /// Resolves one structurally visible key binding into a public create
    /// outcome. Directory lookup deliberately remains structural: slot state
    /// plus immutable publication intent decide whether the binding is public
    /// ownership, private staging, or cleanup work that any participant may
    /// finish.
    /// </summary>
    private StoreStatus ResolveCreateConflict(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        in LockFreeOperationBudget budget)
    {
        var contentionAttempt = 0;
        for (;;)
        {
            StoreStatus lookup = _directory.TryLookup(
                key,
                keyHash,
                budget,
                out ulong exactBinding,
                out DirectoryLocation exactLocation);
            if (lookup == StoreStatus.NotFound)
            {
                return StoreStatus.NotFound;
            }

            if (lookup != StoreStatus.Success)
            {
                if (lookup != StoreStatus.StoreBusy)
                {
                    return lookup;
                }

                if (!budget.TryContinueAfterContention(
                        contentionAttempt++,
                        out StoreStatus lookupTerminal))
                {
                    return lookupTerminal;
                }

                continue;
            }

            Reach(LockFreeCheckpointId.ReserveAfterExistingLookup);
            for (;;)
            {
                StoreStatus classification = ClassifyDirectoryBindingWithSourceRevalidation(
                    exactBinding,
                    exactLocation,
                    out bool sourceChanged,
                    out int state,
                    out SlotPublicationIntent publicationIntent);
                if (sourceChanged)
                {
                    if (!budget.TryContinueAfterContention(
                            contentionAttempt++,
                            out StoreStatus sourceChangedTerminal))
                    {
                        return sourceChangedTerminal;
                    }

                    break;
                }

                if (classification == StoreStatus.NotFound)
                {
                    // Generation advance makes this cell removable residue.
                    // Re-enter structural lookup so its exact-value CAS can
                    // clear the stale binding before a new claim proceeds.
                    break;
                }

                if (classification != StoreStatus.Success)
                {
                    if (classification != StoreStatus.StoreBusy)
                    {
                        return classification;
                    }

                    if (!budget.TryContinueAfterContention(
                            contentionAttempt++,
                            out StoreStatus classificationTerminal))
                    {
                        return classificationTerminal;
                    }

                    continue;
                }

                switch (state)
                {
                    case LockFreeSlotTable.InitializingState:
                    {
                        // Initializing is tentative for both public APIs. Help
                        // the canonical insertion before checking the caller's
                        // contention budget again; the help may establish the
                        // explicit reserve ordering point.
                        StoreStatus help = _directory.HelpMutationForKeyHash(
                            keyHash,
                            budget,
                            ref _checkpoint,
                            maxSteps: 8);
                        if (help is not (StoreStatus.Success or StoreStatus.StoreBusy))
                        {
                            return help;
                        }

                        StoreStatus postHelp = ClassifyDirectoryBindingWithSourceRevalidation(
                            exactBinding,
                            exactLocation,
                            out bool postHelpSourceChanged,
                            out int postHelpState,
                            out _);
                        if (postHelpSourceChanged)
                        {
                            break;
                        }

                        if (postHelp == StoreStatus.NotFound)
                        {
                            break;
                        }

                        if (postHelp != StoreStatus.Success
                            && postHelp != StoreStatus.StoreBusy)
                        {
                            return postHelp;
                        }

                        if (postHelp == StoreStatus.Success
                            && postHelpState != LockFreeSlotTable.InitializingState)
                        {
                            // Reclassify the newly reached terminal state before
                            // consulting the wait budget. In particular, an
                            // explicit helper-won Reserved state must dominate
                            // a deadline observed immediately afterward.
                            continue;
                        }

                        if (!budget.TryContinueAfterContention(
                                contentionAttempt++,
                                out StoreStatus initializingTerminal))
                        {
                            return initializingTerminal;
                        }

                        continue;
                    }

                    case LockFreeSlotTable.ReservedState:
                        if (publicationIntent == SlotPublicationIntent.ExplicitReservation)
                        {
                            return StoreStatus.DuplicateKey;
                        }

                        // Atomic convenience publication owns a private
                        // reservation. Helpers may complete directory metadata
                        // but must never expose Reserved as public key ownership
                        // or commit another process's payload.
                        StoreStatus reservedHelp = _directory.HelpMutationForKeyHash(
                            keyHash,
                            budget,
                            ref _checkpoint,
                            maxSteps: 8);
                        if (reservedHelp is not (StoreStatus.Success or StoreStatus.StoreBusy))
                        {
                            return reservedHelp;
                        }

                        if (!budget.TryContinueAfterContention(
                                contentionAttempt++,
                                out StoreStatus reservedTerminal))
                        {
                            return reservedTerminal;
                        }

                        continue;

                    case LockFreeSlotTable.PublishedState:
                        return StoreStatus.DuplicateKey;

                    case LockFreeSlotTable.RemoveRequestedState:
                    {
                        StoreStatus reclaim = _reclaimer.TryReclaim(
                            exactBinding,
                            budget,
                            ref _checkpoint);
                        StoreStatus normalized =
                            LockFreeStoreEngine.NormalizeExistingGenerationReclaimOutcome(reclaim);
                        if (normalized == StoreStatus.Success)
                        {
                            break;
                        }

                        if (normalized != StoreStatus.StoreBusy)
                        {
                            return normalized;
                        }

                        if (!budget.TryContinueAfterContention(
                                contentionAttempt++,
                                out StoreStatus reclaimTerminal))
                        {
                            return reclaimTerminal;
                        }

                        continue;
                    }

                    case LockFreeSlotTable.AbortingState:
                    {
                        StoreStatus cleanup = CompleteAbortingBinding(exactBinding, budget);
                        if (cleanup == StoreStatus.Success)
                        {
                            break;
                        }

                        if (cleanup != StoreStatus.StoreBusy)
                        {
                            return cleanup;
                        }

                        if (!budget.TryContinueAfterContention(
                                contentionAttempt++,
                                out StoreStatus abortTerminal))
                        {
                            return abortTerminal;
                        }

                        continue;
                    }

                    case LockFreeSlotTable.ReclaimingState:
                    {
                        StoreStatus reclaim = _reclaimer.TryReclaim(
                            exactBinding,
                            budget,
                            ref _checkpoint);
                        if (reclaim == StoreStatus.Success)
                        {
                            break;
                        }

                        if (reclaim != StoreStatus.StoreBusy)
                        {
                            return reclaim;
                        }

                        if (!budget.TryContinueAfterContention(
                                contentionAttempt++,
                                out StoreStatus reclaimTerminal))
                        {
                            return reclaimTerminal;
                        }

                        continue;
                    }

                    default:
                        return CorruptFrom(
                            nameof(LockFreeStoreEngine));
                }

                // The exact generation was cleaned or advanced. A fresh
                // structural lookup decides whether another contender won.
                // Successful cleanup is still operation-wide contention: an
                // adversary can otherwise replace the same key with an
                // unbounded succession of removable generations while one
                // NoWait call keeps making progress forever.
                if (!budget.TryContinueAfterContention(
                        contentionAttempt++,
                        out StoreStatus freshLookupTerminal))
                {
                    return freshLookupTerminal;
                }

                break;
            }
        }
    }

    /// <summary>
    /// A binding returned by lookup is only a cached witness. If its slot
    /// classification would be corruption, jointly revalidate the exact
    /// source cell around a fresh stable slot snapshot. A changed source means
    /// that unlink/reuse overtook the cached lookup and is ordinary
    /// contention; only an unchanged exact source plus a repeated invalid slot
    /// shape may fail closed.
    /// </summary>
    private StoreStatus ClassifyDirectoryBindingWithSourceRevalidation(
        ulong exactBinding,
        DirectoryLocation exactLocation,
        out bool sourceChanged,
        out int state,
        out SlotPublicationIntent publicationIntent)
    {
        sourceChanged = false;
        StoreStatus classification = _slots.ClassifyDirectoryBinding(
            exactBinding,
            out state,
            out publicationIntent);
        if (classification != StoreStatus.CorruptStore)
        {
            return classification;
        }

        StoreStatus sourceStatus = _directory.TryConfirmExactLookupReference(
            exactLocation,
            exactBinding,
            out bool sourceBefore);
        if (sourceStatus != StoreStatus.Success)
        {
            return sourceStatus;
        }

        if (!sourceBefore)
        {
            sourceChanged = true;
            return StoreStatus.Success;
        }

        classification = _slots.ClassifyDirectoryBinding(
            exactBinding,
            out state,
            out publicationIntent);

        sourceStatus = _directory.TryConfirmExactLookupReference(
            exactLocation,
            exactBinding,
            out bool sourceAfter);
        if (sourceStatus != StoreStatus.Success)
        {
            return sourceStatus;
        }

        if (!sourceAfter)
        {
            sourceChanged = true;
            return StoreStatus.Success;
        }

        return classification == StoreStatus.CorruptStore
            ? CorruptHere()
            : classification;
    }

    private StoreStatus ResolveFailedDirectoryInsert(
        in ReservationHandle reservation,
        SlotPublicationIntent publicationIntent,
        StoreStatus failure,
        out bool ordered)
    {
        ordered = false;
        if (publicationIntent == SlotPublicationIntent.ExplicitReservation)
        {
            TentativeReservationAbortResult abort =
                _slots.TryBeginTentativeAbort(reservation);
            if (abort == TentativeReservationAbortResult.Ordered)
            {
                // A rejected insertion cannot subsequently become Reserved;
                // accepting that combination would hide a broken directory
                // serialization invariant.
                if (failure is StoreStatus.DuplicateKey or StoreStatus.CorruptStore)
                {
                    return CorruptHere();
                }

                ordered = true;
                return StoreStatus.Success;
            }

            if (abort == TentativeReservationAbortResult.Corrupt)
            {
                return CorruptHere();
            }

            if (abort == TentativeReservationAbortResult.Aborted)
            {
                StoreStatus cleanup = CompleteAbortingReservation(
                    reservation,
                    LockFreeOperationBudget.StartPostOwnershipCleanup());
                return cleanup == StoreStatus.CorruptStore
                    ? cleanup
                    : failure;
            }

            return failure;
        }

        TentativeReservationAbortResult atomicAbort =
            _slots.TryBeginAtomicCandidateAbort(reservation);
        if (atomicAbort == TentativeReservationAbortResult.Corrupt
            || atomicAbort == TentativeReservationAbortResult.Ordered)
        {
            return CorruptHere();
        }

        if (atomicAbort == TentativeReservationAbortResult.Aborted)
        {
            StoreStatus cleanup = CompleteAbortingReservation(
                reservation,
                LockFreeOperationBudget.StartPostOwnershipCleanup());
            if (cleanup == StoreStatus.CorruptStore)
            {
                return cleanup;
            }
        }

        return failure;
    }

    private StoreStatus CompleteOrderedReservationRecovery(
        in ReservationHandle reservation)
    {
        StoreStatus begin = _slots.TryBeginAbort(reservation);
        if (begin == StoreStatus.Success)
        {
            StoreStatus cleanup = CompleteAbortingReservation(
                reservation,
                LockFreeOperationBudget.StartPostOwnershipCleanup());
            if (cleanup == StoreStatus.CorruptStore)
            {
                return cleanup;
            }
        }

        // Invalid/advanced means another helper already completed physical
        // recovery. The caller still receives the exact ordered handle; using
        // it subsequently reports the ordinary stale-reservation status.
        return StoreStatus.Success;
    }

    private StoreStatus CompleteAbortingBinding(
        ulong exactBinding,
        in LockFreeOperationBudget budget)
    {
        StoreStatus unlink = _directory.TryUnlink(
            exactBinding,
            budget,
            ref _checkpoint);
        if (unlink is not (StoreStatus.Success or StoreStatus.NotFound))
        {
            return unlink;
        }

        return _slots.TryCompleteRecoveryReclaim(exactBinding, budget, ref _checkpoint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReachSuccessfulReservationReturn()
    {
        Reach(LockFreeCheckpointId.DirectoryAfterDescriptorClear);
        Reach(LockFreeCheckpointId.ReserveAfterReservationPublication);
    }

    private StoreStatus CommitReservationCore(in ReservationHandle reservation)
    {
        Reach(LockFreeCheckpointId.CommitBeforePublicationCas);
        return CommitReservationAfterCheckpoint(reservation);
    }

    private StoreStatus CommitReservationCore(
        in ReservationHandle reservation,
        StoreWaitOptions waitOptions,
        long started)
    {
        Reach(LockFreeCheckpointId.CommitBeforePublicationCas);
        StoreStatus bound = CheckOperationBound(waitOptions, started);
        return bound == StoreStatus.Success
            ? CommitReservationAfterCheckpoint(reservation)
            : bound;
    }

    private StoreStatus CommitReservationAfterCheckpoint(in ReservationHandle reservation)
    {
        // CommitSequence is diagnostic metadata. The slot-control CAS below is
        // the publication ordering point, so a monotonic timestamp avoids a
        // store-wide hot counter without weakening the protocol.
        long commitSequence = Math.Max(1, Stopwatch.GetTimestamp());
        StoreStatus status = _slots.CommitReservation(reservation, commitSequence);
        if (status == StoreStatus.Success)
        {
            Reach(LockFreeCheckpointId.CommitAfterPublicationCas);
        }

        return status;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private StoreStatus AbortReservationCore(in ReservationHandle reservation)
    {
        Reach(LockFreeCheckpointId.AbortBeforeAbortCas);
        return AbortReservationAfterCheckpoint(reservation);
    }

    private StoreStatus AbortReservationCore(
        in ReservationHandle reservation,
        StoreWaitOptions waitOptions,
        long started)
    {
        Reach(LockFreeCheckpointId.AbortBeforeAbortCas);
        StoreStatus bound = CheckOperationBound(waitOptions, started);
        return bound == StoreStatus.Success
            ? AbortReservationAfterCheckpoint(reservation)
            : bound;
    }

    private StoreStatus AbortReservationAfterCheckpoint(
        in ReservationHandle reservation)
    {
        StoreStatus begin = _slots.TryBeginAbort(reservation);
        if (begin != StoreStatus.Success)
        {
            return begin;
        }

        Reach(LockFreeCheckpointId.AbortAfterOwnershipReleaseCas);

        StoreStatus cleanup = CompleteAbortingReservation(
            reservation,
            LockFreeOperationBudget.StartPostOwnershipCleanup());
        if (cleanup == StoreStatus.CorruptStore)
        {
            return cleanup;
        }

        if (cleanup == StoreStatus.Success)
        {
            Reach(LockFreeCheckpointId.AbortAfterUnlinkCompletion);
        }

        // The ownership-release CAS is the public abort ordering point. Any
        // ordinary incomplete unlink/reclaim result is now universally
        // helpable and must not be reported as a pre-order StoreBusy/cancel.
        return StoreStatus.Success;
    }

    private StoreStatus CompleteAbortingReservation(
        in ReservationHandle reservation,
        in LockFreeOperationBudget budget)
    {
        if (!TryDecodeSlotBinding(reservation.SlotBinding, out int slotIndex, out _))
        {
            return CorruptHere();
        }

        StoreStatus unlink = _directory.TryUnlink(
            reservation.SlotBinding,
            budget,
            ref _checkpoint);
        if (unlink is not (StoreStatus.Success or StoreStatus.NotFound))
        {
            return unlink;
        }

        return _slots.TryCompleteReclaim(reservation, budget, ref _checkpoint)
            ? StoreStatus.Success
            : StoreStatus.StoreBusy;
    }

    private StoreStatus ValidateOperation(StoreWaitOptions waitOptions)
    {
        if (IsDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        StoreStatus storeState = _storeControl.Validate();
        if (storeState != StoreStatus.Success)
        {
            return storeState;
        }

        if (!waitOptions.IsValid)
        {
            return StoreStatus.UnknownFailure;
        }

        return waitOptions.CancellationToken.IsCancellationRequested
            ? StoreStatus.OperationCanceled
            : StoreStatus.Success;
    }

    private StoreStatus RecordOperationStatus(StoreStatus status)
    {
        // Every public engine path has completed its exact source/state
        // revalidation before reaching this boundary. Caller-input failures
        // use their own statuses, so a remaining CorruptStore is persistent
        // mapped structural corruption and may safely poison the mapping.
        if (status == StoreStatus.CorruptStore)
        {
            _storeControl.MarkCorrupt();
        }

        if (status is StoreStatus.InvalidLease or StoreStatus.InvalidReservation)
        {
            _diagnostics.RecordInvalidToken(stale: false);
        }
        else if (status is StoreStatus.LeaseAlreadyReleased or StoreStatus.ReservationAlreadyCompleted)
        {
            _diagnostics.RecordInvalidToken(stale: true);
        }

        return _diagnostics.RecordStatus(status);
    }

    private StoreStatus CorruptHere(
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeStoreEngine),
            member,
            line);

    private StoreStatus CorruptFrom(
        string component,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            component,
            member,
            line);

    private static bool HasExpired(StoreWaitOptions waitOptions, long started)
    {
        return !waitOptions.IsInfinite
            && waitOptions.Timeout > TimeSpan.Zero
            && Stopwatch.GetElapsedTime(started) >= waitOptions.Timeout;
    }

    private static StoreStatus CheckOperationBound(StoreWaitOptions waitOptions, long started)
    {
        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        return HasExpired(waitOptions, started)
            ? StoreStatus.StoreBusy
            : StoreStatus.Success;
    }

    private static bool TryDecodeSlotBinding(ulong binding, out int slotIndex, out long generation)
    {
        slotIndex = -1;
        generation = 0;
        try
        {
            IndexBinding decoded = IndexBinding.Decode(binding);
            slotIndex = decoded.SlotIndex;
            generation = decoded.Generation;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private StoreStatus TryIsSlotPublished(
        int slotIndex,
        long generation,
        out bool isPublished)
    {
        isPublished = false;
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return CorruptHere();
        }

        long control = AtomicControlWord.LoadAcquire(ref _slots.Slot(slotIndex).Control);
        StoreStatus structure = _slots.ValidateStructuralControl(control);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        ulong raw = unchecked((ulong)control);
        isPublished = (raw & 0x7UL) == LockFreeSlotTable.PublishedState
            && ((raw >> 3) & 0x1_ffff_ffffUL) == (ulong)generation
            && (raw >> 36) == 0;
        return StoreStatus.Success;
    }

    private bool TryValidateLease(in LeaseHandle lease, out int slotIndex, out long generation)
    {
        slotIndex = -1;
        generation = 0;
        const int maximumSnapshotAttempts = 2;
        for (var attempt = 0; attempt < maximumSnapshotAttempts; attempt++)
        {
            if (IsDisposed
                || !_storeControl.IsReady
                || !_leases.TryGetActiveSlotBinding(lease, out ulong slotBinding)
                || slotBinding != lease.SlotBinding)
            {
                return false;
            }

            if (!TryDecodeSlotBinding(slotBinding, out slotIndex, out generation)
                || (uint)slotIndex >= (uint)_layout.SlotCount)
            {
                _ = CorruptHere();
                return false;
            }

            ref ValueSlotMetadataV2 slot = ref _slots.Slot(slotIndex);
            long observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (_slots.ValidateStructuralControl(observed) != StoreStatus.Success)
            {
                return false;
            }

            ulong control = unchecked((ulong)observed);
            int state = (int)(control & 0x7UL);
            if (state is LockFreeSlotTable.PublishedState or LockFreeSlotTable.RemoveRequestedState
                && ((control >> 3) & 0x1_ffff_ffffUL) == (ulong)generation)
            {
                return true;
            }

            // A copied-token release/reclaim race invalidates the active-record
            // proof before it can move this slot. Re-prove the lease first so
            // that legal expiry returns false, while a stable impossible slot
            // lifecycle paired with the exact Active lease poisons the store.
            if (!_storeControl.IsReady
                || !_leases.TryGetActiveSlotBinding(lease, out ulong confirmedBinding)
                || confirmedBinding != lease.SlotBinding)
            {
                return false;
            }

            long confirmed = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (_slots.ValidateStructuralControl(confirmed) != StoreStatus.Success)
            {
                return false;
            }

            if (confirmed != observed)
            {
                continue;
            }

            _ = CorruptHere();
            return false;
        }

        return false;
    }

    private bool TryValidateLeaseProjection(
        in LeaseHandle lease,
        out int slotIndex,
        out int valueLength,
        out int descriptorLength,
        out long payloadOffset,
        out long descriptorOffset)
    {
        slotIndex = -1;
        valueLength = 0;
        descriptorLength = 0;
        payloadOffset = 0;
        descriptorOffset = 0;
        // A logical remove is allowed to change Published(g) to
        // RemoveRequested(g) while an exact active lease continues protecting
        // the generation. Do not turn that single legal transition into a
        // transient empty projection. The active lease prevents reclamation,
        // so after retrying the moving snapshot the metadata is stable in one
        // of the two projectable states.
        const int maximumSnapshotAttempts = 2;
        for (var attempt = 0; attempt < maximumSnapshotAttempts; attempt++)
        {
            if (!TryValidateLease(lease, out slotIndex, out long generation))
            {
                return false;
            }

            ref ValueSlotMetadataV2 slot = ref _slots.Slot(slotIndex);
            long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (_slots.ValidateStructuralControl(control1) != StoreStatus.Success)
            {
                return false;
            }

            ulong directoryBinding = Volatile.Read(ref slot.DirectoryBinding);
            int keyLength = Volatile.Read(ref slot.KeyLength);
            int observedDescriptorLength = Volatile.Read(ref slot.DescriptorLength);
            int observedValueLength = Volatile.Read(ref slot.ValueLength);
            int publicationIntent = Volatile.Read(ref slot.PublicationIntent);
            long keyOffset = Volatile.Read(ref slot.KeyOffset);
            long observedDescriptorOffset = Volatile.Read(ref slot.DescriptorOffset);
            long observedPayloadOffset = Volatile.Read(ref slot.PayloadOffset);
            Reach(LockFreeCheckpointId.ProjectAfterMetadataReadBeforeControlRevalidation);
            long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (control2 != control1)
            {
                if (_slots.ValidateStructuralControl(control2) != StoreStatus.Success)
                {
                    return false;
                }

                continue;
            }

            ulong rawControl = unchecked((ulong)control1);
            int state = (int)(rawControl & 0x7UL);
            long observedGeneration = (long)((rawControl >> 3) & 0x1_ffff_ffffUL);
            bool lifecycleInvalid = state is not (
                    LockFreeSlotTable.PublishedState
                    or LockFreeSlotTable.RemoveRequestedState)
                || observedGeneration != generation;

            long expectedKeyOffset = _layout.KeyStorageOffset + ((long)slotIndex * _layout.KeyStride);
            long expectedDescriptorOffset =
                _layout.DescriptorStorageOffset + ((long)slotIndex * _layout.DescriptorStride);
            long expectedPayloadOffset =
                _layout.PayloadStorageOffset + ((long)slotIndex * _layout.PayloadStride);
            bool metadataInvalid = directoryBinding != lease.SlotBinding
                || keyLength is < 1 || keyLength > _layout.MaxKeyBytes
                || observedDescriptorLength < 0
                || observedDescriptorLength > _layout.MaxDescriptorBytes
                || observedValueLength < 0 || observedValueLength > _layout.MaxValueBytes
                || publicationIntent is not (
                    (int)SlotPublicationIntent.ExplicitReservation
                    or (int)SlotPublicationIntent.AtomicPublication)
                || keyOffset != expectedKeyOffset
                || observedDescriptorOffset != expectedDescriptorOffset
                || observedPayloadOffset != expectedPayloadOffset;

            if (!_storeControl.IsReady
                || !_leases.TryGetActiveSlotBinding(lease, out ulong confirmedBinding)
                || confirmedBinding != lease.SlotBinding)
            {
                return false;
            }

            long finalControl = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (_slots.ValidateStructuralControl(finalControl) != StoreStatus.Success)
            {
                return false;
            }

            if (finalControl != control1)
            {
                continue;
            }

            if (lifecycleInvalid || metadataInvalid)
            {
                // A stable nonprojectable lifecycle while the exact lease is
                // still Active is impossible. A copied-token release race was
                // filtered by the active-record revalidation above and expires
                // benignly instead of poisoning the store.
                _ = CorruptHere();
                return false;
            }

            valueLength = observedValueLength;
            descriptorLength = observedDescriptorLength;
            payloadOffset = expectedPayloadOffset;
            descriptorOffset = expectedDescriptorOffset;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reach(LockFreeCheckpointId checkpoint) =>
        LockFreeCheckpoint.Reach(ref _checkpoint, checkpoint);

    private static int ControlState(long control) => (int)((ulong)control & 0x7UL);

    private static ulong ControlParticipant(long control) => ((ulong)control >> 36) & 0x0fff_ffffUL;

    private static StoreStatus InitializeMapping(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        ulong pidNamespaceId,
        in LockFreeOperationBudget budget)
    {
        StoreStatus cleared = Clear(region.Pointer, layout.RequiredBytes, budget);
        if (cleared != StoreStatus.Success)
        {
            return cleared;
        }

        ref StoreHeaderV2 header = ref Header(region);
        Volatile.Write(ref header.Control, LayoutV2Constants.StoreInitializing);

        header.LayoutMajorVersion = LayoutV2Constants.LayoutMajorVersion;
        header.LayoutMinorVersion = LayoutV2Constants.LayoutMinorVersion;
        header.HeaderLength = layout.HeaderLength;
        header.ResourceProtocolVersion = LayoutV2Constants.ResourceProtocolVersion;
        header.RequiredFeatures = LayoutV2Constants.RequiredFeatures;
        header.OptionalFeatures = LayoutV2Constants.OptionalFeatures;
        header.TotalBytes = layout.TotalBytes;
        header.StoreId = CreateStoreId();
        header.Sequence = 0;
        header.SlotCount = layout.SlotCount;
        header.LeaseRecordCount = layout.LeaseRecordCount;
        header.ParticipantRecordCount = layout.ParticipantRecordCount;
        header.MaxKeyBytes = layout.MaxKeyBytes;
        header.MaxDescriptorBytes = layout.MaxDescriptorBytes;
        header.MaxValueBytes = layout.MaxValueBytes;
        header.ParticipantIndexBits = layout.ParticipantIndexBits;
        header.ParticipantGenerationBits = layout.ParticipantGenerationBits;
        header.ParticipantOffset = layout.ParticipantOffset;
        header.ParticipantLength = layout.ParticipantLength;
        header.ParticipantStride = layout.ParticipantStride;
        header.PrimaryLaneCount = layout.PrimaryLaneCount;
        header.PrimaryBucketCount = layout.PrimaryBucketCount;
        header.PrimaryBucketStride = layout.PrimaryBucketStride;
        header.PrimaryDirectoryOffset = layout.PrimaryDirectoryOffset;
        header.PrimaryDirectoryLength = layout.PrimaryDirectoryLength;
        header.OverflowDirectoryOffset = layout.OverflowDirectoryOffset;
        header.OverflowDirectoryLength = layout.OverflowDirectoryLength;
        header.OverflowStride = layout.OverflowStride;
        header.LeaseStride = layout.LeaseStride;
        header.LeaseRegistryOffset = layout.LeaseRegistryOffset;
        header.LeaseRegistryLength = layout.LeaseRegistryLength;
        header.SlotMetadataStride = layout.SlotMetadataStride;
        header.KeyStride = layout.KeyStride;
        header.SlotMetadataOffset = layout.SlotMetadataOffset;
        header.SlotMetadataLength = layout.SlotMetadataLength;
        header.KeyStorageOffset = layout.KeyStorageOffset;
        header.KeyStorageLength = layout.KeyStorageLength;
        header.DescriptorStride = layout.DescriptorStride;
        header.PayloadStride = layout.PayloadStride;
        header.DescriptorStorageOffset = layout.DescriptorStorageOffset;
        header.DescriptorStorageLength = layout.DescriptorStorageLength;
        header.PayloadStorageOffset = layout.PayloadStorageOffset;
        header.PayloadStorageLength = layout.PayloadStorageLength;
        header.PidNamespaceId = pidNamespaceId;
        header.PidNamespaceMode = OperatingSystem.IsLinux() && pidNamespaceId == 0
            ? LayoutV2Constants.PidNamespaceRecoveryMixed
            : LayoutV2Constants.PidNamespaceRecoveryEnabled;

        var participants = new LockFreeParticipantRegistry(region, layout);
        StoreStatus participantsInitialized = participants.InitializeRecords(budget);
        if (participantsInitialized != StoreStatus.Success)
        {
            return participantsInitialized;
        }

        StoreStatus leasesInitialized = InitializeLeases(region, layout, budget);
        if (leasesInitialized != StoreStatus.Success)
        {
            return leasesInitialized;
        }

        StoreStatus slotsInitialized = InitializeSlots(region, layout, budget);
        if (slotsInitialized != StoreStatus.Success)
        {
            return slotsInitialized;
        }

        StoreStatus bound = budget.Check();
        if (bound != StoreStatus.Success)
        {
            return bound;
        }

        Volatile.Write(ref header.Control, LayoutV2Constants.StoreReady);
        header.Magic = LayoutV2Constants.Magic;
        return StoreStatus.Success;
    }

    private static ulong CaptureStorePidNamespaceId()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        return SharedMemoryStore.Leasing.LeaseOwnerClassifier.TryObserveLinuxPidNamespaceId(
                Environment.ProcessId,
                out ulong pidNamespaceId)
            ? pidNamespaceId
            : 0;
    }

    private static StoreOpenStatus AdmitPidNamespace(
        ref StoreHeaderV2 header,
        ulong currentPidNamespaceId)
    {
        long mode = AtomicControlWord.LoadAcquire(ref header.PidNamespaceMode);
        if (mode is not (LayoutV2Constants.PidNamespaceRecoveryEnabled
            or LayoutV2Constants.PidNamespaceRecoveryMixed))
        {
            return StoreOpenStatus.IncompatibleLayout;
        }

        if (OperatingSystem.IsWindows())
        {
            return header.PidNamespaceId == 0
                ? StoreOpenStatus.Success
                : StoreOpenStatus.IncompatibleLayout;
        }

        if (mode == LayoutV2Constants.PidNamespaceRecoveryEnabled
            && (header.PidNamespaceId == 0
                || currentPidNamespaceId == 0
                || header.PidNamespaceId != currentPidNamespaceId))
        {
            AtomicControlWord.StoreRelease(
                ref header.PidNamespaceMode,
                LayoutV2Constants.PidNamespaceRecoveryMixed);
        }

        return StoreOpenStatus.Success;
    }

    private static StoreStatus InitializeLeases(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        in LockFreeOperationBudget budget)
    {
        long freeControl = unchecked((long)AtomicControlWord.EncodeLease(
            LayoutV2Constants.LeaseFree,
            generation: 1,
            participantToken: 0));
        for (var index = 0; index < layout.LeaseRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref LeaseRecordV2 record = ref *(LeaseRecordV2*)(
                region.Pointer + layout.LeaseRegistryOffset + ((long)index * layout.LeaseStride));
            record.SlotBinding = 0;
            record.AcquireSequence = 0;
            Volatile.Write(ref record.Control, freeControl);
        }

        return StoreStatus.Success;
    }

    private static StoreStatus InitializeSlots(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        in LockFreeOperationBudget budget)
    {
        long freeControl = unchecked((long)AtomicControlWord.EncodeSlot(
            LayoutV2Constants.SlotFree,
            generation: 1,
            participantToken: 0));
        for (var index = 0; index < layout.SlotCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref ValueSlotMetadataV2 slot = ref *(ValueSlotMetadataV2*)(
                region.Pointer + layout.SlotMetadataOffset + ((long)index * layout.SlotMetadataStride));
            slot.KeyOffset = layout.KeyStorageOffset + ((long)index * layout.KeyStride);
            slot.DescriptorOffset = layout.DescriptorStorageOffset + ((long)index * layout.DescriptorStride);
            slot.PayloadOffset = layout.PayloadStorageOffset + ((long)index * layout.PayloadStride);
            Volatile.Write(ref slot.Control, freeControl);
        }

        return StoreStatus.Success;
    }

    private static ref StoreHeaderV2 Header(MemoryMappedStoreRegion region) =>
        ref *(StoreHeaderV2*)region.Pointer;

    private static ulong CreateStoreId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        ulong value = BitConverter.ToUInt64(bytes);
        return value == 0 ? 1UL : value;
    }

    private static StoreStatus Clear(
        byte* pointer,
        long length,
        in LockFreeOperationBudget budget)
    {
        const int ClearChunkBytes = 64 * 1024;
        long cleared = 0;
        var chunkIndex = 0;
        while (cleared < length)
        {
            StoreStatus bound = budget.CheckPeriodic(chunkIndex++);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            int chunk = (int)Math.Min(ClearChunkBytes, length - cleared);
            new Span<byte>(pointer + cleared, chunk).Clear();
            cleared += chunk;
        }

        return StoreStatus.Success;
    }

    private static StoreOpenStatus ToOpenStatus(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.Success => StoreOpenStatus.Success,
            StoreStatus.StoreBusy => StoreOpenStatus.StoreBusy,
            StoreStatus.OperationCanceled => StoreOpenStatus.OperationCanceled,
            StoreStatus.AccessDenied => StoreOpenStatus.AccessDenied,
            StoreStatus.UnsupportedPlatform => StoreOpenStatus.UnsupportedPlatform,
            _ => StoreOpenStatus.MappingFailed
        };
    }
}
