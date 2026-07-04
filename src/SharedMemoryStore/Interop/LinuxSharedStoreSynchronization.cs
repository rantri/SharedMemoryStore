using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("linux")]
internal sealed class LinuxSharedStoreSynchronization : ISharedStoreSynchronization
{
    private readonly LinuxFileLock _fileLock;
    private bool _disposed;

    public LinuxSharedStoreSynchronization(PlatformResourceName resourceName)
    {
        var status = LinuxFileLock.TryOpen(resourceName.LinuxSynchronizationPath, out var fileLock);
        _fileLock = status == StoreStatus.Success && fileLock is not null
            ? fileLock
            : throw CreateOpenException(status);
    }

    public StoreStatus TryEnter(StoreWaitOptions waitOptions)
    {
        if (_disposed)
        {
            return StoreStatus.StoreDisposed;
        }

        return _fileLock.TryAcquire(waitOptions);
    }

    public void Exit()
    {
        _fileLock.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fileLock.Dispose();
    }

    private static Exception CreateOpenException(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.AccessDenied => new UnauthorizedAccessException("SharedMemoryStore Linux synchronization resource access was denied."),
            StoreStatus.UnsupportedPlatform => new PlatformNotSupportedException("SharedMemoryStore Linux synchronization is not available."),
            _ => new IOException("SharedMemoryStore Linux synchronization resource could not be opened.")
        };
    }
}
