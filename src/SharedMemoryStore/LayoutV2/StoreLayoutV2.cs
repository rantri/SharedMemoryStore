namespace SharedMemoryStore.LayoutV2;

internal readonly struct StoreLayoutV2
{
    public StoreLayoutV2(
        long totalBytes,
        int slotCount,
        int leaseRecordCount,
        int participantRecordCount,
        int maxKeyBytes,
        int maxDescriptorBytes,
        int maxValueBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotCount, LayoutV2Constants.MaximumSlotCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseRecordCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(participantRecordCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(participantRecordCount, LayoutV2Constants.MaximumParticipantRecordCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeyBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDescriptorBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueBytes, 1);

        checked
        {
            TotalBytes = totalBytes;
            SlotCount = slotCount;
            LeaseRecordCount = leaseRecordCount;
            ParticipantRecordCount = participantRecordCount;
            MaxKeyBytes = maxKeyBytes;
            MaxDescriptorBytes = maxDescriptorBytes;
            MaxValueBytes = maxValueBytes;
            HeaderLength = LayoutV2Constants.HeaderLength;

            ParticipantIndexBits = RequiredBits(participantRecordCount + 1);
            ParticipantGenerationBits = LayoutV2Constants.ParticipantTokenBits - ParticipantIndexBits;
            if (ParticipantGenerationBits < 8)
            {
                throw new ArgumentOutOfRangeException(nameof(participantRecordCount));
            }

            ParticipantIndexMask = (1 << ParticipantIndexBits) - 1;
            ParticipantGenerationMask = (1 << ParticipantGenerationBits) - 1;

            ParticipantStride = LayoutV2Constants.ParticipantRecordStride;
            ParticipantOffset = HeaderLength;
            ParticipantLength = (long)participantRecordCount * ParticipantStride;

            PrimaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
            PrimaryBucketCount = PrimaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket;
            PrimaryBucketStride = LayoutV2Constants.PrimaryDirectoryBucketStride;
            PrimaryDirectoryOffset = Align64(ParticipantOffset + ParticipantLength);
            PrimaryDirectoryLength = (long)PrimaryBucketCount * PrimaryBucketStride;

            OverflowStride = LayoutV2Constants.OverflowBindingStride;
            OverflowDirectoryOffset = Align8(PrimaryDirectoryOffset + PrimaryDirectoryLength);
            OverflowDirectoryLength = (long)slotCount * OverflowStride;

            LeaseStride = LayoutV2Constants.LeaseRecordStride;
            LeaseRegistryOffset = Align64(OverflowDirectoryOffset + OverflowDirectoryLength);
            LeaseRegistryLength = (long)leaseRecordCount * LeaseStride;

            SlotMetadataStride = LayoutV2Constants.ValueSlotMetadataStride;
            SlotMetadataOffset = Align64(LeaseRegistryOffset + LeaseRegistryLength);
            SlotMetadataLength = (long)slotCount * SlotMetadataStride;

            KeyStride = checked((int)Align8(Math.Max(1, maxKeyBytes)));
            KeyStorageOffset = Align8(SlotMetadataOffset + SlotMetadataLength);
            KeyStorageLength = (long)slotCount * KeyStride;

            DescriptorStride = checked((int)Align8(Math.Max(1, maxDescriptorBytes)));
            DescriptorStorageOffset = Align8(KeyStorageOffset + KeyStorageLength);
            DescriptorStorageLength = (long)slotCount * DescriptorStride;

            PayloadStride = checked((int)Align8(Math.Max(1, maxValueBytes)));
            PayloadStorageOffset = Align8(DescriptorStorageOffset + DescriptorStorageLength);
            PayloadStorageLength = (long)slotCount * PayloadStride;
            RequiredBytes = Align8(PayloadStorageOffset + PayloadStorageLength);
        }
    }

    public long TotalBytes { get; }
    public int SlotCount { get; }
    public int LeaseRecordCount { get; }
    public int ParticipantRecordCount { get; }
    public int MaxKeyBytes { get; }
    public int MaxDescriptorBytes { get; }
    public int MaxValueBytes { get; }
    public int HeaderLength { get; }
    public int ParticipantIndexBits { get; }
    public int ParticipantGenerationBits { get; }
    public int ParticipantIndexMask { get; }
    public int ParticipantGenerationMask { get; }
    public int ParticipantStride { get; }
    public long ParticipantOffset { get; }
    public long ParticipantLength { get; }
    public int PrimaryLaneCount { get; }
    public int PrimaryBucketCount { get; }
    public int BucketCount => PrimaryBucketCount;
    public int PrimaryBucketStride { get; }
    public long PrimaryDirectoryOffset { get; }
    public long PrimaryDirectoryLength { get; }
    public int OverflowStride { get; }
    public long OverflowDirectoryOffset { get; }
    public long OverflowDirectoryLength { get; }
    public int LeaseStride { get; }
    public long LeaseRegistryOffset { get; }
    public long LeaseRegistryLength { get; }
    public int SlotMetadataStride { get; }
    public long SlotMetadataOffset { get; }
    public long SlotMetadataLength { get; }
    public int KeyStride { get; }
    public long KeyStorageOffset { get; }
    public long KeyStorageLength { get; }
    public int DescriptorStride { get; }
    public long DescriptorStorageOffset { get; }
    public long DescriptorStorageLength { get; }
    public int PayloadStride { get; }
    public long PayloadStorageOffset { get; }
    public long PayloadStorageLength { get; }
    public long RequiredBytes { get; }

    public bool FitsWithinTotalBytes() => TotalBytes >= RequiredBytes && TotalBytes > 0;

    public bool MatchesHeader(in StoreHeaderV2 header)
    {
        return header.Magic == LayoutV2Constants.Magic
            && header.LayoutMajorVersion == LayoutV2Constants.LayoutMajorVersion
            && header.LayoutMinorVersion == LayoutV2Constants.LayoutMinorVersion
            && header.HeaderLength == HeaderLength
            && header.ResourceProtocolVersion == LayoutV2Constants.ResourceProtocolVersion
            && LayoutV2Constants.MatchesRequiredFeatures(header.RequiredFeatures)
            && header.TotalBytes == TotalBytes
            && header.StoreId != 0
            && header.SlotCount == SlotCount
            && header.LeaseRecordCount == LeaseRecordCount
            && header.ParticipantRecordCount == ParticipantRecordCount
            && header.MaxKeyBytes == MaxKeyBytes
            && header.MaxDescriptorBytes == MaxDescriptorBytes
            && header.MaxValueBytes == MaxValueBytes
            && header.ParticipantIndexBits == ParticipantIndexBits
            && header.ParticipantGenerationBits == ParticipantGenerationBits
            && header.ParticipantOffset == ParticipantOffset
            && header.ParticipantLength == ParticipantLength
            && header.ParticipantStride == ParticipantStride
            && header.PrimaryLaneCount == PrimaryLaneCount
            && header.PrimaryBucketCount == PrimaryBucketCount
            && header.PrimaryBucketStride == PrimaryBucketStride
            && header.PrimaryDirectoryOffset == PrimaryDirectoryOffset
            && header.PrimaryDirectoryLength == PrimaryDirectoryLength
            && header.OverflowDirectoryOffset == OverflowDirectoryOffset
            && header.OverflowDirectoryLength == OverflowDirectoryLength
            && header.OverflowStride == OverflowStride
            && header.LeaseStride == LeaseStride
            && header.LeaseRegistryOffset == LeaseRegistryOffset
            && header.LeaseRegistryLength == LeaseRegistryLength
            && header.SlotMetadataStride == SlotMetadataStride
            && header.KeyStride == KeyStride
            && header.SlotMetadataOffset == SlotMetadataOffset
            && header.SlotMetadataLength == SlotMetadataLength
            && header.KeyStorageOffset == KeyStorageOffset
            && header.KeyStorageLength == KeyStorageLength
            && header.DescriptorStride == DescriptorStride
            && header.PayloadStride == PayloadStride
            && header.DescriptorStorageOffset == DescriptorStorageOffset
            && header.DescriptorStorageLength == DescriptorStorageLength
            && header.PayloadStorageOffset == PayloadStorageOffset
            && header.PayloadStorageLength == PayloadStorageLength;
    }

    public static long CalculateRequiredBytes(
        int slotCount,
        int maxValueBytes,
        int maxDescriptorBytes,
        int maxKeyBytes,
        int leaseRecordCount,
        int participantRecordCount = 64)
    {
        return new StoreLayoutV2(
            0,
            slotCount,
            leaseRecordCount,
            participantRecordCount,
            maxKeyBytes,
            maxDescriptorBytes,
            maxValueBytes).RequiredBytes;
    }

    public static StoreLayoutV2 FromOptions(SharedMemoryStoreOptions options)
    {
        return new StoreLayoutV2(
            options.TotalBytes,
            options.SlotCount,
            options.LeaseRecordCount,
            options.ParticipantRecordCount,
            options.MaxKeyBytes,
            options.MaxDescriptorBytes,
            options.MaxValueBytes);
    }

    private static int RequiredBits(int distinctValues)
    {
        var bits = 0;
        var value = distinctValues - 1;
        do
        {
            bits++;
            value >>= 1;
        }
        while (value != 0);

        return bits;
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 0 || value > 1 << 30)
        {
            throw new OverflowException("The requested primary directory cannot be represented.");
        }

        var result = 1;
        while (result < value)
        {
            result = checked(result << 1);
        }

        return result;
    }

    private static long Align8(long value) => checked(value + 7) & ~7L;

    private static long Align64(long value) => checked(value + 63) & ~63L;
}
