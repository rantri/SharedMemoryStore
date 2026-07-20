from __future__ import annotations

import hashlib
import json
from pathlib import Path
import struct
import unittest
import unicodedata


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPOSITORY_ROOT / "protocol" / "fixtures" / "v2.0" / "manifest.json"

INT32_MIN = -(1 << 31)
INT32_MAX = (1 << 31) - 1
INT64_MIN = -(1 << 63)
INT64_MAX = (1 << 63) - 1
MAXIMUM_SLOT_COUNT = 1_048_575
MAXIMUM_PARTICIPANT_COUNT = 1_048_575
MAXIMUM_GENERATION = (1 << 33) - 1

CODEC_FAMILIES = {
    "participant_token",
    "participant_control",
    "slot_control",
    "lease_control",
    "binding",
    "spill_summary",
    "directory_location",
    "directory_operation",
}

EXPECTED_RECORDS: dict[str, tuple[int, int, dict[str, int]]] = {
    "store_header": (
        512,
        64,
        {
            "magic": 0,
            "layout_major_version": 4,
            "layout_minor_version": 6,
            "header_length": 8,
            "resource_protocol_version": 12,
            "required_features": 16,
            "optional_features": 24,
            "total_bytes": 32,
            "store_id": 40,
            "control": 48,
            "sequence": 56,
            "slot_count": 64,
            "lease_record_count": 68,
            "participant_record_count": 72,
            "max_key_bytes": 76,
            "max_descriptor_bytes": 80,
            "max_value_bytes": 84,
            "participant_index_bits": 88,
            "participant_generation_bits": 92,
            "participant_offset": 96,
            "participant_length": 104,
            "participant_stride": 112,
            "primary_lane_count": 116,
            "primary_bucket_count": 120,
            "primary_bucket_stride": 124,
            "primary_directory_offset": 128,
            "primary_directory_length": 136,
            "overflow_directory_offset": 144,
            "overflow_directory_length": 152,
            "overflow_stride": 160,
            "lease_stride": 164,
            "lease_registry_offset": 168,
            "lease_registry_length": 176,
            "slot_metadata_stride": 184,
            "key_stride": 188,
            "slot_metadata_offset": 192,
            "slot_metadata_length": 200,
            "key_storage_offset": 208,
            "key_storage_length": 216,
            "descriptor_stride": 224,
            "payload_stride": 228,
            "descriptor_storage_offset": 232,
            "descriptor_storage_length": 240,
            "payload_storage_offset": 248,
            "payload_storage_length": 256,
            "pid_namespace_id": 264,
            "pid_namespace_mode": 272,
        },
    ),
    "participant": (
        64,
        64,
        {
            "control": 0,
            "identity_kind": 8,
            "reserved": 12,
            "process_start_value": 16,
            "open_sequence": 24,
            "pid_namespace_id": 32,
        },
    ),
    "primary_directory_bucket": (
        128,
        64,
        {"spill_summary": 0, "mutation": 8, "lanes": 16},
    ),
    "overflow_binding": (8, 8, {"binding": 0}),
    "lease": (64, 64, {"control": 0, "slot_binding": 8, "acquire_sequence": 16}),
    "value_slot": (
        128,
        64,
        {
            "control": 0,
            "directory_binding": 8,
            "directory_location": 16,
            "directory_operation": 24,
            "key_hash": 32,
            "key_length": 40,
            "descriptor_length": 44,
            "value_length": 48,
            "publication_intent": 52,
            "bytes_advanced": 56,
            "commit_sequence": 64,
            "key_offset": 72,
            "descriptor_offset": 80,
            "payload_offset": 88,
        },
    ),
}

EXPECTED_STATES = {
    "store": {"initializing": 1, "ready": 2, "corrupt": 3, "unsupported": 4},
    "participant": {
        "free": 0,
        "registering": 1,
        "active": 2,
        "closing": 3,
        "recovering": 4,
        "reclaiming": 5,
        "retired": 6,
    },
    "slot": {
        "free": 0,
        "initializing": 1,
        "reserved": 2,
        "published": 3,
        "remove_requested": 4,
        "aborting": 5,
        "reclaiming": 6,
        "retired": 7,
    },
    "lease": {
        "free": 0,
        "claiming": 1,
        "active": 2,
        "releasing": 3,
        "recovering": 4,
        "retired": 5,
    },
    "publication_intent": {
        "none": 0,
        "explicit_reservation": 1,
        "atomic_publication": 2,
    },
    "identity_kind": {
        "unknown": 0,
        "windows_process_creation_file_time": 1,
        "linux_proc_start_ticks": 2,
    },
    "pid_namespace_mode": {"recovery_enabled": 1, "mixed_or_unproven": 2},
}

