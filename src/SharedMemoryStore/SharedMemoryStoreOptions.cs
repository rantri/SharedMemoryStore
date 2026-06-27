using SharedMemoryStore.Layout;

namespace SharedMemoryStore;

/// <summary>
/// Selects how <see cref="SharedMemoryStore.TryCreateOrOpen"/> should resolve the named mapping.
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
/// Configuration used when creating or opening a bounded shared-memory store.
/// </summary>
public sealed class SharedMemoryStoreOptions
{
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
}

/// <summary>
/// Options for explicit stale lease recovery.
/// </summary>
/// <param name="RecoverCurrentProcessLeases">When true, active lease records owned by the current process may be recovered for tests and controlled shutdown.</param>
public readonly record struct LeaseRecoveryOptions(bool RecoverCurrentProcessLeases);

/// <summary>
/// Summary returned by explicit stale lease recovery.
/// </summary>
/// <param name="ScannedRecordCount">The number of lease records inspected.</param>
/// <param name="RecoveredLeaseCount">The number of active records recovered.</param>
/// <param name="UnsupportedLeaseCount">The number of records that could not be recovered because platform support was unavailable.</param>
public readonly record struct LeaseRecoveryReport(
    int ScannedRecordCount,
    int RecoveredLeaseCount,
    int UnsupportedLeaseCount);
