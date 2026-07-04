namespace SharedMemoryStore.Interop;

internal unsafe interface ISharedStoreRegion : IDisposable
{
    long Capacity { get; }

    byte* Pointer { get; }
}