EXPECTED_OPEN_MODES = {"create_new": 0, "open_existing": 1, "create_or_open": 2}

EXPECTED_OPEN_STATUSES = {
    "success": 0,
    "already_exists": 1,
    "not_found": 2,
    "invalid_options": 3,
    "incompatible_layout": 4,
    "unsupported_platform": 5,
    "insufficient_capacity": 6,
    "access_denied": 7,
    "mapping_failed": 8,
    "store_busy": 9,
    "operation_canceled": 10,
    "participant_table_full": 11,
}

EXPECTED_OPERATION_STATUSES = {
    "success": 0,
    "duplicate_key": 1,
    "not_found": 2,
    "key_too_large": 3,
    "value_too_large": 4,
    "descriptor_too_large": 5,
    "store_full": 6,
    "lease_table_full": 7,
    "invalid_lease": 8,
    "lease_already_released": 9,
    "remove_pending": 10,
    "unsupported_platform": 11,
    "store_disposed": 12,
    "corrupt_store": 13,
    "access_denied": 14,
    "unknown_failure": 15,
    "invalid_reservation": 16,
    "reservation_incomplete": 17,
    "reservation_already_completed": 18,
    "reservation_write_out_of_range": 19,
    "invalid_key": 20,
    "store_busy": 21,
    "operation_canceled": 22,
}

EXPECTED_OFFLINE_STATES = {
    "empty",
    "reserved",
    "published",
    "leased",
    "pending-removal",
    "spilled",
    "recovering",
    "reclaimed",
    "corrupt",
}


class LayoutInvalidArgument(ValueError):
    pass


class LayoutArithmeticOverflow(OverflowError):
    pass


def _checked_int32(value: int) -> int:
    if value < INT32_MIN or value > INT32_MAX:
        raise LayoutArithmeticOverflow
    return value


def _checked_int64(value: int) -> int:
    if value < INT64_MIN or value > INT64_MAX:
        raise LayoutArithmeticOverflow
    return value


def _align(value: int, alignment: int) -> int:
    return _checked_int64(_checked_int64(value + alignment - 1) & -alignment)


def _next_power_of_two(value: int) -> int:
    if value <= 0 or value > 1 << 30:
        raise LayoutArithmeticOverflow
    result = 1
    while result < value:
        result = _checked_int32(result << 1)
    return result


def _required_bits(distinct_values: int) -> int:
    return max(1, (distinct_values - 1).bit_length())


