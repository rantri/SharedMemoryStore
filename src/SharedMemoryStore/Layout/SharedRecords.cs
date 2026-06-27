using System.Runtime.InteropServices;

namespace SharedMemoryStore.Layout;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct StoreHeader
{
    public int Magic;
    public int LayoutMajorVersion;
    public int LayoutMinorVersion;
    public int HeaderLength;
    public long TotalBytes;
    public int SlotCount;
    public int LeaseRecordCount;
    public int MaxKeyBytes;
    public int MaxDescriptorBytes;
    public int MaxValueBytes;
    public int IndexEntryCount;
    public int IndexEntrySize;
    public long IndexOffset;
    public long IndexLength;
    public long LeaseRegistryOffset;
    public long LeaseRegistryLength;
    public long SlotMetadataOffset;
    public long SlotMetadataLength;
    public long DescriptorStorageOffset;
    public long DescriptorStorageLength;
    public long PayloadStorageOffset;
    public long PayloadStorageLength;
    public long StoreId;
    public int StoreState;
    public int Reserved;
    public long Sequence;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SharedIndexEntryHeader
{
    public int State;
    public int KeyLength;
    public ulong KeyHash;
    public int SlotIndex;
    public int SlotGeneration;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SharedSlotMetadata
{
    public int State;
    public int Generation;
    public int UsageCount;
    public int KeyLength;
    public int DescriptorLength;
    public int ValueLength;
    public int PublisherProcessId;
    public int Reserved;
    public ulong KeyHash;
    public long DescriptorOffset;
    public long PayloadOffset;
    public long CommittedSequence;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SharedLeaseRecord
{
    public int State;
    public int LeaseRecordId;
    public int SlotIndex;
    public int SlotGeneration;
    public int OwnerProcessId;
    public int Reserved;
    public long AcquireSequence;
}
