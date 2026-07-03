using System.Buffers.Binary;
using System.Text;

internal static class StoreByteEncoding
{
    public const int Int32ByteCount = 4;
    public const int BasicDescriptorByteCount = 4;

    public static void WriteInt32LittleEndian(int value, Span<byte> destination)
    {
        if (destination.Length < Int32ByteCount)
        {
            throw new ArgumentException("Destination must have room for four bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
    }

    public static void WriteBasicDescriptor(short schemaVersion, short flags, Span<byte> destination)
    {
        if (destination.Length < BasicDescriptorByteCount)
        {
            throw new ArgumentException("Destination must have room for four bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteInt16LittleEndian(destination[0..2], schemaVersion);
        BinaryPrimitives.WriteInt16LittleEndian(destination[2..4], flags);
    }

    public static int GetUtf8ByteCount(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetByteCount(value);
    }

    public static bool TryWriteUtf8(string value, Span<byte> destination, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(value);

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        return true;
    }
}
