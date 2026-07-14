using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Options;

namespace SharedMemoryStore;

/// <summary>
/// Selects how <see cref="MemoryStore"/> should resolve the named mapping.
/// </summary>
public enum OpenMode
{
    /// <summary>Create a new mapping and fail when one already exists.</summary>
    CreateNew = 0,

    /// <summary>Open an existing mapping and fail when one does not exist.</summary>
    OpenExisting = 1,

    /// <summary>Create the mapping if needed, otherwise open the existing mapping.</summary>
    CreateOrOpen = 2
}

/// <summary>
/// Selects the shared-memory layout and concurrency protocol used by a store.
/// </summary>
public enum StoreProfile
{
    /// <summary>
    /// Uses the compatible layout-v1.2 implementation and its legacy synchronization protocol.
    /// This remains the default profile.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Uses the layout-v2 lock-free implementation. Progress is system-wide lock-free, not
    /// wait-free for every caller under sustained contention.
    /// </summary>
    LockFree = 1
}

/// <summary>
/// Configuration used when creating or opening a bounded named shared-memory store.
/// </summary>
public sealed class SharedMemoryStoreOptions
{
    /// <summary>
    /// Gets the explicitly requested shared-memory layout and concurrency profile. The default
    /// is <see cref="StoreProfile.Legacy"/>; opening never auto-selects an existing opposite profile.
    /// </summary>
    public StoreProfile Profile { get; init; } = StoreProfile.Legacy;

    /// <summary>Gets the OS-visible name of the mapped store.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the requested open behavior for the named store.</summary>
    public OpenMode OpenMode { get; init; } = OpenMode.CreateOrOpen;

    /// <summary>Gets the total byte length of the mapped region.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Gets the number of reusable value slots in the region.</summary>
    public int SlotCount { get; init; }

    /// <summary>Gets the maximum payload length, in bytes, that one slot can hold.</summary>
    public int MaxValueBytes { get; init; }

    /// <summary>Gets the maximum descriptor length, in bytes, that one slot can hold.</summary>
    public int MaxDescriptorBytes { get; init; }

    /// <summary>Gets the maximum key length, in bytes, stored inline in the shared index.</summary>
    public int MaxKeyBytes { get; init; }

    /// <summary>Gets the maximum number of simultaneously active lease records.</summary>
    public int LeaseRecordCount { get; init; }

    /// <summary>
    /// Gets the layout-v2 participant-record capacity. Each open store handle consumes one record;
    /// the default is 64. Legacy stores ignore this value.
    /// </summary>
    public int ParticipantRecordCount { get; init; } = 64;

    /// <summary>Gets a value indicating whether explicit stale lease recovery is enabled.</summary>
    public bool EnableLeaseRecovery { get; init; }

    /// <summary>
    /// Calculates the minimum mapped-region length required for the supplied layout dimensions.
    /// </summary>
    public static long CalculateRequiredBytes(
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount)
    {
        return StoreLayout.CalculateRequiredBytes(
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount);
    }

    /// <summary>
    /// Calculates the minimum mapped-region length for an explicitly selected store profile.
    /// </summary>
    /// <remarks>
    /// The lock-free profile validates its layout-v2 slot and participant limits. The existing
    /// profile-less overload retains layout-v1.2 sizing.
    /// </remarks>
    public static long CalculateRequiredBytes(
        StoreProfile profile,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (profile == StoreProfile.Legacy)
        {
            return CalculateRequiredBytes(
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount);
        }

        return StoreLayoutV2.CalculateRequiredBytes(
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount);
    }

    /// <summary>
    /// Creates valid ordinary store options and derives the required mapped-region size.
    /// </summary>
    public static SharedMemoryStoreOptions Create(
        string name,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        OpenMode openMode = OpenMode.CreateOrOpen,
        bool enableLeaseRecovery = false)
    {
        return new SharedMemoryStoreOptions
        {
            Profile = StoreProfile.Legacy,
            Name = name,
            OpenMode = openMode,
            SlotCount = slotCount,
            MaxValueBytes = maxValueBytes,
            MaxDescriptorBytes = maxDescriptorBytes,
            MaxKeyBytes = maxKeyBytes,
            LeaseRecordCount = leaseRecordCount,
            EnableLeaseRecovery = enableLeaseRecovery,
            TotalBytes = CalculateRequiredBytes(
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount)
        };
    }

