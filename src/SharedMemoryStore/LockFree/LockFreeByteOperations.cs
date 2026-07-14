using SharedMemoryStore.Layout;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Budget-aware byte-linear primitives for the layout-v2 engine. Chunking is
/// local-only work control; it does not change the canonical hash or mapped
/// byte representation.
/// </summary>
internal static class LockFreeByteOperations
{
    private const int ChunkBytes = 64;

    internal static StoreStatus TryHash(
        ReadOnlySpan<byte> key,
        in LockFreeOperationBudget budget,
        out ulong hash)
    {
        hash = StoreKey.HashSeed;
        int chunkIndex = 0;
        for (var offset = 0; offset < key.Length; offset += ChunkBytes, chunkIndex++)
        {
            StoreStatus bound = budget.CheckPeriodic(chunkIndex);
            if (bound != StoreStatus.Success)
            {
                hash = 0;
                return bound;
            }

            int length = Math.Min(ChunkBytes, key.Length - offset);
            hash = StoreKey.ContinueHash(hash, key.Slice(offset, length));
        }

        return budget.CheckPeriodic(chunkIndex);
    }

    internal static StoreStatus TryCopy(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        in LockFreeOperationBudget budget)
    {
        return TryCopy(source, destination, budget, out _);
    }

    internal static StoreStatus TryCopy(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        in LockFreeOperationBudget budget,
        out int copiedBytes)
    {
        copiedBytes = 0;
        if (destination.Length < source.Length)
        {
            return StoreStatus.CorruptStore;
        }

        int chunkIndex = 0;
        for (var offset = 0; offset < source.Length; offset += ChunkBytes, chunkIndex++)
        {
            StoreStatus bound = budget.CheckPeriodic(chunkIndex);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            int length = Math.Min(ChunkBytes, source.Length - offset);
            source.Slice(offset, length).CopyTo(destination.Slice(offset, length));
            copiedBytes += length;
        }

        return budget.CheckPeriodic(chunkIndex);
    }
}
