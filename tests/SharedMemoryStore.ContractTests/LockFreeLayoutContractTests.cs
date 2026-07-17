using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public void ManifestDeclaresSms2AsTheSoleCurrentProtocolAndPinsRequiredFeatures()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement protocol = document.RootElement.GetProperty("protocol");

        Assert.Equal(new[] { "2.0" }, ReadJsonStrings(protocol, "creatable_layouts"));
        Assert.Equal(new[] { "2.0" }, ReadJsonStrings(protocol, "readable_layouts"));
        Assert.Equal(
            "reject-before-payload-access",
            protocol.GetProperty("noncurrent_mapping_policy").GetString());
        Assert.False(protocol.TryGetProperty("retired_layouts", out _));
        Assert.Equal("SMS2", protocol.GetProperty("magic_ascii").GetString());
        Assert.Equal("32534d53", protocol.GetProperty("magic_integer_hex").GetString());
        Assert.Equal("534d5332", protocol.GetProperty("little_endian_bytes_hex").GetString());
        Assert.Equal("little", protocol.GetProperty("byte_order").GetString());
        Assert.Equal("x86_64", protocol.GetProperty("required_architecture").GetString());
        Assert.Equal(8, protocol.GetProperty("atomic_width").GetInt32());
        Assert.Equal("sequentially-consistent", protocol.GetProperty("rmw_order").GetString());
        Assert.Equal(7UL, protocol.GetProperty("required_features").GetUInt64());
        Assert.Equal(0UL, protocol.GetProperty("optional_features").GetUInt64());
        Assert.Equal(
            new ulong[] { 0, 1, 3 },
            protocol.GetProperty("incompatible_draft_required_feature_masks")
                .EnumerateArray()
                .Select(static item => item.GetUInt64())
                .ToArray());

        JsonElement bits = protocol.GetProperty("required_feature_bits");
        AssertJsonIntegerMap(
            bits,
            new Dictionary<string, int>
            {
                ["versioned_empty_spill_summary"] = 1,
                ["publication_intent"] = 2,
                ["pid_namespace_identity"] = 4
            });
        Assert.Equal(
            protocol.GetProperty("required_features").GetUInt64(),
            bits.EnumerateObject().Aggregate(0UL, static (mask, bit) => mask | bit.Value.GetUInt64()));
    }

    [Fact]
    public void ManifestPinsEverySms2RecordFieldOffset()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement records = document.RootElement.GetProperty("records");

        AssertJsonRecord(
            records,
            "store_header",
            512,
            ("magic", 0),
            ("layout_major_version", 4),
            ("layout_minor_version", 6),
            ("header_length", 8),
            ("resource_protocol_version", 12),
            ("required_features", 16),
            ("optional_features", 24),
            ("total_bytes", 32),
            ("store_id", 40),
            ("control", 48),
            ("sequence", 56),
            ("slot_count", 64),
            ("lease_record_count", 68),
            ("participant_record_count", 72),
            ("max_key_bytes", 76),
            ("max_descriptor_bytes", 80),
            ("max_value_bytes", 84),
            ("participant_index_bits", 88),
            ("participant_generation_bits", 92),
            ("participant_offset", 96),
            ("participant_length", 104),
            ("participant_stride", 112),
            ("primary_lane_count", 116),
            ("primary_bucket_count", 120),
            ("primary_bucket_stride", 124),
            ("primary_directory_offset", 128),
            ("primary_directory_length", 136),
            ("overflow_directory_offset", 144),
            ("overflow_directory_length", 152),
            ("overflow_stride", 160),
            ("lease_stride", 164),
            ("lease_registry_offset", 168),
            ("lease_registry_length", 176),
            ("slot_metadata_stride", 184),
            ("key_stride", 188),
            ("slot_metadata_offset", 192),
            ("slot_metadata_length", 200),
            ("key_storage_offset", 208),
            ("key_storage_length", 216),
            ("descriptor_stride", 224),
            ("payload_stride", 228),
            ("descriptor_storage_offset", 232),
            ("descriptor_storage_length", 240),
            ("payload_storage_offset", 248),
            ("payload_storage_length", 256),
            ("pid_namespace_id", 264),
            ("pid_namespace_mode", 272));
        Assert.Equal(64, records.GetProperty("store_header").GetProperty("alignment").GetInt32());

        AssertJsonRecord(
            records,
            "participant",
            64,
            ("control", 0),
            ("identity_kind", 8),
            ("reserved", 12),
            ("process_start_value", 16),
            ("open_sequence", 24),
            ("pid_namespace_id", 32));
        AssertJsonRecord(
            records,
            "primary_directory_bucket",
            128,
            ("spill_summary", 0),
            ("mutation", 8),
            ("lanes", 16));
        Assert.Equal(
            8,
            records.GetProperty("primary_directory_bucket").GetProperty("lane_count").GetInt32());
        AssertJsonRecord(records, "overflow_binding", 8, ("binding", 0));
        AssertJsonRecord(
            records,
            "lease",
            64,
            ("control", 0),
            ("slot_binding", 8),
            ("acquire_sequence", 16));
        AssertJsonRecord(
            records,
            "value_slot",
            128,
            ("control", 0),
            ("directory_binding", 8),
            ("directory_location", 16),
            ("directory_operation", 24),
            ("key_hash", 32),
            ("key_length", 40),
            ("descriptor_length", 44),
            ("value_length", 48),
            ("publication_intent", 52),
            ("bytes_advanced", 56),
            ("commit_sequence", 64),
            ("key_offset", 72),
            ("descriptor_offset", 80),
            ("payload_offset", 88));

        Assert.Equal(
            new[]
            {
                "lease", "overflow_binding", "participant", "primary_directory_bucket",
                "store_header", "value_slot"
            },
            records.EnumerateObject().Select(static record => record.Name).Order().ToArray());
    }

    [Fact]
    public void ManifestPublishesValidAndMalformedVectorsForEverySms2Codec()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement vectors = document.RootElement.GetProperty("codec_vectors");
        string[] expectedFamilies =
        [
            "binding",
            "directory_location",
            "directory_operation",
            "lease_control",
            "participant_control",
            "participant_token",
            "slot_control",
            "spill_summary"
        ];

        Assert.Equal(
            expectedFamilies,
            vectors.EnumerateObject().Select(static family => family.Name).Order().ToArray());
        foreach (string family in expectedFamilies)
        {
            AssertJsonCodecVectors(vectors, family);
        }
    }

    [Fact]
    public void ManifestPublishesCheckedSizingLimitsAndExecutableLayoutVectors()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement sizing = document.RootElement.GetProperty("sizing");
        JsonElement limits = sizing.GetProperty("limits");

        AssertJsonLimit(limits, "slot_count", minimum: 1, maximum: MaximumLockFreeSlotCount);
        AssertJsonLimit(limits, "lease_record_count", minimum: 1);
        AssertJsonLimit(
            limits,
            "participant_record_count",
            minimum: 1,
            maximum: MaximumLockFreeSlotCount);
        AssertJsonLimit(limits, "max_key_bytes", minimum: 1);
        AssertJsonLimit(limits, "max_descriptor_bytes", minimum: 0);
        AssertJsonLimit(limits, "max_value_bytes", minimum: 1);

        string[] requiredExpectedFields =
        [
            "header_length",
            "participant_index_bits",
            "participant_generation_bits",
            "participant_stride",
            "participant_offset",
            "participant_length",
            "primary_lane_count",
            "primary_bucket_count",
            "primary_bucket_stride",
            "primary_directory_offset",
            "primary_directory_length",
            "overflow_stride",
            "overflow_directory_offset",
            "overflow_directory_length",
            "lease_stride",
            "lease_registry_offset",
            "lease_registry_length",
            "slot_metadata_stride",
            "slot_metadata_offset",
            "slot_metadata_length",
            "key_stride",
            "key_storage_offset",
            "key_storage_length",
            "descriptor_stride",
            "descriptor_storage_offset",
            "descriptor_storage_length",
            "payload_stride",
            "payload_storage_offset",
            "payload_storage_length",
            "required_bytes"
        ];

        JsonElement[] validVectors = sizing.GetProperty("valid_vectors").EnumerateArray().ToArray();
        Assert.NotEmpty(validVectors);
        AssertUniqueJsonNames(validVectors);
        foreach (JsonElement vector in validVectors)
        {
            JsonElement input = vector.GetProperty("input");
            object layout = CreateLayoutFromJson(input);
            JsonElement expected = vector.GetProperty("expected");

            foreach (string field in requiredExpectedFields)
            {
                Assert.True(expected.TryGetProperty(field, out _), $"Sizing vector '{vector.GetProperty("name").GetString()}' is missing {field}.");
            }

            foreach (JsonProperty property in expected.EnumerateObject())
            {
                Assert.Equal(property.Value.GetInt64(), GetInt64(layout, property.Name));
            }
        }

        JsonElement[] invalidVectors = sizing.GetProperty("invalid_vectors").EnumerateArray().ToArray();
        Assert.NotEmpty(invalidVectors);
        AssertUniqueJsonNames(invalidVectors);
        Assert.Contains(invalidVectors, static vector => vector.GetProperty("error").GetString() == "invalid_argument");
        Assert.Contains(invalidVectors, static vector => vector.GetProperty("error").GetString() == "arithmetic_overflow");
        foreach (JsonElement vector in invalidVectors)
        {
            Exception thrown = Assert.ThrowsAny<Exception>(() => CreateLayoutFromJson(vector.GetProperty("input")));
            Exception error = Unwrap(thrown);
            switch (vector.GetProperty("error").GetString())
            {
                case "invalid_argument":
                    Assert.IsAssignableFrom<ArgumentOutOfRangeException>(error);
                    break;
                case "arithmetic_overflow":
                    Assert.IsAssignableFrom<OverflowException>(error);
                    break;
                default:
                    throw new Xunit.Sdk.XunitException(
                        $"Sizing vector '{vector.GetProperty("name").GetString()}' has an unknown error classification.");
            }
        }
    }

    [Fact]
    public void ManifestPublishesFnvAndExactKeyCollisionVectors()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement root = document.RootElement;
        JsonElement[] hashes = root.GetProperty("hash_vectors").EnumerateArray().ToArray();

        Assert.NotEmpty(hashes);
        AssertUniqueJsonNames(hashes);
        foreach (JsonElement vector in hashes)
        {
            byte[] bytes = ReadLowerHex(vector, "bytes_hex");
            Assert.Equal(bytes.Length != 0, vector.GetProperty("valid_store_key").GetBoolean());
            string expectedHash = ReadFixedLowerHex(vector, "expected_hash_hex", 16);
            Assert.Equal(Convert.ToUInt64(expectedHash, 16), Fnv1a64(bytes));
        }

        Assert.Contains(hashes, static vector => vector.GetProperty("bytes_hex").GetString() == string.Empty);
        Assert.Contains(
            hashes,
            static vector => ReadLowerHex(vector, "bytes_hex").Contains((byte)0));

        JsonElement[] exactKeys = root.GetProperty("exact_key_vectors").EnumerateArray().ToArray();
        Assert.NotEmpty(exactKeys);
        AssertUniqueJsonNames(exactKeys);
        foreach (JsonElement vector in exactKeys)
        {
            byte[] left = ReadLowerHex(vector, "left_hex");
            byte[] right = ReadLowerHex(vector, "right_hex");
            _ = ReadFixedLowerHex(vector, "shared_hash_hex", 16);
            Assert.Equal(left.AsSpan().SequenceEqual(right), vector.GetProperty("equal").GetBoolean());
        }

        Assert.Contains(exactKeys, static vector => vector.GetProperty("equal").GetBoolean());
        Assert.Contains(
            exactKeys,
            static vector =>
                !vector.GetProperty("equal").GetBoolean()
                && !ReadLowerHex(vector, "left_hex").AsSpan().SequenceEqual(ReadLowerHex(vector, "right_hex")));
    }

    [Fact]
    public void ManifestPublishesCrossPlatformResourceAndOwnershipArtifactVectors()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement names = document.RootElement.GetProperty("resource_name_vectors");
        JsonElement[] windows = names.GetProperty("windows").EnumerateArray().ToArray();
        JsonElement[] linux = names.GetProperty("linux").EnumerateArray().ToArray();
        Assert.NotEmpty(windows);
        Assert.NotEmpty(linux);
        AssertUniqueJsonNames(windows);
        AssertUniqueJsonNames(linux);

        Type resourceNameType = RequireType("SharedMemoryStore.Interop.PlatformResourceName");
        MethodInfo create = resourceNameType.GetMethod(
                "Create",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("PlatformResourceName.Create is absent.");

        foreach (JsonElement vector in windows)
        {
            string publicName = vector.GetProperty("public_name").GetString()!;
            object actual = create.Invoke(null, [publicName])!;
            Assert.Equal(publicName, vector.GetProperty("region_name").GetString());
            Assert.Equal(GetString(actual, "WindowsRegionName"), vector.GetProperty("region_name").GetString());
            Assert.Equal(
                GetString(actual, "WindowsSynchronizationName"),
                vector.GetProperty("synchronization_name").GetString());
        }

        foreach (JsonElement vector in linux)
        {
            string publicName = vector.GetProperty("public_name").GetString()!;
            object actual = create.Invoke(null, [publicName])!;
            string fragment = GetString(actual, "ResourceFragment");
            Assert.Equal(fragment, vector.GetProperty("fragment").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicName)).AsSpan(0, 8)).ToLowerInvariant(),
                vector.GetProperty("sha256_prefix_hex").GetString());

            JsonElement files = vector.GetProperty("files");
            Assert.Equal(Path.GetFileName(GetString(actual, "LinuxRegionPath")), files.GetProperty("region").GetString());
            Assert.Equal(
                Path.GetFileName(GetString(actual, "LinuxSynchronizationPath")),
                files.GetProperty("synchronization").GetString());
            Assert.Equal(Path.GetFileName(GetString(actual, "LinuxOwnersPath")), files.GetProperty("owners").GetString());
            Assert.Equal(
                Path.GetFileName(GetString(actual, "LinuxLifecycleLockPath")),
                files.GetProperty("lifecycle").GetString());

            string ownerToken = ReadFixedLowerHex(vector, "owner_token", 32);
            string owners = files.GetProperty("owners").GetString()!;
            Assert.Equal(owners + ".anchor." + ownerToken, vector.GetProperty("owner_anchor").GetString());
            Assert.Equal(
                owners + ".released." + ownerToken + ".ready",
                vector.GetProperty("release_marker").GetString());
        }
    }

    [Fact]
    public void ManifestPinsOpenModesPublicStatusesAndAllSms2States()
    {
        using JsonDocument document = LoadV2Manifest();
        JsonElement root = document.RootElement;

        AssertJsonIntegerMap(
            root.GetProperty("open_modes"),
            new Dictionary<string, int>
            {
                ["create_new"] = 0,
                ["open_existing"] = 1,
                ["create_or_open"] = 2
            });

        JsonElement statuses = root.GetProperty("statuses");
        AssertJsonIntegerMap(
            statuses.GetProperty("open"),
            new Dictionary<string, int>
            {
                ["success"] = 0,
                ["already_exists"] = 1,
                ["not_found"] = 2,
                ["invalid_options"] = 3,
                ["incompatible_layout"] = 4,
                ["unsupported_platform"] = 5,
                ["insufficient_capacity"] = 6,
                ["access_denied"] = 7,
                ["mapping_failed"] = 8,
                ["store_busy"] = 9,
                ["operation_canceled"] = 10,
                ["participant_table_full"] = 11
            });
        AssertJsonIntegerMap(
            statuses.GetProperty("operation"),
            new Dictionary<string, int>
            {
                ["success"] = 0,
                ["duplicate_key"] = 1,
                ["not_found"] = 2,
                ["key_too_large"] = 3,
                ["value_too_large"] = 4,
                ["descriptor_too_large"] = 5,
                ["store_full"] = 6,
                ["lease_table_full"] = 7,
                ["invalid_lease"] = 8,
                ["lease_already_released"] = 9,
                ["remove_pending"] = 10,
                ["unsupported_platform"] = 11,
                ["store_disposed"] = 12,
                ["corrupt_store"] = 13,
                ["access_denied"] = 14,
                ["unknown_failure"] = 15,
                ["invalid_reservation"] = 16,
                ["reservation_incomplete"] = 17,
                ["reservation_already_completed"] = 18,
                ["reservation_write_out_of_range"] = 19,
                ["invalid_key"] = 20,
                ["store_busy"] = 21,
                ["operation_canceled"] = 22
            });

        JsonElement states = root.GetProperty("states");
        Assert.Equal(
            new[]
            {
                "identity_kind", "lease", "participant", "pid_namespace_mode",
                "publication_intent", "slot", "store"
            },
            states.EnumerateObject().Select(static state => state.Name).Order().ToArray());
        AssertJsonIntegerMap(states.GetProperty("store"), new Dictionary<string, int>
        {
            ["initializing"] = 1,
            ["ready"] = 2,
            ["corrupt"] = 3,
            ["unsupported"] = 4
        });
        AssertJsonIntegerMap(states.GetProperty("participant"), new Dictionary<string, int>
        {
            ["free"] = 0,
            ["registering"] = 1,
            ["active"] = 2,
            ["closing"] = 3,
            ["recovering"] = 4,
            ["reclaiming"] = 5,
            ["retired"] = 6
        });
        AssertJsonIntegerMap(states.GetProperty("slot"), new Dictionary<string, int>
        {
            ["free"] = 0,
            ["initializing"] = 1,
            ["reserved"] = 2,
            ["published"] = 3,
            ["remove_requested"] = 4,
            ["aborting"] = 5,
            ["reclaiming"] = 6,
            ["retired"] = 7
        });
        AssertJsonIntegerMap(states.GetProperty("lease"), new Dictionary<string, int>
        {
            ["free"] = 0,
            ["claiming"] = 1,
            ["active"] = 2,
            ["releasing"] = 3,
            ["recovering"] = 4,
            ["retired"] = 5
        });
        AssertJsonIntegerMap(states.GetProperty("publication_intent"), new Dictionary<string, int>
        {
            ["none"] = 0,
            ["explicit_reservation"] = 1,
            ["atomic_publication"] = 2
        });
        AssertJsonIntegerMap(states.GetProperty("identity_kind"), new Dictionary<string, int>
        {
            ["unknown"] = 0,
            ["windows_process_creation_file_time"] = 1,
            ["linux_proc_start_ticks"] = 2
        });
        AssertJsonIntegerMap(states.GetProperty("pid_namespace_mode"), new Dictionary<string, int>
        {
            ["recovery_enabled"] = 1,
            ["mixed_or_unproven"] = 2
        });
    }

    [Fact]
    public void ManifestPublishesOfflineOnlySnapshotsForEveryRequiredLifecycleState()
    {
        using JsonDocument document = LoadV2Manifest();
        string manifestDirectory = Path.GetDirectoryName(GetV2ManifestPath())!;
        JsonElement[] fixtures = document.RootElement.GetProperty("offline_fixtures").EnumerateArray().ToArray();
        string[] expectedStates =
        [
            "corrupt",
            "empty",
            "leased",
            "pending-removal",
            "published",
            "reclaimed",
            "recovering",
            "reserved",
            "spilled"
        ];

        Assert.Equal(
            expectedStates,
            fixtures.Select(static fixture => fixture.GetProperty("state").GetString()!).Order().ToArray());
        AssertUniqueJsonNames(fixtures, "state");
        foreach (JsonElement fixture in fixtures)
        {
            string state = fixture.GetProperty("state").GetString()!;
            Assert.True(fixture.GetProperty("offline_only").GetBoolean());
            string binaryPath = ResolveFixturePath(manifestDirectory, fixture.GetProperty("binary_path").GetString()!);
            string snapshotPath = ResolveFixturePath(manifestDirectory, fixture.GetProperty("snapshot_path").GetString()!);
            Assert.True(File.Exists(binaryPath), $"Offline binary fixture '{binaryPath}' is absent.");
            Assert.True(File.Exists(snapshotPath), $"Offline snapshot fixture '{snapshotPath}' is absent.");

            byte[] binary = File.ReadAllBytes(binaryPath);
            byte[] snapshotBytes = File.ReadAllBytes(snapshotPath);
            Assert.True(binary.Length >= 512, $"Offline binary fixture '{binaryPath}' is shorter than the SMS2 header.");
            Assert.True(binary.AsSpan(0, 4).SequenceEqual(new byte[] { 0x53, 0x4d, 0x53, 0x32 }));
            Assert.Equal(fixture.GetProperty("byte_length").GetInt64(), binary.LongLength);
            Assert.Equal(
                ReadFixedLowerHex(fixture, "binary_sha256_hex", 64),
                Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant());
            Assert.Equal(
                ReadFixedLowerHex(fixture, "snapshot_sha256_hex", 64),
                Convert.ToHexString(SHA256.HashData(snapshotBytes)).ToLowerInvariant());

            using JsonDocument snapshot = JsonDocument.Parse(snapshotBytes);
            Assert.True(snapshot.RootElement.GetProperty("offline_only").GetBoolean());
            Assert.Equal(state, snapshot.RootElement.GetProperty("state").GetString());
        }
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
    public void PublicSizingAndCreateHelperUseTheCanonicalV2Layout()
    {
        long publicBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(3, 17, 9, 7, 5, 64);
        long internalBytes = GetInt64(CreateLayout(
            slotCount: 3,
            leaseRecordCount: 5,
            participantCount: 64,
            maxKeyBytes: 7,
            maxDescriptorBytes: 9,
            maxValueBytes: 17), "RequiredBytes");

        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            $"sms-layout-contract-{Guid.NewGuid():N}",
            3,
            17,
            9,
            7,
            5,
            64);

        Assert.Equal(internalBytes, publicBytes);
        Assert.Equal(publicBytes, options.TotalBytes);
    }

    private static JsonDocument LoadV2Manifest() =>
        JsonDocument.Parse(File.ReadAllText(GetV2ManifestPath()));

    private static string GetV2ManifestPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharedMemoryStore.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "protocol", "fixtures", "v2.0", "manifest.json");
    }

    private static string[] ReadJsonStrings(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName)
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();

    private static void AssertJsonIntegerMap(
        JsonElement actual,
        IReadOnlyDictionary<string, int> expected)
    {
        Assert.Equal(
            expected.Keys.Order().ToArray(),
            actual.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        foreach ((string name, int value) in expected)
        {
            Assert.Equal(value, actual.GetProperty(name).GetInt32());
        }
    }

    private static void AssertJsonRecord(
        JsonElement records,
        string recordName,
        int expectedSize,
        params (string Name, int Offset)[] expectedFields)
    {
        JsonElement record = records.GetProperty(recordName);
        JsonElement fields = record.GetProperty("fields");
        Assert.Equal(expectedSize, record.GetProperty("size").GetInt32());
        Assert.Equal(
            expectedFields.Select(static field => field.Name).Order().ToArray(),
            fields.EnumerateObject().Select(static field => field.Name).Order().ToArray());
        foreach ((string name, int offset) in expectedFields)
        {
            Assert.Equal(offset, fields.GetProperty(name).GetInt32());
        }
    }

    private static void AssertJsonCodecVectors(JsonElement codecs, string family)
    {
        JsonElement[] vectors = codecs.GetProperty(family).EnumerateArray().ToArray();
        Assert.NotEmpty(vectors);
        AssertUniqueJsonNames(vectors);
        Assert.Contains(vectors, static vector => vector.GetProperty("valid").GetBoolean());
        Assert.Contains(vectors, static vector => !vector.GetProperty("valid").GetBoolean());

        foreach (JsonElement vector in vectors)
        {
            _ = ReadFixedLowerHex(vector, "encoded_hex", 16);
            if (vector.GetProperty("valid").GetBoolean())
            {
                JsonElement parts = vector.GetProperty("parts");
                Assert.Equal(JsonValueKind.Object, parts.ValueKind);
                Assert.NotEmpty(parts.EnumerateObject());
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(vector.GetProperty("reason").GetString()));
            }
        }
    }

    private static void AssertJsonLimit(
        JsonElement limits,
        string name,
        int minimum,
        int? maximum = null)
    {
        JsonElement limit = limits.GetProperty(name);
        Assert.Equal(minimum, limit.GetProperty("min").GetInt32());
        if (maximum.HasValue)
        {
            Assert.Equal(maximum.Value, limit.GetProperty("max").GetInt32());
        }

        Assert.Equal(
            maximum.HasValue ? new[] { "max", "min" } : new[] { "min" },
            limit.EnumerateObject().Select(static property => property.Name).Order().ToArray());
    }

    private static void AssertUniqueJsonNames(JsonElement[] values, string propertyName = "name")
    {
        string[] names = values.Select(value => value.GetProperty(propertyName).GetString()!).ToArray();
        Assert.All(names, static name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static object CreateLayoutFromJson(JsonElement input)
    {
        string[] expectedInputFields =
        [
            "lease_record_count",
            "max_descriptor_bytes",
            "max_key_bytes",
            "max_value_bytes",
            "participant_record_count",
            "slot_count"
        ];
        Assert.Equal(
            expectedInputFields,
            input.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        return CreateLayout(
            slotCount: input.GetProperty("slot_count").GetInt32(),
            leaseRecordCount: input.GetProperty("lease_record_count").GetInt32(),
            participantCount: input.GetProperty("participant_record_count").GetInt32(),
            maxKeyBytes: input.GetProperty("max_key_bytes").GetInt32(),
            maxDescriptorBytes: input.GetProperty("max_descriptor_bytes").GetInt32(),
            maxValueBytes: input.GetProperty("max_value_bytes").GetInt32());
    }

    private static byte[] ReadLowerHex(JsonElement parent, string propertyName)
    {
        string value = parent.GetProperty(propertyName).GetString()!;
        Assert.Equal(value.ToLowerInvariant(), value);
        Assert.Equal(0, value.Length % 2);
        return Convert.FromHexString(value);
    }

    private static string ReadFixedLowerHex(JsonElement parent, string propertyName, int expectedLength)
    {
        string value = parent.GetProperty(propertyName).GetString()!;
        Assert.Equal(expectedLength, value.Length);
        Assert.Equal(value.ToLowerInvariant(), value);
        _ = Convert.FromHexString(value);
        return value;
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 0xcbf2_9ce4_8422_2325;
        const ulong prime = 0x0000_0100_0000_01b3;
        ulong hash = offsetBasis;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash = unchecked(hash * prime);
        }

        return hash;
    }

    private static string GetString(object instance, params string[] candidateNames)
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
                return Assert.IsType<string>(property.GetValue(instance));
            }

            FieldInfo? field = type.GetFields(flags)
                .SingleOrDefault(candidate => Normalize(candidate.Name) == normalized);
            if (field is not null)
            {
                return Assert.IsType<string>(field.GetValue(instance));
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"{type.FullName} is missing string member {string.Join("/", candidateNames)}.");
    }

    private static string ResolveFixturePath(string manifestDirectory, string relativePath)
    {
        Assert.False(Path.IsPathRooted(relativePath));
        string root = Path.GetFullPath(manifestDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath));
        Assert.True(
            resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase),
            $"Fixture path '{relativePath}' escapes the v2.0 fixture directory.");
        return resolved;
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
