namespace SharedMemoryStore.Interop;

/// <summary>
/// Identifies whether the current cold-open attempt created the physical
/// mapping or attached to a mapping that was already owned by another handle.
/// Only the physical creator is allowed to initialize an unpublished header.
/// </summary>
internal enum RegionOpenDisposition
{
    CreatedNew,
    OpenedExisting
}
