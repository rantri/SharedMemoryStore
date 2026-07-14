namespace SharedMemoryStore.LockFree;

internal static class AtomicControlWord
{
    public static ulong EncodeParticipant(int state, int incarnation, int pid)
    {
        if (state is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (incarnation is < 0 or > 0x0fff_ffff)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(pid);
        return (uint)state | ((ulong)(uint)incarnation << 3) | ((ulong)(uint)pid << 31);
    }

    public static ulong EncodeSlot(int state, long generation, int participantToken) =>
        EncodeOwnedLifecycle(state, generation, participantToken);

    public static ulong EncodeLease(int state, long generation, int participantToken) =>
        EncodeOwnedLifecycle(state, generation, participantToken);

    public static long LoadAcquire(ref long location) => Volatile.Read(ref location);

    public static void StoreRelease(ref long location, long value) => Volatile.Write(ref location, value);

    public static long CompareExchange(ref long location, long value, long comparand) =>
        Interlocked.CompareExchange(ref location, value, comparand);

    public static long Exchange(ref long location, long value) => Interlocked.Exchange(ref location, value);

    private static ulong EncodeOwnedLifecycle(int state, long generation, int participantToken)
    {
        if (state is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (generation is < 1 or > 0x1_ffff_ffffL)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (participantToken is < 0 or > 0x0fff_ffff)
        {
            throw new ArgumentOutOfRangeException(nameof(participantToken));
        }

        return (uint)state | ((ulong)generation << 3) | ((ulong)(uint)participantToken << 36);
    }
}
