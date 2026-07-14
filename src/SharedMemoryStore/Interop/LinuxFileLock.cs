using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("linux")]
internal sealed class LinuxFileLock : IDisposable
{
    private static readonly ConcurrentDictionary<string, LocalLockEntry> LocalLocks = new(StringComparer.Ordinal);

    private readonly string _localLockPath;
    private readonly LocalLockEntry _localLockEntry;
    private bool _locked;
    private bool _localLockHeld;
    private bool _disposed;

    private LinuxFileLock(string path)
    {
        _localLockPath = Path.GetFullPath(path);
        _localLockEntry = AcquireLocalLockEntry(_localLockPath);
        try
        {
            // POSIX process-associated record locks are dropped when any file
            // descriptor for the same inode is closed. Every managed lock
            // object for one path must therefore share this exact stream for
            // the complete local reference lifetime.
            _ = _localLockEntry.Stream;
        }
        catch
        {
            ReleaseLocalLockEntry(_localLockPath, _localLockEntry);
            throw;
        }
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

        LinuxFileLock candidate;
        try
        {
            candidate = new LinuxFileLock(path);
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
                _localLockEntry.Stream.Lock(0, 1);
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
            fileLock = new LinuxFileLock(path);
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
                _localLockEntry.Stream.Unlock(0, 1);
            }
            catch
            {
                // The stream is being torn down; callers receive operation status before this point.
            }

            _locked = false;
        }

        if (_localLockHeld)
        {
            _localLockEntry.LocalGate.Release();
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
        ReleaseLocalLockEntry(_localLockPath, _localLockEntry);
    }

    private bool TryAcquireLocal(StoreWaitOptions waitOptions, long startTimestamp)
    {
        while (true)
        {
            // FileStream.Lock uses a process-associated POSIX record lock on
            // Linux, so another handle in this process would not contend at
            // the kernel boundary. A deliberately non-reentrant local gate
            // preserves binary ownership even for two handles on one thread.
            if (_localLockEntry.LocalGate.Wait(0))
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
            // The value factory creates only an unevaluated Lazy<FileStream>,
            // so discarded GetOrAdd candidates never open or close a sibling
            // descriptor for this inode.
            var entry = LocalLocks.GetOrAdd(path, static key => new LocalLockEntry(key));
            lock (entry.ReferenceGate)
            {
                if (entry.Retired)
                {
                    if (entry.RetirementFailure is not null)
                    {
                        throw new IOException(
                            "The prior Linux file-lock descriptor could not be closed safely; "
                            + "opening a sibling descriptor is refused.",
                            entry.RetirementFailure);
                    }
                }
                else
                {
                    entry.ReferenceCount++;
                    return entry;
                }
            }

            // This contender observed the old generation before its last
            // releaser removed it. Yield without holding any gate and retry;
            // unrelated paths remain entirely independent.
            Thread.Yield();
        }
    }

    private static void ReleaseLocalLockEntry(string path, LocalLockEntry entry)
    {
        lock (entry.ReferenceGate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.Retired = true;
                try
                {
                    // Close before removing the retired registry entry. A new
                    // generation for this path cannot open its descriptor until
                    // this process no longer has an older sibling descriptor
                    // whose close could release the new generation's lock.
                    entry.DisposeStream();
                }
                catch (Exception exception)
                {
                    // Retain a fail-closed tombstone. Removing this entry after
                    // an uncertain close could allow a new descriptor to open
                    // and then have its process-associated lock invalidated by
                    // the old descriptor closing later.
                    entry.RetirementFailure = exception;
                }

                if (entry.RetirementFailure is null)
                {
                    // Remove before releasing ReferenceGate. A contender that
                    // already observed this generation will see Retired after
                    // the close; later contenders can create a new generation
                    // without a preemption-sensitive spin window.
                    bool removed = ((ICollection<KeyValuePair<string, LocalLockEntry>>)LocalLocks).Remove(
                        new KeyValuePair<string, LocalLockEntry>(path, entry));
                    if (!removed
                        && LocalLocks.TryGetValue(path, out LocalLockEntry? current)
                        && ReferenceEquals(current, entry))
                    {
                        entry.RetirementFailure = new IOException(
                            "The retired Linux file-lock registry entry could not be removed safely.");
                    }
                }
            }
        }
    }

    private sealed class LocalLockEntry
    {
        private readonly Lazy<FileStream> _stream;

        internal LocalLockEntry(string path)
        {
            _stream = new Lazy<FileStream>(
                () => OpenLockFile(path),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public FileStream Stream => _stream.Value;

        public SemaphoreSlim LocalGate { get; } = new(1, 1);

        public object ReferenceGate { get; } = new();

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }

        public Exception? RetirementFailure { get; set; }

        public void DisposeStream()
        {
            if (_stream.IsValueCreated)
            {
                _stream.Value.Dispose();
            }
        }
    }
}
