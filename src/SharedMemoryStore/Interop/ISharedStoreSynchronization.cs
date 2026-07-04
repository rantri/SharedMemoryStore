namespace SharedMemoryStore.Interop;

internal interface ISharedStoreSynchronization : IDisposable
{
    StoreStatus TryEnter(StoreWaitOptions waitOptions);

    void Exit();
}
