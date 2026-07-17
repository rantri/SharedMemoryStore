namespace SharedMemoryStore;

/// <summary>
/// Describes the outcome of creating or opening a shared memory store.
/// </summary>
public enum StoreOpenStatus
{
    /// <summary>The store was created or opened successfully.</summary>
    Success = 0,

    /// <summary>A store with the requested name already exists.</summary>
    AlreadyExists = 1,

    /// <summary>No store with the requested name exists.</summary>
    NotFound = 2,

    /// <summary>The supplied options are invalid.</summary>
    InvalidOptions = 3,

    /// <summary>The existing mapped layout is not compatible with the supplied options.</summary>
    IncompatibleLayout = 4,

    /// <summary>The current platform does not support the requested named shared-memory behavior.</summary>
    UnsupportedPlatform = 5,

    /// <summary>The configured region size cannot contain the requested layout.</summary>
    InsufficientCapacity = 6,

    /// <summary>The process does not have sufficient access to create or open the mapping.</summary>
    AccessDenied = 7,

    /// <summary>The runtime failed to create or open the memory mapping.</summary>
    MappingFailed = 8,

    /// <summary>
    /// The selected open bound expired during cold lifecycle coordination or participant claim.
    /// </summary>
    StoreBusy = 9,

    /// <summary>The open or create operation was canceled before a handle became active.</summary>
    OperationCanceled = 10,

    /// <summary>No reusable layout-v2 participant record is currently available.</summary>
    ParticipantTableFull = 11
}

/// <summary>
/// Describes the deterministic outcome of a public store operation.
/// </summary>
public enum StoreStatus
{
    /// <summary>The operation completed successfully.</summary>
    Success = 0,

    /// <summary>The supplied key already identifies a published, pending-removal, or pending-reservation value.</summary>
    DuplicateKey = 1,

    /// <summary>The supplied key does not identify a published value.</summary>
    NotFound = 2,

    /// <summary>The supplied key exceeds the configured maximum key length.</summary>
    KeyTooLarge = 3,

    /// <summary>The supplied value exceeds the configured maximum value length.</summary>
    ValueTooLarge = 4,

    /// <summary>The supplied descriptor exceeds the configured maximum descriptor length.</summary>
    DescriptorTooLarge = 5,

    /// <summary>No reusable slot is currently available.</summary>
    StoreFull = 6,

    /// <summary>No reusable lease record is currently available.</summary>
    LeaseTableFull = 7,

    /// <summary>The supplied lease does not match an active lease record.</summary>
    InvalidLease = 8,

    /// <summary>The lease has already been released.</summary>
    LeaseAlreadyReleased = 9,

    /// <summary>
    /// The key is logically absent, but active leases protect its generation or bounded
    /// post-removal classification and physical reclamation work remains incomplete.
    /// </summary>
    RemovePending = 10,

    /// <summary>The current platform does not support the requested operation.</summary>
    UnsupportedPlatform = 11,

    /// <summary>The store has been disposed.</summary>
    StoreDisposed = 12,

    /// <summary>The store detected an impossible or unsafe shared-memory state.</summary>
    CorruptStore = 13,

    /// <summary>The process does not have sufficient access for the operation.</summary>
    AccessDenied = 14,

    /// <summary>An unexpected runtime failure occurred.</summary>
    UnknownFailure = 15,

    /// <summary>The reservation token does not match a pending slot generation.</summary>
    InvalidReservation = 16,

    /// <summary>The reservation has not advanced exactly the announced payload length.</summary>
    ReservationIncomplete = 17,

    /// <summary>The reservation has already been committed, aborted, disposed, or recovered.</summary>
    ReservationAlreadyCompleted = 18,

    /// <summary>The reservation write progress would move outside the announced payload length.</summary>
    ReservationWriteOutOfRange = 19,

    /// <summary>The supplied key is empty or otherwise invalid.</summary>
    InvalidKey = 20,

    /// <summary>
    /// The operation exhausted its bounded local retry, revalidation, helping, or backoff budget.
    /// </summary>
    StoreBusy = 21,

    /// <summary>The operation was canceled before its documented ordering point.</summary>
    OperationCanceled = 22
}
