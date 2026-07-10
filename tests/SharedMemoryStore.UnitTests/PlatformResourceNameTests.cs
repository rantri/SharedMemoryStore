using System.Runtime.Versioning;
using SharedMemoryStore.Interop;
using SharedMemoryStore.UnitTests.TestSupport;

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

    [Fact]
    public void WindowsGlobalMappingsUseGlobalSynchronizationScope()
    {
        var resourceName = PlatformResourceName.Create(@"Global\sms.compatibility");

        Assert.Equal(@"Global\sms.compatibility", resourceName.WindowsRegionName);
        Assert.StartsWith(@"Global\SharedMemoryStore-", resourceName.WindowsSynchronizationName, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxResourcesArePrivateToTheCreatingIdentity()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        var resource = PlatformResourceName.Create(options.Name);
        var privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var privateDirectoryMode = privateFileMode | UnixFileMode.UserExecute;

        Assert.Equal(privateDirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(resource.LinuxRegionPath)!));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(resource.LinuxRegionPath));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(resource.LinuxSynchronizationPath));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(resource.LinuxOwnersPath));
        Assert.Equal(privateFileMode, File.GetUnixFileMode(resource.LinuxLifecycleLockPath));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void LinuxResourceDirectoryRejectsSymbolicLinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sms-symlink-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Directory.CreateSymbolicLink(link, target);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => LinuxSharedMemoryDirectory.EnsureExists(link));
            Assert.NotEqual(LinuxSharedMemoryDirectory.PrivateDirectoryMode, File.GetUnixFileMode(target));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target);
            Directory.Delete(root);
        }
    }
}
