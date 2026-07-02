using System.Buffers;
using System.Threading;
using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Ingest;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using SharedMemoryStore.Leasing;
using SharedMemoryStore.Options;
using SharedMemoryStore.Slots;

namespace SharedMemoryStore;

/// <summary>
/// Disposable owner of one bounded named shared-memory value store.
/// </summary>
public sealed unsafe class SharedMemoryStore : IDisposable
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
    private bool _disposed;

    private SharedMemoryStore(MemoryMappedStoreRegion region, StoreLayout layout, string storeName, bool leaseRecoveryEnabled)
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
    public static StoreOpenStatus TryCreateOrOpen(in SharedMemoryStoreOptions options, out SharedMemoryStore? store)
    {
        store = null;

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

        SharedMemoryStore candidate;
        try
        {
            candidate = new SharedMemoryStore(region, layout, options.Name, options.EnableLeaseRecovery);
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
        candidate.EnterStoreLock();
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
        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
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
            try
            {
                _writer.Write(ref slot, value, descriptor);
                if (!_index.TryInsert(key, hash, slotIndex, slot.Generation))
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

    /// <summary>
    /// Reserves one key, fixed descriptor, and announced payload length for direct store-owned payload writes.
    /// </summary>
    /// <remarks>
    /// The reservation remains invisible to readers until <see cref="ValueReservation.Commit"/> succeeds.
    /// Callers must advance exactly <paramref name="payloadLength"/> bytes before commit. Disposing an active
    /// reservation aborts it and descriptor bytes are immutable after reservation creation.
    /// </remarks>
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        out ValueReservation reservation)
    {
        reservation = default;

        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
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
            try
            {
                _slots.PrepareReservation(slotIndex, hash, key.Length, descriptor.Length, payloadLength);
                _writer.WriteDescriptor(ref slot, descriptor);
                if (!_index.TryInsert(key, hash, slotIndex, slot.Generation))
                {
                    _slots.Abort(slotIndex);
                    return Record(StoreStatus.DuplicateKey);
                }

                reservation = new ValueReservation(this, slotIndex, slot.Generation, payloadLength);
                return StoreStatus.Success;
            }
            catch (Exception)
            {
                _index.TryRemoveSlot(slotIndex, slot.Generation);
                _slots.Abort(slotIndex);
                return Record(StoreStatus.UnknownFailure);
            }
        }
        finally
        {
            ExitStoreLock();
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
        if (_disposed)
        {
            copiedBytes = 0;
            return Record(StoreStatus.StoreDisposed);
        }

        var status = SegmentedPublisher.Publish(this, key, payload, descriptor, out copiedBytes);
        return status == StoreStatus.Success ? status : Record(status);
    }

    /// <summary>
    /// Acquires a read lease for the value currently published under the supplied key.
    /// </summary>
    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, out ValueLease lease)
    {
        lease = default;

        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
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
            if (!_index.TryFind(key, hash, out var slotIndex, out var generation))
            {
                return Record(StoreStatus.NotFound);
            }

            ref var slot = ref _slots.GetSlot(slotIndex);
            if (Volatile.Read(ref slot.State) != LayoutConstants.SlotPublished || slot.Generation != generation)
            {
                return Record(StoreStatus.NotFound);
            }

            var sequence = Interlocked.Increment(ref Header.Sequence);
            if (!_leases.TryActivate(slotIndex, generation, sequence, out var leaseRecordId))
            {
                return Record(StoreStatus.LeaseTableFull);
            }

            Interlocked.Increment(ref slot.UsageCount);
            if (Volatile.Read(ref slot.State) != LayoutConstants.SlotPublished || slot.Generation != generation)
            {
                _ = LeaseRelease.Release(_leases, _slots, _reclaimer, slotIndex, generation, leaseRecordId);
                return Record(StoreStatus.NotFound);
            }

            lease = new ValueLease(this, slotIndex, generation, leaseRecordId);
            return StoreStatus.Success;
        }
        finally
        {
            ExitStoreLock();
        }
    }

    /// <summary>
    /// Removes the value identified by the supplied key and reclaims its slot when no active lease protects it.
    /// </summary>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key)
    {
        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
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
            if (!_index.TryFind(key, hash, out var slotIndex, out var generation))
            {
                return Record(StoreStatus.NotFound);
            }

            var status = _reclaimer.RequestRemove(slotIndex, generation);
            return status == StoreStatus.Success ? status : Record(status);
        }
        finally
        {
            ExitStoreLock();
        }
    }

    /// <summary>
    /// Explicitly recovers stale active lease records according to the supplied owner policy.
    /// </summary>
    public StoreStatus TryRecoverLeases(in LeaseRecoveryOptions options, out LeaseRecoveryReport report)
    {
        if (_disposed)
        {
            report = default;
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
        try
        {
            var ready = EnsureReady();
            if (ready != StoreStatus.Success)
            {
                report = default;
                return Record(ready);
            }

            var status = LeaseRecovery.Recover(_leases, _slots, _reclaimer, _leaseRecoveryEnabled, options, out report);
            return status == StoreStatus.Success ? status : Record(status);
        }
        finally
        {
            ExitStoreLock();
        }
    }

    /// <summary>
    /// Explicitly recovers stale pending reservations according to the supplied owner policy.
    /// </summary>
    public StoreStatus TryRecoverReservations(in ReservationRecoveryOptions options, out ReservationRecoveryReport report)
    {
        if (_disposed)
        {
            report = default;
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
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

    /// <summary>
    /// Returns a caller-formatted diagnostic snapshot without writing to console or mutating store state.
    /// </summary>
    public DiagnosticsSnapshot GetDiagnostics()
    {
        if (_disposed)
        {
            return _diagnostics.CreateSnapshot(_layout.TotalBytes, _layout.SlotCount, 0, 0, 0, 0, 0);
        }

        EnterStoreLock();
        try
        {
            if (_disposed)
            {
                return _diagnostics.CreateSnapshot(_layout.TotalBytes, _layout.SlotCount, 0, 0, 0, 0, 0);
            }

            var states = _slots.CountStates();
            return _diagnostics.CreateSnapshot(
                _layout.TotalBytes,
                _layout.SlotCount,
                states.Free,
                states.Published,
                states.PendingRemoval,
                states.ActiveReservations,
                _leases.ActiveCount());
        }
        finally
        {
            ExitStoreLock();
        }
    }

    /// <summary>
    /// Releases this store handle and invalidates future operations and lease span projections.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnterStoreLock();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _region.Dispose();
        }
        finally
        {
            ExitStoreLock();
            _mutex.Dispose();
        }
    }

    internal bool IsLeaseActive(int slotIndex, int generation, int leaseRecordId)
    {
        if (_disposed)
        {
            return false;
        }

        EnterStoreLock();
        try
        {
            return !_disposed && _leases.IsActive(leaseRecordId, slotIndex, generation);
        }
        finally
        {
            ExitStoreLock();
        }
    }

    internal int GetValueLength(int slotIndex, int generation)
    {
        return _disposed ? 0 : _reader.GetValueLength(slotIndex, generation);
    }

    internal int GetDescriptorLength(int slotIndex, int generation)
    {
        return _disposed ? 0 : _reader.GetDescriptorLength(slotIndex, generation);
    }

    internal ReadOnlySpan<byte> GetValueSpan(int slotIndex, int generation)
    {
        return _disposed ? ReadOnlySpan<byte>.Empty : _reader.GetValueSpan(slotIndex, generation);
    }

    internal ReadOnlySpan<byte> GetDescriptorSpan(int slotIndex, int generation)
    {
        return _disposed ? ReadOnlySpan<byte>.Empty : _reader.GetDescriptorSpan(slotIndex, generation);
    }

    internal StoreStatus ReleaseLease(int slotIndex, int generation, int leaseRecordId)
    {
        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
        try
        {
            if (_disposed)
            {
                return Record(StoreStatus.StoreDisposed);
            }

            var status = LeaseRelease.Release(_leases, _slots, _reclaimer, slotIndex, generation, leaseRecordId);
            return status == StoreStatus.Success ? status : Record(status);
        }
        finally
        {
            ExitStoreLock();
        }
    }

    internal StoreLayout Layout => _layout;

    internal ref StoreHeader Header => ref *(StoreHeader*)_region.Pointer;

    internal ref SharedSlotMetadata GetSlotForTesting(int slotIndex) => ref _slots.GetSlot(slotIndex);

    internal bool IsReservationPending(int slotIndex, int generation)
    {
        if (_disposed)
        {
            return false;
        }

        EnterStoreLock();
        try
        {
            return !_disposed && _slots.IsPendingReservation(slotIndex, generation);
        }
        finally
        {
            ExitStoreLock();
        }
    }

    internal int GetReservationBytesWritten(int slotIndex, int generation)
    {
        if (_disposed)
        {
            return 0;
        }

        EnterStoreLock();
        try
        {
            return !_disposed && _slots.ValidatePendingReservation(slotIndex, generation, out var slot) == StoreStatus.Success
                ? slot.Reserved
                : 0;
        }
        finally
        {
            ExitStoreLock();
        }
    }

    internal Span<byte> GetReservationSpan(int slotIndex, int generation, int sizeHint)
    {
        if (_disposed)
        {
            return Span<byte>.Empty;
        }

        EnterStoreLock();
        try
        {
            if (_disposed || _slots.ValidatePendingReservation(slotIndex, generation, out var slot) != StoreStatus.Success)
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

    internal Memory<byte> GetReservationMemory(int slotIndex, int generation, int sizeHint)
    {
        if (_disposed)
        {
            return Memory<byte>.Empty;
        }

        EnterStoreLock();
        try
        {
            if (_disposed || _slots.ValidatePendingReservation(slotIndex, generation, out var slot) != StoreStatus.Success)
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

    internal StoreStatus AdvanceReservation(int slotIndex, int generation, int byteCount)
    {
        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
        try
        {
            var ready = EnsureReady();
            if (ready != StoreStatus.Success)
            {
                return Record(ready);
            }

            var status = _slots.AdvanceReservation(slotIndex, generation, byteCount);
            return status == StoreStatus.Success ? status : Record(status);
        }
        finally
        {
            ExitStoreLock();
        }
    }

    internal StoreStatus CommitReservation(int slotIndex, int generation)
    {
        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
        try
        {
            var ready = EnsureReady();
            if (ready != StoreStatus.Success)
            {
                return Record(ready);
            }

            var status = _slots.ValidatePendingReservation(slotIndex, generation, out var slot);
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

    internal StoreStatus AbortReservation(int slotIndex, int generation, bool countAbort)
    {
        if (_disposed)
        {
            return Record(StoreStatus.StoreDisposed);
        }

        EnterStoreLock();
        try
        {
            var ready = EnsureReady();
            if (ready != StoreStatus.Success)
            {
                return Record(ready);
            }

            var status = _slots.ValidatePendingReservation(slotIndex, generation, out _);
            if (status != StoreStatus.Success)
            {
                return Record(status);
            }

            if (!_index.TryRemoveSlot(slotIndex, generation))
            {
                return Record(StoreStatus.CorruptStore);
            }

            _slots.Reclaim(slotIndex);
            if (countAbort)
            {
                _diagnostics.RecordReservationAbort();
            }

            return StoreStatus.Success;
        }
        finally
        {
            ExitStoreLock();
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
