using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.UnitTests;

[SupportedOSPlatform("linux")]
public sealed class LinuxOwnerAnchorTests
{
    [Fact]
    public async Task HeldAnchorIsLiveToLocalAndSeparateDescriptorProbesAndDisposesCrossThread()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid token = Guid.NewGuid();
        LinuxOwnerAnchor anchor = await Task.Run(() => LinuxOwnerAnchor.Create(ownersPath, token));
        string anchorPath = LinuxOwnerAnchor.GetPath(ownersPath, token);
        try
        {
            Assert.True(File.Exists(anchorPath));
            Assert.Equal(
                LinuxSharedMemoryDirectory.PrivateFileMode,
                File.GetUnixFileMode(anchorPath));
            Assert.Equal(LinuxOwnerAnchorState.Locked, LinuxOwnerAnchor.Probe(ownersPath, token));
            Assert.Equal(
                LinuxOwnerAnchorState.Locked,
                LinuxOwnerAnchor.Probe(ownersPath, token, honorLocalRegistry: false));
        }
        finally
        {
            await Task.Run(anchor.Dispose);
        }

        Assert.Equal(LinuxOwnerAnchorState.Missing, LinuxOwnerAnchor.Probe(ownersPath, token));
        Assert.False(File.Exists(anchorPath));
    }

    [Fact]
    public void PresentUnlockedAnchorIsStaleAndMissingAnchorIsLegacyFallbackCandidate()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid token = Guid.NewGuid();
        string anchorPath = LinuxOwnerAnchor.GetPath(ownersPath, token);

        Assert.Equal(LinuxOwnerAnchorState.Missing, LinuxOwnerAnchor.Probe(ownersPath, token));
        File.WriteAllBytes(anchorPath, []);
        File.SetUnixFileMode(anchorPath, LinuxSharedMemoryDirectory.PrivateFileMode);

        Assert.Equal(LinuxOwnerAnchorState.Unlocked, LinuxOwnerAnchor.Probe(ownersPath, token));
    }

    [Fact]
    public void DuplicateCreateDoesNotDeleteTheExistingLockedAnchor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid token = Guid.NewGuid();
        using LinuxOwnerAnchor first = LinuxOwnerAnchor.Create(ownersPath, token);

        Assert.Throws<IOException>(() => LinuxOwnerAnchor.Create(ownersPath, token));

        Assert.True(File.Exists(first.AnchorPath));
        Assert.Equal(
            LinuxOwnerAnchorState.Locked,
            LinuxOwnerAnchor.Probe(ownersPath, token, honorLocalRegistry: false));
    }

    [Fact]
    public void SymbolicLinkAnchorIsRetainedConservatively()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid token = Guid.NewGuid();
        string targetPath = Path.Combine(directory.Path, "target");
        string anchorPath = LinuxOwnerAnchor.GetPath(ownersPath, token);
        File.WriteAllBytes(targetPath, []);
        File.CreateSymbolicLink(anchorPath, targetPath);

        Assert.Equal(LinuxOwnerAnchorState.Ambiguous, LinuxOwnerAnchor.Probe(ownersPath, token));
    }

    [Fact]
    public void DirectoryAndDanglingLinkAnchorsAreRetainedConservatively()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid directoryToken = Guid.NewGuid();
        string directoryAnchor = LinuxOwnerAnchor.GetPath(ownersPath, directoryToken);
        Directory.CreateDirectory(directoryAnchor);
        Assert.Equal(
            LinuxOwnerAnchorState.Ambiguous,
            LinuxOwnerAnchor.Probe(ownersPath, directoryToken));

        Guid linkToken = Guid.NewGuid();
        string linkAnchor = LinuxOwnerAnchor.GetPath(ownersPath, linkToken);
        File.CreateSymbolicLink(linkAnchor, Path.Combine(directory.Path, "missing-target"));
        Assert.Equal(
            LinuxOwnerAnchorState.Ambiguous,
            LinuxOwnerAnchor.Probe(ownersPath, linkToken));
    }

    [Fact]
    public void ReferencedFifoProbeIsAmbiguousAndRetained()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid token = Guid.NewGuid();
        string fifoPath = CreateFifoAnchor(ownersPath, token);
        File.WriteAllText(
            ownersPath,
            $"{Environment.ProcessId}:proc-test:{token:N}{Environment.NewLine}");

        Assert.Equal(
            LinuxOwnerAnchorState.Ambiguous,
            LinuxOwnerAnchor.Probe(ownersPath, token, honorLocalRegistry: false));
        Assert.True(File.Exists(fifoPath));
    }

    [Fact]
    public void SweepRetainsUnreferencedFifoArtifact()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid token = Guid.NewGuid();
        string fifoPath = CreateFifoAnchor(ownersPath, token);

        LinuxOwnerAnchor.SweepUnreferencedArtifacts(ownersPath, new HashSet<Guid>());

        Assert.True(File.Exists(fifoPath));
        Assert.Equal(
            LinuxOwnerAnchorState.Ambiguous,
            LinuxOwnerAnchor.Probe(ownersPath, token, honorLocalRegistry: false));
    }

    [Fact]
    public void SweepDeletesOnlyWellFormedUnlockedUnreferencedArtifacts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        string ownersPath = Path.Combine(directory.Path, "store.owners");
        Guid staleToken = Guid.NewGuid();
        Guid referencedToken = Guid.NewGuid();
        Guid lockedToken = Guid.NewGuid();
        Guid ambiguousToken = Guid.NewGuid();
        string stalePath = CreateUnlockedAnchor(ownersPath, staleToken);
        string referencedPath = CreateUnlockedAnchor(ownersPath, referencedToken);
        string ambiguousPath = LinuxOwnerAnchor.GetPath(ownersPath, ambiguousToken);
        string ambiguousTarget = Path.Combine(directory.Path, "ambiguous-target");
        File.WriteAllBytes(ambiguousTarget, []);
        File.CreateSymbolicLink(ambiguousPath, ambiguousTarget);
        string malformedPath = ownersPath + ".anchor.not-a-valid-owner-token";
        File.WriteAllBytes(malformedPath, []);
        using LinuxOwnerAnchor locked = LinuxOwnerAnchor.Create(ownersPath, lockedToken);
        string lockedPath = locked.AnchorPath;

        LinuxOwnerAnchor.SweepUnreferencedArtifacts(
            ownersPath,
            new HashSet<Guid> { referencedToken });

        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(referencedPath));
        Assert.True(File.Exists(lockedPath));
        Assert.True(File.Exists(ambiguousPath));
        Assert.True(File.Exists(ambiguousTarget));
        Assert.True(File.Exists(malformedPath));
        Assert.Equal(
            LinuxOwnerAnchorState.Locked,
            LinuxOwnerAnchor.Probe(ownersPath, lockedToken, honorLocalRegistry: false));
        Assert.Equal(
            LinuxOwnerAnchorState.Ambiguous,
            LinuxOwnerAnchor.Probe(ownersPath, ambiguousToken, honorLocalRegistry: false));
    }

    private static string CreateUnlockedAnchor(string ownersPath, Guid token)
    {
        string path = LinuxOwnerAnchor.GetPath(ownersPath, token);
        File.WriteAllBytes(path, []);
        File.SetUnixFileMode(path, LinuxSharedMemoryDirectory.PrivateFileMode);
        return path;
    }

    private static string CreateFifoAnchor(string ownersPath, Guid token)
    {
        string path = LinuxOwnerAnchor.GetPath(ownersPath, token);
        int result = MkFifo(path, 0x180); // 0600
        Assert.True(result == 0, $"mkfifo failed with errno {Marshal.GetLastPInvokeError()}.");
        return path;
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sms-owner-anchor-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
