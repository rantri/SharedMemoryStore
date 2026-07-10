using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace SharedMemoryStore.InteropTests.TestSupport;

internal sealed class ForeignStoreLock : IDisposable
{
    private readonly string _publicName;
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private readonly TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _holder;
    private int _disposed;

    private ForeignStoreLock(string publicName)
    {
        _publicName = publicName;
        _holder = new Thread(Hold)
        {
            IsBackground = true,
            Name = "SharedMemoryStore foreign-lock test holder"
        };
    }

    public static async Task<ForeignStoreLock> AcquireAsync(string publicName)
    {
        var result = new ForeignStoreLock(publicName);
        result._holder.Start();
        try
        {
            await result._acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public static string LinuxRegionPath(string publicName) =>
        LinuxPath(publicName, ".region");

    public static string LinuxSynchronizationPath(string publicName) =>
        LinuxPath(publicName, ".lock");

    public static string LinuxOwnersPath(string publicName) =>
        LinuxPath(publicName, ".owners");

    public static string LinuxLifecyclePath(string publicName) =>
        LinuxPath(publicName, ".lifecycle");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        if (_holder.IsAlive && !_holder.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The foreign store lock holder did not stop.");
        }

        _release.Dispose();
    }

    private void Hold()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                HoldWindows();
            }
            else if (OperatingSystem.IsLinux())
            {
                HoldLinux();
            }
            else
            {
                throw new PlatformNotSupportedException("Interop contention tests support Windows and Linux.");
            }
        }
        catch (Exception exception)
        {
            _acquired.TrySetException(exception);
        }
    }

    private void HoldWindows()
    {
        using var mutex = new Mutex(initiallyOwned: false, BuildWindowsSynchronizationName(_publicName));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new TimeoutException("Could not acquire the Windows interoperability mutex.");
            }

            _acquired.TrySetResult();
            _release.Wait();
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private void HoldLinux()
    {
        var path = LinuxSynchronizationPath(_publicName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Lock(0, 1);
        try
        {
            _acquired.TrySetResult();
            _release.Wait();
        }
        finally
        {
            stream.Unlock(0, 1);
        }
    }

    private static string BuildWindowsSynchronizationName(string publicName)
    {
        var scope = publicName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
            ? @"Global\"
            : @"Local\";
        var sanitized = string.Create(publicName.Length, publicName, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var value = source[index];
                destination[index] = char.IsLetterOrDigit(value) || value is '-' or '_' ? value : '_';
            }
        });
        return scope + "SharedMemoryStore-" + sanitized;
    }

    private static string LinuxPath(string publicName, string suffix)
    {
        var sanitized = new StringBuilder(publicName.Length);
        foreach (var value in publicName)
        {
            sanitized.Append(char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' ? value : '_');
        }

        var readable = sanitized.ToString().Trim('_', '.');
        if (readable.Length == 0)
        {
            readable = "store";
        }
        else if (readable.Length > 80)
        {
            readable = readable[..80];
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicName));
        var digest = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        var directory = Path.Combine(Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(), "SharedMemoryStore");
        return Path.Combine(directory, $"sms-{readable}-{digest}{suffix}");
    }
}
