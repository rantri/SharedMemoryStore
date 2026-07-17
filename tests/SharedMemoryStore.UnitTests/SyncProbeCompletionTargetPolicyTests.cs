namespace SharedMemoryStore.UnitTests;

public sealed class SyncProbeCompletionTargetPolicyTests
{
    private const long MixedTarget = 100_000_000;
    private const long FrameTarget = 100_000;

    [Theory]
    [InlineData((int)ProbeScenarioKind.MixedChurn, MixedTarget, 0)]
    [InlineData((int)ProbeScenarioKind.LargeIngest, 0, FrameTarget)]
    [InlineData((int)ProbeScenarioKind.Autonomous, 0, 0)]
    [InlineData((int)ProbeScenarioKind.BrokerDirected, 0, 0)]
    public void PolicyResolvesExactlyOneApplicableTarget(
        int scenario,
        long expectedOperations,
        long expectedFrames)
    {
        ProbeRunTargets targets = ProbeCompletionTargetPolicy.Resolve(
            (ProbeScenarioKind)scenario,
            MixedTarget,
            FrameTarget);

        Assert.Equal(expectedOperations, targets.OperationTarget);
        Assert.Equal(expectedFrames, targets.FrameTarget);
        Assert.False(targets.OperationTarget > 0 && targets.FrameTarget > 0);
        Assert.Equal(expectedOperations > 0 || expectedFrames > 0, targets.HasCountTarget);
    }

    [Fact]
    public void NegativeTargetsAreRejectedBeforeScenarioResolution()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeCompletionTargetPolicy.Resolve(
            ProbeScenarioKind.Autonomous,
            -1,
            FrameTarget));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeCompletionTargetPolicy.Resolve(
            ProbeScenarioKind.Autonomous,
            MixedTarget,
            -1));
    }
}
