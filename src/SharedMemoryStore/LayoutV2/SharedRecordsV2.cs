using System.Runtime.InteropServices;

namespace SharedMemoryStore.LayoutV2;

[StructLayout(LayoutKind.Explicit, Size = LayoutV2Constants.ParticipantRecordStride)]
internal struct ParticipantRecordV2
{
    [FieldOffset(0)] public long Control;
    [FieldOffset(8)] public int IdentityKind;
    [FieldOffset(12)] public int Reserved;
    [FieldOffset(16)] public long ProcessStartValue;
    [FieldOffset(24)] public long OpenSequence;
    [FieldOffset(32)] public ulong PidNamespaceId;
}

[StructLayout(LayoutKind.Explicit, Size = LayoutV2Constants.PrimaryDirectoryBucketStride)]
internal unsafe struct PrimaryDirectoryBucketV2
{
    [FieldOffset(0)] public long SpillSummary;
    [FieldOffset(8)] public long Mutation;
    [FieldOffset(16)] public fixed long Lanes[LayoutV2Constants.PrimaryLanesPerBucket];
}

[StructLayout(LayoutKind.Explicit, Size = LayoutV2Constants.LeaseRecordStride)]
internal struct LeaseRecordV2
{
    [FieldOffset(0)] public long Control;
    [FieldOffset(8)] public ulong SlotBinding;
    [FieldOffset(16)] public long AcquireSequence;
}

[StructLayout(LayoutKind.Explicit, Size = LayoutV2Constants.ValueSlotMetadataStride)]
internal struct ValueSlotMetadataV2
{
    [FieldOffset(0)] public long Control;
    [FieldOffset(8)] public ulong DirectoryBinding;
    [FieldOffset(16)] public long DirectoryLocation;
    [FieldOffset(24)] public long DirectoryOperation;
    [FieldOffset(32)] public ulong KeyHash;
    [FieldOffset(40)] public int KeyLength;
    [FieldOffset(44)] public int DescriptorLength;
    [FieldOffset(48)] public int ValueLength;
    [FieldOffset(52)] public int PublicationIntent;
    [FieldOffset(56)] public long BytesAdvanced;
    [FieldOffset(64)] public long CommitSequence;
    [FieldOffset(72)] public long KeyOffset;
    [FieldOffset(80)] public long DescriptorOffset;
    [FieldOffset(88)] public long PayloadOffset;
}

internal enum SlotPublicationIntent
{
    None = 0,
    ExplicitReservation = 1,
    AtomicPublication = 2,
}

[StructLayout(LayoutKind.Explicit, Size = LayoutV2Constants.HeaderLength)]
internal struct StoreHeaderV2
{
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ushort LayoutMajorVersion;
    [FieldOffset(6)] public ushort LayoutMinorVersion;
    [FieldOffset(8)] public int HeaderLength;
    [FieldOffset(12)] public int ResourceProtocolVersion;
    [FieldOffset(16)] public ulong RequiredFeatures;
    [FieldOffset(24)] public ulong OptionalFeatures;
    [FieldOffset(32)] public long TotalBytes;
    [FieldOffset(40)] public ulong StoreId;
    [FieldOffset(48)] public long Control;
    [FieldOffset(56)] public long Sequence;
    [FieldOffset(64)] public int SlotCount;
    [FieldOffset(68)] public int LeaseRecordCount;
    [FieldOffset(72)] public int ParticipantRecordCount;
    [FieldOffset(76)] public int MaxKeyBytes;
    [FieldOffset(80)] public int MaxDescriptorBytes;
    [FieldOffset(84)] public int MaxValueBytes;
    [FieldOffset(88)] public int ParticipantIndexBits;
    [FieldOffset(92)] public int ParticipantGenerationBits;
    [FieldOffset(96)] public long ParticipantOffset;
    [FieldOffset(104)] public long ParticipantLength;
    [FieldOffset(112)] public int ParticipantStride;
    [FieldOffset(116)] public int PrimaryLaneCount;
    [FieldOffset(120)] public int PrimaryBucketCount;
    [FieldOffset(124)] public int PrimaryBucketStride;
    [FieldOffset(128)] public long PrimaryDirectoryOffset;
    [FieldOffset(136)] public long PrimaryDirectoryLength;
    [FieldOffset(144)] public long OverflowDirectoryOffset;
    [FieldOffset(152)] public long OverflowDirectoryLength;
    [FieldOffset(160)] public int OverflowStride;
    [FieldOffset(164)] public int LeaseStride;
    [FieldOffset(168)] public long LeaseRegistryOffset;
    [FieldOffset(176)] public long LeaseRegistryLength;
    [FieldOffset(184)] public int SlotMetadataStride;
    [FieldOffset(188)] public int KeyStride;
    [FieldOffset(192)] public long SlotMetadataOffset;
    [FieldOffset(200)] public long SlotMetadataLength;
    [FieldOffset(208)] public long KeyStorageOffset;
    [FieldOffset(216)] public long KeyStorageLength;
    [FieldOffset(224)] public int DescriptorStride;
    [FieldOffset(228)] public int PayloadStride;
    [FieldOffset(232)] public long DescriptorStorageOffset;
    [FieldOffset(240)] public long DescriptorStorageLength;
    [FieldOffset(248)] public long PayloadStorageOffset;
    [FieldOffset(256)] public long PayloadStorageLength;
    [FieldOffset(264)] public ulong PidNamespaceId;
    [FieldOffset(272)] public long PidNamespaceMode;
}
