using System.Buffers;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Sparse, warmed per-slot memory managers for retained-capable direct ingest.
/// Span-only users allocate no manager table; DangerousGetMemory creates one
/// small page/manager on first use and reuses it for later slot generations.
/// </summary>
internal sealed unsafe class LockFreeReservationMemory : IDisposable
{
    private const int ManagerPageShift = 8;
    private const int ManagerPageSize = 1 << ManagerPageShift;
    private const int ManagerPageMask = ManagerPageSize - 1;

    private readonly byte* _payloadStorage;
    private readonly int _payloadStride;
    private readonly int _maxValueBytes;
    private readonly int _slotCount;
    private readonly LockFreeSlotTable _slots;
    private SlotPayloadMemoryManager?[][]? _managerPages;
    private int _disposed;

    internal LockFreeReservationMemory(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        LockFreeSlotTable slots)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(slots);
        _payloadStorage = region.Pointer + layout.PayloadStorageOffset;
        _payloadStride = layout.PayloadStride;
        _maxValueBytes = layout.MaxValueBytes;
        _slotCount = layout.SlotCount;
        _slots = slots;
    }

    internal Span<byte> GetSpan(ReservationHandle reservation, int sizeHint)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !_slots.TryGetWritableRange(
                reservation,
                sizeHint,
                out int slotIndex,
                out int offset,
                out int length))
        {
            return Span<byte>.Empty;
        }

        return new Span<byte>(
            _payloadStorage + ((long)slotIndex * _payloadStride) + offset,
            length);
    }

    internal Memory<byte> GetMemory(in ReservationHandle reservation, int sizeHint)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !_slots.TryGetWritableRange(
                reservation,
                sizeHint,
                out int slotIndex,
                out int offset,
                out int length))
        {
            return Memory<byte>.Empty;
        }

        SlotPayloadMemoryManager manager = GetOrCreateManager(slotIndex);
        manager.Activate(reservation);
        Memory<byte> memory = manager.Memory;
        return memory.Length >= offset + length
            ? memory.Slice(offset, length)
            : Memory<byte>.Empty;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SlotPayloadMemoryManager?[][]? pages = Interlocked.Exchange(ref _managerPages, null);
        if (pages is null)
        {
            return;
        }

        foreach (SlotPayloadMemoryManager?[]? page in pages)
        {
            if (page is null)
            {
                continue;
            }

            foreach (SlotPayloadMemoryManager? manager in page)
            {
                if (manager is not null)
                {
                    ((IDisposable)manager).Dispose();
                }
            }
        }
    }

    private SlotPayloadMemoryManager GetOrCreateManager(int slotIndex)
    {
        SlotPayloadMemoryManager?[][]? pages = Volatile.Read(ref _managerPages);
        if (pages is null)
        {
            var created = new SlotPayloadMemoryManager?[
                (_slotCount + ManagerPageSize - 1) >> ManagerPageShift][];
            pages = Interlocked.CompareExchange(ref _managerPages, created, null) ?? created;
        }

        int pageIndex = slotIndex >> ManagerPageShift;
        SlotPayloadMemoryManager?[]? page = Volatile.Read(ref pages[pageIndex]);
        if (page is null)
        {
            var created = new SlotPayloadMemoryManager?[ManagerPageSize];
            page = Interlocked.CompareExchange(ref pages[pageIndex], created, null) ?? created;
        }

        int entryIndex = slotIndex & ManagerPageMask;
        SlotPayloadMemoryManager? manager = Volatile.Read(ref page[entryIndex]);
        if (manager is not null)
        {
            return manager;
        }

        var candidate = new SlotPayloadMemoryManager(
            _payloadStorage + ((long)slotIndex * _payloadStride),
            _maxValueBytes,
            _slots);
        manager = Interlocked.CompareExchange(ref page[entryIndex], candidate, null);
        if (manager is null)
        {
            return candidate;
        }

        ((IDisposable)candidate).Dispose();
        return manager;
    }

    private sealed unsafe class SlotPayloadMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;
        private readonly LockFreeSlotTable _slots;
        private ReservationHandle _reservation;
        private int _disposed;

        internal SlotPayloadMemoryManager(
            byte* pointer,
            int length,
            LockFreeSlotTable slots)
        {
            _pointer = pointer;
            _length = length;
            _slots = slots;
        }

        internal void Activate(in ReservationHandle reservation)
        {
            _reservation = reservation;
        }

        public override Span<byte> GetSpan()
        {
            return Volatile.Read(ref _disposed) != 0 || !_slots.IsReservationPending(_reservation)
                ? Span<byte>.Empty
                : new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_slots.IsReservationPending(_reservation))
            {
                throw new InvalidOperationException("The reservation is no longer writable.");
            }

            if ((uint)elementIndex > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            Volatile.Write(ref _disposed, 1);
            _reservation = default;
        }
    }
}
