using System.Threading;

namespace SharedMemoryStore.Interop;

internal sealed class WindowsSharedStoreSynchronization : ISharedStoreSynchronization
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public WindowsSharedStoreSynchronization(PlatformResourceName resourceName)
    {
        _mutex = new Mutex(false, resourceName.WindowsSynchronizationName);
    }

    public StoreStatus TryEnter(StoreWaitOptions waitOptions)
    {
        if (_disposed)
        {
            return StoreStatus.StoreDisposed;
        }

        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        bool acquired;
        try
        {
            acquired = WaitForMutex(waitOptions);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        catch (ObjectDisposedException)
        {
            return StoreStatus.StoreDisposed;
        }
        catch (UnauthorizedAccessException)
        {
            return StoreStatus.AccessDenied;
        }

        if (acquired)
        {
            return StoreStatus.Success;
        }

        return waitOptions.CancellationToken.IsCancellationRequested
            ? StoreStatus.OperationCanceled
            : StoreStatus.StoreBusy;
    }

    public void Exit()
    {
        _mutex.ReleaseMutex();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Dispose();
    }

    private bool WaitForMutex(StoreWaitOptions waitOptions)
    {
        if (!waitOptions.CancellationToken.CanBeCanceled)
        {
            return waitOptions.IsInfinite
                ? _mutex.WaitOne(Timeout.InfiniteTimeSpan)
                : _mutex.WaitOne(waitOptions.Timeout);
        }

        var waitHandles = new WaitHandle[] { _mutex, waitOptions.CancellationToken.WaitHandle };
        var signaled = waitOptions.IsInfinite
            ? WaitHandle.WaitAny(waitHandles)
            : WaitHandle.WaitAny(waitHandles, waitOptions.Timeout);

        return signaled switch
        {
            0 => true,
            1 => false,
            WaitHandle.WaitTimeout => false,
            _ => false
        };
    }
}
