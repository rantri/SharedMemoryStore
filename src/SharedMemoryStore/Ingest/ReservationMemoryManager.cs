using System.Buffers;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Ingest;

internal sealed unsafe class ReservationMemoryManager : IDisposable
{
    private readonly byte* _payloadStorage;
    private readonly int _payloadStride;
    private readonly int _maxValueBytes;
    private readonly Dictionary<int, SlotPayloadMemoryManager> _payloads = new();
    private bool _disposed;

    public ReservationMemoryManager(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _payloadStorage = region.Pointer + layout.PayloadStorageOffset;
        _payloadStride = layout.PayloadStride;
        _maxValueBytes = layout.MaxValueBytes;
    }

    public Span<byte> GetSpan(int slotIndex, int offset, int length)
    {
        if (_disposed)
        {
            return Span<byte>.Empty;
        }

        return new Span<byte>(_payloadStorage + ((long)slotIndex * _payloadStride) + offset, length);
    }

    public Memory<byte> GetMemory(int slotIndex, int offset, int length)
    {
        if (_disposed)
        {
            return Memory<byte>.Empty;
        }

        if (!_payloads.TryGetValue(slotIndex, out var payload))
        {
            payload = new SlotPayloadMemoryManager(
                _payloadStorage + ((long)slotIndex * _payloadStride),
                _maxValueBytes);
            _payloads.Add(slotIndex, payload);
        }

        return payload.Memory.Slice(offset, length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var payload in _payloads.Values)
        {
            ((IDisposable)payload).Dispose();
        }

        _payloads.Clear();
    }

    private sealed unsafe class SlotPayloadMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;
        private bool _disposed;

        public SlotPayloadMemoryManager(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan()
        {
            return _disposed
                ? Span<byte>.Empty
                : new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SlotPayloadMemoryManager));
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
            _disposed = true;
        }
    }
}
