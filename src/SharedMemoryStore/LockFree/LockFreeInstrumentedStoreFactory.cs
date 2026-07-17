using SharedMemoryStore.Interop;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Options;
using System.Diagnostics;

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
        StoreOpenStatus validation = SharedMemoryStoreOptionsValidator.Validate(options, out _);
        if (validation != StoreOpenStatus.Success)
        {
            return validation;
        }

        long waitStartTimestamp = Stopwatch.GetTimestamp();
        StoreOpenStatus mapped = SharedStorePlatform.TryBeginOpen(
            options,
            StoreWaitOptions.Default,
            waitStartTimestamp,
            out SharedStoreOpenScope? openScope);
        if (mapped != StoreOpenStatus.Success || openScope is null)
        {
            return mapped;
        }

        LockFreeStoreEngine<InstrumentedLockFreeCheckpoint>? engine = null;
        StoreOpenStatus status;
        try
        {
            using (openScope)
            {
                status = LockFreeStoreEngine<InstrumentedLockFreeCheckpoint>.TryCreateOrOpenUnderColdGate(
                    options,
                    StoreWaitOptions.Default,
                    waitStartTimestamp,
                    openScope.Region,
                    openScope.Synchronization,
                    openScope.Disposition,
                    checkpoint,
                    out engine);
                if (status == StoreOpenStatus.Success && engine is not null)
                {
                    openScope.TransferResourceOwnership();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            engine?.Dispose();
            return StoreOpenStatus.AccessDenied;
        }
        catch
        {
            engine?.Dispose();
            return StoreOpenStatus.MappingFailed;
        }

        if (status != StoreOpenStatus.Success || engine is null)
        {
            return status;
        }

        try
        {
            store = StoreEngineFactory.WrapOwnedEngine(engine);
            return StoreOpenStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return StoreOpenStatus.AccessDenied;
        }
        catch
        {
            return StoreOpenStatus.MappingFailed;
        }
    }
}
