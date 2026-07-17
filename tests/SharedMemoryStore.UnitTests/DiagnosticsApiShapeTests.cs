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
        Assert.DoesNotContain("Profile", publicProperties);
        Assert.DoesNotContain("TombstoneIndexEntryCount", publicProperties);
        Assert.DoesNotContain("TombstonePressureRatio", publicProperties);
        Assert.DoesNotContain("IndexCompactionCount", publicProperties);
        Assert.DoesNotContain(publicProperties, name => name.EndsWith("Failures", StringComparison.Ordinal));
        Assert.Equal(
            typeof(StoreProtocolInfo),
            typeof(DiagnosticsSnapshot).GetProperty(nameof(DiagnosticsSnapshot.ProtocolInfo))!.PropertyType);
        Assert.NotNull(typeof(DiagnosticsSnapshot).GetMethod(nameof(DiagnosticsSnapshot.GetFailureCount)));
    }
}
