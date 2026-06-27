namespace SharedMemoryStore.Layout;

internal readonly struct StoreLayout
{
    public StoreLayout(
        long totalBytes,
        int slotCount,
        int leaseRecordCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes)
    {
        TotalBytes = totalBytes;
        SlotCount = slotCount;
        LeaseRecordCount = leaseRecordCount;
        MaxValueBytes = maxValueBytes;
        MaxDescriptorBytes = maxDescriptorBytes;
        MaxKeyBytes = maxKeyBytes;
        HeaderLength = Align(System.Runtime.InteropServices.Marshal.SizeOf<StoreHeader>());
        IndexEntryCount = NextPowerOfTwo(Math.Max(4, slotCount * 2));
        IndexEntrySize = Align(System.Runtime.InteropServices.Marshal.SizeOf<SharedIndexEntryHeader>() + maxKeyBytes);
        IndexOffset = HeaderLength;
        IndexLength = checked((long)IndexEntryCount * IndexEntrySize);
        LeaseRegistryOffset = Align(IndexOffset + IndexLength);
        LeaseRegistryLength = checked((long)leaseRecordCount * System.Runtime.InteropServices.Marshal.SizeOf<SharedLeaseRecord>());
        SlotMetadataOffset = Align(LeaseRegistryOffset + LeaseRegistryLength);
        SlotMetadataLength = checked((long)slotCount * System.Runtime.InteropServices.Marshal.SizeOf<SharedSlotMetadata>());
        DescriptorStride = Align(Math.Max(1, maxDescriptorBytes));
        DescriptorStorageOffset = Align(SlotMetadataOffset + SlotMetadataLength);
        DescriptorStorageLength = checked((long)slotCount * DescriptorStride);
        PayloadStride = Align(Math.Max(1, maxValueBytes));
        PayloadStorageOffset = Align(DescriptorStorageOffset + DescriptorStorageLength);
        PayloadStorageLength = checked((long)slotCount * PayloadStride);
        RequiredBytes = Align(PayloadStorageOffset + PayloadStorageLength);
    }

    public long TotalBytes { get; }
    public int SlotCount { get; }
    public int LeaseRecordCount { get; }
    public int MaxValueBytes { get; }
    public int MaxDescriptorBytes { get; }
    public int MaxKeyBytes { get; }
    public int HeaderLength { get; }
    public int IndexEntryCount { get; }
    public int IndexEntrySize { get; }
    public long IndexOffset { get; }
    public long IndexLength { get; }
    public long LeaseRegistryOffset { get; }
    public long LeaseRegistryLength { get; }
    public long SlotMetadataOffset { get; }
    public long SlotMetadataLength { get; }
    public int DescriptorStride { get; }
    public long DescriptorStorageOffset { get; }
    public long DescriptorStorageLength { get; }
    public int PayloadStride { get; }
    public long PayloadStorageOffset { get; }
    public long PayloadStorageLength { get; }
    public long RequiredBytes { get; }

    public static long CalculateRequiredBytes(
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount)
    {
        var layout = new StoreLayout(
            0,
            slotCount,
            leaseRecordCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes);
        return layout.RequiredBytes;
    }

    public static StoreLayout FromOptions(SharedMemoryStoreOptions options)
    {
        return new StoreLayout(
            options.TotalBytes,
            options.SlotCount,
            options.LeaseRecordCount,
            options.MaxValueBytes,
            options.MaxDescriptorBytes,
            options.MaxKeyBytes);
    }

    public static StoreLayout FromHeader(in StoreHeader header)
    {
        return new StoreLayout(
            header.TotalBytes,
            header.SlotCount,
            header.LeaseRecordCount,
            header.MaxValueBytes,
            header.MaxDescriptorBytes,
            header.MaxKeyBytes);
    }

    public bool MatchesHeader(in StoreHeader header)
    {
        return header.HeaderLength == HeaderLength
            && header.TotalBytes == TotalBytes
            && header.SlotCount == SlotCount
            && header.LeaseRecordCount == LeaseRecordCount
            && header.MaxKeyBytes == MaxKeyBytes
            && header.MaxDescriptorBytes == MaxDescriptorBytes
            && header.MaxValueBytes == MaxValueBytes
            && header.IndexEntryCount == IndexEntryCount
            && header.IndexEntrySize == IndexEntrySize
            && header.IndexOffset == IndexOffset
            && header.IndexLength == IndexLength
            && header.LeaseRegistryOffset == LeaseRegistryOffset
            && header.LeaseRegistryLength == LeaseRegistryLength
            && header.SlotMetadataOffset == SlotMetadataOffset
            && header.SlotMetadataLength == SlotMetadataLength
            && header.DescriptorStorageOffset == DescriptorStorageOffset
            && header.DescriptorStorageLength == DescriptorStorageLength
            && header.PayloadStorageOffset == PayloadStorageOffset
            && header.PayloadStorageLength == PayloadStorageLength;
    }

    public bool FitsWithinTotalBytes()
    {
        return TotalBytes >= RequiredBytes
            && TotalBytes > 0
            && PayloadStorageOffset >= 0
            && PayloadStorageOffset + PayloadStorageLength <= TotalBytes;
    }

    public static int Align(int value)
    {
        return (value + LayoutConstants.Alignment - 1) & ~(LayoutConstants.Alignment - 1);
    }

    public static long Align(long value)
    {
        return (value + LayoutConstants.Alignment - 1) & ~(LayoutConstants.Alignment - 1);
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}
