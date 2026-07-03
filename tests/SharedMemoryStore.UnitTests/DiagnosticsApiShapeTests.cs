using System.Reflection;

namespace SharedMemoryStore.UnitTests;

public sealed class DiagnosticsApiShapeTests
{
    [Fact]
    public void DiagnosticsSnapshotUsesAggregateFailureCounts()
    {
        var publicProperties = typeof(DiagnosticsSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("FailedCommitCount", publicProperties);
        Assert.DoesNotContain(publicProperties, name => name.EndsWith("Failures", StringComparison.Ordinal));
        Assert.NotNull(typeof(DiagnosticsSnapshot).GetMethod(nameof(DiagnosticsSnapshot.GetFailureCount)));
    }
}
