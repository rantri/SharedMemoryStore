using System.Runtime.Versioning;

namespace SharedMemoryStore.Interop;

/// <summary>
/// Derives and validates the per-store Linux directory used only for volatile
/// owner evidence. Stable region, synchronization, owner-sidecar, and
/// lifecycle resources remain in the shared root; directory enumeration is
/// confined to this store so unrelated names cannot consume a cold-open
/// budget as the namespace grows.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxOwnerArtifactStore
{
    internal const string DirectorySuffix = ".artifacts";
    internal const string AnchorPrefix = "anchor.";
    internal const string ReleasePrefix = "released.";
    internal const string FinalizedReleaseSuffix = ".ready";

    internal static string GetDirectory(string ownersPath) =>
        ownersPath + DirectorySuffix;

    internal static void EnsureDirectory(string ownersPath) =>
        LinuxSharedMemoryDirectory.EnsureExists(GetDirectory(ownersPath));

    internal static string GetAnchorPath(string ownersPath, Guid ownerToken) =>
        Path.Combine(GetDirectory(ownersPath), AnchorPrefix + ownerToken.ToString("N"));

    internal static string GetReleaseMarkerPath(string ownersPath, Guid ownerToken) =>
        Path.Combine(
            GetDirectory(ownersPath),
            ReleasePrefix + ownerToken.ToString("N") + FinalizedReleaseSuffix);

    internal static string[] EnumerateAnchors(string ownersPath)
    {
        EnsureDirectory(ownersPath);
        return Directory.GetFileSystemEntries(
            GetDirectory(ownersPath),
            AnchorPrefix + "*",
            SearchOption.TopDirectoryOnly);
    }

    internal static string[] EnumerateReleaseMarkers(string ownersPath, bool finalizedOnly)
    {
        EnsureDirectory(ownersPath);
        return Directory.GetFiles(
            GetDirectory(ownersPath),
            ReleasePrefix + (finalizedOnly ? "*" + FinalizedReleaseSuffix : "*"),
            SearchOption.TopDirectoryOnly);
    }
}
