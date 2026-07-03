using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Ingest;

internal sealed unsafe class ReservationMemoryManager : IDisposable
{
    private readonly byte* _payloadStorage;
    private readonly int _payloadStride;
    private bool _disposed;

    public ReservationMemoryManager(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _payloadStorage = region.Pointer + layout.PayloadStorageOffset;
        _payloadStride = layout.PayloadStride;
    }

    public Span<byte> GetSpan(int slotIndex, int offset, int length)
    {
        if (_disposed)
        {
            return Span<byte>.Empty;
        }

        return new Span<byte>(_payloadStorage + ((long)slotIndex * _payloadStride) + offset, length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
