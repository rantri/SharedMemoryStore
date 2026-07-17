namespace SharedMemoryStore.LockFree;

internal static unsafe class StoreKey
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static StoreStatus Validate(ReadOnlySpan<byte> key, int maxKeyBytes)
    {
        if (key.Length <= 0)
        {
            return StoreStatus.InvalidKey;
        }

        return key.Length > maxKeyBytes
            ? StoreStatus.KeyTooLarge
            : StoreStatus.Success;
    }

    public static ulong Hash(ReadOnlySpan<byte> key) => ContinueHash(OffsetBasis, key);

    /// <summary>
    /// Continues the canonical FNV-1a key hash across one caller-selected
    /// chunk while preserving the persisted hash identity.
    /// </summary>
    internal static ulong ContinueHash(ulong hash, ReadOnlySpan<byte> key)
    {
        foreach (byte value in key)
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash;
    }

    internal static ulong HashSeed => OffsetBasis;

    public static bool Equals(byte* storedKey, int storedLength, ReadOnlySpan<byte> key)
    {
        return storedLength == key.Length
            && key.SequenceEqual(new ReadOnlySpan<byte>(storedKey, storedLength));
    }
}
