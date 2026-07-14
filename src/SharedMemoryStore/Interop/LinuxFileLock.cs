using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SharedMemoryStore.Interop;

[SupportedOSPlatform("linux")]
internal sealed class LinuxFileLock : IDisposable
{
    private const int OpenFileDescriptionSetLock = 37;
    private const short WriteLock = 1;
    private const short Unlock = 2;
    private const short SeekSet = 0;

    private const int Interrupted = 4;
    private const int TryAgain = 11;
    private const int PermissionDenied = 13;
    private const int InvalidArgument = 22;
    private const int FunctionNotImplemented = 38;
    private const int OperationNotSupported = 95;

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _localGate = new(1, 1);
    private bool _locked;
    private bool _localLockHeld;
    private bool _streamClosed;
    private bool _unusable;
    private bool _disposed;

    private LinuxFileLock(string path)
    {
        _stream = OpenLockFile(Path.GetFullPath(path));
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

        StoreStatus status = candidate.TryAcquire(waitOptions);
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
        if (_disposed || _unusable)
        {
            return StoreStatus.StoreDisposed;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
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

            var request = LinuxFlock.Create(WriteLock);
            if (Fcntl(_stream.SafeFileHandle, OpenFileDescriptionSetLock, ref request) == 0)
            {
                _locked = true;
                return StoreStatus.Success;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == Interrupted)
            {
                if (!waitOptions.IsInfinite
                    && Stopwatch.GetElapsedTime(startTimestamp) >= waitOptions.Timeout)
                {
                    Release();
                    return StoreStatus.StoreBusy;
                }

                continue;
            }

            if (error is InvalidArgument or FunctionNotImplemented or OperationNotSupported)
            {
                Release();
                return StoreStatus.UnsupportedPlatform;
            }

            if (error is not (PermissionDenied or TryAgain))
            {
                Release();
                return StoreStatus.UnknownFailure;
            }

            if (!waitOptions.IsInfinite
                && Stopwatch.GetElapsedTime(startTimestamp) >= waitOptions.Timeout)
            {
                Release();
                return StoreStatus.StoreBusy;
            }

            if (!WaitBeforeRetry(waitOptions, startTimestamp))
            {
                StoreStatus status = waitOptions.CancellationToken.IsCancellationRequested
                    ? StoreStatus.OperationCanceled
                    : StoreStatus.StoreBusy;
                Release();
                return status;
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
            var request = LinuxFlock.Create(Unlock);
            int result;
            do
            {
                result = Fcntl(
                    _stream.SafeFileHandle,
                    OpenFileDescriptionSetLock,
                    ref request);
            }
            while (result != 0 && Marshal.GetLastPInvokeError() == Interrupted);

            bool unlocked = result == 0;

            _locked = false;
            if (!unlocked)
            {
                // Releasing the process-local gate while an OFD lock might
                // still be present would let local callers run while foreign
                // callers remain excluded. Retire this descriptor first;
                // close is the kernel-guaranteed OFD-lock release boundary.
                _unusable = true;
                try
                {
                    CloseStream();
                }
                catch
                {
                    // This wrapper remains unusable even if descriptor close
                    // could not be confirmed; no local work can pass it.
                }
            }
        }

        if (_localLockHeld)
        {
            _localLockHeld = false;
            _localGate.Release();
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
        CloseStream();
    }

    private bool TryAcquireLocal(StoreWaitOptions waitOptions, long startTimestamp)
    {
        while (true)
        {
            // OFD locks contend across separately opened descriptors, loaded
            // assemblies, and native modules in one PID. One lock wrapper can
            // still be shared by several local callers, so keep it explicitly
            // non-reentrant before entering the kernel.
            if (_localGate.Wait(0))
            {
                _localLockHeld = true;
                return true;
            }

            if (!waitOptions.IsInfinite
                && Stopwatch.GetElapsedTime(startTimestamp) >= waitOptions.Timeout)
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
            TimeSpan remaining = waitOptions.Timeout - Stopwatch.GetElapsedTime(startTimestamp);
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

    private void CloseStream()
    {
        if (_streamClosed)
        {
            return;
        }

        _streamClosed = true;
        _stream.Dispose();
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(
        SafeFileHandle fileDescriptor,
        int command,
        ref LinuxFlock request);

    // SharedMemoryStore requires a 64-bit process. Linux x64 and arm64 both
    // use this LP64 struct flock ABI; OFD commands require l_pid to be zero.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct LinuxFlock
    {
        [FieldOffset(0)]
        internal short Type;

        [FieldOffset(2)]
        internal short Whence;

        [FieldOffset(8)]
        internal long Start;

        [FieldOffset(16)]
        internal long Length;

        [FieldOffset(24)]
        internal int ProcessId;

        internal static LinuxFlock Create(short type) => new()
        {
            Type = type,
            Whence = SeekSet,
            Start = 0,
            Length = 1,
            ProcessId = 0
        };
    }
}
