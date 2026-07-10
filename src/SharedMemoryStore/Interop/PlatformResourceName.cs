using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

internal sealed class PlatformResourceName
{
    private const int MaxReadableFragmentLength = 80;

    private PlatformResourceName(
        string publicName,
        string resourceFragment,
        string windowsRegionName,
        string windowsSynchronizationName,
        string linuxRegionPath,
        string linuxSynchronizationPath,
        string linuxOwnersPath,
        string linuxLifecycleLockPath)
    {
        PublicName = publicName;
        ResourceFragment = resourceFragment;
        WindowsRegionName = windowsRegionName;
        WindowsSynchronizationName = windowsSynchronizationName;
        LinuxRegionPath = linuxRegionPath;
        LinuxSynchronizationPath = linuxSynchronizationPath;
        LinuxOwnersPath = linuxOwnersPath;
        LinuxLifecycleLockPath = linuxLifecycleLockPath;
    }

    public string PublicName { get; }

    public string ResourceFragment { get; }

    public string RegionResourceName => OperatingSystem.IsWindows() ? WindowsRegionName : LinuxRegionPath;

    public string SynchronizationResourceName => OperatingSystem.IsWindows() ? WindowsSynchronizationName : LinuxSynchronizationPath;

    public string WindowsRegionName { get; }

    public string WindowsSynchronizationName { get; }

    public string LinuxRegionPath { get; }

    public string LinuxSynchronizationPath { get; }

    public string LinuxOwnersPath { get; }

    public string LinuxLifecycleLockPath { get; }

    public static PlatformResourceName Create(string publicName)
    {
        var fragment = BuildResourceFragment(publicName);
        var directory = LinuxSharedMemoryDirectory.GetPath();
        return new PlatformResourceName(
            publicName,
            fragment,
            publicName,
            BuildWindowsSynchronizationName(publicName),
            Path.Combine(directory, fragment + ".region"),
            Path.Combine(directory, fragment + ".lock"),
            Path.Combine(directory, fragment + ".owners"),
            Path.Combine(directory, fragment + ".lifecycle"));
    }

    public static string BuildWindowsSynchronizationName(string publicName)
    {
        var resourceScope = publicName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
            ? @"Global\"
            : @"Local\";
        return resourceScope + "SharedMemoryStore-" + string.Create(publicName.Length, publicName, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var value = source[i];
                destination[i] = char.IsLetterOrDigit(value) || value is '-' or '_' ? value : '_';
            }
        });
    }

    private static string BuildResourceFragment(string publicName)
    {
        var sanitized = new StringBuilder(publicName.Length);
        foreach (var value in publicName)
        {
            sanitized.Append(char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.'
                ? value
                : '_');
        }

        var readable = sanitized.ToString().Trim('_', '.');
        if (readable.Length == 0)
        {
            readable = "store";
        }

        if (readable.Length > MaxReadableFragmentLength)
        {
            readable = readable[..MaxReadableFragmentLength];
        }

        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(publicName), hashBytes);
        var hash = Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
        return "sms-" + readable + "-" + hash;
    }
}

internal static class LinuxSharedMemoryDirectory
{
    public const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string GetPath()
    {
        var root = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();

        return Path.Combine(root, "SharedMemoryStore");
    }

    [SupportedOSPlatform("linux")]
    public static void EnsureExists(string path)
    {
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null
            || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The SharedMemoryStore Linux resource directory must not be a symbolic link.");
        }

        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }
}
