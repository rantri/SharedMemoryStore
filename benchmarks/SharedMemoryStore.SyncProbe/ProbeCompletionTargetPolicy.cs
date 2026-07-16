using SharedMemoryStore;

internal readonly record struct ProbeRunTargets(long OperationTarget, long FrameTarget)
{
    internal bool HasCountTarget => OperationTarget > 0 || FrameTarget > 0;
}

internal static class ProbeCompletionTargetPolicy
{
    internal static ProbeRunTargets Resolve(
        StoreProfile profile,
        ProbeScenarioKind scenarioKind,
        IReadOnlyCollection<StoreProfile> countBoundProfiles,
        long mixedOperationTarget,
        long largeFrameTarget)
    {
        ArgumentNullException.ThrowIfNull(countBoundProfiles);
        ArgumentOutOfRangeException.ThrowIfNegative(mixedOperationTarget);
        ArgumentOutOfRangeException.ThrowIfNegative(largeFrameTarget);

        if (!countBoundProfiles.Contains(profile))
        {
            return default;
        }

        return scenarioKind switch
        {
            ProbeScenarioKind.MixedChurn => new ProbeRunTargets(mixedOperationTarget, 0),
            ProbeScenarioKind.LargeIngest => new ProbeRunTargets(0, largeFrameTarget),
            _ => default
        };
    }
}
