using System.Reflection;
using System.Xml.Linq;

namespace SharedMemoryStore.ContractTests;

public sealed class LockFreePackageContractTests
{
    private static readonly Assembly StoreAssembly = typeof(MemoryStore).Assembly;

    [Fact]
    public void PackageMetadataSeparatesNuGetLayoutAndResourceProtocolVersions()
    {
        string project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SharedMemoryStore",
            "SharedMemoryStore.csproj"));
        var document = XDocument.Parse(project);
        XElement propertyGroup = Assert.Single(document.Root!.Elements("PropertyGroup"));

        Assert.Equal("2.0.0", propertyGroup.Element("Version")?.Value);
        Assert.Equal(new Version(2, 0, 0, 0), StoreAssembly.GetName().Version);

        string releaseNotes = propertyGroup.Element("PackageReleaseNotes")?.Value ?? string.Empty;
        Assert.Contains("layout 2.0", releaseNotes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resource protocol 2", releaseNotes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy", releaseNotes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default", releaseNotes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lock-free", releaseNotes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C++", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("Python", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("layout 1.2", releaseNotes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no in-place", releaseNotes, StringComparison.OrdinalIgnoreCase);
    }

    internal static void AssertEveryAdditiveLockFreePublicSymbolHasPackagedXmlDocumentation()
    {
        IReadOnlyDictionary<string, string> members = LoadDocumentationMembers();
        string[] expectedMembers =
        [
            "T:SharedMemoryStore.StoreProfile",
            "F:SharedMemoryStore.StoreProfile.Legacy",
            "F:SharedMemoryStore.StoreProfile.LockFree",
            "P:SharedMemoryStore.SharedMemoryStoreOptions.Profile",
            "P:SharedMemoryStore.SharedMemoryStoreOptions.ParticipantRecordCount",
            "M:SharedMemoryStore.SharedMemoryStoreOptions.CalculateRequiredBytes(SharedMemoryStore.StoreProfile,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)",
            "M:SharedMemoryStore.SharedMemoryStoreOptions.CreateLockFree(System.String,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,SharedMemoryStore.OpenMode,System.Boolean)",
            "T:SharedMemoryStore.StoreProtocolInfo",
            "P:SharedMemoryStore.StoreProtocolInfo.Profile",
            "P:SharedMemoryStore.StoreProtocolInfo.LayoutMajorVersion",
            "P:SharedMemoryStore.StoreProtocolInfo.LayoutMinorVersion",
            "P:SharedMemoryStore.StoreProtocolInfo.ResourceProtocolVersion",
            "P:SharedMemoryStore.StoreProtocolInfo.RequiredFeatures",
            "P:SharedMemoryStore.StoreProtocolInfo.OptionalFeatures",
            "P:SharedMemoryStore.MemoryStore.Profile",
            "P:SharedMemoryStore.MemoryStore.ProtocolInfo",
            "F:SharedMemoryStore.StoreOpenStatus.ParticipantTableFull",
            "P:SharedMemoryStore.DiagnosticsSnapshot.Profile",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ProtocolInfo",
            "P:SharedMemoryStore.DiagnosticsSnapshot.InitializingSlotCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ReservedSlotCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ReclaimingSlotCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RetiredSlotCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ClaimingLeaseCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RecoveringLeaseCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.FreeLeaseCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RetiredLeaseCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ParticipantRecordCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.FreeParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RegisteringParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ActiveParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ClosingParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RecoveringParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ReclaimingParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RetiredParticipantCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.IsParticipantTableExhausted",
            "P:SharedMemoryStore.DiagnosticsSnapshot.PrimaryDirectoryOccupancy",
            "P:SharedMemoryStore.DiagnosticsSnapshot.SpilledBucketCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.OverflowDirectoryOccupancy",
            "P:SharedMemoryStore.DiagnosticsSnapshot.OverflowScanCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.MaxObservedOverflowScanLength",
            "P:SharedMemoryStore.DiagnosticsSnapshot.CasRetryCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.HelpedTransitionCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ContentionBudgetExhaustionCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.InvalidTokenCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.StaleTokenCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RecoveryAttemptCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.RecoveredTransitionCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.CurrentOwnerClassificationCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.LiveOwnerClassificationCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.StaleOwnerClassificationCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.UnsupportedOwnerClassificationCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.InconsistentOwnerClassificationCount",
            "P:SharedMemoryStore.DiagnosticsSnapshot.ChangingOwnerClassificationCount"
        ];

        var missing = expectedMembers.Where(member => !members.ContainsKey(member)).ToArray();
        Assert.True(missing.Length == 0, "Missing XML documentation members: " + string.Join(", ", missing));
        Assert.All(expectedMembers, member => Assert.False(string.IsNullOrWhiteSpace(members[member])));
    }

    [Fact]
    public void ChangedConcurrencyAndLifetimeContractsAreExplicitInPackagedXmlDocumentation()
    {
        IReadOnlyDictionary<string, string> members = LoadDocumentationMembers();

        AssertContainsAll(
            members["F:SharedMemoryStore.StoreProfile.LockFree"],
            "lock-free",
            "wait-free");
        AssertContainsAll(
            members["P:SharedMemoryStore.SharedMemoryStoreOptions.ParticipantRecordCount"],
            "layout-v2",
            "handle",
            "64");
        AssertContainsAll(
            members["T:SharedMemoryStore.StoreProtocolInfo"],
            "independent",
            "package");
        AssertContainsAll(
            members["T:SharedMemoryStore.StoreWaitOptions"],
            "legacy",
            "lock-free",
            "local");
        AssertContainsAll(
            members["F:SharedMemoryStore.StoreStatus.RemovePending"],
            "logically absent",
            "bounded",
            "physical reclamation");
        AssertContainsAll(
            members["F:SharedMemoryStore.StoreStatus.StoreBusy"],
            "legacy",
            "lock-free",
            "local retry");
        AssertContainsAll(
            members["F:SharedMemoryStore.StoreStatus.OperationCanceled"],
            "ordering point");
        AssertContainsAll(
            members["M:SharedMemoryStore.MemoryStore.TryRemove(System.ReadOnlySpan{System.Byte},SharedMemoryStore.StoreWaitOptions)"],
            "logically absent",
            "RemovePending",
            "physical");
        AssertContainsAll(
            members["T:SharedMemoryStore.ValueReservation"],
            "single-producer",
            "copied",
            "concurrent");
        AssertContainsAll(
            members["P:SharedMemoryStore.ValueLease.ValueSpan"],
            "release",
            "store disposal");
    }

    private static IReadOnlyDictionary<string, string> LoadDocumentationMembers()
    {
        string xmlPath = Path.ChangeExtension(StoreAssembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), $"Expected generated package documentation at '{xmlPath}'.");

        return XDocument.Load(xmlPath)
            .Descendants("member")
            .Where(member => member.Attribute("name") is not null)
            .ToDictionary(
                member => member.Attribute("name")!.Value,
                member => Normalize(member.Value),
                StringComparer.Ordinal);
    }

    private static void AssertContainsAll(string documentation, params string[] expectedFragments)
    {
        Assert.All(
            expectedFragments,
            fragment => Assert.Contains(fragment, documentation, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
