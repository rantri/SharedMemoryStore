namespace FrameValue;

internal readonly record struct FrameDescriptor(int Width, int Height, int PixelBytes, long TimestampTicks)
{
    // Sample-owned descriptor layout: width, height, payload byte count, timestamp ticks.
    public byte[] ToBytes()
    {
        var bytes = new byte[20];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), Width);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), Height);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), PixelBytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(12, 8), TimestampTicks);
        return bytes;
    }

    public static FrameDescriptor FromBytes(ReadOnlySpan<byte> bytes)
    {
        return new FrameDescriptor(
            BitConverter.ToInt32(bytes[..4]),
            BitConverter.ToInt32(bytes.Slice(4, 4)),
            BitConverter.ToInt32(bytes.Slice(8, 4)),
            BitConverter.ToInt64(bytes.Slice(12, 8)));
    }
}
