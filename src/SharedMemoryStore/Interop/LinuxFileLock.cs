using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("linux")]
internal sealed class LinuxFileLock : IDisposable
{
    private static readonly ConcurrentDictionary<string, LocalLockEntry> LocalLocks = new(StringComparer.Ordinal);

    private readonly FileStream _stream;
    private readonly string _localLockPath;
    private readonly LocalLockEntry _localLockEntry;
    private bool _locked;
    private bool _localLockHeld;
    private bool _disposed;

    private LinuxFileLock(string path, FileStream stream)
    {
        _stream = stream;
        _localLockPath = Path.GetFullPath(path);
        _localLockEntry = AcquireLocalLockEntry(_localLockPath);
    }

    public static StoreStatus TryAcquire(
        string path,
        StoreWaitOptions waitOptions,
        out LinuxFileLock? fileLock)
    {
        fileLock = null;
        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreStatus.OperationCanceled;
        }

        FileStream stream;
        try
        {
            stream = OpenLockFile(path);
        }
        catch (UnauthorizedAccessException)
        {
            return StoreStatus.AccessDenied;
        }
        catch (PlatformNotSupportedException)
        {
            return StoreStatus.UnsupportedPlatform;
        }
        catch
        {
            return StoreStatus.UnknownFailure;
        }

        var candidate = new LinuxFileLock(path, stream);
        var status = candidate.TryAcquire(waitOptions);
        if (status != StoreStatus.Success)
        {
            candidate.Dispose();
            return status;
        }

        fileLock = candidate;
        return StoreStatus.Success;
    }

    public StoreStatus TryAcquire(StoreWaitOptions waitOptions)
    {
        if (_disposed)
        {
            return StoreStatus.StoreDisposed;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        if (!TryAcquireLocal(waitOptions, startTimestamp))
        {
            return waitOptions.CancellationToken.IsCancellationRequested
                ? StoreStatus.OperationCanceled
                : StoreStatus.StoreBusy;
        }

        while (true)
        {
            if (waitOptions.CancellationToken.IsCancellationRequested)
            {
                Release();
                return StoreStatus.OperationCanceled;
            }

            try
            {
                _stream.Lock(0, 1);
                _locked = true;
                return StoreStatus.Success;
            }
            catch (IOException)
            {
                if (!waitOptions.IsInfinite && Stopwatch.GetElapsedTime(startTimestamp) >= waitOptions.Timeout)
                {
                    Release();
                    return StoreStatus.StoreBusy;
                }

                if (WaitBeforeRetry(waitOptions, startTimestamp))
                {
                    continue;
                }

                var status = waitOptions.CancellationToken.IsCancellationRequested
                    ? StoreStatus.OperationCanceled
                    : StoreStatus.StoreBusy;
                Release();
                return status;
            }
            catch (UnauthorizedAccessException)
            {
                Release();
                return StoreStatus.AccessDenied;
            }
            catch (PlatformNotSupportedException)
            {
                Release();
                return StoreStatus.UnsupportedPlatform;
            }
            catch (ObjectDisposedException)
            {
                Release();
                return StoreStatus.StoreDisposed;
            }
            catch
            {
                Release();
                return StoreStatus.UnknownFailure;
            }
        }
    }

    public static StoreStatus TryOpen(string path, out LinuxFileLock? fileLock)
    {
        fileLock = null;
        try
        {
            var stream = OpenLockFile(path);

            fileLock = new LinuxFileLock(path, stream);
            return StoreStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return StoreStatus.AccessDenied;
        }
        catch (PlatformNotSupportedException)
        {
            return StoreStatus.UnsupportedPlatform;
        }
        catch
        {
            return StoreStatus.UnknownFailure;
        }
    }

    public void Release()
    {
        if (_locked)
        {
            try
            {
                _stream.Unlock(0, 1);
            }
            catch
            {
                // The stream is being torn down; callers receive operation status before this point.
            }

            _locked = false;
        }

        if (_localLockHeld)
        {
            Monitor.Exit(_localLockEntry.SyncRoot);
            _localLockHeld = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        _stream.Dispose();
        ReleaseLocalLockEntry(_localLockPath, _localLockEntry);
    }

    private bool TryAcquireLocal(StoreWaitOptions waitOptions, long startTimestamp)
    {
        while (true)
        {
            if (Monitor.TryEnter(_localLockEntry.SyncRoot))
            {
                _localLockHeld = true;
                return true;
            }

            if (!waitOptions.IsInfinite && Stopwatch.GetElapsedTime(startTimestamp) >= waitOptions.Timeout)
            {
                return false;
            }

            if (!WaitBeforeRetry(waitOptions, startTimestamp))
            {
                return false;
            }
        }
    }

    private static bool WaitBeforeRetry(StoreWaitOptions waitOptions, long startTimestamp)
    {
        var sleep = TimeSpan.FromMilliseconds(10);
        if (!waitOptions.IsInfinite)
        {
            var remaining = waitOptions.Timeout - Stopwatch.GetElapsedTime(startTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            sleep = remaining < sleep ? remaining : sleep;
        }

        return waitOptions.CancellationToken.WaitHandle.WaitOne(sleep) == false;
    }

    private static FileStream OpenLockFile(string path)
    {
        LinuxSharedMemoryDirectory.EnsureExists(Path.GetDirectoryName(path) ?? ".");
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete,
            UnixCreateMode = LinuxSharedMemoryDirectory.PrivateFileMode
        });
        try
        {
            File.SetUnixFileMode(path, LinuxSharedMemoryDirectory.PrivateFileMode);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static LocalLockEntry AcquireLocalLockEntry(string path)
    {
        while (true)
        {
            var entry = LocalLocks.GetOrAdd(path, static _ => new LocalLockEntry());
            lock (entry.ReferenceGate)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount++;
                return entry;
            }
        }
    }

    private static void ReleaseLocalLockEntry(string path, LocalLockEntry entry)
    {
        var remove = false;
        lock (entry.ReferenceGate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.Retired = true;
                remove = true;
            }
        }

        if (remove)
        {
            _ = ((ICollection<KeyValuePair<string, LocalLockEntry>>)LocalLocks).Remove(
                new KeyValuePair<string, LocalLockEntry>(path, entry));
        }
    }

    private sealed class LocalLockEntry
    {
        public object SyncRoot { get; } = new();

        public object ReferenceGate { get; } = new();

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }
}