def _calculate_layout(
    *,
    slot_count: int,
    lease_record_count: int,
    participant_record_count: int,
    max_key_bytes: int,
    max_descriptor_bytes: int,
    max_value_bytes: int,
) -> dict[str, int]:
    values = (
        slot_count,
        lease_record_count,
        participant_record_count,
        max_key_bytes,
        max_descriptor_bytes,
        max_value_bytes,
    )
    for value in values:
        _checked_int32(value)

    if not 1 <= slot_count <= MAXIMUM_SLOT_COUNT:
        raise LayoutInvalidArgument
    if lease_record_count < 1:
        raise LayoutInvalidArgument
    if not 1 <= participant_record_count <= MAXIMUM_PARTICIPANT_COUNT:
        raise LayoutInvalidArgument
    if max_key_bytes < 1 or max_descriptor_bytes < 0 or max_value_bytes < 1:
        raise LayoutInvalidArgument

    participant_index_bits = _required_bits(participant_record_count + 1)
    participant_generation_bits = 28 - participant_index_bits
    if participant_generation_bits < 8:
        raise LayoutInvalidArgument

    participant_offset = 512
    participant_length = _checked_int64(participant_record_count * 64)
    primary_lane_count = _next_power_of_two(max(32, _checked_int32(slot_count * 4)))
    primary_bucket_count = primary_lane_count // 8
    primary_directory_offset = _align(participant_offset + participant_length, 64)
    primary_directory_length = _checked_int64(primary_bucket_count * 128)
    overflow_directory_offset = _align(
        primary_directory_offset + primary_directory_length, 8
    )
    overflow_directory_length = _checked_int64(slot_count * 8)
    lease_registry_offset = _align(
        overflow_directory_offset + overflow_directory_length, 64
    )
    lease_registry_length = _checked_int64(lease_record_count * 64)
    slot_metadata_offset = _align(lease_registry_offset + lease_registry_length, 64)
    slot_metadata_length = _checked_int64(slot_count * 128)
    key_stride = _checked_int32(_align(max(1, max_key_bytes), 8))
    key_storage_offset = _align(slot_metadata_offset + slot_metadata_length, 8)
    key_storage_length = _checked_int64(slot_count * key_stride)
    descriptor_stride = _checked_int32(_align(max(1, max_descriptor_bytes), 8))
    descriptor_storage_offset = _align(key_storage_offset + key_storage_length, 8)
    descriptor_storage_length = _checked_int64(slot_count * descriptor_stride)
    payload_stride = _checked_int32(_align(max(1, max_value_bytes), 8))
    payload_storage_offset = _align(
        descriptor_storage_offset + descriptor_storage_length, 8
    )
    payload_storage_length = _checked_int64(slot_count * payload_stride)
    required_bytes = _align(payload_storage_offset + payload_storage_length, 8)

    return {
        "header_length": 512,
        "participant_index_bits": participant_index_bits,
        "participant_generation_bits": participant_generation_bits,
        "participant_stride": 64,
        "participant_offset": participant_offset,
        "participant_length": participant_length,
        "primary_lane_count": primary_lane_count,
        "primary_bucket_count": primary_bucket_count,
        "primary_bucket_stride": 128,
        "primary_directory_offset": primary_directory_offset,
        "primary_directory_length": primary_directory_length,
        "overflow_stride": 8,
        "overflow_directory_offset": overflow_directory_offset,
        "overflow_directory_length": overflow_directory_length,
        "lease_stride": 64,
        "lease_registry_offset": lease_registry_offset,
        "lease_registry_length": lease_registry_length,
        "slot_metadata_stride": 128,
        "slot_metadata_offset": slot_metadata_offset,
        "slot_metadata_length": slot_metadata_length,
        "key_stride": key_stride,
        "key_storage_offset": key_storage_offset,
        "key_storage_length": key_storage_length,
        "descriptor_stride": descriptor_stride,
        "descriptor_storage_offset": descriptor_storage_offset,
        "descriptor_storage_length": descriptor_storage_length,
        "payload_stride": payload_stride,
        "payload_storage_offset": payload_storage_offset,
        "payload_storage_length": payload_storage_length,
        "required_bytes": required_bytes,
    }


def _fnv1a_64(value: bytes) -> int:
    result = 0xCBF29CE484222325
    for current in value:
        result ^= current
        result = (result * 0x00000100000001B3) & 0xFFFFFFFFFFFFFFFF
    return result


def _utf16_code_units(value: str) -> list[int]:
    encoded = value.encode("utf-16-le")
    return [
        int.from_bytes(encoded[index : index + 2], "little")
        for index in range(0, len(encoded), 2)
    ]


def _is_dotnet_letter_or_digit(code_unit: int) -> bool:
    category = unicodedata.category(chr(code_unit))
    return category.startswith("L") or category == "Nd"


def _derive_windows_resource(public_name: str) -> dict[str, str]:
    readable = "".join(
        chr(code_unit)
        if _is_dotnet_letter_or_digit(code_unit) or code_unit in (ord("-"), ord("_"))
        else "_"
        for code_unit in _utf16_code_units(public_name)
    )
    scope = "Global\\" if public_name[:7].lower() == "global\\" else "Local\\"
    return {
        "region_name": public_name,
        "synchronization_name": f"{scope}SharedMemoryStore-{readable}",
    }


def _derive_linux_resource(public_name: str) -> dict[str, object]:
    readable = "".join(
        chr(code_unit)
        if code_unit < 128
        and (chr(code_unit).isalnum() or code_unit in (ord("-"), ord("_"), ord(".")))
        else "_"
        for code_unit in _utf16_code_units(public_name)
    ).strip("_.")
    readable = (readable or "store")[:80]
    digest = hashlib.sha256(public_name.encode("utf-8")).hexdigest()[:16]
    fragment = f"sms-{readable}-{digest}"
    return {
        "sha256_prefix_hex": digest,
        "fragment": fragment,
        "files": {
            "region": f"{fragment}.region",
            "synchronization": f"{fragment}.lock",
            "owners": f"{fragment}.owners",
            "lifecycle": f"{fragment}.lifecycle",
        },
    }


