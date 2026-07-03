using System.Buffers;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Ingest;

internal sealed unsafe class ReservationMemoryManager : IDisposable
{
    private readonly SlotPayloadMemoryManager[] _payloads;
    private bool _disposed;

    public ReservationMemoryManager(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _payloads = new SlotPayloadMemoryManager[layout.SlotCount];
        for (var i = 0; i < _payloads.Length; i++)
        {
            _payloads[i] = new SlotPayloadMemoryManager(region.Pointer + layout.PayloadStorageOffset + ((long)i * layout.PayloadStride), layout.MaxValueBytes);
        }
    }

    public Span<byte> GetSpan(int slotIndex, int offset, int length)
    {
        if (_disposed)
        {
            return Span<byte>.Empty;
        }

        return _payloads[slotIndex].GetSpan().Slice(offset, length);
    }

    public Memory<byte> GetMemory(int slotIndex, int offset, int length)
    {
        if (_disposed)
        {
            return Memory<byte>.Empty;
        }

        return _payloads[slotIndex].Memory.Slice(offset, length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var payload in _payloads)
        {
            ((IDisposable)payload).Dispose();
        }
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
            if (_disposed)
            {
                return Span<byte>.Empty;
            }

            return new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            if (_disposed)
            {
                return default;
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
