namespace SharedMemoryStore.Layout;

internal static unsafe class StoreKey
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static StoreStatus Validate(ReadOnlySpan<byte> key, int maxKeyBytes)
    {
        return key.Length is <= 0 || key.Length > maxKeyBytes
            ? StoreStatus.KeyTooLarge
            : StoreStatus.Success;
    }

    public static ulong Hash(ReadOnlySpan<byte> key)
    {
        var hash = OffsetBasis;
        foreach (var value in key)
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash;
    }

    public static bool Equals(byte* storedKey, int storedLength, ReadOnlySpan<byte> key)
    {
        return storedLength == key.Length
            && key.SequenceEqual(new ReadOnlySpan<byte>(storedKey, storedLength));
    }
}
