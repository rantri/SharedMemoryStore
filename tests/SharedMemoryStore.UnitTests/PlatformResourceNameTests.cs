using SharedMemoryStore.Interop;

namespace SharedMemoryStore.UnitTests;

public sealed class PlatformResourceNameTests
{
    [Fact]
    public void ResourceNamesAreDeterministicForSamePublicName()
    {
        var first = PlatformResourceName.Create("store/name");
        var second = PlatformResourceName.Create("store/name");

        Assert.Equal(first.ResourceFragment, second.ResourceFragment);
        Assert.Equal(first.LinuxRegionPath, second.LinuxRegionPath);
        Assert.Equal(first.LinuxSynchronizationPath, second.LinuxSynchronizationPath);
        Assert.Equal(first.WindowsRegionName, second.WindowsRegionName);
        Assert.Equal(first.WindowsSynchronizationName, second.WindowsSynchronizationName);
    }

    [Fact]
    public void SanitizedLinuxNamesKeepDifferentPublicNamesDistinct()
    {
        var slash = PlatformResourceName.Create("store/name");
        var question = PlatformResourceName.Create("store?name");

        Assert.NotEqual(slash.ResourceFragment, question.ResourceFragment);
        Assert.EndsWith(".region", slash.LinuxRegionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("..", Path.GetFileName(slash.LinuxRegionPath), StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRegionNamePreservesPublicNameForCompatibility()
    {
        var resourceName = PlatformResourceName.Create("sms.compatibility");

        Assert.Equal("sms.compatibility", resourceName.WindowsRegionName);
        Assert.StartsWith(@"Local\SharedMemoryStore-", resourceName.WindowsSynchronizationName, StringComparison.Ordinal);
    }
}
