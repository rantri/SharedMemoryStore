using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Ingest;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Leasing;
using SharedMemoryStore.Lifecycle;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.Options;
using SharedMemoryStore.Slots;

namespace SharedMemoryStore;

/// <summary>
/// Disposable process-local handle for one bounded named shared-memory value store.
/// </summary>
public sealed unsafe class MemoryStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryMappedStoreRegion _region;
    private readonly ISharedStoreSynchronization _synchronization;
    private readonly StoreLayout _layout;
    private readonly SharedKeyIndex _index;
    private readonly ReusableSlotTable _slots;
    private readonly SlotWriter _writer;
    private readonly SlotReader _reader;
    private readonly SlotReclaimer _reclaimer;
    private readonly LeaseRegistry _leases;
    private readonly StoreDiagnostics _diagnostics;
    private readonly bool _leaseRecoveryEnabled;
    private readonly ReservationMemoryManager _reservationMemory;
    private readonly StoreLifecycleGate _lifecycle = new();
    private readonly IStoreEngine? _engine;
    private long _indexCompactionCount;
    private bool _disposed;

    private MemoryStore(
        MemoryMappedStoreRegion region,
        ISharedStoreSynchronization synchronization,
        StoreLayout layout,
        bool leaseRecoveryEnabled)
    {
        _engine = null;
        Profile = StoreProfile.Legacy;
        ProtocolInfo = new StoreProtocolInfo(StoreProfile.Legacy, 1, 2, 1, 0, 0);
        _region = region;
        _synchronization = synchronization;
        _layout = layout;
        _leaseRecoveryEnabled = leaseRecoveryEnabled;
        _index = new SharedKeyIndex(region, layout);
        _slots = new ReusableSlotTable(region, layout);
        _writer = new SlotWriter(region);
        _reader = new SlotReader(region, _slots);
        _reclaimer = new SlotReclaimer(_slots, _index);
        _leases = new LeaseRegistry(region, layout);
        _diagnostics = new StoreDiagnostics();
        _reservationMemory = new ReservationMemoryManager(region, layout);
    }

    internal MemoryStore(IStoreEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        Profile = engine.Profile;
        ProtocolInfo = engine.ProtocolInfo;
        _region = null!;
        _synchronization = null!;
        _layout = default;
        _index = null!;
        _slots = null!;
        _writer = null!;
        _reader = null!;
        _reclaimer = null!;
        _leases = null!;
        _diagnostics = null!;
        _reservationMemory = null!;
    }

    /// <summary>
    /// Gets the explicitly selected layout and concurrency profile used by this handle.
    /// </summary>
    public StoreProfile Profile { get; }

    /// <summary>
    /// Gets the immutable persisted layout and resource-protocol identity, independently of the
    /// package version.
    /// </summary>
    public StoreProtocolInfo ProtocolInfo { get; }

    /// <summary>
    /// Creates or opens a named store using the supplied options and the default bounded wait policy.
    /// </summary>
    public static StoreOpenStatus TryCreateOrOpen(in SharedMemoryStoreOptions options, out MemoryStore? store)
    {
        return TryCreateOrOpen(options, StoreWaitOptions.Default, out store);
    }

    /// <summary>
    /// Creates or opens a named store using the supplied options and caller-selected cold-path
    /// lifecycle/participant wait policy.
    /// </summary>
    public static StoreOpenStatus TryCreateOrOpen(
        in SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        out MemoryStore? store)
    {
        store = null;

        if (!waitOptions.IsValid)
        {
            return StoreOpenStatus.InvalidOptions;
        }

        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreOpenStatus.OperationCanceled;
        }

        var validation = SharedMemoryStoreOptionsValidator.Validate(options, out var layout);
        if (validation != StoreOpenStatus.Success)
        {
            return validation;
        }

        if (options.Profile == StoreProfile.LockFree
            && !LayoutV2Constants.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture))
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        var waitStartTimestamp = Stopwatch.GetTimestamp();
        StoreOpenStatus mappingStatus = SharedStorePlatform.TryBeginOpen(
            options,
            waitOptions,
            waitStartTimestamp,
            out SharedStoreOpenScope? openScope);
        if (mappingStatus != StoreOpenStatus.Success || openScope is null)
        {
            return mappingStatus;
        }

        if (options.Profile == StoreProfile.LockFree)
        {
            StoreOpenStatus lockFreeStatus;
            IStoreEngine? engine = null;
            try
            {
                using (openScope)
                {
                    lockFreeStatus = StoreEngineFactory.TryCreateLockFreeUnderColdGate(
                        options,
                        waitOptions,
                        waitStartTimestamp,
                        openScope.Region,
                        openScope.Synchronization,
                        openScope.Disposition,
                        out engine);
                    if (lockFreeStatus == StoreOpenStatus.Success && engine is not null)
                    {
                        openScope.TransferResourceOwnership();
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                engine?.Dispose();
                lockFreeStatus = StoreOpenStatus.AccessDenied;
            }
            catch (Exception)
            {
                engine?.Dispose();
                lockFreeStatus = StoreOpenStatus.MappingFailed;
            }

            if (lockFreeStatus != StoreOpenStatus.Success || engine is null)
            {
                store = null;
                return lockFreeStatus;
            }

            try
            {
                // Facade construction occurs only after the platform gates are
                // released. Its failure cleanup may dispose the Linux region
                // and enter .lifecycle through exact-owner release.
                store = StoreEngineFactory.WrapOwnedEngine(engine);
                return StoreOpenStatus.Success;
            }
            catch (UnauthorizedAccessException)
            {
                store = null;
                return StoreOpenStatus.AccessDenied;
            }
            catch (Exception)
            {
                store = null;
                return StoreOpenStatus.MappingFailed;
            }
        }

        StoreStatus legacyRemainingStatus = SharedStorePlatform.TryGetRemainingWaitOptions(
            waitOptions,
            waitStartTimestamp,
            out _);
        if (legacyRemainingStatus != StoreStatus.Success)
        {
            openScope.Dispose();
            return ToOpenStatus(legacyRemainingStatus);
        }

        MemoryStore? candidate = null;
        StoreOpenStatus initializeStatus;
        try
        {
            using (openScope)
            {
                candidate = new MemoryStore(
                    openScope.Region,
                    openScope.Synchronization,
                    layout,
                    options.EnableLeaseRecovery);
                openScope.TransferResourceOwnership();
                initializeStatus = candidate.InitializeOrValidate(
                    options,
                    openScope.Disposition);
            }
        }
        catch (UnauthorizedAccessException)
        {
            candidate?.DisposeUninitialized();
            return StoreOpenStatus.AccessDenied;
        }
        catch (Exception)
        {
            candidate?.DisposeUninitialized();
            return StoreOpenStatus.MappingFailed;
        }

        if (initializeStatus != StoreOpenStatus.Success)
        {
            candidate.DisposeUninitialized();
            return initializeStatus;
        }

        try
        {
            store = StoreEngineFactory.WrapLegacy(candidate);
            return StoreOpenStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            store = null;
            return StoreOpenStatus.AccessDenied;
        }
        catch (Exception)
        {
            store = null;
            return StoreOpenStatus.MappingFailed;
        }
    }

    /// <summary>
    /// Publishes immutable payload bytes and optional descriptor bytes under an opaque byte key.
    /// </summary>
    public StoreStatus TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor = default)
    {
        return TryPublish(key, value, descriptor, StoreWaitOptions.Default);
    }

    /// <summary>
    /// Publishes immutable payload bytes using the supplied profile-specific bounded wait policy.
    /// </summary>
    public StoreStatus TryPublish(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.TryPublish(key, value, descriptor, remainingWait);
            }
        }

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var validation = ValidateOperationInput(key, value, descriptor);
                if (validation != StoreStatus.Success)
                {
                    return Record(validation);
                }

                var hash = StoreKey.Hash(key);
                if (_index.TryFind(key, hash, out var existingSlotIndex, out _))
                {
                    ref var existingSlot = ref _slots.GetSlot(existingSlotIndex);
                    if (Volatile.Read(ref existingSlot.State) is LayoutConstants.SlotPublished or LayoutConstants.SlotPublishing or LayoutConstants.SlotRemoveRequested)
                    {
                        return Record(StoreStatus.DuplicateKey);
                    }
                }

                if (!_slots.TryReserve(out var slotIndex))
                {
                    return Record(StoreStatus.StoreFull);
                }

                ref var slot = ref _slots.GetSlot(slotIndex);
                var lifecycleId = SlotLifecycleId.FromSlot(slot);
                var indexInserted = false;
                try
                {
                    _writer.Write(ref slot, value, descriptor);
                    if (!_index.TryInsert(key, hash, slotIndex, lifecycleId))
                    {
                        _slots.Abort(slotIndex);
                        return Record(StoreStatus.DuplicateKey);
                    }
                    indexInserted = true;

                    var sequence = Interlocked.Increment(ref Header.Sequence);
                    _slots.Commit(slotIndex, hash, key.Length, descriptor.Length, value.Length, sequence);
                    return StoreStatus.Success;
                }
                catch (Exception)
                {
                    if (indexInserted)
                    {
                        _index.TryRemoveSlot(slotIndex, lifecycleId, hash);
                    }
                    _slots.Abort(slotIndex);
                    return Record(StoreStatus.UnknownFailure);
                }
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Reserves one key, fixed descriptor, and announced payload length for direct store-owned payload writes.
    /// </summary>
    /// <remarks>
    /// The reservation remains invisible to readers until commit succeeds.
    /// A reservation is a single-producer lifecycle; concurrent access through copied reservation
    /// structs is unsupported.
    /// Callers write through immediate <see cref="ValueReservation.GetSpan(int)"/> views or advanced
    /// <see cref="ValueReservation.DangerousGetMemory(int)"/> direct-I/O views and must advance exactly
    /// <paramref name="payloadLength"/> bytes before commit. Disposing an active reservation aborts it,
    /// and descriptor bytes are immutable after reservation creation.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        out ValueReservation reservation)
    {
        return TryReserve(key, payloadLength, descriptor, StoreWaitOptions.Default, out reservation);
    }

    /// <summary>
    /// Reserves store-owned payload storage using the supplied profile-specific bounded wait policy.
    /// </summary>
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out ValueReservation reservation)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                reservation = default;
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                var status = _engine.TryReserve(
                    key,
                    payloadLength,
                    descriptor,
                    remainingWait,
                    out var handle);
                reservation = status == StoreStatus.Success ? new ValueReservation(this, handle) : default;
                return status;
            }
        }

        reservation = default;

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var validation = ValidateReservationInput(key, payloadLength, descriptor);
                if (validation != StoreStatus.Success)
                {
                    return Record(validation);
                }

                var hash = StoreKey.Hash(key);
                if (_index.TryFind(key, hash, out var existingSlotIndex, out _))
                {
                    ref var existingSlot = ref _slots.GetSlot(existingSlotIndex);
                    if (Volatile.Read(ref existingSlot.State) is LayoutConstants.SlotPublished or LayoutConstants.SlotPublishing or LayoutConstants.SlotRemoveRequested)
                    {
                        return Record(StoreStatus.DuplicateKey);
                    }
                }

                if (!_slots.TryReserve(out var slotIndex))
                {
                    return Record(StoreStatus.StoreFull);
                }

                ref var slot = ref _slots.GetSlot(slotIndex);
                var lifecycleId = SlotLifecycleId.FromSlot(slot);
                try
                {
                    _slots.PrepareReservation(slotIndex, hash, key.Length, descriptor.Length, payloadLength);
                    _writer.WriteDescriptor(ref slot, descriptor);
                    if (!_index.TryInsert(key, hash, slotIndex, lifecycleId))
                    {
                        _slots.Abort(slotIndex);
                        return Record(StoreStatus.DuplicateKey);
                    }

                    reservation = new ValueReservation(this, slotIndex, lifecycleId, payloadLength);
                    return StoreStatus.Success;
                }
                catch (Exception)
                {
                    _index.TryRemoveSlot(slotIndex, lifecycleId, hash);
                    _slots.Abort(slotIndex);
                    return Record(StoreStatus.UnknownFailure);
                }
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Publishes a segmented payload as one contiguous store value without requiring a caller-owned full-payload array.
    /// </summary>
    public StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        in ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        out long copiedBytes)
    {
        return TryPublishSegments(key, payload, descriptor, StoreWaitOptions.Default, out copiedBytes);
    }

    /// <summary>
    /// Publishes a segmented payload using the supplied profile-specific bounded wait policy.
    /// </summary>
    public StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        in ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                copiedBytes = 0;
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.TryPublishSegments(
                    key,
                    payload,
                    descriptor,
                    remainingWait,
                    out copiedBytes);
            }
        }

        copiedBytes = 0;
        if (payload.Length > int.MaxValue)
        {
            return Record(StoreStatus.ValueTooLarge);
        }

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var validation = ValidateOperationInput(key, (int)payload.Length, descriptor);
                if (validation != StoreStatus.Success)
                {
                    return Record(validation);
                }

                var hash = StoreKey.Hash(key);
                if (_index.TryFind(key, hash, out var existingSlotIndex, out _))
                {
                    ref var existingSlot = ref _slots.GetSlot(existingSlotIndex);
                    if (Volatile.Read(ref existingSlot.State) is LayoutConstants.SlotPublished or LayoutConstants.SlotPublishing or LayoutConstants.SlotRemoveRequested)
                    {
                        return Record(StoreStatus.DuplicateKey);
                    }
                }

                if (!_slots.TryReserve(out var slotIndex))
                {
                    return Record(StoreStatus.StoreFull);
                }

                ref var slot = ref _slots.GetSlot(slotIndex);
                var lifecycleId = SlotLifecycleId.FromSlot(slot);
                var indexInserted = false;
                try
                {
                    _writer.WriteDescriptor(ref slot, descriptor);
                    _writer.WriteSegments(ref slot, payload, out copiedBytes);
                    if (copiedBytes != payload.Length)
                    {
                        _slots.Abort(slotIndex);
                        return Record(StoreStatus.UnknownFailure);
                    }

                    if (!_index.TryInsert(key, hash, slotIndex, lifecycleId))
                    {
                        _slots.Abort(slotIndex);
                        return Record(StoreStatus.DuplicateKey);
                    }
                    indexInserted = true;

                    var sequence = Interlocked.Increment(ref Header.Sequence);
                    _slots.Commit(slotIndex, hash, key.Length, descriptor.Length, (int)payload.Length, sequence);
                    return StoreStatus.Success;
                }
                catch (Exception)
                {
                    if (indexInserted)
                    {
                        _index.TryRemoveSlot(slotIndex, lifecycleId, hash);
                    }
                    _slots.Abort(slotIndex);
                    return Record(StoreStatus.UnknownFailure);
                }
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Acquires a read lease for the value currently published under the supplied key.
    /// </summary>
    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, out ValueLease lease)
    {
        return TryAcquire(key, StoreWaitOptions.Default, out lease);
    }

    /// <summary>
    /// Acquires a shared read lease using the supplied profile-specific bounded wait policy.
    /// </summary>
    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions, out ValueLease lease)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                lease = default;
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                var status = _engine.TryAcquire(key, remainingWait, out var handle);
                lease = status == StoreStatus.Success ? new ValueLease(this, handle) : default;
                return status;
            }
        }

        lease = default;

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
                if (keyStatus != StoreStatus.Success)
                {
                    return Record(keyStatus);
                }

                var hash = StoreKey.Hash(key);
                if (!_index.TryFind(key, hash, out var slotIndex, out var lifecycleId))
                {
                    return Record(StoreStatus.NotFound);
                }

                ref var slot = ref _slots.GetSlot(slotIndex);
                if (Volatile.Read(ref slot.State) != LayoutConstants.SlotPublished
                    || !lifecycleId.Matches(slot.Generation, slot.ReuseEpoch))
                {
                    return Record(StoreStatus.NotFound);
                }

                var sequence = Interlocked.Increment(ref Header.Sequence);
                if (!_leases.TryActivate(slotIndex, lifecycleId, sequence, out var leaseRecordId))
                {
                    return Record(StoreStatus.LeaseTableFull);
                }

                Interlocked.Increment(ref slot.UsageCount);
                if (Volatile.Read(ref slot.State) != LayoutConstants.SlotPublished
                    || !lifecycleId.Matches(slot.Generation, slot.ReuseEpoch))
                {
                    _ = LeaseRelease.Release(_leases, _slots, _reclaimer, slotIndex, lifecycleId, leaseRecordId);
                    return Record(StoreStatus.NotFound);
                }

                lease = new ValueLease(this, slotIndex, lifecycleId, leaseRecordId);
                return StoreStatus.Success;
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Logically removes the value identified by the supplied key and cooperatively reclaims its
    /// storage after every protecting lease is released or safely recovered.
    /// </summary>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key)
    {
        return TryRemove(key, StoreWaitOptions.Default);
    }

    /// <summary>
    /// Logically removes the value using the supplied profile-specific bounded wait policy. After
    /// the removal ordering point the key is logically absent. The RemovePending
    /// (<see cref="StoreStatus.RemovePending"/>) result reports either a protecting lease or
    /// incomplete bounded post-removal work; physical
    /// reclamation may complete cooperatively after this call returns.
    /// </summary>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.TryRemove(key, remainingWait);
            }
        }

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
                if (keyStatus != StoreStatus.Success)
                {
                    return Record(keyStatus);
                }

                var hash = StoreKey.Hash(key);
                if (!_index.TryFind(key, hash, out var slotIndex, out var lifecycleId))
                {
                    return Record(StoreStatus.NotFound);
                }

                var status = _reclaimer.RequestRemove(slotIndex, lifecycleId);
                if (status == StoreStatus.Success)
                {
                    MaybeCompactIndex();
                }

                return status == StoreStatus.Success ? status : Record(status);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Explicitly recovers stale active lease records according to the supplied owner policy.
    /// </summary>
    /// <remarks>
    /// When <see cref="LeaseRecoveryOptions.RecoverCurrentProcessLeases"/> is true, the caller
    /// must first quiesce lease acquisition, projection, borrowed-span use, and release across
    /// every current-process handle attached to this mapping, and keep them quiescent until this
    /// call returns. False remains safe during normal concurrent lease activity.
    /// </remarks>
    public StoreStatus TryRecoverLeases(in LeaseRecoveryOptions options, out LeaseRecoveryReport report)
    {
        return TryRecoverLeases(options, StoreWaitOptions.Default, out report);
    }

    /// <summary>
    /// Explicitly recovers stale active lease records using the supplied profile-specific bounded wait policy.
    /// </summary>
    /// <remarks>
    /// When <see cref="LeaseRecoveryOptions.RecoverCurrentProcessLeases"/> is true, the caller
    /// must first quiesce lease acquisition, projection, borrowed-span use, and release across
    /// every current-process handle attached to this mapping, and keep them quiescent until this
    /// call returns. The library deliberately adds no hot-path gate for this administrative
    /// test/controlled-shutdown override.
    /// </remarks>
    public StoreStatus TryRecoverLeases(
        in LeaseRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out LeaseRecoveryReport report)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                report = default;
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.TryRecoverLeases(options, remainingWait, out report);
            }
        }

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            report = default;
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    report = default;
                    return Record(ready);
                }

                var status = LeaseRecovery.Recover(_leases, _slots, _reclaimer, _leaseRecoveryEnabled, options, out report);
                if (status == StoreStatus.Success)
                {
                    if (report.RecoveredLeaseCount > 0)
                    {
                        MaybeCompactIndex();
                    }

                    _diagnostics.RecordLeaseRecoveryResults(
                        report.RecoveredLeaseCount,
                        report.ActiveLeaseCount,
                        report.UnsupportedLeaseCount,
                        report.FailedRecoveryCount);
                    return status;
                }

                return Record(status);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Explicitly recovers stale pending reservations according to the supplied owner policy.
    /// </summary>
    public StoreStatus TryRecoverReservations(in ReservationRecoveryOptions options, out ReservationRecoveryReport report)
    {
        return TryRecoverReservations(options, StoreWaitOptions.Default, out report);
    }

    /// <summary>
    /// Explicitly recovers stale pending reservations using the supplied profile-specific bounded wait policy.
    /// </summary>
    public StoreStatus TryRecoverReservations(
        in ReservationRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out ReservationRecoveryReport report)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                report = default;
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.TryRecoverReservations(options, remainingWait, out report);
            }
        }

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            report = default;
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    report = default;
                    return Record(ready);
                }

                var status = ReservationRecovery.Recover(_layout, _slots, _index, options, out report);
                if (status == StoreStatus.Success)
                {
                    if (report.RecoveredReservationCount > 0)
                    {
                        MaybeCompactIndex();
                    }

                    _diagnostics.RecordReservationRecoveryResults(
                        report.RecoveredReservationCount,
                        report.ActiveReservationCount,
                        report.UnsupportedReservationCount,
                        report.FailedRecoveryCount);
                    return status;
                }

                return Record(status);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Returns a caller-formatted diagnostic snapshot without writing to console or mutating store state.
    /// </summary>
    public DiagnosticsSnapshot GetDiagnostics()
    {
        _ = TryGetDiagnostics(StoreWaitOptions.Default, out var snapshot);
        return snapshot;
    }

    /// <summary>
    /// Attempts to create a caller-formatted diagnostic snapshot using the default wait policy.
    /// </summary>
    public StoreStatus TryGetDiagnostics(out DiagnosticsSnapshot snapshot)
    {
        return TryGetDiagnostics(StoreWaitOptions.Default, out snapshot);
    }

    /// <summary>
    /// Attempts to create a bounded, moment-in-time diagnostic snapshot using the supplied
    /// profile-specific wait policy without imposing a global data-path pause.
    /// </summary>
    public StoreStatus TryGetDiagnostics(StoreWaitOptions waitOptions, out DiagnosticsSnapshot snapshot)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                snapshot = engineEnterStatus == StoreStatus.StoreDisposed
                    ? CreateDisposedSnapshot()
                    : default;
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.TryGetDiagnostics(remainingWait, out snapshot);
            }
        }

        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            snapshot = enterStatus == StoreStatus.StoreDisposed ? CreateDisposedSnapshot() : default;
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var states = _slots.CountStates();
                var indexState = _index.CountStates();
                snapshot = _diagnostics.CreateSnapshot(
                    _layout.TotalBytes,
                    _layout.SlotCount,
                    states.Free,
                    states.Published,
                    states.PendingRemoval,
                    states.ActiveReservations,
                    _leases.ActiveCount(),
                    indexState,
                    Volatile.Read(ref _indexCompactionCount));
                return StoreStatus.Success;
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    /// <summary>
    /// Releases this local store handle and invalidates future operations, lease spans, and
    /// reservation spans without disposing other handles attached to the mapping.
    /// </summary>
    public void Dispose()
    {
        if (!_lifecycle.IsDisposingOrDisposed
            && _engine is ILockFreeCheckpointEmitter checkpointEmitter)
        {
            checkpointEmitter.ReachCheckpoint(LockFreeCheckpointId.DisposalBeforeLocalGateClose);
        }

        if (!_lifecycle.TryBeginDispose())
        {
            return;
        }

        try
        {
            if (_engine is not null)
            {
                _engine.Dispose();
                return;
            }

            // TryBeginDispose has closed local entry and drained every active
            // operation, so teardown does not need the ordinary cross-process
            // operation lock. DisposeCore closes that descriptor before the
            // mapping's exact-owner cleanup can enter Linux .lifecycle.
            DisposeCore();
        }
        finally
        {
            _lifecycle.CompleteDispose();
        }
    }

    internal ReservationHandle CreateLegacyReservationHandle(
        int slotIndex,
        SlotLifecycleId lifecycleId,
        int payloadLength)
    {
        return new ReservationHandle(
            unchecked((ulong)Header.StoreId),
            unchecked((ulong)lifecycleId.ReuseEpoch),
            EncodeLegacySlotBinding(slotIndex, lifecycleId.Generation),
            payloadLength);
    }

    internal LeaseHandle CreateLegacyLeaseHandle(
        int slotIndex,
        SlotLifecycleId lifecycleId,
        int leaseRecordId)
    {
        return new LeaseHandle(
            unchecked((ulong)Header.StoreId),
            unchecked((ulong)lifecycleId.ReuseEpoch),
            EncodeLegacySlotBinding(slotIndex, lifecycleId.Generation),
            checked((uint)(leaseRecordId + 1)));
    }

    internal bool IsLeaseActive(in LeaseHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return false;
            }

            using (engineOperation)
            {
                return _engine.IsLeaseActive(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return false;
        }

        return TryDecodeLegacyLeaseHandle(handle, out var slotIndex, out var lifecycleId, out var leaseRecordId)
            && IsLeaseActive(slotIndex, lifecycleId, leaseRecordId);
    }

    internal int GetValueLength(in LeaseHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return 0;
            }

            using (engineOperation)
            {
                return _engine.GetValueLength(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return 0;
        }

        return TryDecodeLegacyLeaseHandle(handle, out var slotIndex, out var lifecycleId, out _)
            ? GetValueLength(slotIndex, lifecycleId)
            : 0;
    }

    internal int GetDescriptorLength(in LeaseHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return 0;
            }

            using (engineOperation)
            {
                return _engine.GetDescriptorLength(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return 0;
        }

        return TryDecodeLegacyLeaseHandle(handle, out var slotIndex, out var lifecycleId, out _)
            ? GetDescriptorLength(slotIndex, lifecycleId)
            : 0;
    }

    internal ReadOnlySpan<byte> GetValueSpan(LeaseHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return ReadOnlySpan<byte>.Empty;
            }

            using (engineOperation)
            {
                return _engine.GetValueSpan(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return TryDecodeLegacyLeaseHandle(handle, out var slotIndex, out var lifecycleId, out _)
            ? GetValueSpan(slotIndex, lifecycleId)
            : ReadOnlySpan<byte>.Empty;
    }

    internal ReadOnlySpan<byte> GetDescriptorSpan(LeaseHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return ReadOnlySpan<byte>.Empty;
            }

            using (engineOperation)
            {
                return _engine.GetDescriptorSpan(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return TryDecodeLegacyLeaseHandle(handle, out var slotIndex, out var lifecycleId, out _)
            ? GetDescriptorSpan(slotIndex, lifecycleId)
            : ReadOnlySpan<byte>.Empty;
    }

    internal StoreStatus ReleaseLease(in LeaseHandle handle, StoreWaitOptions waitOptions)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.ReleaseLease(handle, remainingWait);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        return TryDecodeLegacyLeaseHandle(handle, out var slotIndex, out var lifecycleId, out var leaseRecordId)
            ? ReleaseLease(slotIndex, lifecycleId, leaseRecordId, waitOptions)
            : StoreStatus.InvalidLease;
    }

    internal bool IsReservationPending(in ReservationHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return false;
            }

            using (engineOperation)
            {
                return _engine.IsReservationPending(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return false;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            && IsReservationPending(slotIndex, lifecycleId);
    }

    internal int GetReservationBytesWritten(in ReservationHandle handle)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return 0;
            }

            using (engineOperation)
            {
                return _engine.GetReservationBytesWritten(handle);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return 0;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            ? GetReservationBytesWritten(slotIndex, lifecycleId)
            : 0;
    }

    internal Span<byte> GetReservationSpan(ReservationHandle handle, int sizeHint)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return Span<byte>.Empty;
            }

            using (engineOperation)
            {
                return _engine.GetReservationSpan(handle, sizeHint);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return Span<byte>.Empty;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            ? GetReservationSpan(slotIndex, lifecycleId, sizeHint)
            : Span<byte>.Empty;
    }

    internal Memory<byte> GetReservationMemory(ReservationHandle handle, int sizeHint)
    {
        if (_engine is not null)
        {
            if (!_lifecycle.TryEnter(out var engineOperation))
            {
                return Memory<byte>.Empty;
            }

            using (engineOperation)
            {
                return _engine.DangerousGetReservationMemory(handle, sizeHint);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return Memory<byte>.Empty;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            ? GetReservationMemory(slotIndex, lifecycleId, sizeHint)
            : Memory<byte>.Empty;
    }

    internal StoreStatus AdvanceReservation(
        in ReservationHandle handle,
        int byteCount,
        StoreWaitOptions waitOptions)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.AdvanceReservation(handle, byteCount, remainingWait);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            ? AdvanceReservation(slotIndex, lifecycleId, byteCount, waitOptions)
            : StoreStatus.InvalidReservation;
    }

    internal StoreStatus CommitReservation(in ReservationHandle handle, StoreWaitOptions waitOptions)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.CommitReservation(handle, remainingWait);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            ? CommitReservation(slotIndex, lifecycleId, waitOptions)
            : StoreStatus.InvalidReservation;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal StoreStatus AbortReservation(
        in ReservationHandle handle,
        bool countAbort,
        StoreWaitOptions waitOptions)
    {
        if (_engine is not null)
        {
            if (!TryEnterEngineOperation(
                    waitOptions,
                    out var engineOperation,
                    out StoreWaitOptions remainingWait,
                    out StoreStatus engineEnterStatus))
            {
                return _engine.RecordFacadeStatus(engineEnterStatus);
            }

            using (engineOperation)
            {
                return _engine.AbortReservation(handle, remainingWait);
            }
        }

        if (_lifecycle.IsDisposingOrDisposed)
        {
            return StoreStatus.StoreDisposed;
        }

        return TryDecodeLegacyReservationHandle(handle, out var slotIndex, out var lifecycleId)
            ? AbortReservation(slotIndex, lifecycleId, countAbort, waitOptions)
            : StoreStatus.InvalidReservation;
    }

    internal static int DecodeLegacySlotIndex(ulong slotBinding) => unchecked((int)(uint)slotBinding) - 1;

    internal static int DecodeLegacyLeaseRecordId(in LeaseHandle handle) => unchecked((int)handle.LeaseToken) - 1;

    internal static SlotLifecycleId DecodeLegacyLifecycle(in LeaseHandle handle) =>
        new(checked((int)(handle.SlotBinding >> 32)), unchecked((long)handle.ParticipantToken));

    internal static SlotLifecycleId DecodeLegacyLifecycle(in ReservationHandle handle) =>
        new(checked((int)(handle.SlotBinding >> 32)), unchecked((long)handle.ParticipantToken));

    private static ulong EncodeLegacySlotBinding(int slotIndex, int generation) =>
        ((ulong)checked((uint)generation) << 32) | checked((uint)(slotIndex + 1));

    private bool TryDecodeLegacyReservationHandle(
        in ReservationHandle handle,
        out int slotIndex,
        out SlotLifecycleId lifecycleId)
    {
        slotIndex = -1;
        lifecycleId = default;
        if (handle.StoreId != unchecked((ulong)Header.StoreId))
        {
            return false;
        }

        slotIndex = DecodeLegacySlotIndex(handle.SlotBinding);
        lifecycleId = DecodeLegacyLifecycle(handle);
        return slotIndex >= 0 && slotIndex < _layout.SlotCount && lifecycleId.IsValid;
    }

    private bool TryDecodeLegacyLeaseHandle(
        in LeaseHandle handle,
        out int slotIndex,
        out SlotLifecycleId lifecycleId,
        out int leaseRecordId)
    {
        slotIndex = -1;
        lifecycleId = default;
        leaseRecordId = -1;
        if (handle.StoreId != unchecked((ulong)Header.StoreId))
        {
            return false;
        }

        slotIndex = DecodeLegacySlotIndex(handle.SlotBinding);
        lifecycleId = DecodeLegacyLifecycle(handle);
        leaseRecordId = DecodeLegacyLeaseRecordId(handle);
        return slotIndex >= 0 && slotIndex < _layout.SlotCount
            && leaseRecordId >= 0 && leaseRecordId < _layout.LeaseRecordCount
            && lifecycleId.IsValid;
    }

    internal bool IsLeaseActive(int slotIndex, SlotLifecycleId lifecycleId, int leaseRecordId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return false;
        }

        using (operation)
        {
            try
            {
                return _leases.IsActive(leaseRecordId, slotIndex, lifecycleId);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal int GetValueLength(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return 0;
        }

        using (operation)
        {
            try
            {
                return _reader.GetValueLength(slotIndex, lifecycleId);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal int GetDescriptorLength(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return 0;
        }

        using (operation)
        {
            try
            {
                return _reader.GetDescriptorLength(slotIndex, lifecycleId);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal ReadOnlySpan<byte> GetValueSpan(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        using (operation)
        {
            try
            {
                return _reader.GetValueSpan(slotIndex, lifecycleId);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal ReadOnlySpan<byte> GetDescriptorSpan(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        using (operation)
        {
            try
            {
                return _reader.GetDescriptorSpan(slotIndex, lifecycleId);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal StoreStatus ReleaseLease(int slotIndex, SlotLifecycleId lifecycleId, int leaseRecordId)
    {
        return ReleaseLease(slotIndex, lifecycleId, leaseRecordId, StoreWaitOptions.Default);
    }

    internal StoreStatus ReleaseLease(
        int slotIndex,
        SlotLifecycleId lifecycleId,
        int leaseRecordId,
        StoreWaitOptions waitOptions)
    {
        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var status = LeaseRelease.Release(_leases, _slots, _reclaimer, slotIndex, lifecycleId, leaseRecordId);
                if (status == StoreStatus.Success)
                {
                    MaybeCompactIndex();
                }

                return status == StoreStatus.Success ? status : Record(status);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    private MemoryStore LegacyCore => _engine is Engines.LegacyV12.LegacyV12StoreEngine legacy ? legacy.Core : this;

    internal StoreLayout Layout => LegacyCore._layout;

    internal ref StoreHeader Header => ref *(StoreHeader*)LegacyCore._region.Pointer;

    internal ref SharedSlotMetadata GetSlotForTesting(int slotIndex) => ref LegacyCore._slots.GetSlot(slotIndex);

    internal ref SharedLeaseRecord GetLeaseRecordForTesting(int leaseRecordId) => ref LegacyCore._leases.GetRecord(leaseRecordId);

    internal void SetSlotSearchCursorForTesting(int nextSearch) => LegacyCore._slots.SetNextSearchForTesting(nextSearch);

    internal void SetLeaseSearchCursorForTesting(int nextSearch) => LegacyCore._leases.SetNextSearchForTesting(nextSearch);

    internal void SetSlotLifecycleForTesting(int slotIndex, SlotLifecycleId lifecycleId)
    {
        ref var slot = ref LegacyCore._slots.GetSlot(slotIndex);
        slot.Generation = lifecycleId.Generation;
        slot.ReuseEpoch = lifecycleId.ReuseEpoch;
    }

    internal IndexStateCounts CountIndexStatesForTesting() => LegacyCore._index.CountStates();

    internal bool IsReservationPending(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return false;
        }

        using (operation)
        {
            try
            {
                return _slots.IsPendingReservation(slotIndex, lifecycleId);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal int GetReservationBytesWritten(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return 0;
        }

        using (operation)
        {
            try
            {
                return _slots.ValidatePendingReservation(slotIndex, lifecycleId, out var slot) == StoreStatus.Success
                    ? slot.Reserved
                    : 0;
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal Span<byte> GetReservationSpan(int slotIndex, SlotLifecycleId lifecycleId, int sizeHint)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return Span<byte>.Empty;
        }

        using (operation)
        {
            try
            {
                if (_slots.ValidatePendingReservation(slotIndex, lifecycleId, out var slot) != StoreStatus.Success)
                {
                    return Span<byte>.Empty;
                }

                var remaining = slot.ValueLength - slot.Reserved;
                if (remaining <= 0 || sizeHint < 0 || sizeHint > remaining)
                {
                    return Span<byte>.Empty;
                }

                return _reservationMemory.GetSpan(slotIndex, slot.Reserved, remaining);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal Memory<byte> GetReservationMemory(int slotIndex, SlotLifecycleId lifecycleId, int sizeHint)
    {
        if (!TryEnterOperation(StoreWaitOptions.Default, out var operation, out _))
        {
            return Memory<byte>.Empty;
        }

        using (operation)
        {
            try
            {
                if (_slots.ValidatePendingReservation(slotIndex, lifecycleId, out var slot) != StoreStatus.Success)
                {
                    return Memory<byte>.Empty;
                }

                var remaining = slot.ValueLength - slot.Reserved;
                if (remaining <= 0 || sizeHint < 0 || sizeHint > remaining)
                {
                    return Memory<byte>.Empty;
                }

                return _reservationMemory.GetMemory(slotIndex, slot.Reserved, remaining);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal StoreStatus AdvanceReservation(int slotIndex, SlotLifecycleId lifecycleId, int byteCount)
    {
        return AdvanceReservation(slotIndex, lifecycleId, byteCount, StoreWaitOptions.Default);
    }

    internal StoreStatus AdvanceReservation(
        int slotIndex,
        SlotLifecycleId lifecycleId,
        int byteCount,
        StoreWaitOptions waitOptions)
    {
        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var status = _slots.AdvanceReservation(slotIndex, lifecycleId, byteCount);
                return status == StoreStatus.Success ? status : Record(status);
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal StoreStatus CommitReservation(int slotIndex, SlotLifecycleId lifecycleId)
    {
        return CommitReservation(slotIndex, lifecycleId, StoreWaitOptions.Default);
    }

    internal StoreStatus CommitReservation(int slotIndex, SlotLifecycleId lifecycleId, StoreWaitOptions waitOptions)
    {
        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var status = _slots.ValidatePendingReservation(slotIndex, lifecycleId, out var slot);
                if (status != StoreStatus.Success)
                {
                    return Record(status);
                }

                if (slot.Reserved != slot.ValueLength)
                {
                    return Record(StoreStatus.ReservationIncomplete);
                }

                var sequence = Interlocked.Increment(ref Header.Sequence);
                _slots.Commit(slotIndex, slot.KeyHash, slot.KeyLength, slot.DescriptorLength, slot.ValueLength, sequence);
                return StoreStatus.Success;
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    internal StoreStatus AbortReservation(int slotIndex, SlotLifecycleId lifecycleId, bool countAbort)
    {
        return AbortReservation(slotIndex, lifecycleId, countAbort, StoreWaitOptions.Default);
    }

    internal StoreStatus AbortReservation(
        int slotIndex,
        SlotLifecycleId lifecycleId,
        bool countAbort,
        StoreWaitOptions waitOptions)
    {
        if (!TryEnterOperation(waitOptions, out var operation, out var enterStatus))
        {
            return Record(enterStatus);
        }

        using (operation)
        {
            try
            {
                var ready = EnsureReady();
                if (ready != StoreStatus.Success)
                {
                    return Record(ready);
                }

                var status = _slots.ValidatePendingReservation(slotIndex, lifecycleId, out var slot);
                if (status != StoreStatus.Success)
                {
                    return Record(status);
                }

                if (!_index.TryRemoveSlot(slotIndex, lifecycleId, slot.KeyHash))
                {
                    return Record(StoreStatus.CorruptStore);
                }

                var reclaimStatus = _slots.Reclaim(slotIndex);
                if (reclaimStatus != StoreStatus.Success)
                {
                    return Record(reclaimStatus);
                }
                if (countAbort)
                {
                    _diagnostics.RecordReservationAbort();
                }

                MaybeCompactIndex();
                return StoreStatus.Success;
            }
            finally
            {
                ExitStoreLock();
            }
        }
    }

    private StoreOpenStatus InitializeOrValidate(
        SharedMemoryStoreOptions options,
        RegionOpenDisposition disposition)
    {
        // Existing regions are mapped at their actual backing capacity so an
        // opposite-profile header can be rejected without first projecting the
        // caller's requested (possibly much larger) view. Prove the complete
        // fixed v1 header is mapped before creating a ref to it.
        if (_region.Capacity < Marshal.SizeOf<StoreHeader>())
        {
            return StoreOpenStatus.IncompatibleLayout;
        }

        ref var header = ref Header;
        if (disposition == RegionOpenDisposition.CreatedNew)
        {
            if (options.OpenMode == OpenMode.OpenExisting)
            {
                return StoreOpenStatus.IncompatibleLayout;
            }

            // Initialization clears RequiredBytes and publishes TotalBytes in
            // the header. Both lengths must describe storage actually mapped by
            // this handle before either write is attempted.
            if (_region.Capacity < _layout.RequiredBytes
                || _region.Capacity < _layout.TotalBytes)
            {
                return StoreOpenStatus.IncompatibleLayout;
            }

            InitializeHeader();
            _slots.Initialize();
            _leases.Initialize();
            return StoreOpenStatus.Success;
        }

        if (options.OpenMode == OpenMode.CreateNew)
        {
            return StoreOpenStatus.AlreadyExists;
        }

        if (header.Magic == 0)
        {
            // An existing unpublished mapping may belong to an older creator
            // that exposed the region before entering the ordinary gate. This
            // opener has no proof of initialization ownership and must not
            // mutate it.
            return options.OpenMode == OpenMode.CreateOrOpen
                ? StoreOpenStatus.StoreBusy
                : StoreOpenStatus.IncompatibleLayout;
        }

        if (header.Magic != LayoutConstants.Magic
            || header.LayoutMajorVersion != LayoutConstants.LayoutMajorVersion
            || !_layout.MatchesHeader(header)
            || !ValidateSectionBounds(header))
        {
            return StoreOpenStatus.IncompatibleLayout;
        }

        // Header and section validation proves the requested v1 topology, but
        // it does not prove that a live backing file was not truncated after the
        // header was committed. Never accept the layout unless both its declared
        // extent and its computed required extent fit in the actual view.
        if (header.TotalBytes < _layout.RequiredBytes
            || _region.Capacity < header.TotalBytes
            || _region.Capacity < _layout.RequiredBytes)
        {
            return StoreOpenStatus.IncompatibleLayout;
        }

        return Volatile.Read(ref header.StoreState) == LayoutConstants.StoreUnsupported
            ? StoreOpenStatus.UnsupportedPlatform
            : StoreOpenStatus.Success;
    }

    private void InitializeHeader()
    {
        ClearRegion(_layout.RequiredBytes);

        ref var header = ref Header;
        header.Magic = LayoutConstants.Magic;
        header.LayoutMajorVersion = LayoutConstants.LayoutMajorVersion;
        header.LayoutMinorVersion = LayoutConstants.LayoutMinorVersion;
        header.HeaderLength = _layout.HeaderLength;
        header.TotalBytes = _layout.TotalBytes;
        header.SlotCount = _layout.SlotCount;
        header.LeaseRecordCount = _layout.LeaseRecordCount;
        header.MaxKeyBytes = _layout.MaxKeyBytes;
        header.MaxDescriptorBytes = _layout.MaxDescriptorBytes;
        header.MaxValueBytes = _layout.MaxValueBytes;
        header.IndexEntryCount = _layout.IndexEntryCount;
        header.IndexEntrySize = _layout.IndexEntrySize;
        header.IndexOffset = _layout.IndexOffset;
        header.IndexLength = _layout.IndexLength;
        header.LeaseRegistryOffset = _layout.LeaseRegistryOffset;
        header.LeaseRegistryLength = _layout.LeaseRegistryLength;
        header.SlotMetadataOffset = _layout.SlotMetadataOffset;
        header.SlotMetadataLength = _layout.SlotMetadataLength;
        header.DescriptorStorageOffset = _layout.DescriptorStorageOffset;
        header.DescriptorStorageLength = _layout.DescriptorStorageLength;
        header.PayloadStorageOffset = _layout.PayloadStorageOffset;
        header.PayloadStorageLength = _layout.PayloadStorageLength;
        header.StoreId = DateTime.UtcNow.Ticks ^ Environment.ProcessId;
        header.Sequence = 0;
        Volatile.Write(ref header.StoreState, LayoutConstants.StoreReady);
    }

    private void ClearRegion(long length)
    {
        var pointer = _region.Pointer;
        for (var i = 0L; i < length; i++)
        {
            pointer[i] = 0;
        }
    }

    private StoreStatus ValidateOperationInput(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor)
    {
        return ValidateOperationInput(key, value.Length, descriptor);
    }

    private StoreStatus ValidateOperationInput(ReadOnlySpan<byte> key, int valueLength, ReadOnlySpan<byte> descriptor)
    {
        var keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
        if (keyStatus != StoreStatus.Success)
        {
            return keyStatus;
        }

        if (valueLength > _layout.MaxValueBytes)
        {
            return StoreStatus.ValueTooLarge;
        }

        return descriptor.Length > _layout.MaxDescriptorBytes
            ? StoreStatus.DescriptorTooLarge
            : StoreStatus.Success;
    }

    private StoreStatus ValidateReservationInput(ReadOnlySpan<byte> key, int payloadLength, ReadOnlySpan<byte> descriptor)
    {
        var keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
        if (keyStatus != StoreStatus.Success)
        {
            return keyStatus;
        }

        if (payloadLength < 0 || payloadLength > _layout.MaxValueBytes)
        {
            return StoreStatus.ValueTooLarge;
        }

        return descriptor.Length > _layout.MaxDescriptorBytes
            ? StoreStatus.DescriptorTooLarge
            : StoreStatus.Success;
    }

    private bool TryEnterEngineOperation(
        StoreWaitOptions waitOptions,
        out StoreLifecycleGate.Operation operation,
        out StoreWaitOptions remainingWait,
        out StoreStatus status)
    {
        long started = Stopwatch.GetTimestamp();
        status = _lifecycle.TryEnter(waitOptions, started, out operation);
        if (status != StoreStatus.Success)
        {
            remainingWait = default;
            return false;
        }

        if (TryGetRemainingWaitOptions(
                waitOptions,
                started,
                out remainingWait,
                out status))
        {
            return true;
        }

        operation.Dispose();
        operation = default;
        return false;
    }

    private static bool TryGetRemainingWaitOptions(
        StoreWaitOptions waitOptions,
        long started,
        out StoreWaitOptions remainingWait,
        out StoreStatus status)
    {
        remainingWait = default;
        if (!waitOptions.IsValid)
        {
            status = StoreStatus.UnknownFailure;
            return false;
        }

        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            status = StoreStatus.OperationCanceled;
            return false;
        }

        if (waitOptions.IsInfinite || waitOptions.Timeout == TimeSpan.Zero)
        {
            remainingWait = waitOptions;
            status = StoreStatus.Success;
            return true;
        }

        TimeSpan remaining = waitOptions.Timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            status = StoreStatus.StoreBusy;
            return false;
        }

        remainingWait = new StoreWaitOptions(remaining, waitOptions.CancellationToken);
        status = StoreStatus.Success;
        return true;
    }

    private bool TryEnterOperation(
        StoreWaitOptions waitOptions,
        out StoreLifecycleGate.Operation operation,
        out StoreStatus status)
    {
        operation = default;
        if (!waitOptions.IsValid)
        {
            status = StoreStatus.UnknownFailure;
            return false;
        }

        if (!_lifecycle.TryEnter(out operation))
        {
            status = StoreStatus.StoreDisposed;
            return false;
        }

        if (TryEnterStoreLock(waitOptions, out status))
        {
            return true;
        }

        operation.Dispose();
        return false;
    }

    private bool TryEnterStoreLock(StoreWaitOptions waitOptions, out StoreStatus status)
    {
        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            status = StoreStatus.OperationCanceled;
            return false;
        }

        var waitStartTimestamp = Stopwatch.GetTimestamp();
        bool gateEntered;
        try
        {
            gateEntered = waitOptions.IsInfinite
                ? _gate.Wait(Timeout.InfiniteTimeSpan, waitOptions.CancellationToken)
                : _gate.Wait(waitOptions.Timeout, waitOptions.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            status = StoreStatus.OperationCanceled;
            return false;
        }

        if (!gateEntered)
        {
            status = waitOptions.CancellationToken.IsCancellationRequested
                ? StoreStatus.OperationCanceled
                : StoreStatus.StoreBusy;
            return false;
        }

        if (_lifecycle.IsDisposingOrDisposed || _disposed)
        {
            _gate.Release();
            status = StoreStatus.StoreDisposed;
            return false;
        }

        status = _synchronization.TryEnter(waitOptions.RemainingSince(waitStartTimestamp));
        if (status != StoreStatus.Success)
        {
            _gate.Release();
            return false;
        }

        status = StoreStatus.Success;
        return true;
    }

    private static StoreOpenStatus ToOpenStatus(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.Success => StoreOpenStatus.Success,
            StoreStatus.StoreBusy => StoreOpenStatus.StoreBusy,
            StoreStatus.OperationCanceled => StoreOpenStatus.OperationCanceled,
            StoreStatus.StoreDisposed => StoreOpenStatus.StoreBusy,
            StoreStatus.AccessDenied => StoreOpenStatus.AccessDenied,
            StoreStatus.UnsupportedPlatform => StoreOpenStatus.UnsupportedPlatform,
            _ => StoreOpenStatus.MappingFailed
        };
    }

    private void DisposeUninitialized()
    {
        try
        {
            DisposeCore();
        }
        finally
        {
            _lifecycle.CompleteDispose();
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reservationMemory.Dispose();
        try
        {
            // Region disposal may acquire Linux .lifecycle and commit final-
            // owner cleanup. Retire the ordinary lock descriptor first so no
            // reopening participant can inherit an obsolete inode generation.
            _synchronization.Dispose();
        }
        finally
        {
            _region.Dispose();
        }
    }

    internal DiagnosticsSnapshot CreateDisposedSnapshot()
    {
        if (_engine is not null)
        {
            return _engine.CreateDisposedDiagnosticsSnapshot();
        }

        return _diagnostics.CreateSnapshot(
            _layout.TotalBytes,
            _layout.SlotCount,
            0,
            0,
            0,
            0,
            0,
            new IndexStateCounts(_layout.IndexEntryCount, 0, 0, 0, 0, 0),
            Volatile.Read(ref _indexCompactionCount));
    }

    private StoreStatus EnsureReady()
    {
        if (_disposed)
        {
            return StoreStatus.StoreDisposed;
        }

        return Volatile.Read(ref Header.StoreState) switch
        {
            LayoutConstants.StoreReady => StoreStatus.Success,
            LayoutConstants.StoreUnsupported => StoreStatus.UnsupportedPlatform,
            LayoutConstants.StoreCorrupt => StoreStatus.CorruptStore,
            _ => StoreStatus.UnknownFailure
        };
    }

    private StoreStatus Record(StoreStatus status)
    {
        if (status == StoreStatus.CorruptStore && !_disposed)
        {
            Volatile.Write(ref Header.StoreState, LayoutConstants.StoreCorrupt);
        }

        _diagnostics.Record(status);
        return status;
    }

    /// <summary>
    /// Records a status rejected by the outer profile-neutral facade. This is
    /// deliberately diagnostics-only so it remains safe after failed lifecycle
    /// entry and while another thread may be disposing mapped resources.
    /// </summary>
    internal StoreStatus RecordFacadeStatus(StoreStatus status)
    {
        _diagnostics.Record(status);
        return status;
    }

    private void MaybeCompactIndex()
    {
        var state = _index.CountStates();
        if (state.TombstoneCount == 0)
        {
            return;
        }

        var tombstonePressure = state.TombstonePressureRatio >= 0.35d || state.EmptyCount == 0;
        var probePressure = state.MaxObservedProbeLength >= Math.Max(1, (state.EntryCount * 3) / 4);
        if ((tombstonePressure || probePressure) && _index.TryCompact())
        {
            Interlocked.Increment(ref _indexCompactionCount);
        }
    }

    private static bool ValidateSectionBounds(in StoreHeader header)
    {
        return header.IndexOffset >= header.HeaderLength
            && header.IndexOffset + header.IndexLength <= header.TotalBytes
            && header.LeaseRegistryOffset >= header.IndexOffset + header.IndexLength
            && header.LeaseRegistryOffset + header.LeaseRegistryLength <= header.TotalBytes
            && header.SlotMetadataOffset >= header.LeaseRegistryOffset + header.LeaseRegistryLength
            && header.SlotMetadataOffset + header.SlotMetadataLength <= header.TotalBytes
            && header.DescriptorStorageOffset >= header.SlotMetadataOffset + header.SlotMetadataLength
            && header.DescriptorStorageOffset + header.DescriptorStorageLength <= header.TotalBytes
            && header.PayloadStorageOffset >= header.DescriptorStorageOffset + header.DescriptorStorageLength
            && header.PayloadStorageOffset + header.PayloadStorageLength <= header.TotalBytes;
    }

    private void ExitStoreLock()
    {
        _synchronization.Exit();
        _gate.Release();
    }
}
