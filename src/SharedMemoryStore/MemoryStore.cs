using System.Buffers;
using System.Threading;
using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Ingest;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using SharedMemoryStore.Leasing;
using SharedMemoryStore.Lifecycle;
using SharedMemoryStore.Options;
using SharedMemoryStore.Slots;

namespace SharedMemoryStore;

/// <summary>
/// Disposable owner of one bounded named shared-memory value store.
/// </summary>
public sealed unsafe class MemoryStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Mutex _mutex;
    private readonly MemoryMappedStoreRegion _region;
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
    private long _indexCompactionCount;
    private bool _disposed;

    private MemoryStore(MemoryMappedStoreRegion region, StoreLayout layout, string storeName, bool leaseRecoveryEnabled)
    {
        _mutex = new Mutex(false, BuildMutexName(storeName));
        _region = region;
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

    /// <summary>
    /// Creates or opens a named store using the supplied options.
    /// </summary>
    public static StoreOpenStatus TryCreateOrOpen(in SharedMemoryStoreOptions options, out MemoryStore? store)
    {
        return TryCreateOrOpen(options, StoreWaitOptions.Default, out store);
    }

    /// <summary>
    /// Creates or opens a named store using the supplied options and wait policy.
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

        var mappingStatus = MemoryMappedStoreRegion.TryOpen(options, out var region);
        if (mappingStatus != StoreOpenStatus.Success || region is null)
        {
            return mappingStatus;
        }

        MemoryStore candidate;
        try
        {
            candidate = new MemoryStore(region, layout, options.Name, options.EnableLeaseRecovery);
        }
        catch (UnauthorizedAccessException)
        {
            region.Dispose();
            return StoreOpenStatus.AccessDenied;
        }
        catch (Exception)
        {
            region.Dispose();
            return StoreOpenStatus.MappingFailed;
        }

        StoreOpenStatus initializeStatus;
        if (!candidate.TryEnterStoreLock(waitOptions, out var lockStatus))
        {
            candidate.DisposeUninitialized();
            return ToOpenStatus(lockStatus);
        }

        try
        {
            initializeStatus = candidate.InitializeOrValidate(options);
        }
        finally
        {
            candidate.ExitStoreLock();
        }

        if (initializeStatus != StoreOpenStatus.Success)
        {
            candidate.Dispose();
            return initializeStatus;
        }

        store = candidate;
        return StoreOpenStatus.Success;
    }

    /// <summary>
    /// Publishes immutable value bytes and optional descriptor bytes under an opaque byte key.
    /// </summary>
    public StoreStatus TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor = default)
    {
        return TryPublish(key, value, descriptor, StoreWaitOptions.Default);
    }

    /// <summary>
    /// Publishes immutable value bytes using the supplied wait policy for shared synchronization.
    /// </summary>
    public StoreStatus TryPublish(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor,
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
                try
                {
                    _writer.Write(ref slot, value, descriptor);
                    if (!_index.TryInsert(key, hash, slotIndex, lifecycleId))
                    {
                        _slots.Abort(slotIndex);
                        return Record(StoreStatus.DuplicateKey);
                    }

                    var sequence = Interlocked.Increment(ref Header.Sequence);
                    _slots.Commit(slotIndex, hash, key.Length, descriptor.Length, value.Length, sequence);
                    return StoreStatus.Success;
                }
                catch (Exception)
                {
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
    /// Callers must advance exactly <paramref name="payloadLength"/> bytes before commit. Disposing an active
    /// reservation aborts it and descriptor bytes are immutable after reservation creation.
    /// </remarks>
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        out ValueReservation reservation)
    {
        return TryReserve(key, payloadLength, descriptor, StoreWaitOptions.Default, out reservation);
    }

    /// <summary>
    /// Reserves store-owned payload storage using the supplied wait policy for shared synchronization.
    /// </summary>
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out ValueReservation reservation)
    {
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
                    _index.TryRemoveSlot(slotIndex, lifecycleId);
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
    /// Publishes a segmented payload as one contiguous store value without allocating a temporary full-payload array.
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
    /// Publishes a segmented payload using the supplied wait policy for each synchronized reservation operation.
    /// </summary>
    public StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        in ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes)
    {
        var status = SegmentedPublisher.Publish(this, key, payload, descriptor, waitOptions, out copiedBytes);
        return status == StoreStatus.Success ? status : Record(status);
    }

    /// <summary>
    /// Acquires a read lease for the value currently published under the supplied key.
    /// </summary>
    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, out ValueLease lease)
    {
        return TryAcquire(key, StoreWaitOptions.Default, out lease);
    }

    /// <summary>
    /// Acquires a read lease using the supplied wait policy for shared synchronization.
    /// </summary>
    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions, out ValueLease lease)
    {
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
    /// Removes the value identified by the supplied key and reclaims its slot when no active lease protects it.
    /// </summary>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key)
    {
        return TryRemove(key, StoreWaitOptions.Default);
    }

    /// <summary>
    /// Removes the value identified by the supplied key using the supplied wait policy for shared synchronization.
    /// </summary>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions)
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
    public StoreStatus TryRecoverLeases(in LeaseRecoveryOptions options, out LeaseRecoveryReport report)
    {
        return TryRecoverLeases(options, StoreWaitOptions.Default, out report);
    }

    /// <summary>
    /// Explicitly recovers stale active lease records using the supplied wait policy for shared synchronization.
    /// </summary>
    public StoreStatus TryRecoverLeases(
        in LeaseRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out LeaseRecoveryReport report)
    {
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
    /// Explicitly recovers stale pending reservations using the supplied wait policy for shared synchronization.
    /// </summary>
    public StoreStatus TryRecoverReservations(
        in ReservationRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out ReservationRecoveryReport report)
    {
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
    /// Attempts to create a caller-formatted diagnostic snapshot using the supplied wait policy.
    /// </summary>
    public StoreStatus TryGetDiagnostics(StoreWaitOptions waitOptions, out DiagnosticsSnapshot snapshot)
    {
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
    /// Releases this store handle and invalidates future operations and lease span projections.
    /// </summary>
    public void Dispose()
    {
        if (!_lifecycle.TryBeginDispose())
        {
            return;
        }

        try
        {
            var lockTaken = TryEnterStoreLock(StoreWaitOptions.Default, out _);
            if (lockTaken)
            {
                try
                {
                    DisposeCore();
                }
                finally
                {
                    ExitStoreLock();
                }
            }
            else
            {
                DisposeCore();
            }
        }
        finally
        {
            _lifecycle.CompleteDispose();
            _mutex.Dispose();
        }
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

    internal StoreLayout Layout => _layout;

    internal ref StoreHeader Header => ref *(StoreHeader*)_region.Pointer;

    internal ref SharedSlotMetadata GetSlotForTesting(int slotIndex) => ref _slots.GetSlot(slotIndex);

    internal ref SharedLeaseRecord GetLeaseRecordForTesting(int leaseRecordId) => ref _leases.GetRecord(leaseRecordId);

    internal void SetSlotSearchCursorForTesting(int nextSearch) => _slots.SetNextSearchForTesting(nextSearch);

    internal void SetLeaseSearchCursorForTesting(int nextSearch) => _leases.SetNextSearchForTesting(nextSearch);

    internal void SetSlotLifecycleForTesting(int slotIndex, SlotLifecycleId lifecycleId)
    {
        ref var slot = ref _slots.GetSlot(slotIndex);
        slot.Generation = lifecycleId.Generation;
        slot.ReuseEpoch = lifecycleId.ReuseEpoch;
    }

    internal IndexStateCounts CountIndexStatesForTesting() => _index.CountStates();

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

                var status = _slots.ValidatePendingReservation(slotIndex, lifecycleId, out _);
                if (status != StoreStatus.Success)
                {
                    return Record(status);
                }

                if (!_index.TryRemoveSlot(slotIndex, lifecycleId))
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

    private StoreOpenStatus InitializeOrValidate(SharedMemoryStoreOptions options)
    {
        ref var header = ref Header;
        if (options.OpenMode == OpenMode.CreateNew || header.Magic == 0)
        {
            if (options.OpenMode == OpenMode.OpenExisting)
            {
                return StoreOpenStatus.IncompatibleLayout;
            }

            InitializeHeader();
            _slots.Initialize();
            _leases.Initialize();
            return StoreOpenStatus.Success;
        }

        if (header.Magic != LayoutConstants.Magic
            || header.LayoutMajorVersion != LayoutConstants.LayoutMajorVersion
            || !_layout.MatchesHeader(header)
            || !ValidateSectionBounds(header))
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
        var keyStatus = StoreKey.Validate(key, _layout.MaxKeyBytes);
        if (keyStatus != StoreStatus.Success)
        {
            return keyStatus;
        }

        if (value.Length > _layout.MaxValueBytes)
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

        bool acquired;
        try
        {
            acquired = WaitForStoreMutex(waitOptions);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner ended without releasing the mutex; shared state is validated after acquisition.
            acquired = true;
        }

        if (!acquired)
        {
            status = waitOptions.CancellationToken.IsCancellationRequested
                ? StoreStatus.OperationCanceled
                : StoreStatus.StoreBusy;
            return false;
        }

        Monitor.Enter(_gate);
        if (_lifecycle.IsDisposingOrDisposed || _disposed)
        {
            Monitor.Exit(_gate);
            _mutex.ReleaseMutex();
            status = StoreStatus.StoreDisposed;
            return false;
        }

        status = StoreStatus.Success;
        return true;
    }

    private bool WaitForStoreMutex(StoreWaitOptions waitOptions)
    {
        if (!waitOptions.CancellationToken.CanBeCanceled)
        {
            return waitOptions.IsInfinite
                ? _mutex.WaitOne(System.Threading.Timeout.InfiniteTimeSpan)
                : _mutex.WaitOne(waitOptions.Timeout);
        }

        var waitHandles = new WaitHandle[] { _mutex, waitOptions.CancellationToken.WaitHandle };
        var signaled = waitOptions.IsInfinite
            ? WaitHandle.WaitAny(waitHandles)
            : WaitHandle.WaitAny(waitHandles, waitOptions.Timeout);

        return signaled switch
        {
            0 => true,
            1 => false,
            WaitHandle.WaitTimeout => false,
            _ => false
        };
    }

    private static StoreOpenStatus ToOpenStatus(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.Success => StoreOpenStatus.Success,
            StoreStatus.StoreBusy => StoreOpenStatus.StoreBusy,
            StoreStatus.OperationCanceled => StoreOpenStatus.OperationCanceled,
            StoreStatus.StoreDisposed => StoreOpenStatus.StoreBusy,
            _ => StoreOpenStatus.MappingFailed
        };
    }

    private void DisposeUninitialized()
    {
        DisposeCore();
        _mutex.Dispose();
        _lifecycle.CompleteDispose();
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reservationMemory.Dispose();
        _region.Dispose();
    }

    private DiagnosticsSnapshot CreateDisposedSnapshot()
    {
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

    private void EnterStoreLock()
    {
        try
        {
            _mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The previous owner ended without releasing the mutex; the shared state remains validated separately.
        }

        Monitor.Enter(_gate);
    }

    private void ExitStoreLock()
    {
        Monitor.Exit(_gate);
        _mutex.ReleaseMutex();
    }

    private static string BuildMutexName(string storeName)
    {
        return @"Local\SharedMemoryStore-" + string.Create(storeName.Length, storeName, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var value = source[i];
                destination[i] = char.IsLetterOrDigit(value) || value is '-' or '_' ? value : '_';
            }
        });
    }
}
