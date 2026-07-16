using SharedMemoryStore.Interop;

namespace SharedMemoryStore.LockFreeAgent;

internal static class LinuxFileLockCommands
{
    private const int InvalidArgumentsExitCode = 64;
    private const int UnsupportedPlatformExitCode = 65;

    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || !Path.IsPathFullyQualified(arguments[1]))
        {
            return InvalidArgumentsExitCode;
        }

        if (!OperatingSystem.IsLinux())
        {
            return UnsupportedPlatformExitCode;
        }

        StoreStatus status = LinuxFileLock.TryAcquire(
            arguments[1],
            StoreWaitOptions.NoWait,
            out LinuxFileLock? fileLock);
        using (fileLock)
        {
            Console.WriteLine("RESULT " + status);
        }

        return 0;
    }
}
