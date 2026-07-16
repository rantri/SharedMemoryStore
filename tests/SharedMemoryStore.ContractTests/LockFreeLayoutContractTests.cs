using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharedMemoryStore.ContractTests;

/// <summary>
/// Freezes the language-neutral layout-2.0 wire contract without taking a
/// compile-time dependency on the implementation types. The reflection is
/// intentional: this test is introduced before the v2 layout and must compile
/// while failing specifically because that layout is not implemented yet.
/// </summary>
public sealed class LockFreeLayoutContractTests
{
    private const long MaximumDirectoryTargetIndex = (1L << 22) - 1;
    private const long MaximumSlotGeneration = (1L << 33) - 1;
    private const int MaximumLockFreeSlotCount = 1_048_575;
    private static readonly Assembly StoreAssembly = typeof(MemoryStore).Assembly;

    [Fact]
    public void PublishedV2ManifestMatchesTheExecutableRecordContract()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharedMemoryStore.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "protocol", "fixtures", "v2.0", "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var protocol = document.RootElement.GetProperty("protocol");
        var records = document.RootElement.GetProperty("records");
        var directoryLocation = document.RootElement.GetProperty("directory_location");
        var directoryOperation = document.RootElement.GetProperty("directory_operation");
        var publicationIntent = document.RootElement.GetProperty("publication_intent");
        var pidNamespaceIdentity = document.RootElement.GetProperty("pid_namespace_identity");
        var sizing = document.RootElement.GetProperty("sizing");

        Assert.Equal(2, protocol.GetProperty("layout_major").GetInt32());
        Assert.Equal(0, protocol.GetProperty("layout_minor").GetInt32());
        Assert.Equal(2, protocol.GetProperty("resource_protocol").GetInt32());
        Assert.Equal(7UL, protocol.GetProperty("required_features").GetUInt64());
        Assert.Equal(
            1UL,
            protocol.GetProperty("required_feature_bits")
                .GetProperty("versioned_empty_spill_summary")
                .GetUInt64());
        Assert.Equal(
            2UL,
            protocol.GetProperty("required_feature_bits")
                .GetProperty("publication_intent")
                .GetUInt64());
        Assert.Equal(
            4UL,
            protocol.GetProperty("required_feature_bits")
                .GetProperty("pid_namespace_identity")
                .GetUInt64());
        Assert.Equal("32534d53", protocol.GetProperty("magic_integer_hex").GetString());
        Assert.Equal(512, records.GetProperty("store_header").GetProperty("size").GetInt32());
        Assert.Equal(
            264,
            records.GetProperty("store_header")
                .GetProperty("fields")
                .GetProperty("pid_namespace_id")
                .GetInt32());
        Assert.Equal(
            272,
            records.GetProperty("store_header")
                .GetProperty("fields")
                .GetProperty("pid_namespace_mode")
                .GetInt32());
        Assert.Equal(64, records.GetProperty("participant").GetProperty("size").GetInt32());
        Assert.Equal(
            32,
            records.GetProperty("participant")
                .GetProperty("fields")
                .GetProperty("pid_namespace_id")
                .GetInt32());
        Assert.Equal(128, records.GetProperty("primary_directory_bucket").GetProperty("size").GetInt32());
        Assert.Equal(64, records.GetProperty("lease").GetProperty("size").GetInt32());
        Assert.Equal(128, records.GetProperty("value_slot").GetProperty("size").GetInt32());
        Assert.Equal(
            52,
            records.GetProperty("value_slot")
                .GetProperty("fields")
                .GetProperty("publication_intent")
                .GetInt32());

        Assert.Equal(52, publicationIntent.GetProperty("field_offset").GetInt32());
        Assert.Equal(0, publicationIntent.GetProperty("values").GetProperty("none").GetInt32());
        Assert.Equal(
            1,
            publicationIntent.GetProperty("values").GetProperty("explicit_reservation").GetInt32());
        Assert.Equal(
            2,
            publicationIntent.GetProperty("values").GetProperty("atomic_publication").GetInt32());
        Assert.Equal(264, pidNamespaceIdentity.GetProperty("header_id_offset").GetInt32());
        Assert.Equal(272, pidNamespaceIdentity.GetProperty("header_mode_offset").GetInt32());
        Assert.Equal(32, pidNamespaceIdentity.GetProperty("participant_id_offset").GetInt32());
        Assert.Equal(
            1,
            pidNamespaceIdentity.GetProperty("modes").GetProperty("recovery_enabled").GetInt32());
        Assert.Equal(
            2,
            pidNamespaceIdentity.GetProperty("modes").GetProperty("mixed_or_unproven").GetInt32());

        AssertJsonBitRange(directoryLocation, "kind_bits", 0, 1);
        AssertJsonBitRange(directoryLocation, "index_bits", 2, 23);
        AssertJsonBitRange(directoryLocation, "slot_generation_bits", 24, 56);
        AssertJsonBitRange(directoryLocation, "reserved_bits", 57, 63);

        AssertJsonBitRange(directoryOperation, "intent_bits", 0, 1);
        AssertJsonBitRange(directoryOperation, "phase_bits", 2, 4);
        AssertJsonBitRange(directoryOperation, "target_kind_bits", 5, 6);
        AssertJsonBitRange(directoryOperation, "target_index_bits", 7, 28);
        AssertJsonBitRange(directoryOperation, "slot_generation_bits", 29, 61);
        AssertJsonBitRange(directoryOperation, "reserved_bits", 62, 63);

