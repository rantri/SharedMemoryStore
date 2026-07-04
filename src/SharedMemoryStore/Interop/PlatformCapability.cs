namespace SharedMemoryStore.Interop;

[Flags]
internal enum PlatformCapability
{
    None = 0,
    SharedRegion = 1 << 0,
    SharedSynchronization = 1 << 1,
    OwnerLiveness = 1 << 2,
    PermissionBoundary = 1 << 3,
    Cleanup = 1 << 4,
    Capacity = 1 << 5
}

internal enum PlatformSupportState
{
    Supported,
    Unsupported,
    Restricted
}

internal readonly record struct PlatformCapabilityStatus(
    PlatformSupportState State,
    PlatformCapability Capabilities,
    StoreOpenStatus OpenStatus);
