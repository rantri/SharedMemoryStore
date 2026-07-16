using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SharedMemoryStore.Interop;

internal enum LinuxOwnerAnchorState
{
    Missing,
    Locked,
    Unlocked,
    Ambiguous
}

internal readonly record struct LinuxOwnerAnchorArtifact(Guid OwnerToken, string Path);

/// <summary>
/// Holds one private Linux owner-liveness anchor. The anchor deliberately uses
/// <c>flock</c>, not the resource protocol's POSIX record lock: it is private to
/// current managed owners, is safe to release from another thread, and a probe
/// through a separately opened file descriptor cannot acquire it reentrantly.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxOwnerAnchor : IDisposable
{
    private const string AnchorSegment = ".anchor.";
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int DuplicateDescriptorCloseOnExec = 1030;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x0001;
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFileType = 0x8000;
    private const uint OwnerReadWriteMode = 0x180; // 0600
    private const int NoEntryError = 2;
    private const int WouldBlockError = 11;

    private static readonly ConcurrentDictionary<string, LinuxOwnerAnchor> LocalAnchors =
        new(StringComparer.Ordinal);

    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private int _disposed;

    private LinuxOwnerAnchor(string path, SafeFileHandle handle)
    {
        _path = path;
        _handle = handle;
    }

    internal string AnchorPath => _path;

    internal static LinuxOwnerAnchor Create(string ownersPath, Guid ownerToken)
    {
        string path = GetPath(ownersPath, ownerToken);
        LinuxSharedMemoryDirectory.EnsureExists(Path.GetDirectoryName(path) ?? ".");
        SafeFileHandle? handle = null;
        var created = false;
        try
        {
            int descriptor = Open(
                path,
                OpenReadWrite | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
                OwnerReadWriteMode);
            if (descriptor < 0)
            {
                throw new IOException(
                    "Unable to create the Linux owner anchor.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            created = true;
            handle = OwnDescriptor(descriptor);
            File.SetUnixFileMode(path, LinuxSharedMemoryDirectory.PrivateFileMode);
            if (Flock(handle, LockExclusive | LockNonBlocking) != 0)
            {
                throw new IOException(
                    "Unable to acquire the Linux owner anchor.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var anchor = new LinuxOwnerAnchor(path, handle);
            handle = null;
            if (!LocalAnchors.TryAdd(path, anchor))
            {
                anchor.Dispose();
                throw new IOException("A local Linux owner anchor already uses the generated token.");
            }

            return anchor;
        }
        catch
        {
            handle?.Dispose();
            if (created)
            {
                TryDeleteArtifact(path);
            }

            throw;
        }
    }

    internal static LinuxOwnerAnchorState Probe(string ownersPath, Guid ownerToken) =>
        Probe(ownersPath, ownerToken, honorLocalRegistry: true);

    internal static LinuxOwnerAnchorState Probe(
        string ownersPath,
        Guid ownerToken,
        bool honorLocalRegistry)
    {
        string path = GetPath(ownersPath, ownerToken);
        if (honorLocalRegistry
            && LocalAnchors.TryGetValue(path, out LinuxOwnerAnchor? local)
            && Volatile.Read(ref local._disposed) == 0)
        {
            return LinuxOwnerAnchorState.Locked;
        }

        return ProbePath(path, deleteWhenUnlocked: false);
    }

    private static LinuxOwnerAnchorState ProbePath(string path, bool deleteWhenUnlocked)
    {
        try
        {
            int descriptor = Open(
                path,
                OpenReadWrite | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
                OwnerReadWriteMode);
            if (descriptor < 0)
            {
                return Marshal.GetLastPInvokeError() == NoEntryError
                    ? LinuxOwnerAnchorState.Missing
                    : LinuxOwnerAnchorState.Ambiguous;
            }

            using SafeFileHandle handle = OwnDescriptor(descriptor);
            if (!IsRegularFile(handle))
            {
                return LinuxOwnerAnchorState.Ambiguous;
            }

            if (Flock(handle, LockExclusive | LockNonBlocking) == 0)
            {
                try
                {
                    if (deleteWhenUnlocked)
                    {
                        // Keep the separately opened description locked until the
                        // pathname is removed. Cooperative creators cannot replace a
                        // same-store anchor while the caller holds .lifecycle.
                        TryDeleteArtifact(path);
                    }
                }
                finally
                {
                    _ = Flock(handle, LockUnlock);
                }

                return LinuxOwnerAnchorState.Unlocked;
            }

            return Marshal.GetLastPInvokeError() == WouldBlockError
                ? LinuxOwnerAnchorState.Locked
                : LinuxOwnerAnchorState.Ambiguous;
        }
        catch (FileNotFoundException)
        {
            return LinuxOwnerAnchorState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return LinuxOwnerAnchorState.Missing;
        }
        catch
        {
            return LinuxOwnerAnchorState.Ambiguous;
        }
    }

    private static bool IsRegularFile(SafeFileHandle handle)
    {
        try
        {
            int descriptor = handle.DangerousGetHandle().ToInt32();
            int result = Statx(
                descriptor,
                string.Empty,
                AtEmptyPath,
                StatxType,
                out LinuxStatx metadata);
            GC.KeepAlive(handle);
            return result == 0
                && (metadata.Mask & StatxType) != 0
                && (metadata.Mode & FileTypeMask) == RegularFileType;
        }
        catch
        {
            // A missing libc entry point, unsupported kernel operation, invalid
            // descriptor, or marshaling uncertainty must retain the artifact.
            return false;
        }
    }

    internal static string GetPath(string ownersPath, Guid ownerToken) =>
        ownersPath + AnchorSegment + ownerToken.ToString("N");

    /// <summary>
    /// Removes only well-formed, unreferenced anchor artifacts whose lock can
    /// be acquired through a separate open description. Malformed names and
    /// locked or ambiguous artifacts are deliberately retained.
    /// </summary>
    internal static void SweepUnreferencedArtifacts(
        string ownersPath,
        IReadOnlySet<Guid> referencedOwnerTokens)
    {
        foreach (LinuxOwnerAnchorArtifact artifact in EnumerateWellFormedArtifacts(ownersPath))
        {
            if (referencedOwnerTokens.Contains(artifact.OwnerToken))
            {
                continue;
            }

            // This deliberately bypasses LocalAnchors. The independently
            // opened descriptor is the authoritative cross-process probe.
            _ = ProbePath(artifact.Path, deleteWhenUnlocked: true);
        }
    }

    private static SafeFileHandle OwnDescriptor(int descriptor)
    {
        // SafeFileHandle treats zero as invalid. Native open may legitimately
        // return fd 0 when standard input is closed, so duplicate that one onto
        // a close-on-exec descriptor which SafeFileHandle can own safely.
        if (descriptor == 0)
        {
            int duplicate = Fcntl(
                descriptor,
                DuplicateDescriptorCloseOnExec,
                minimumDescriptor: 1);
            int error = Marshal.GetLastPInvokeError();
            _ = Close(descriptor);
            if (duplicate < 0)
            {
                throw new IOException(
                    "Unable to own the Linux anchor descriptor.",
                    new Win32Exception(error));
            }

            descriptor = duplicate;
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    internal static LinuxOwnerAnchorArtifact[] EnumerateWellFormedArtifacts(string ownersPath)
    {
        string directory = Path.GetDirectoryName(ownersPath) ?? ".";
        if (!Directory.Exists(directory))
        {
            return [];
        }

        string prefix = Path.GetFileName(ownersPath) + AnchorSegment;
        try
        {
            var artifacts = new List<LinuxOwnerAnchorArtifact>();
            foreach (string path in Directory.GetFileSystemEntries(
                         directory,
                         prefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)
                    || name.Length != prefix.Length + 32)
                {
                    continue;
                }

                string tokenText = name[prefix.Length..];
                if (Guid.TryParseExact(tokenText, "N", out Guid token)
                    && string.Equals(tokenText, token.ToString("N"), StringComparison.Ordinal))
                {
                    artifacts.Add(new LinuxOwnerAnchorArtifact(token, path));
                }
            }

            return artifacts.ToArray();
        }
        catch
        {
            // Enumeration is advisory cleanup. Failure must retain artifacts,
            // never turn uncertainty into deletion.
            return [];
        }
    }

    internal static void ReleaseLocalAfterOwnerAbsent(string ownersPath, Guid ownerToken)
    {
        string path = GetPath(ownersPath, ownerToken);
        if (LocalAnchors.TryGetValue(path, out LinuxOwnerAnchor? anchor))
        {
            anchor.Dispose();
            return;
        }

        _ = ProbePath(path, deleteWhenUnlocked: true);
    }

    internal static void TryDeleteArtifact(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch
        {
            // Anchor artifacts are advisory only after their exact owner line is
            // committed absent. A later lifecycle cleanup can retry deletion.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = ((ICollection<KeyValuePair<string, LinuxOwnerAnchor>>)LocalAnchors).Remove(
            new KeyValuePair<string, LinuxOwnerAnchor>(_path, this));
        try
        {
            _ = Flock(_handle, LockUnlock);
        }
        catch
        {
        }

        _handle.Dispose();
        TryDeleteArtifact(_path);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(
        int fileDescriptor,
        int command,
        int minimumDescriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatx metadata);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle fileDescriptor, int operation);

    // Linux statx is an architecture-neutral, versioned 256-byte ABI. Only the
    // returned-mask and file-type fields are projected; explicit offsets avoid
    // depending on the architecture-specific layout of struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        internal uint Mask;

        [FieldOffset(28)]
        internal ushort Mode;
    }
}
