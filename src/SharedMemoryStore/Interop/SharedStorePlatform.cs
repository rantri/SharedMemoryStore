namespace SharedMemoryStore.Interop;

internal static class SharedStorePlatform
{
    public static StoreOpenStatus TryBeginOpen(
        SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        long waitStartTimestamp,
        out SharedStoreOpenScope? scope)
    {
        scope = null;
        if (!Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        PlatformResourceName resourceName = PlatformResourceName.Create(options.Name);
        if (OperatingSystem.IsLinux())
        {
            return LinuxSharedMemoryRegion.TryBeginColdOpen(
                resourceName,
                options,
                waitOptions,
                waitStartTimestamp,
                out scope);
        }

        if (!OperatingSystem.IsWindows())
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        WindowsSharedStoreSynchronization? synchronization = null;
        MemoryMappedStoreRegion? region = null;
        bool synchronizationEntered = false;
        try
        {
            synchronization = new WindowsSharedStoreSynchronization(resourceName);
            StoreStatus remainingStatus = TryGetRemainingWaitOptions(
                waitOptions,
                waitStartTimestamp,
                out StoreWaitOptions remainingWait);
            if (remainingStatus != StoreStatus.Success)
            {
                return ToOpenStatus(remainingStatus);
            }

            StoreStatus enterStatus = synchronization.TryEnter(remainingWait);
            if (enterStatus != StoreStatus.Success)
            {
                return ToOpenStatus(enterStatus);
            }

            synchronizationEntered = true;
            remainingStatus = TryGetRemainingWaitOptions(
                waitOptions,
                waitStartTimestamp,
                out _);
            if (remainingStatus != StoreStatus.Success)
            {
                return ToOpenStatus(remainingStatus);
            }

            StoreOpenStatus openStatus = WindowsSharedMemoryRegion.TryOpen(
                resourceName,
                options,
                out region,
                out RegionOpenDisposition disposition);
            if (openStatus != StoreOpenStatus.Success || region is null)
            {
                return openStatus;
            }

            scope = new SharedStoreOpenScope(
                region,
                synchronization,
                outerLifecycleGate: null,
                disposition);
            region = null;
            synchronization = null;
            synchronizationEntered = false;
            return StoreOpenStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return StoreOpenStatus.AccessDenied;
        }
        catch (PlatformNotSupportedException)
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }
        catch
        {
            return StoreOpenStatus.MappingFailed;
        }
        finally
        {
            try
            {
                if (synchronizationEntered)
                {
                    synchronization?.Exit();
                }
            }
            finally
            {
                try
                {
                    synchronization?.Dispose();
                }
                finally
                {
                    region?.Dispose();
                }
            }
        }
    }

    public static StoreOpenStatus TryOpenRegion(
        SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        out MemoryMappedStoreRegion? region)
    {
        region = null;
        if (!Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        var resourceName = PlatformResourceName.Create(options.Name);
        if (OperatingSystem.IsWindows())
        {
            return WindowsSharedMemoryRegion.TryOpen(resourceName, options, out region);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxSharedMemoryRegion.TryOpen(resourceName, options, waitOptions, out region);
        }

        return StoreOpenStatus.UnsupportedPlatform;
    }

    public static ISharedStoreSynchronization CreateSynchronization(PlatformResourceName resourceName)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSharedStoreSynchronization(resourceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSharedStoreSynchronization(resourceName);
        }

        throw new PlatformNotSupportedException("SharedMemoryStore supports Linux and Windows shared synchronization.");
    }

    internal static StoreStatus TryGetRemainingWaitOptions(
        StoreWaitOptions waitOptions,
        long waitStartTimestamp,
        out StoreWaitOptions remainingWait)
    {
        remainingWait = default;
        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        if (waitOptions.IsInfinite || waitOptions.Timeout == TimeSpan.Zero)
        {
            remainingWait = waitOptions;
            return StoreStatus.Success;
        }

        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(waitStartTimestamp);
        if (elapsed >= waitOptions.Timeout)
        {
            return StoreStatus.StoreBusy;
        }

        remainingWait = new StoreWaitOptions(
            waitOptions.Timeout - elapsed,
            waitOptions.CancellationToken);
        return StoreStatus.Success;
    }

    private static StoreOpenStatus ToOpenStatus(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.Success => StoreOpenStatus.Success,
            StoreStatus.StoreBusy => StoreOpenStatus.StoreBusy,
            StoreStatus.OperationCanceled => StoreOpenStatus.OperationCanceled,
            StoreStatus.AccessDenied => StoreOpenStatus.AccessDenied,
            StoreStatus.UnsupportedPlatform => StoreOpenStatus.UnsupportedPlatform,
            _ => StoreOpenStatus.MappingFailed
        };
    }
}
