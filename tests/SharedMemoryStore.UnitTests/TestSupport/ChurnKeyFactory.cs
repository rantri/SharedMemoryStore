namespace SharedMemoryStore.UnitTests.TestSupport;

internal static class ChurnKeyFactory
{
    public static byte[] Key(int value)
    {
        return BitConverter.GetBytes(value);
    }

    public static IEnumerable<byte[]> Keys(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return Key(i);
        }
    }
}
