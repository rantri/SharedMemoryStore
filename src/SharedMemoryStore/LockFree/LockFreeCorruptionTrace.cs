using System.Runtime.CompilerServices;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Retains the first structural-failure origin on the current thread. The
/// success path never touches this state; stress harnesses can therefore
/// distinguish rare protocol failures without adding shared-memory traffic.
/// </summary>
internal static class LockFreeCorruptionTrace
{
    [ThreadStatic]
    private static CorruptionOrigin? _first;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static StoreStatus Corrupt(
        string component,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
    {
        _first ??= new CorruptionOrigin(component, member, line);
        return StoreStatus.CorruptStore;
    }

    internal static string? Consume()
    {
        CorruptionOrigin? origin = _first;
        _first = null;
        return origin is { } value
            ? $"{value.Component}.{value.Member}:{value.Line}"
            : null;
    }

    private readonly record struct CorruptionOrigin(string Component, string Member, int Line);
}