    /// <summary>
    /// Creates layout-v2 lock-free store options and derives the required mapped-region size.
    /// </summary>
    /// <remarks>
    /// Selection is explicit and never converts an existing layout-v1.2 mapping. One participant
    /// record is consumed by each successfully opened handle.
    /// </remarks>
    public static SharedMemoryStoreOptions CreateLockFree(
        string name,
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64,
        OpenMode openMode = OpenMode.CreateOrOpen,
        bool enableLeaseRecovery = false)
    {
        return new SharedMemoryStoreOptions
        {
            Profile = StoreProfile.LockFree,
            Name = name,
            OpenMode = openMode,
            SlotCount = slotCount,
            MaxValueBytes = maxValueBytes,
            MaxDescriptorBytes = maxDescriptorBytes,
            MaxKeyBytes = maxKeyBytes,
            LeaseRecordCount = leaseRecordCount,
            ParticipantRecordCount = participantRecordCount,
            EnableLeaseRecovery = enableLeaseRecovery,
            TotalBytes = CalculateRequiredBytes(
                StoreProfile.LockFree,
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                participantRecordCount)
        };
    }

    /// <summary>
    /// Validates this option instance and returns actionable public validation details.
    /// </summary>
    public StoreOptionsValidationResult Validate()
    {
        return SharedMemoryStoreOptionsValidator.ValidateDetailed(this, out _);
    }

    /// <summary>
    /// Validates an option instance and returns actionable public validation details.
    /// </summary>
    public static StoreOptionsValidationResult Validate(SharedMemoryStoreOptions? options)
    {
        return SharedMemoryStoreOptionsValidator.ValidateDetailed(options, out _);
    }
}

/// <summary>
/// Options for explicit owner-controlled stale lease recovery.
/// </summary>
/// <param name="RecoverCurrentProcessLeases">
/// When true, active lease records owned by the current process may be recovered
/// for tests and controlled shutdown only after the caller has quiesced all
/// current-process lease acquisition, projection, borrowed-span use, and release
/// across every handle attached to the mapping. That process-wide quiescence must
/// remain in force until recovery returns. Concurrent use of this override with
/// current-process lease activity is unsupported and is not guarded by a
/// hot-path gate. False remains safe during normal concurrent lease activity.
/// A live record still in its Claiming initialization phase remains protected
/// until participant closing or recovery proves its claimant is quiescent.
/// </param>
public readonly record struct LeaseRecoveryOptions(bool RecoverCurrentProcessLeases);

/// <summary>
/// Summary returned by explicit owner-controlled stale lease recovery.
/// </summary>
public readonly record struct LeaseRecoveryReport
{
    /// <summary>
    /// Initializes a recovery report using the original three-result shape.
    /// </summary>
    public LeaseRecoveryReport(int scannedRecordCount, int recoveredLeaseCount, int unsupportedLeaseCount)
        : this(scannedRecordCount, recoveredLeaseCount, 0, unsupportedLeaseCount, 0)
    {
    }

    /// <summary>
    /// Initializes a recovery report with all owner-safety decision counts.
    /// </summary>
    public LeaseRecoveryReport(
        int scannedRecordCount,
        int recoveredLeaseCount,
        int activeLeaseCount,
        int unsupportedLeaseCount,
        int failedRecoveryCount)
    {
        ScannedRecordCount = scannedRecordCount;
        RecoveredLeaseCount = recoveredLeaseCount;
        ActiveLeaseCount = activeLeaseCount;
        UnsupportedLeaseCount = unsupportedLeaseCount;
        FailedRecoveryCount = failedRecoveryCount;
    }

    /// <summary>Gets the number of lease records inspected.</summary>
    public int ScannedRecordCount { get; }

    /// <summary>Gets the number of active records recovered.</summary>
    public int RecoveredLeaseCount { get; }

    /// <summary>Gets the number of active records skipped because their owner is still live or not eligible.</summary>
    public int ActiveLeaseCount { get; }

    /// <summary>Gets the number of records that could not be evaluated because platform support was unavailable.</summary>
    public int UnsupportedLeaseCount { get; }

    /// <summary>Gets the number of records rejected because shared state was inconsistent or unsafe to mutate.</summary>
    public int FailedRecoveryCount { get; }
}
