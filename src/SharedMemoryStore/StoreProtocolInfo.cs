namespace SharedMemoryStore;

/// <summary>
/// Describes the persisted layout and shared-resource protocol used by a store handle. These
/// protocol versions are independent of the NuGet package version and must be checked separately.
/// </summary>
/// <param name="Profile">The selected store profile.</param>
/// <param name="LayoutMajorVersion">The persisted layout major version.</param>
/// <param name="LayoutMinorVersion">The persisted layout minor version.</param>
/// <param name="ResourceProtocolVersion">The version of the named-resource protocol.</param>
/// <param name="RequiredFeatures">Feature bits every compatible opener must understand.</param>
/// <param name="OptionalFeatures">Feature bits a compatible opener may safely ignore.</param>
public readonly record struct StoreProtocolInfo(
    StoreProfile Profile,
    int LayoutMajorVersion,
    int LayoutMinorVersion,
    int ResourceProtocolVersion,
    ulong RequiredFeatures,
    ulong OptionalFeatures);
