using SharedMemoryStore.Engines.LegacyV12;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.Engines;

internal static class StoreEngineFactory
{
    internal static MemoryStore WrapLegacy(MemoryStore legacyCore) =>
        WrapOwnedEngine(new LegacyV12StoreEngine(legacyCore));

    /// <summary>
    /// Transfers one fully constructed engine into the public facade. If facade
    /// initialization throws (including an engine property getter), ownership
    /// remains here and the engine is disposed exactly once before rethrowing.
    /// </summary>
    internal static MemoryStore WrapOwnedEngine(IStoreEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        try
        {
            return new MemoryStore(engine);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    internal static StoreOpenStatus TryWrapLockFree(
        SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        MemoryMappedStoreRegion region,
        ISharedStoreSynchronization coldSynchronization,
        out MemoryStore? store)
    {
        store = null;
        StoreOpenStatus status = LockFreeStoreEngine.TryCreateOrOpen(
            options,
            waitOptions,
            region,
            coldSynchronization,
            out var engine);
        if (status == StoreOpenStatus.Success && engine is not null)
        {
            store = WrapOwnedEngine(engine);
        }

        return status;
    }
}