        Assert.Equal(1, sizing.GetProperty("slot_count_min").GetInt32());
        Assert.Equal(MaximumLockFreeSlotCount, sizing.GetProperty("slot_count_max").GetInt32());
    }

    [Theory]
    [InlineData(64, 7, 21, 0x7f, 0x1f_ffff)]
    [InlineData(1, 1, 27, 0x1, 0x7ff_ffff)]
    [InlineData(1_048_575, 20, 8, 0x0f_ffff, 0xff)]
    public void ParticipantTokenSplitAndMasksAreDerivedFromConfiguredCapacity(
        int participantCount,
        int expectedIndexBits,
        int expectedGenerationBits,
        int expectedIndexMask,
        int expectedGenerationMask)
    {
        object layout = CreateLayout(participantCount: participantCount);

        Assert.Equal(expectedIndexBits, GetInt64(layout, "ParticipantIndexBits", "ParticipantTokenIndexBits"));
        Assert.Equal(expectedGenerationBits, GetInt64(layout, "ParticipantGenerationBits", "ParticipantTokenGenerationBits"));
        Assert.Equal(expectedIndexMask, GetInt64(layout, "ParticipantIndexMask", "ParticipantTokenIndexMask"));
        Assert.Equal(expectedGenerationMask, GetInt64(layout, "ParticipantGenerationMask", "ParticipantTokenGenerationMask"));
    }

    [Fact]
    public void DefaultParticipantTokenEncodesIndexPlusOneAndRetiresAtConfiguredTerminalGeneration()
    {
        object layout = CreateLayout(participantCount: 64);
        Type tokenType = RequireType("SharedMemoryStore.LockFree.ParticipantToken");
        const int recordIndex = 63;
        const int terminalGeneration = 0x1f_ffff;

        ulong encoded = Encode(
            tokenType,
            new Dictionary<string, object>
            {
                ["recordIndex"] = recordIndex,
                ["index"] = recordIndex,
                ["generation"] = terminalGeneration,
                ["incarnation"] = terminalGeneration,
                ["participantCount"] = 64,
                ["indexBits"] = 7
            });

        Assert.Equal(((ulong)terminalGeneration << 7) | 64UL, encoded);
        Assert.Equal(terminalGeneration, GetInt64(layout, "ParticipantGenerationMask", "ParticipantTokenGenerationMask"));

        Exception error = Assert.ThrowsAny<Exception>(() => Encode(
            tokenType,
            new Dictionary<string, object>
            {
                ["recordIndex"] = recordIndex,
                ["index"] = recordIndex,
                ["generation"] = terminalGeneration + 1,
                ["incarnation"] = terminalGeneration + 1,
                ["participantCount"] = 64,
                ["indexBits"] = 7
            }));

        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(Unwrap(error));
    }

    [Theory]
    [InlineData(0, 1, 0x0000_0000_8000_0001UL)]
    [InlineData(5, 9, 0x0000_0004_8000_0006UL)]
    [InlineData(2_147_483_646, 8_589_934_591, ulong.MaxValue)]
    public void IndexBindingCodecUsesThirtyOneIndexBitsAndThirtyThreeGenerationBits(
        int slotIndex,
        long generation,
        ulong expected)
    {
        Type bindingType = RequireType("SharedMemoryStore.LockFree.IndexBinding");

        ulong encoded = Encode(
            bindingType,
            new Dictionary<string, object>
            {
                ["slotIndex"] = slotIndex,
                ["index"] = slotIndex,
                ["generation"] = generation
            });

        Assert.Equal(expected, encoded);
        AssertDecoded(bindingType, encoded, ("slotIndex", slotIndex), ("generation", generation));
    }

    [Theory]
    [InlineData(0, 1, 0x0020_0000_0010_0001UL, 0x0000_0000_0010_0001UL)]
    [InlineData(5, 9, 0x0020_0000_0090_0006UL, 0x0000_0000_0090_0006UL)]
    [InlineData(1_048_574, 8_589_934_591, 0x003f_ffff_ffff_ffffUL, 0x001f_ffff_ffff_ffffUL)]
    public void SpillSummaryCodecCarriesTwentyBitIndexThirtyThreeBitGenerationAndVersionedEmpty(
        int slotIndex,
        long generation,
        ulong expectedPresent,
        ulong expectedEmpty)
    {
        Type bindingType = RequireType("SharedMemoryStore.LockFree.IndexBinding");
        Type summaryType = RequireType("SharedMemoryStore.LockFree.SpillSummary");
        ulong binding = Encode(
            bindingType,
            new Dictionary<string, object>
            {
                ["slotIndex"] = slotIndex,
                ["index"] = slotIndex,
                ["generation"] = generation
            });
        var values = new Dictionary<string, object> { ["binding"] = binding };
        ulong present = EncodeNamed(summaryType, "Present", values);
        ulong empty = EncodeNamed(summaryType, "Empty", values);
        object presentDecoded = Decode(summaryType, present);
        object emptyDecoded = Decode(summaryType, empty);

        Assert.Equal(expectedPresent, present);
        Assert.Equal(expectedEmpty, empty);
        Assert.Equal(1, GetInt64(presentDecoded, "IsPresent"));
        Assert.Equal(0, GetInt64(emptyDecoded, "IsPresent"));
        Assert.Equal(binding, GetUInt64(presentDecoded, "Binding"));
        Assert.Equal(binding, GetUInt64(emptyDecoded, "Binding"));
        Assert.Equal(empty, GetUInt64(presentDecoded, "EmptyValue"));
    }

    [Fact]
    public void SpillSummaryRejectsMalformedAndReservedTokensAndNeverReturnsToInitialZero()
    {
        Type bindingType = RequireType("SharedMemoryStore.LockFree.IndexBinding");
        Type summaryType = RequireType("SharedMemoryStore.LockFree.SpillSummary");
        ulong firstBinding = Encode(
            bindingType,
            new Dictionary<string, object> { ["slotIndex"] = 0, ["index"] = 0, ["generation"] = 1L });
        ulong laterBinding = Encode(
            bindingType,
            new Dictionary<string, object> { ["slotIndex"] = 0, ["index"] = 0, ["generation"] = 2L });
        ulong presentFirst = EncodeNamed(
            summaryType,
            "Present",
            new Dictionary<string, object> { ["binding"] = firstBinding });
        ulong emptyFirst = GetUInt64(Decode(summaryType, presentFirst), "EmptyValue");
        ulong presentLater = EncodeNamed(
            summaryType,
            "Present",
            new Dictionary<string, object> { ["binding"] = laterBinding });

        Assert.Equal(0UL, GetUInt64(Decode(summaryType, 0), "Value"));
        Assert.NotEqual(0UL, emptyFirst);
        Assert.Equal(3, new HashSet<ulong> { presentFirst, emptyFirst, presentLater }.Count);
        AssertDecodeRejected(summaryType, 1UL << 53);
        AssertDecodeRejected(summaryType, (1UL << 53) | 1UL);
        AssertDecodeRejected(summaryType, presentFirst | (1UL << 54));

        ulong outOfRangeBinding = Encode(
            bindingType,
            new Dictionary<string, object>
            {
                ["slotIndex"] = 1_048_575,
                ["index"] = 1_048_575,
                ["generation"] = 1L
            });
        Exception encodeError = Assert.ThrowsAny<Exception>(() => EncodeNamed(
            summaryType,
            "Present",
            new Dictionary<string, object> { ["binding"] = outOfRangeBinding }));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(Unwrap(encodeError));
    }

    [Fact]
    public void RequiredFeatureMaskFencesEveryOlderV2DraftBothWays()
    {
        const ulong zeroFeatureDraft = 0;
        const ulong spillOnlyDraft = 1;
        Type constantsType = RequireType("SharedMemoryStore.LayoutV2.LayoutV2Constants");
        ulong newRequiredFeatures = GetStaticUInt64(constantsType, "RequiredFeatures");
        ulong spillFeature = GetStaticUInt64(
            constantsType,
            "SpillSummaryVersionedEmptyRequiredFeature");
        ulong publicationIntentFeature = GetStaticUInt64(
            constantsType,
            "PublicationIntentRequiredFeature");
        ulong pidNamespaceFeature = GetStaticUInt64(
            constantsType,
            "PidNamespaceIdentityRequiredFeature");
        MethodInfo matches = constantsType.GetMethod(
            "MatchesRequiredFeatures",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException(
                "LayoutV2Constants.MatchesRequiredFeatures is absent.");

        Assert.Equal(1UL, spillFeature);
        Assert.Equal(2UL, publicationIntentFeature);
        Assert.Equal(4UL, pidNamespaceFeature);
        Assert.Equal(
            spillFeature | publicationIntentFeature | pidNamespaceFeature,
            newRequiredFeatures);
        Assert.Equal(7UL, newRequiredFeatures);
        Assert.True((bool)matches.Invoke(null, [newRequiredFeatures])!);
        Assert.False((bool)matches.Invoke(null, [zeroFeatureDraft])!);
        Assert.False((bool)matches.Invoke(null, [spillOnlyDraft])!);
        Assert.False((bool)matches.Invoke(null, [publicationIntentFeature])!);
        Assert.False((bool)matches.Invoke(null, [spillFeature | publicationIntentFeature])!);
        Assert.NotEqual(0UL, newRequiredFeatures & ~spillOnlyDraft);
    }

    [Fact]
    public void PublicationIntentEnumAssignmentsAreWireStable()
    {
        Type intentType = RequireType("SharedMemoryStore.LayoutV2.SlotPublicationIntent");

        Assert.True(intentType.IsEnum);
        Assert.Equal(0, Convert.ToInt32(Enum.Parse(intentType, "None")));
        Assert.Equal(1, Convert.ToInt32(Enum.Parse(intentType, "ExplicitReservation")));
        Assert.Equal(2, Convert.ToInt32(Enum.Parse(intentType, "AtomicPublication")));
    }

    [Theory]
    [InlineData(1, 0, 1, 0x0000_0000_0100_0001UL)]
    [InlineData(2, 17, 9, 0x0000_0000_0900_0046UL)]
    [InlineData(2, MaximumDirectoryTargetIndex, MaximumSlotGeneration, 0x01ff_ffff_ffff_fffeUL)]
    public void DirectoryLocationCodecUsesKindTargetIndexAndExactSlotGeneration(
        int kind,
        long cellIndex,
        long generation,
        ulong expected)
    {
        Type locationType = RequireType("SharedMemoryStore.LockFree.DirectoryLocation");

        ulong encoded = Encode(
            locationType,
            new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["targetKind"] = kind,
                ["index"] = cellIndex,
                ["cellIndex"] = cellIndex,
                ["targetIndex"] = cellIndex,
                ["generation"] = generation,
                ["slotGeneration"] = generation
            });

        Assert.Equal(expected, encoded);
        AssertDecoded(
            locationType,
            encoded,
            ("kind", kind),
            ("index", cellIndex),
            ("generation", generation));
    }

    [Theory]
    [InlineData(1, 1, 0, 0, 1, 0x0000_0000_2000_0005UL)]
    [InlineData(1, 4, 0, 0, 9, 0x0000_0001_2000_0011UL)]
    [InlineData(2, 3, 1, 4, 5, 0x0000_0000_a000_022eUL)]
    [InlineData(2, 5, 2, MaximumDirectoryTargetIndex, MaximumSlotGeneration, 0x3fff_ffff_ffff_ffd6UL)]
    public void DirectoryOperationCodecUsesIntentPhaseTargetAndExactSlotGeneration(
        int intent,
        int phase,
        int targetKind,
        long targetIndex,
        long generation,
        ulong expected)
    {
        Type operationType = RequireType("SharedMemoryStore.LockFree.DirectoryOperation");

        ulong encoded = Encode(
            operationType,
            new Dictionary<string, object>
            {
                ["intent"] = intent,
                ["phase"] = phase,
                ["kind"] = targetKind,
                ["targetKind"] = targetKind,
                ["index"] = targetIndex,
                ["cellIndex"] = targetIndex,
                ["targetIndex"] = targetIndex,
                ["generation"] = generation,
                ["slotGeneration"] = generation
            });

        Assert.Equal(expected, encoded);
        AssertDecoded(
            operationType,
            encoded,
            ("intent", intent),
            ("phase", phase),
            ("kind", targetKind),
            ("index", targetIndex),
            ("generation", generation));
    }

    [Fact]
    public void DirectoryLocationRejectsZeroOrOverflowGenerationOutOfRangeTargetAndEveryReservedBit()
    {
        Type locationType = RequireType("SharedMemoryStore.LockFree.DirectoryLocation");

        AssertEncodeRejected(
            locationType,
            new Dictionary<string, object>
            {
                ["kind"] = 1,
                ["index"] = 0L,
                ["generation"] = 0L
            });
        AssertEncodeRejected(
            locationType,
            new Dictionary<string, object>
            {
                ["kind"] = 1,
                ["index"] = 0L,
                ["generation"] = MaximumSlotGeneration + 1
            });
        AssertEncodeRejected(
            locationType,
            new Dictionary<string, object>
            {
                ["kind"] = 1,
                ["index"] = MaximumDirectoryTargetIndex + 1,
                ["generation"] = 1L
            });

        AssertDecodeRejected(locationType, 1UL); // Primary/index zero, but generation zero.
        const ulong valid = 0x0000_0000_0100_0001UL;
        for (var bit = 57; bit <= 63; bit++)
        {
            AssertDecodeRejected(locationType, valid | (1UL << bit));
        }
    }

    [Fact]
    public void DirectoryOperationRejectsZeroOrOverflowGenerationOutOfRangeTargetAndEveryReservedBit()
    {
        Type operationType = RequireType("SharedMemoryStore.LockFree.DirectoryOperation");

        AssertEncodeRejected(
            operationType,
            new Dictionary<string, object>
            {
                ["intent"] = 1,
                ["phase"] = 1,
                ["targetKind"] = 0,
                ["targetIndex"] = 0L,
                ["generation"] = 0L
            });
        AssertEncodeRejected(
            operationType,
            new Dictionary<string, object>
            {
                ["intent"] = 1,
                ["phase"] = 1,
                ["targetKind"] = 0,
                ["targetIndex"] = 0L,
                ["generation"] = MaximumSlotGeneration + 1
            });
        AssertEncodeRejected(
            operationType,
            new Dictionary<string, object>
            {
                ["intent"] = 2,
                ["phase"] = 3,
                ["targetKind"] = 1,
                ["targetIndex"] = MaximumDirectoryTargetIndex + 1,
                ["generation"] = 1L
            });

        AssertDecodeRejected(operationType, 5UL); // Insert/Prepared, but generation zero.
        const ulong valid = 0x0000_0000_2000_0005UL;
        for (var bit = 62; bit <= 63; bit++)
        {
            AssertDecodeRejected(operationType, valid | (1UL << bit));
        }
    }

    [Fact]
    public void ParticipantSlotAndLeaseControlWordsUseTheDocumentedBitPartitions()
    {
        Type controlType = RequireType("SharedMemoryStore.LockFree.AtomicControlWord");
        const int participantState = 2;
        const int participantIncarnation = 5;
        const int pid = 1_234;
        ulong participant = EncodeNamed(
            controlType,
            "Participant",
            new Dictionary<string, object>
            {
                ["state"] = participantState,
                ["incarnation"] = participantIncarnation,
                ["generation"] = participantIncarnation,
                ["pid"] = pid,
                ["processId"] = pid
            });

        Assert.Equal(
            (ulong)participantState | ((ulong)participantIncarnation << 3) | ((ulong)pid << 31),
            participant);
        Assert.Equal(0UL, participant >> 63);

        const int lifecycleState = 2;
        const long lifecycleGeneration = 9;
        const int participantToken = 0x0123_4567;
        ulong expectedOwned = (ulong)lifecycleState
            | ((ulong)lifecycleGeneration << 3)
            | ((ulong)participantToken << 36);
        var ownedParts = new Dictionary<string, object>
        {
            ["state"] = lifecycleState,
            ["generation"] = lifecycleGeneration,
            ["incarnation"] = lifecycleGeneration,
            ["participantToken"] = participantToken,
            ["token"] = participantToken
        };

        Assert.Equal(expectedOwned, EncodeNamed(controlType, "Slot", ownedParts));
        Assert.Equal(expectedOwned, EncodeNamed(controlType, "Lease", ownedParts));
    }

    [Fact]
    public void SharedRecordStridesAndFieldOffsetsAreExactAndAtomicWordsAreAligned()
    {
        AssertRecord(
            "SharedMemoryStore.LayoutV2.StoreHeaderV2",
            512,
            ("PidNamespaceId", 264),
            ("PidNamespaceMode", 272));

        AssertRecord(
            "SharedMemoryStore.LayoutV2.ParticipantRecordV2",
            64,
            ("Control", 0),
            ("IdentityKind", 8),
            ("Reserved", 12),
            ("ProcessStartValue", 16),
            ("OpenSequence", 24),
            ("PidNamespaceId", 32));

        AssertRecord(
            "SharedMemoryStore.LayoutV2.PrimaryDirectoryBucketV2",
            128,
            ("SpillSummary", 0),
            ("Mutation", 8),
            ("Lanes", 16));

        AssertRecord(
            "SharedMemoryStore.LayoutV2.LeaseRecordV2",
            64,
            ("Control", 0),
            ("SlotBinding", 8),
            ("AcquireSequence", 16));

        AssertRecord(
            "SharedMemoryStore.LayoutV2.ValueSlotMetadataV2",
            128,
            ("Control", 0),
            ("DirectoryBinding", 8),
            ("DirectoryLocation", 16),
            ("DirectoryOperation", 24),
            ("KeyHash", 32),
            ("KeyLength", 40),
            ("DescriptorLength", 44),
            ("ValueLength", 48),
            ("PublicationIntent", 52),
            ("BytesAdvanced", 56),
            ("CommitSequence", 64),
            ("KeyOffset", 72),
            ("DescriptorOffset", 80),
            ("PayloadOffset", 88));
    }

    [Fact]
    public void LayoutSectionsFollowTheCanonicalOrderSizesStridesAndAlignment()
    {
        const int slots = 3;
        const int leases = 5;
        const int participants = 64;
        object layout = CreateLayout(
            slotCount: slots,
            leaseRecordCount: leases,
            participantCount: participants,
            maxKeyBytes: 7,
            maxDescriptorBytes: 9,
            maxValueBytes: 17);

        long headerLength = GetInt64(layout, "HeaderLength");
        long participantOffset = GetInt64(layout, "ParticipantOffset", "ParticipantRecordsOffset");
        long participantLength = GetInt64(layout, "ParticipantLength", "ParticipantRecordsLength");
        long primaryOffset = GetInt64(layout, "PrimaryDirectoryOffset", "PrimaryBucketOffset");
        long primaryLength = GetInt64(layout, "PrimaryDirectoryLength", "PrimaryBucketLength");
        long overflowOffset = GetInt64(layout, "OverflowDirectoryOffset", "OverflowOffset");
        long overflowLength = GetInt64(layout, "OverflowDirectoryLength", "OverflowLength");
        long leaseOffset = GetInt64(layout, "LeaseRegistryOffset", "LeaseOffset");
        long leaseLength = GetInt64(layout, "LeaseRegistryLength", "LeaseLength");
        long slotOffset = GetInt64(layout, "SlotMetadataOffset", "ValueSlotMetadataOffset");
        long slotLength = GetInt64(layout, "SlotMetadataLength", "ValueSlotMetadataLength");
        long keyOffset = GetInt64(layout, "KeyStorageOffset", "KeyOffset");
        long keyLength = GetInt64(layout, "KeyStorageLength", "KeyLength");
        long descriptorOffset = GetInt64(layout, "DescriptorStorageOffset", "DescriptorOffset");
        long descriptorLength = GetInt64(layout, "DescriptorStorageLength", "DescriptorLength");
        long payloadOffset = GetInt64(layout, "PayloadStorageOffset", "PayloadOffset");
        long payloadLength = GetInt64(layout, "PayloadStorageLength", "PayloadLength");

        Assert.Equal(0, headerLength % 64);
        Assert.Equal(headerLength, participantOffset);
        Assert.Equal(participants * 64, participantLength);
        Assert.Equal(64, GetInt64(layout, "ParticipantStride", "ParticipantRecordStride"));

        Assert.Equal(Align64(participantOffset + participantLength), primaryOffset);
        Assert.Equal(128, GetInt64(layout, "PrimaryBucketStride", "PrimaryDirectoryBucketStride"));
        Assert.Equal(GetInt64(layout, "BucketCount", "PrimaryBucketCount") * 128, primaryLength);

        Assert.Equal(Align8(primaryOffset + primaryLength), overflowOffset);
        Assert.Equal(slots * 8, overflowLength);
        Assert.Equal(8, GetInt64(layout, "OverflowStride", "OverflowBindingStride"));

        Assert.Equal(Align64(overflowOffset + overflowLength), leaseOffset);
        Assert.Equal(leases * 64, leaseLength);
        Assert.Equal(64, GetInt64(layout, "LeaseStride", "LeaseRecordStride"));

        Assert.Equal(Align64(leaseOffset + leaseLength), slotOffset);
        Assert.Equal(slots * 128, slotLength);
        Assert.Equal(128, GetInt64(layout, "SlotMetadataStride", "ValueSlotMetadataStride"));

        Assert.Equal(Align8(slotOffset + slotLength), keyOffset);
        Assert.Equal(slots * 8, keyLength);
        Assert.Equal(8, GetInt64(layout, "KeyStride"));

        Assert.Equal(Align8(keyOffset + keyLength), descriptorOffset);
        Assert.Equal(slots * 16, descriptorLength);
        Assert.Equal(16, GetInt64(layout, "DescriptorStride"));

        Assert.Equal(Align8(descriptorOffset + descriptorLength), payloadOffset);
        Assert.Equal(slots * 24, payloadLength);
        Assert.Equal(24, GetInt64(layout, "PayloadStride"));
        Assert.Equal(Align8(payloadOffset + payloadLength), GetInt64(layout, "RequiredBytes"));

        foreach (long offset in new[]
                 {
                     participantOffset, primaryOffset, overflowOffset, leaseOffset,
                     slotOffset, keyOffset, descriptorOffset, payloadOffset
                 })
        {
            Assert.Equal(0, offset % 8);
        }

        foreach (long offset in new[] { participantOffset, primaryOffset, leaseOffset, slotOffset })
        {
            Assert.Equal(0, offset % 64);
        }
    }

    [Fact]
    public void LockFreeSlotCountAcceptsTheContractMaximumAndRejectsTheNextValue()
    {
        object maximum = CreateLayout(
            slotCount: MaximumLockFreeSlotCount,
            leaseRecordCount: 1,
            participantCount: 1,
            maxKeyBytes: 1,
            maxDescriptorBytes: 0,
            maxValueBytes: 1);

        Assert.Equal(MaximumLockFreeSlotCount, GetInt64(maximum, "SlotCount"));
        Assert.Equal(1 << 22, GetInt64(maximum, "PrimaryLaneCount"));

        Exception error = Assert.ThrowsAny<Exception>(() => CreateLayout(
            slotCount: MaximumLockFreeSlotCount + 1,
            leaseRecordCount: 1,
            participantCount: 1,
            maxKeyBytes: 1,
            maxDescriptorBytes: 0,
            maxValueBytes: 1));

        Assert.IsType<ArgumentOutOfRangeException>(Unwrap(error));
    }

    [Fact]
    public void LayoutArithmeticThrowsInsteadOfWrapping()
    {
        Exception error = Assert.ThrowsAny<Exception>(() => CreateLayout(
            slotCount: 1,
            leaseRecordCount: 1,
            participantCount: 64,
            maxKeyBytes: 1,
            maxDescriptorBytes: 0,
            maxValueBytes: int.MaxValue));

        Assert.IsType<OverflowException>(Unwrap(error));
    }

    [Fact]
    public void LayoutV2ExplicitlyGatesSupportToX64()
    {
        Type constantsType = RequireType("SharedMemoryStore.LayoutV2.LayoutV2Constants");
        MethodInfo gate = constantsType
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                method.ReturnType == typeof(bool)
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(Architecture)
                && method.Name.Contains("Support", StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException(
                $"{constantsType.FullName} must expose the testable x64 architecture gate.");

        Assert.True((bool)gate.Invoke(null, [Architecture.X64])!);
        Assert.False((bool)gate.Invoke(null, [Architecture.X86])!);
        Assert.False((bool)gate.Invoke(null, [Architecture.Arm64])!);
    }

    [Fact]
    public void AtomicControlWordRmwWrappersCallSequentiallyConsistentInterlockedPrimitives()
    {
        Type atomicType = RequireType("SharedMemoryStore.LockFree.AtomicControlWord");
        MethodInfo[] methods = atomicType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Contains(methods, method => CallsInterlocked(method, nameof(Interlocked.CompareExchange)));
        Assert.Contains(methods, method => CallsInterlocked(method, nameof(Interlocked.Exchange)));
    }

    [Fact]
    public void PublicProfileAwareSizingAndCreateHelperUseTheCanonicalV2Layout()
    {
        Type profileType = RequireType("SharedMemoryStore.StoreProfile");
        object lockFree = Enum.Parse(profileType, "LockFree");
        MethodInfo calculate = typeof(SharedMemoryStoreOptions)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .SingleOrDefault(method =>
                method.Name == nameof(SharedMemoryStoreOptions.CalculateRequiredBytes)
                && method.GetParameters().Length == 7
                && method.GetParameters()[0].ParameterType == profileType)
            ?? throw new Xunit.Sdk.XunitException("The profile-aware CalculateRequiredBytes overload is absent.");

        object?[] sizingArguments = [lockFree, 3, 17, 9, 7, 5, 64];
        long publicBytes = Convert.ToInt64(calculate.Invoke(null, sizingArguments));
        long internalBytes = GetInt64(CreateLayout(
            slotCount: 3,
            leaseRecordCount: 5,
            participantCount: 64,
            maxKeyBytes: 7,
            maxDescriptorBytes: 9,
            maxValueBytes: 17), "RequiredBytes");

        MethodInfo create = typeof(SharedMemoryStoreOptions)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "CreateLockFree")
            ?? throw new Xunit.Sdk.XunitException("SharedMemoryStoreOptions.CreateLockFree is absent.");
        object options = create.Invoke(
            null,
            BindArguments(
                create.GetParameters(),
                new Dictionary<string, object>
                {
                    ["name"] = $"sms-layout-contract-{Guid.NewGuid():N}",
                    ["slotCount"] = 3,
                    ["maxValueBytes"] = 17,
                    ["maxDescriptorBytes"] = 9,
                    ["maxKeyBytes"] = 7,
                    ["leaseRecordCount"] = 5,
                    ["participantRecordCount"] = 64,
                    ["participantCount"] = 64
                }))!;

        Assert.Equal(internalBytes, publicBytes);
        Assert.Equal(publicBytes, GetInt64(options, "TotalBytes"));
    }

    private static object CreateLayout(
        int slotCount = 3,
        int leaseRecordCount = 5,
        int participantCount = 64,
        int maxKeyBytes = 7,
        int maxDescriptorBytes = 9,
        int maxValueBytes = 17)
    {
        Type layoutType = RequireType("SharedMemoryStore.LayoutV2.StoreLayoutV2");
        var values = new Dictionary<string, object>
        {
            ["totalBytes"] = 0L,
            ["slotCount"] = slotCount,
            ["leaseRecordCount"] = leaseRecordCount,
            ["participantRecordCount"] = participantCount,
            ["participantCount"] = participantCount,
            ["maxKeyBytes"] = maxKeyBytes,
            ["maxDescriptorBytes"] = maxDescriptorBytes,
            ["maxValueBytes"] = maxValueBytes
        };

        foreach (ConstructorInfo constructor in layoutType
                     .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderByDescending(candidate => candidate.GetParameters().Length))
        {
            if (TryBindArguments(constructor.GetParameters(), values, out object?[]? arguments))
            {
                return constructor.Invoke(arguments);
            }
        }

        foreach (MethodInfo factory in layoutType
                     .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(method => method.ReturnType == layoutType)
                     .OrderByDescending(candidate => candidate.GetParameters().Length))
        {
            if (TryBindArguments(factory.GetParameters(), values, out object?[]? arguments))
            {
                return factory.Invoke(null, arguments)!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"{layoutType.FullName} needs a dimension-based constructor or factory for contract validation.");
    }

    private static Type RequireType(string fullName)
    {
        return StoreAssembly.GetType(fullName, throwOnError: false)
            ?? throw new Xunit.Sdk.XunitException($"Required layout-2.0 type {fullName} is absent.");
    }

    private static ulong Encode(Type codecType, IReadOnlyDictionary<string, object> values)
    {
        IEnumerable<MethodInfo> factories = codecType
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method =>
                method.Name.Contains("Encode", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Pack", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Create", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("FromParts", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(method => method.GetParameters().Length);

        foreach (MethodInfo factory in factories)
        {
            if (!TryBindArguments(factory.GetParameters(), values, out object?[]? arguments))
            {
                continue;
            }

            object? result = factory.Invoke(null, arguments);
            return ToRaw(result, codecType);
        }

        foreach (ConstructorInfo constructor in codecType
                     .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderByDescending(candidate => candidate.GetParameters().Length))
        {
            if (TryBindArguments(constructor.GetParameters(), values, out object?[]? arguments))
            {
                return ToRaw(constructor.Invoke(arguments), codecType);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"{codecType.FullName} needs an encode/create factory or constructor for its documented parts.");
    }

    private static void AssertEncodeRejected(
        Type codecType,
        IReadOnlyDictionary<string, object> values)
    {
        Exception error = Assert.ThrowsAny<Exception>(() => Encode(codecType, values));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(Unwrap(error));
    }

    private static void AssertDecodeRejected(Type codecType, ulong raw)
    {
        Exception error = Assert.ThrowsAny<Exception>(() => Decode(codecType, raw));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(Unwrap(error));
    }

    private static object Decode(Type codecType, ulong raw)
    {
        foreach (MethodInfo method in codecType
                     .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(method =>
                         method.Name.Contains("Decode", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("FromRaw", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("FromValue", StringComparison.OrdinalIgnoreCase)))
        {
            var values = new Dictionary<string, object>
            {
                ["raw"] = raw,
                ["value"] = raw,
                ["word"] = raw,
                ["encoded"] = raw
            };
            if (!TryBindArguments(method.GetParameters(), values, out object?[]? arguments))
            {
                continue;
            }

            object? decoded = method.Invoke(null, arguments);
            if (decoded is not null && decoded is not bool)
            {
                return decoded;
            }
        }

        throw new Xunit.Sdk.XunitException($"{codecType.FullName} needs a scalar decode method.");
    }

    private static ulong EncodeNamed(
        Type codecType,
        string family,
        IReadOnlyDictionary<string, object> values)
    {
        foreach (MethodInfo method in codecType
                     .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(method => method.Name.Contains(family, StringComparison.OrdinalIgnoreCase))
                     .Where(method =>
                         method.Name.Contains("Encode", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("Pack", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("Create", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryBindArguments(method.GetParameters(), values, out object?[]? arguments))
            {
                return ToRaw(method.Invoke(null, arguments), codecType);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"{codecType.FullName} needs a {family} encode/pack/create method with the documented fields.");
    }

    private static ulong ToRaw(object? value, Type codecType)
    {
        Assert.NotNull(value);
        Type valueType = value.GetType();
        if (IsIntegral(valueType))
        {
            return Convert.ToUInt64(value);
        }

        return GetUInt64(value, "Value", "RawValue", "EncodedValue", "PackedValue", "Word", "Control");
    }

    private static void AssertDecoded(Type codecType, ulong raw, params (string Name, object Expected)[] expectedParts)
    {
        object? decoded = null;
        foreach (MethodInfo method in codecType
                     .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(method =>
                         method.Name.Contains("Decode", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("FromRaw", StringComparison.OrdinalIgnoreCase)
                         || method.Name.Contains("FromValue", StringComparison.OrdinalIgnoreCase)))
        {
            ParameterInfo[] parameters = method.GetParameters();
            var values = new Dictionary<string, object>
            {
                ["raw"] = raw,
                ["value"] = raw,
                ["word"] = raw,
                ["encoded"] = raw
            };

            if (!TryBindArguments(parameters, values, out object?[]? arguments))
            {
                continue;
            }

            object? result = method.Invoke(null, arguments);
            if (result is bool success)
            {
                Assert.True(success);
                foreach ((string name, object expected) in expectedParts)
                {
                    int index = Array.FindIndex(parameters, parameter =>
                        parameter.IsOut && SemanticNameMatches(parameter.Name, name));
                    Assert.True(index >= 0, $"{method.Name} does not decode {name}.");
                    Assert.Equal(Convert.ToUInt64(expected), Convert.ToUInt64(arguments![index]));
                }

                return;
            }

            decoded = result;
            if (decoded is not null)
            {
                break;
            }
        }

        Assert.NotNull(decoded);
        foreach ((string name, object expected) in expectedParts)
        {
            Assert.Equal(Convert.ToUInt64(expected), checked((ulong)GetInt64(decoded, name)));
        }
    }

    private static void AssertRecord(string fullName, int expectedSize, params (string Field, int Offset)[] fields)
    {
        Type recordType = RequireType(fullName);
        Assert.True(recordType.IsValueType, $"{fullName} must be a fixed-width value type.");
        Assert.Equal(expectedSize, Marshal.SizeOf(recordType));

        foreach ((string expectedName, int expectedOffset) in fields)
        {
            FieldInfo field = FindField(recordType, expectedName, expectedOffset);
            int actualOffset = checked((int)Marshal.OffsetOf(recordType, field.Name));
            Assert.Equal(expectedOffset, actualOffset);
            if (field.FieldType == typeof(long) || field.FieldType == typeof(ulong))
            {
                Assert.Equal(0, actualOffset % 8);
            }
        }
    }

    private static void AssertJsonBitRange(
        JsonElement parent,
        string propertyName,
        int expectedFirst,
        int expectedLast)
    {
        int[] actual = parent.GetProperty(propertyName)
            .EnumerateArray()
            .Select(static item => item.GetInt32())
            .ToArray();
        Assert.Equal(new[] { expectedFirst, expectedLast }, actual);
    }

    private static FieldInfo FindField(Type type, string expectedName, int expectedOffset)
    {
        string normalized = Normalize(expectedName);
        string singular = normalized.TrimEnd('s');
        return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .Where(field =>
                   {
                       string actual = Normalize(field.Name);
                       return actual == normalized
                           || actual.Contains(normalized, StringComparison.Ordinal)
                           || actual.StartsWith(singular, StringComparison.Ordinal);
                   })
                   .SingleOrDefault(field => checked((int)Marshal.OffsetOf(type, field.Name)) == expectedOffset)
            ?? throw new Xunit.Sdk.XunitException($"{type.FullName} is missing field {expectedName}.");
    }

    private static long GetInt64(object instance, params string[] candidateNames)
    {
        Type type = instance.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in candidateNames)
        {
            string normalized = Normalize(name);
            PropertyInfo? property = type.GetProperties(flags)
                .SingleOrDefault(candidate => Normalize(candidate.Name) == normalized);
            if (property is not null)
            {
                return Convert.ToInt64(property.GetValue(instance));
            }

            FieldInfo? field = type.GetFields(flags)
                .SingleOrDefault(candidate => Normalize(candidate.Name) == normalized);
            if (field is not null)
            {
                return Convert.ToInt64(field.GetValue(instance));
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"{type.FullName} is missing integral member {string.Join("/", candidateNames)}.");
    }

    private static ulong GetUInt64(object instance, params string[] candidateNames)
    {
        Type type = instance.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in candidateNames)
        {
            string normalized = Normalize(name);
            PropertyInfo? property = type.GetProperties(flags)
                .SingleOrDefault(candidate => Normalize(candidate.Name) == normalized);
            if (property is not null)
            {
                return Convert.ToUInt64(property.GetValue(instance));
            }

            FieldInfo? field = type.GetFields(flags)
                .SingleOrDefault(candidate => Normalize(candidate.Name) == normalized);
            if (field is not null)
            {
                return Convert.ToUInt64(field.GetValue(instance));
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"{type.FullName} is missing integral member {string.Join("/", candidateNames)}.");
    }

    private static ulong GetStaticUInt64(Type type, string name)
    {
        BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo? field = type.GetField(name, flags);
        if (field is not null)
        {
            return Convert.ToUInt64(field.GetValue(null));
        }

        PropertyInfo? property = type.GetProperty(name, flags);
        if (property is not null)
        {
            return Convert.ToUInt64(property.GetValue(null));
        }

        throw new Xunit.Sdk.XunitException($"{type.FullName} is missing static integral member {name}.");
    }

    private static object?[] BindArguments(ParameterInfo[] parameters, IReadOnlyDictionary<string, object> values)
    {
        Assert.True(TryBindArguments(parameters, values, out object?[]? arguments));
        return arguments!;
    }

    private static bool TryBindArguments(
        ParameterInfo[] parameters,
        IReadOnlyDictionary<string, object> values,
        out object?[]? arguments)
    {
        arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            Type targetType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;

            if (parameter.IsOut)
            {
                arguments[index] = targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
                continue;
            }

            object? supplied = FindValue(parameter.Name, values);
            if (supplied is null)
            {
                if (parameter.HasDefaultValue)
                {
                    arguments[index] = parameter.DefaultValue;
                    continue;
                }

                arguments = null;
                return false;
            }

            try
            {
                arguments[index] = ConvertArgument(supplied, targetType);
            }
            catch (Exception)
            {
                arguments = null;
                return false;
            }
        }

        return true;
    }

    private static object? FindValue(string? parameterName, IReadOnlyDictionary<string, object> values)
    {
        foreach ((string name, object value) in values)
        {
            if (SemanticNameMatches(parameterName, name))
            {
                return value;
            }
        }

        return null;
    }

    private static bool SemanticNameMatches(string? left, string right)
    {
        string normalizedLeft = Normalize(left ?? string.Empty);
        string normalizedRight = Normalize(right);
        if (normalizedLeft == normalizedRight)
        {
            return true;
        }

        return (normalizedLeft, normalizedRight) switch
        {
            ("participantrecordcount", "participantcount") or ("participantcount", "participantrecordcount") => true,
            ("recordindex", "index") or ("index", "recordindex") => true,
            ("cellindex", "index") or ("index", "cellindex") => true,
            ("targetindex", "index") or ("index", "targetindex") => true,
            ("targetindex", "cellindex") or ("cellindex", "targetindex") => true,
            ("targetkind", "kind") or ("kind", "targetkind") => true,
            ("generation", "incarnation") or ("incarnation", "generation") => true,
            ("rawvalue", "raw") or ("raw", "rawvalue") => true,
            ("encodedvalue", "encoded") or ("encoded", "encodedvalue") => true,
            _ => false
        };
    }

    private static object ConvertArgument(object value, Type targetType)
    {
        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType.IsEnum)
        {
            return Enum.ToObject(targetType, value);
        }

        return Convert.ChangeType(value, targetType);
    }

    private static bool IsIntegral(Type type)
    {
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } target)
        {
            exception = target.InnerException!;
        }

        return exception;
    }

    private static bool CallsInterlocked(MethodInfo method, string calledMethodName)
    {
        MethodBody? body = method.GetMethodBody();
        byte[]? il = body?.GetILAsByteArray();
        if (il is null)
        {
            return false;
        }

        for (var position = 0; position < il.Length;)
        {
            OpCode opcode;
            byte first = il[position++];
            if (first == 0xfe)
            {
                opcode = MultiByteOpCodes[il[position++]];
            }
            else
            {
                opcode = SingleByteOpCodes[first];
            }

            int operandSize = GetOperandSize(opcode.OperandType, il, position);
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, position);
                try
                {
                    MethodBase? called = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method.GetGenericArguments());
                    if (called?.DeclaringType == typeof(Interlocked) && called.Name == calledMethodName)
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // Invalid tokens cannot represent the required call.
                }
            }

            position += operandSize;
        }

        return false;
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int operandPosition)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, operandPosition) * 4),
            _ => throw new InvalidOperationException($"Unknown IL operand type {operandType}.")
        };
    }

    private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeTable(multiByte: false);
    private static readonly OpCode[] MultiByteOpCodes = BuildOpCodeTable(multiByte: true);

    private static OpCode[] BuildOpCodeTable(bool multiByte)
    {
        var table = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var opcode = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)opcode.Value);
            if ((!multiByte && value < 0x100) || (multiByte && (value & 0xff00) == 0xfe00))
            {
                table[value & 0xff] = opcode;
            }
        }

        return table;
    }

    private static long Align8(long value) => checked(value + 7) & ~7L;

    private static long Align64(long value) => checked(value + 63) & ~63L;

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
