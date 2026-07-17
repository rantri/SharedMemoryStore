using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SharedMemoryStore.InteropAgent;

/// <summary>Holds the inherited cold synchronization resource on its owning thread.</summary>
internal sealed class AgentColdLock : IDisposable
{
    private readonly string _publicName;
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private readonly TaskCompletionSource _acquired = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _holder;
    private int _disposed;

    private AgentColdLock(string publicName)
    {
        _publicName = publicName;
        _holder = new Thread(Hold)
        {
            IsBackground = true,
            Name = "SharedMemoryStore managed interop cold-lock holder"
        };
    }

    internal static AgentColdLock Acquire(string publicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicName);
        var result = new AgentColdLock(publicName);
        result._holder.Start();
        try
        {
            result._acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        if (_holder.IsAlive && !_holder.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The managed interop cold-lock holder did not stop.");
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
                throw new PlatformNotSupportedException(
                    "Cold-lock injection supports Windows and Linux.");
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
                throw new TimeoutException("Could not acquire the Windows cold mutex.");
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
        using var stream = new FileStream(
            LinuxSynchronizationPath(_publicName),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
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
        string scope = publicName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
            ? @"Global\"
            : @"Local\";
        string sanitized = string.Create(publicName.Length, publicName, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                char value = source[index];
                destination[index] = char.IsLetterOrDigit(value) || value is '-' or '_' ? value : '_';
            }
        });
        return scope + "SharedMemoryStore-" + sanitized;
    }

    private static string LinuxSynchronizationPath(string publicName)
    {
        var sanitized = new StringBuilder(publicName.Length);
        foreach (char value in publicName)
        {
            sanitized.Append(char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' ? value : '_');
        }

        string readable = sanitized.ToString().Trim('_', '.');
        readable = readable.Length switch
        {
            0 => "store",
            > 80 => readable[..80],
            _ => readable
        };
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicName));
        string digest = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        string directory = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "SharedMemoryStore");
        return Path.Combine(directory, $"sms-{readable}-{digest}.lock");
    }
}
