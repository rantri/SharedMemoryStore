namespace SharedMemoryStore.Layout;

internal static class LayoutConstants
{
    public const int Magic = 0x31534D53; // SMS1, little-endian.
    public const int LayoutMajorVersion = 1;
    public const int LayoutMinorVersion = 1;
    public const int Alignment = 8;

    public const int StoreInitializing = 0;
    public const int StoreReady = 1;
    public const int StoreDisposing = 2;
    public const int StoreCorrupt = 3;
    public const int StoreUnsupported = 4;

    public const int IndexEmpty = 0;
    public const int IndexOccupied = 1;
    public const int IndexTombstone = 2;

    public const int SlotFree = 0;
    public const int SlotPublishing = 1; // Pending reservation or internal pre-commit publish.
    public const int SlotPublished = 2;
    public const int SlotRemoveRequested = 3;
    public const int SlotReclaiming = 4;

    public const int LeaseFree = 0;
    public const int LeaseActive = 1;
    public const int LeaseReleased = 2;
    public const int LeaseAbandoned = 3;
}
