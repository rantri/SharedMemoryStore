using SharedMemoryStore.Interop;
using System.Runtime.Versioning;

namespace SharedMemoryStore.IntegrationTests.TestSupport;

internal static class PlatformCapabilityProbe
{
    public static bool IsSupportedHost => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static bool IsDockerAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            return process is not null && process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static IDisposable HoldStoreSynchronization(string storeName)
    {
        var resourceName = PlatformResourceName.Create(storeName);
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSynchronizationHolder(resourceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSynchronizationHolder(resourceName);
        }

        throw new PlatformNotSupportedException("SharedMemoryStore integration synchronization is available on Linux and Windows.");
    }

    private sealed class WindowsSynchronizationHolder : IDisposable
    {
        private readonly Mutex _mutex;

        public WindowsSynchronizationHolder(PlatformResourceName resourceName)
        {
            _mutex = new Mutex(false, resourceName.WindowsSynchronizationName);
            _mutex.WaitOne();
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    [SupportedOSPlatform("linux")]
    private sealed class LinuxSynchronizationHolder : IDisposable
    {
        private readonly LinuxFileLock _lock;

        public LinuxSynchronizationHolder(PlatformResourceName resourceName)
        {
            var status = LinuxFileLock.TryAcquire(
                resourceName.LinuxSynchronizationPath,
                StoreWaitOptions.Infinite,
                out var fileLock);

            Assert.Equal(StoreStatus.Success, status);
            _lock = Assert.IsType<LinuxFileLock>(fileLock);
        }

        public void Dispose()
        {
            _lock.Dispose();
        }
    }
}
