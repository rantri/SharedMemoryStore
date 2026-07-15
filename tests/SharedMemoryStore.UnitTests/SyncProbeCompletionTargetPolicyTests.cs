using SharedMemoryStore;

namespace SharedMemoryStore.UnitTests;

public sealed class SyncProbeCompletionTargetPolicyTests
{
    private const long MixedTarget = 100_000_000;
    private const long FrameTarget = 100_000;

    [Theory]
    [InlineData(StoreProfile.Legacy, (int)ProbeScenarioKind.MixedChurn, 0, 0)]
    [InlineData(StoreProfile.Legacy, (int)ProbeScenarioKind.LargeIngest, 0, 0)]
    [InlineData(StoreProfile.LockFree, (int)ProbeScenarioKind.MixedChurn, MixedTarget, 0)]
    [InlineData(StoreProfile.LockFree, (int)ProbeScenarioKind.LargeIngest, 0, FrameTarget)]
    [InlineData(StoreProfile.LockFree, (int)ProbeScenarioKind.Autonomous, 0, 0)]
    [InlineData(StoreProfile.LockFree, (int)ProbeScenarioKind.BrokerDirected, 0, 0)]
    public void LockFreeOnlyPolicyResolvesExactlyOneApplicableTarget(
        StoreProfile profile,
        int scenario,
        long expectedOperations,
        long expectedFrames)
    {
        ProbeRunTargets targets = ProbeCompletionTargetPolicy.Resolve(
            profile,
            (ProbeScenarioKind)scenario,
            [StoreProfile.LockFree],
            MixedTarget,
            FrameTarget);

        Assert.Equal(expectedOperations, targets.OperationTarget);
        Assert.Equal(expectedFrames, targets.FrameTarget);
        Assert.False(targets.OperationTarget > 0 && targets.FrameTarget > 0);
        Assert.Equal(expectedOperations > 0 || expectedFrames > 0, targets.HasCountTarget);
    }

    [Theory]
    [InlineData(StoreProfile.Legacy)]
    [InlineData(StoreProfile.LockFree)]
    public void DefaultBothPolicyPreservesPreviousCountBoundBehavior(StoreProfile profile)
    {
        StoreProfile[] both = [StoreProfile.Legacy, StoreProfile.LockFree];

        Assert.Equal(
            new ProbeRunTargets(MixedTarget, 0),
            ProbeCompletionTargetPolicy.Resolve(
                profile,
                ProbeScenarioKind.MixedChurn,
                both,
                MixedTarget,
                FrameTarget));
        Assert.Equal(
            new ProbeRunTargets(0, FrameTarget),
            ProbeCompletionTargetPolicy.Resolve(
                profile,
                ProbeScenarioKind.LargeIngest,
                both,
                MixedTarget,
                FrameTarget));
    }

    [Fact]
    public void NegativeTargetsAreRejectedBeforeProfileResolution()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeCompletionTargetPolicy.Resolve(
            StoreProfile.Legacy,
            ProbeScenarioKind.Autonomous,
            [],
            -1,
            FrameTarget));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeCompletionTargetPolicy.Resolve(
            StoreProfile.Legacy,
            ProbeScenarioKind.Autonomous,
            [],
            MixedTarget,
            -1));
    }
}