def _encode_codec(family: str, parts: dict[str, object]) -> int:
    if family == "participant_token":
        count = int(parts["participant_record_count"])
        index = int(parts["record_index"])
        generation = int(parts["generation"])
        if not 1 <= count <= MAXIMUM_PARTICIPANT_COUNT:
            raise ValueError
        index_bits = _required_bits(count + 1)
        generation_mask = (1 << (28 - index_bits)) - 1
        if not 0 <= index < count or not 1 <= generation <= generation_mask:
            raise ValueError
        return (generation << index_bits) | index + 1

    if family == "participant_control":
        count = int(parts["participant_record_count"])
        state = int(parts["state"])
        incarnation = int(parts["incarnation"])
        process_id = int(parts["process_id"])
        if not 1 <= count <= MAXIMUM_PARTICIPANT_COUNT:
            raise ValueError
        generation_mask = (1 << (28 - _required_bits(count + 1))) - 1
        owned = state in (1, 2, 3, 4)
        if (
            not 0 <= state <= 6
            or not 1 <= incarnation <= generation_mask
            or (owned and process_id <= 0)
            or (not owned and process_id != 0)
            or process_id > INT32_MAX
            or (state == 6 and incarnation != generation_mask)
        ):
            raise ValueError
        return state | (incarnation << 3) | (process_id << 31)

    if family in ("slot_control", "lease_control"):
        state = int(parts["state"])
        generation = int(parts["generation"])
        participant_token = int(parts["participant_token"])
        maximum_state = 7 if family == "slot_control" else 5
        owned_states = (1, 2)
        if (
            not 0 <= state <= maximum_state
            or not 1 <= generation <= MAXIMUM_GENERATION
            or not 0 <= participant_token < 1 << 28
            or ((state in owned_states) != (participant_token != 0))
            or (state == maximum_state and generation != MAXIMUM_GENERATION)
        ):
            raise ValueError
        return state | (generation << 3) | (participant_token << 36)

    if family == "binding":
        if bool(parts.get("empty", False)):
            return 0
        index = int(parts["slot_index"])
        generation = int(parts["generation"])
        if not 0 <= index < (1 << 31) - 1 or not 1 <= generation <= MAXIMUM_GENERATION:
            raise ValueError
        return index + 1 | (generation << 31)

    if family == "spill_summary":
        if bool(parts.get("initial_empty", False)):
            return 0
        index = int(parts["slot_index"])
        generation = int(parts["generation"])
        present = bool(parts["present"])
        if not 0 <= index < MAXIMUM_SLOT_COUNT or not 1 <= generation <= MAXIMUM_GENERATION:
            raise ValueError
        return index + 1 | (generation << 20) | (int(present) << 53)

    if family == "directory_location":
        kind = int(parts["kind"])
        index = int(parts["index"])
        generation = int(parts["generation"])
        if kind == index == generation == 0:
            return 0
        if kind not in (1, 2) or not 0 <= index < 1 << 22 or not 1 <= generation <= MAXIMUM_GENERATION:
            raise ValueError
        return kind | (index << 2) | (generation << 24)

    if family == "directory_operation":
        intent = int(parts["intent"])
        phase = int(parts["phase"])
        target_kind = int(parts["target_kind"])
        target_index = int(parts["target_index"])
        generation = int(parts["generation"])
        if intent == phase == target_kind == target_index == generation == 0:
            return 0
        if intent not in (1, 2) or not 1 <= phase <= 5 or not 1 <= generation <= MAXIMUM_GENERATION:
            raise ValueError
        no_target = target_kind == target_index == 0
        valid_target = target_kind in (1, 2) and 0 <= target_index < 1 << 22
        if phase in (1, 4) and not no_target:
            raise ValueError
        if phase in (2, 3) and not valid_target:
            raise ValueError
        if phase == 5 and intent == 1 and not valid_target:
            raise ValueError
        if phase == 5 and intent == 2 and not (no_target or valid_target):
            raise ValueError
        return (
            intent
            | (phase << 2)
            | (target_kind << 5)
            | (target_index << 7)
            | (generation << 29)
        )

    raise AssertionError(f"Unknown codec family: {family}")


class ProtocolManifestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))

    def _require_keys(self, value: dict[str, object], keys: set[str], context: str) -> None:
        missing = sorted(keys - value.keys())
        self.assertFalse(missing, f"{context} is missing required keys: {missing}")

    def test_manifest_declares_sms2_as_the_only_creatable_and_readable_protocol(self) -> None:
        protocol = self.manifest["protocol"]
        self._require_keys(
            protocol,
            {
                "creatable_layouts",
                "readable_layouts",
                "noncurrent_mapping_policy",
                "layout_major",
                "layout_minor",
                "resource_protocol",
                "magic_ascii",
                "magic_integer_hex",
                "little_endian_bytes_hex",
                "byte_order",
                "required_architecture",
                "atomic_width",
                "atomic_alignment",
                "acquire_load_order",
                "release_store_order",
                "rmw_order",
                "required_features",
                "optional_features",
                "required_feature_bits",
                "incompatible_draft_required_feature_masks",
            },
            "protocol",
        )
        self.assertEqual(1, self.manifest["format_version"])
        self.assertEqual(["2.0"], protocol["creatable_layouts"])
        self.assertEqual(["2.0"], protocol["readable_layouts"])
        self.assertNotIn("retired_layouts", protocol)
        self.assertEqual(
            "reject-before-payload-access",
            protocol["noncurrent_mapping_policy"],
        )
        self.assertEqual(2, protocol["layout_major"])
        self.assertEqual(0, protocol["layout_minor"])
        self.assertEqual(2, protocol["resource_protocol"])
        self.assertEqual("SMS2", protocol["magic_ascii"])
        self.assertEqual("32534d53", protocol["magic_integer_hex"])
        self.assertEqual("534d5332", protocol["little_endian_bytes_hex"])
        self.assertEqual("little", protocol["byte_order"])
        self.assertEqual("x86_64", protocol["required_architecture"])
        self.assertEqual(8, protocol["atomic_width"])
        self.assertEqual(8, protocol["atomic_alignment"])
        self.assertEqual("acquire", protocol["acquire_load_order"])
        self.assertEqual("release", protocol["release_store_order"])
        self.assertEqual("sequentially-consistent", protocol["rmw_order"])
        self.assertEqual(7, protocol["required_features"])
        self.assertEqual(0, protocol["optional_features"])
        self.assertEqual(
            {
                "versioned_empty_spill_summary": 1,
                "publication_intent": 2,
                "pid_namespace_identity": 4,
            },
            protocol["required_feature_bits"],
        )
        self.assertEqual([0, 1, 3], protocol["incompatible_draft_required_feature_masks"])

    def test_every_record_size_alignment_and_field_offset_is_exact(self) -> None:
        records = self.manifest["records"]
        self.assertEqual(set(EXPECTED_RECORDS), set(records))
        for name, (size, alignment, fields) in EXPECTED_RECORDS.items():
            with self.subTest(record=name):
                record = records[name]
                self._require_keys(record, {"size", "alignment", "fields"}, f"record {name}")
                self.assertEqual(size, record["size"])
                self.assertEqual(alignment, record["alignment"])
                self.assertEqual(fields, record["fields"])
        self.assertEqual(8, records["primary_directory_bucket"]["lane_count"])

    def test_all_state_families_and_wire_assignments_are_exact(self) -> None:
        self.assertEqual(EXPECTED_STATES, self.manifest["states"])

    def test_every_control_codec_has_valid_boundary_and_malformed_vectors(self) -> None:
        self._require_keys(self.manifest, {"codec_vectors"}, "manifest")
        codecs = self.manifest["codec_vectors"]
        self.assertEqual(CODEC_FAMILIES, set(codecs))
        all_reasons: set[str] = set()
        for family in sorted(CODEC_FAMILIES):
            vectors = codecs[family]
            with self.subTest(codec=family):
                self.assertGreaterEqual(len(vectors), 4)
                self.assertEqual(len(vectors), len({vector["name"] for vector in vectors}))
                valid_count = 0
                invalid_count = 0
                for vector in vectors:
                    self._require_keys(
                        vector,
                        {"name", "valid", "encoded_hex"},
                        f"codec vector {family}/{vector.get('name', '<unnamed>')}",
                    )
                    raw = int(vector["encoded_hex"], 16)
                    self.assertEqual(f"{raw:016x}", vector["encoded_hex"])
                    self.assertLess(raw, 1 << 64)
                    if vector["valid"]:
                        valid_count += 1
                        self._require_keys(vector, {"parts"}, f"valid codec vector {family}")
                        self.assertEqual(raw, _encode_codec(family, vector["parts"]))
                    else:
                        invalid_count += 1
                        self._require_keys(vector, {"reason"}, f"invalid codec vector {family}")
                        self.assertIsInstance(vector["reason"], str)
                        self.assertTrue(vector["reason"])
                        all_reasons.add(vector["reason"])
                self.assertGreaterEqual(valid_count, 2)
                self.assertGreaterEqual(invalid_count, 2)
                self.assertTrue(any("terminal" in vector["name"] for vector in vectors))

        self.assertTrue(
            {
                "reserved_bits_nonzero",
                "zero_generation",
                "out_of_range",
                "invalid_state",
                "invalid_owner",
            }.issubset(all_reasons)
        )

    def test_sizing_limits_and_every_valid_and_invalid_vector_are_executable(self) -> None:
        sizing = self.manifest["sizing"]
        self._require_keys(sizing, {"limits", "valid_vectors", "invalid_vectors"}, "sizing")
        self.assertEqual(
            {
                "slot_count": {"min": 1, "max": MAXIMUM_SLOT_COUNT},
                "participant_record_count": {
                    "min": 1,
                    "max": MAXIMUM_PARTICIPANT_COUNT,
                },
                "lease_record_count": {"min": 1},
                "max_key_bytes": {"min": 1},
                "max_descriptor_bytes": {"min": 0},
                "max_value_bytes": {"min": 1},
            },
            sizing["limits"],
        )
        self.assertGreaterEqual(len(sizing["valid_vectors"]), 4)
        for vector in sizing["valid_vectors"]:
            with self.subTest(vector=vector["name"]):
                self.assertEqual(vector["expected"], _calculate_layout(**vector["input"]))

        errors = {
            "invalid_argument": LayoutInvalidArgument,
            "arithmetic_overflow": LayoutArithmeticOverflow,
        }
        observed_errors: set[str] = set()
        self.assertGreaterEqual(len(sizing["invalid_vectors"]), 8)
        for vector in sizing["invalid_vectors"]:
            with self.subTest(vector=vector["name"]):
                expected_error = vector["error"]
                observed_errors.add(expected_error)
                with self.assertRaises(errors[expected_error]):
                    _calculate_layout(**vector["input"])
        self.assertEqual(set(errors), observed_errors)

    def test_hash_and_exact_key_vectors_cover_binary_keys_and_collisions(self) -> None:
        self._require_keys(
            self.manifest,
            {"hash_vectors", "exact_key_vectors"},
            "manifest",
        )
        vectors = self.manifest["hash_vectors"]
        self.assertGreaterEqual(len(vectors), 6)
        self.assertEqual(len(vectors), len({vector["name"] for vector in vectors}))
        for vector in vectors:
            value = bytes.fromhex(vector["bytes_hex"])
            self.assertEqual(value.hex(), vector["bytes_hex"])
            self.assertEqual(f"{_fnv1a_64(value):016x}", vector["expected_hash_hex"])
            self.assertEqual(bool(value), vector["valid_store_key"])
        self.assertTrue(any("00" in vector["bytes_hex"] for vector in vectors))
        self.assertTrue(
            any(any(byte >= 0x80 for byte in bytes.fromhex(vector["bytes_hex"])) for vector in vectors)
        )

        exact_vectors = self.manifest["exact_key_vectors"]
        self.assertGreaterEqual(len(exact_vectors), 3)
        collision_seen = False
        for vector in exact_vectors:
            left = bytes.fromhex(vector["left_hex"])
            right = bytes.fromhex(vector["right_hex"])
            shared_hash = int(vector["shared_hash_hex"], 16)
            self.assertEqual(f"{shared_hash:016x}", vector["shared_hash_hex"])
            self.assertEqual(left == right, vector["equal"])
            collision_seen |= left != right
        self.assertTrue(collision_seen, "At least one distinct-key shared-hash vector is required.")

    def test_windows_and_linux_resource_name_vectors_match_protocol_two_identity(self) -> None:
        self._require_keys(self.manifest, {"resource_name_vectors"}, "manifest")
        vectors = self.manifest["resource_name_vectors"]
        self.assertEqual({"windows", "linux"}, set(vectors))
        self.assertGreaterEqual(len(vectors["windows"]), 4)
        self.assertGreaterEqual(len(vectors["linux"]), 4)
        for vector in vectors["windows"]:
            expected = _derive_windows_resource(vector["public_name"])
            self.assertEqual(expected["region_name"], vector["region_name"])
            self.assertEqual(
                expected["synchronization_name"],
                vector["synchronization_name"],
            )
        for vector in vectors["linux"]:
            expected = _derive_linux_resource(vector["public_name"])
            self.assertEqual(expected["sha256_prefix_hex"], vector["sha256_prefix_hex"])
            self.assertEqual(expected["fragment"], vector["fragment"])
            self.assertEqual(expected["files"], vector["files"])
            owner_token = vector["owner_token"]
            self.assertEqual(32, len(owner_token))
            self.assertEqual(owner_token, f"{int(owner_token, 16):032x}")
            owners = expected["files"]["owners"]
            self.assertEqual(
                f"{owners}.artifacts/anchor.{owner_token}",
                vector["owner_anchor"],
            )
            self.assertEqual(
                f"{owners}.artifacts/released.{owner_token}.ready",
                vector["release_marker"],
            )

    def test_open_mode_assignments_are_exact(self) -> None:
        self._require_keys(self.manifest, {"open_modes"}, "manifest")
        self.assertEqual(EXPECTED_OPEN_MODES, self.manifest["open_modes"])

    def test_public_status_assignments_are_complete_and_exact(self) -> None:
        self._require_keys(self.manifest, {"statuses"}, "manifest")
        self.assertEqual(
            {"open": EXPECTED_OPEN_STATUSES, "operation": EXPECTED_OPERATION_STATUSES},
            self.manifest["statuses"],
        )

    def test_all_nine_mapped_fixtures_are_integrity_checked_and_offline_only(self) -> None:
        self._require_keys(self.manifest, {"offline_fixtures"}, "manifest")
        fixtures = self.manifest["offline_fixtures"]
        self.assertEqual(EXPECTED_OFFLINE_STATES, {fixture["state"] for fixture in fixtures})
        fixture_root = MANIFEST_PATH.parent.resolve()
        for fixture in fixtures:
            with self.subTest(state=fixture["state"]):
                self._require_keys(
                    fixture,
                    {
                        "state",
                        "binary_path",
                        "snapshot_path",
                        "byte_length",
                        "binary_sha256_hex",
                        "snapshot_sha256_hex",
                        "offline_only",
                    },
                    f"offline fixture {fixture['state']}",
                )
                self.assertIs(fixture["offline_only"], True)
                binary_path = (fixture_root / fixture["binary_path"]).resolve()
                snapshot_path = (fixture_root / fixture["snapshot_path"]).resolve()
                self.assertTrue(binary_path.is_relative_to(fixture_root))
                self.assertTrue(snapshot_path.is_relative_to(fixture_root))
                binary = binary_path.read_bytes()
                snapshot_bytes = snapshot_path.read_bytes()
                self.assertEqual(fixture["byte_length"], len(binary))
                self.assertEqual(
                    fixture["binary_sha256_hex"],
                    hashlib.sha256(binary).hexdigest(),
                )
                self.assertEqual(
                    fixture["snapshot_sha256_hex"],
                    hashlib.sha256(snapshot_bytes).hexdigest(),
                )
                self.assertEqual(b"SMS2", binary[:4])
                self.assertEqual((2, 0, 512), struct.unpack_from("<HHi", binary, 4))

                snapshot = json.loads(snapshot_bytes)
                self.assertIs(snapshot["offline_only"], True)
                self.assertEqual(fixture["state"], snapshot["state"])
                self.assertEqual(
                    {
                        "layout_major": 2,
                        "layout_minor": 0,
                        "resource_protocol": 2,
                        "required_features": 7,
                        "optional_features": 0,
                    },
                    snapshot["protocol"],
                )


if __name__ == "__main__":
    unittest.main()
