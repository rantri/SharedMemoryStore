using SharedMemoryStore.Interop;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Options;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Friend-assembly-only construction seam for deterministic protocol pauses.
/// Ordinary public construction always uses the statically empty checkpoint
/// specialization.
/// </summary>
internal static class LockFreeInstrumentedStoreFactory
{
    internal static StoreOpenStatus TryCreateOrOpen(
        SharedMemoryStoreOptions options,
        InstrumentedLockFreeCheckpoint checkpoint,
        out MemoryStore? store)
    {
        store = null;
        if (options.Profile != StoreProfile.LockFree)
        {
            return StoreOpenStatus.InvalidOptions;
        }

        StoreOpenStatus validation = SharedMemoryStoreOptionsValidator.Validate(options, out _);
        if (validation != StoreOpenStatus.Success)
        {
            return validation;
        }

        StoreOpenStatus mapped = SharedStorePlatform.TryOpen(
            options,
            StoreWaitOptions.Default,
            out var region,
            out var synchronization);
        if (mapped != StoreOpenStatus.Success || region is null || synchronization is null)
        {
            return mapped;
        }

        StoreOpenStatus status = LockFreeStoreEngine<InstrumentedLockFreeCheckpoint>.TryCreateOrOpen(
            options,
            StoreWaitOptions.Default,
            region,
            synchronization,
            checkpoint,
            out var engine);
        if (status == StoreOpenStatus.Success && engine is not null)
        {
            store = StoreEngineFactory.WrapOwnedEngine(engine);
            return StoreOpenStatus.Success;
        }

        region.Dispose();
        synchronization.Dispose();
        return status;
    }
}
