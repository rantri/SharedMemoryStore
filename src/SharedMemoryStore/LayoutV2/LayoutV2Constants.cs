using System.Runtime.InteropServices;

namespace SharedMemoryStore.LayoutV2;

internal static class LayoutV2Constants
{
    public const uint Magic = 0x3253_4D53;
    public const int LayoutMajorVersion = 2;
    public const int LayoutMinorVersion = 0;
    public const int ResourceProtocolVersion = 2;
    public const int HeaderLength = 512;
    public const int AtomicAlignment = 8;
    public const int CacheLineSize = 64;
    public const int ParticipantRecordStride = 64;
    public const int PrimaryDirectoryBucketStride = 128;
    public const int PrimaryLanesPerBucket = 8;
    public const int OverflowBindingStride = 8;
    public const int LeaseRecordStride = 64;
    public const int ValueSlotMetadataStride = 128;
    public const int MaximumSlotCount = 1_048_575;
    public const int MaximumParticipantRecordCount = 1_048_575;
    public const int SlotGenerationBits = 33;
    public const int ParticipantTokenBits = 28;
    public const ulong SpillSummaryVersionedEmptyRequiredFeature = 1UL << 0;
    public const ulong PublicationIntentRequiredFeature = 1UL << 1;
    public const ulong PidNamespaceIdentityRequiredFeature = 1UL << 2;
    public const ulong RequiredFeatures =
        SpillSummaryVersionedEmptyRequiredFeature
        | PublicationIntentRequiredFeature
        | PidNamespaceIdentityRequiredFeature;
    public const ulong OptionalFeatures = 0;

    public const long StoreInitializing = 1;
    public const long StoreReady = 2;
    public const long StoreCorrupt = 3;
    public const long StoreUnsupported = 4;

    public const long PidNamespaceRecoveryEnabled = 1;
    public const long PidNamespaceRecoveryMixed = 2;

    public const int ParticipantFree = 0;
    public const int ParticipantRegistering = 1;
    public const int ParticipantActive = 2;
    public const int ParticipantClosing = 3;
    public const int ParticipantRecovering = 4;
    public const int ParticipantReclaiming = 5;
    public const int ParticipantRetired = 6;

    public const int IdentityUnknown = 0;
    public const int IdentityWindowsProcessCreationFileTime = 1;
    public const int IdentityLinuxProcStartTicks = 2;

    public const int SlotFree = 0;
    public const int LeaseFree = 0;

    public static bool MatchesRequiredFeatures(ulong mappedRequiredFeatures) =>
        mappedRequiredFeatures == RequiredFeatures;

    public static bool IsSupportedArchitecture(Architecture architecture) => architecture == Architecture.X64;
}
