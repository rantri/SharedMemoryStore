from __future__ import annotations

import ctypes
import hashlib
import json
from pathlib import Path
import struct
import unittest
import unicodedata


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPOSITORY_ROOT / "protocol" / "fixtures" / "v1.2" / "manifest.json"

INT32_MIN = -(1 << 31)
INT32_MAX = (1 << 31) - 1
INT64_MIN = -(1 << 63)
INT64_MAX = (1 << 63) - 1


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


def _align_int32(value: int) -> int:
    return _checked_int32(_checked_int32(value + 7) & ~7)


def _align_int64(value: int) -> int:
    return _checked_int64(_checked_int64(value + 7) & ~7)


def _next_power_of_two(value: int) -> int:
    if value <= 0 or value > 1 << 30:
        raise LayoutArithmeticOverflow

    result = 1
    while result < value:
        result = _checked_int32(result << 1)
    return result


def _calculate_layout(
    *,
    slot_count: int,
    max_value_bytes: int,
    max_descriptor_bytes: int,
    max_key_bytes: int,
    lease_record_count: int,
) -> dict[str, int]:
    if slot_count < 1:
        raise LayoutInvalidArgument
    if max_value_bytes < 1:
        raise LayoutInvalidArgument
    if max_descriptor_bytes < 0:
        raise LayoutInvalidArgument
    if max_key_bytes < 1:
        raise LayoutInvalidArgument
    if lease_record_count < 1:
        raise LayoutInvalidArgument

    for value in (
        slot_count,
        max_value_bytes,
        max_descriptor_bytes,
        max_key_bytes,
        lease_record_count,
    ):
        _checked_int32(value)

    header_length = _align_int32(160)
    doubled_slots = _checked_int32(slot_count * 2)
    index_entry_count = _next_power_of_two(max(4, doubled_slots))
    index_entry_size = _align_int32(_checked_int32(32 + max_key_bytes))
    index_offset = header_length
    index_length = _checked_int64(index_entry_count * index_entry_size)
    lease_registry_offset = _align_int64(_checked_int64(index_offset + index_length))
    lease_registry_length = _checked_int64(lease_record_count * 40)
    slot_metadata_offset = _align_int64(
        _checked_int64(lease_registry_offset + lease_registry_length)
    )
    slot_metadata_length = _checked_int64(slot_count * 72)
    descriptor_stride = _align_int32(max(1, max_descriptor_bytes))
    descriptor_storage_offset = _align_int64(
        _checked_int64(slot_metadata_offset + slot_metadata_length)
    )
    descriptor_storage_length = _checked_int64(slot_count * descriptor_stride)
    payload_stride = _align_int32(max(1, max_value_bytes))
    payload_storage_offset = _align_int64(
        _checked_int64(descriptor_storage_offset + descriptor_storage_length)
    )
    payload_storage_length = _checked_int64(slot_count * payload_stride)
    required_bytes = _align_int64(
        _checked_int64(payload_storage_offset + payload_storage_length)
    )

    return {
        "header_length": header_length,
        "index_entry_count": index_entry_count,
        "index_entry_size": index_entry_size,
        "index_offset": index_offset,
        "index_length": index_length,
        "lease_registry_offset": lease_registry_offset,
        "lease_registry_length": lease_registry_length,
        "slot_metadata_offset": slot_metadata_offset,
        "slot_metadata_length": slot_metadata_length,
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
    return [int.from_bytes(encoded[index : index + 2], "little") for index in range(0, len(encoded), 2)]


def _is_dotnet_letter_or_digit(code_unit: int) -> bool:
    category = unicodedata.category(chr(code_unit))
    return category.startswith("L") or category == "Nd"


def _derive_resource_names(public_name: str) -> dict[str, object]:
    code_units = _utf16_code_units(public_name)

    windows_readable = "".join(
        chr(code_unit)
        if _is_dotnet_letter_or_digit(code_unit) or code_unit in (ord("-"), ord("_"))
        else "_"
        for code_unit in code_units
    )
    scope = "Global\\" if public_name[:7].lower() == "global\\" else "Local\\"

    linux_readable = "".join(
        chr(code_unit)
        if code_unit < 128
        and (chr(code_unit).isalnum() or code_unit in (ord("-"), ord("_"), ord(".")))
        else "_"
        for code_unit in code_units
    ).strip("_.")
    linux_readable = (linux_readable or "store")[:80]
    digest = hashlib.sha256(public_name.encode("utf-8")).hexdigest()[:16]
    fragment = f"sms-{linux_readable}-{digest}"

    return {
        "utf16_code_units": len(code_units),
        "windows_region_name": public_name,
        "windows_synchronization_name": f"{scope}SharedMemoryStore-{windows_readable}",
        "linux_sha256_prefix_hex": digest,
        "linux_fragment": fragment,
        "linux_files": {
            "region": f"{fragment}.region",
            "synchronization": f"{fragment}.lock",
            "owners": f"{fragment}.owners",
            "lifecycle": f"{fragment}.lifecycle",
        },
    }


def _read_int32(region: bytes, offset: int) -> int:
    return struct.unpack_from("<i", region, offset)[0]


def _read_int64(region: bytes, offset: int) -> int:
    return struct.unpack_from("<q", region, offset)[0]


def _read_uint64(region: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", region, offset)[0]


def _normalize_mapped_region(fixture_name: str, region: bytes) -> dict[str, object]:
    index_state_names = ("Empty", "Occupied", "Tombstone")
    slot_state_names = ("Free", "Publishing", "Published", "RemoveRequested", "Reclaiming")
    lease_state_names = ("Free", "Active", "Released", "Abandoned")

    index_entries: list[dict[str, object]] = []
    index_count = _read_int32(region, 44)
    index_stride = _read_int32(region, 48)
    index_offset = _read_int64(region, 56)
    for entry_index in range(index_count):
        offset = index_offset + (entry_index * index_stride)
        state = _read_int32(region, offset)
        if state == 0:
            continue
        key_length = _read_int32(region, offset + 4)
        index_entries.append(
            {
                "entry_index": entry_index,
                "state": state,
                "state_name": index_state_names[state],
                "key_hex": region[offset + 32 : offset + 32 + key_length].hex(),
                "key_hash_hex": f"{_read_uint64(region, offset + 8):016x}",
                "slot_index": _read_int32(region, offset + 16),
                "slot_generation": _read_int32(region, offset + 20),
                "slot_reuse_epoch": _read_int64(region, offset + 24),
            }
        )

    lease_records: list[dict[str, object]] = []
    lease_count = _read_int32(region, 28)
    lease_offset = _read_int64(region, 72)
    for record_index in range(lease_count):
        offset = lease_offset + (record_index * 40)
        state = _read_int32(region, offset)
        if state == 0:
            continue
        lease_records.append(
            {
                "record_id": _read_int32(region, offset + 4),
                "state": state,
                "state_name": lease_state_names[state],
                "slot_index": _read_int32(region, offset + 8),
                "slot_generation": _read_int32(region, offset + 12),
                "slot_reuse_epoch": _read_int64(region, offset + 16),
                "owner_process_id": _read_int32(region, offset + 24),
                "acquire_sequence": _read_int64(region, offset + 32),
            }
        )

    slots: list[dict[str, object]] = []
    slot_count = _read_int32(region, 24)
    slot_offset = _read_int64(region, 88)
    for slot_index in range(slot_count):
        offset = slot_offset + (slot_index * 72)
        state = _read_int32(region, offset)
        descriptor_length = _read_int32(region, offset + 24)
        payload_length = _read_int32(region, offset + 28)
        descriptor_offset = _read_int64(region, offset + 48)
        payload_offset = _read_int64(region, offset + 56)
        slots.append(
            {
                "slot_index": slot_index,
                "state": state,
                "state_name": slot_state_names[state],
                "generation": _read_int32(region, offset + 4),
                "reuse_epoch": _read_int64(region, offset + 8),
                "usage_count": _read_int32(region, offset + 16),
                "key_length": _read_int32(region, offset + 20),
                "descriptor_hex": region[
                    descriptor_offset : descriptor_offset + descriptor_length
                ].hex(),
                "payload_hex": region[payload_offset : payload_offset + payload_length].hex(),
                "publisher_process_id": _read_int32(region, offset + 32),
                "reservation_bytes_written": _read_int32(region, offset + 36),
                "key_hash_hex": f"{_read_uint64(region, offset + 40):016x}",
                "descriptor_offset": descriptor_offset,
                "payload_offset": payload_offset,
                "committed_sequence": _read_int64(region, offset + 64),
            }
        )

    return {
        "format_version": 1,
        "fixture": fixture_name,
        "offline_only": True,
        "protocol": {
            "layout_major": _read_int32(region, 4),
            "layout_minor": _read_int32(region, 8),
            "byte_order": "little",
        },
        "header": {
            "magic_hex": f"{_read_int32(region, 0) & 0xFFFFFFFF:08x}",
            "header_length": _read_int32(region, 12),
            "total_bytes": _read_int64(region, 16),
            "slot_count": slot_count,
            "lease_record_count": lease_count,
            "max_key_bytes": _read_int32(region, 32),
            "max_descriptor_bytes": _read_int32(region, 36),
            "max_value_bytes": _read_int32(region, 40),
            "index_entry_count": index_count,
            "index_entry_size": index_stride,
            "index_offset": index_offset,
            "index_length": _read_int64(region, 64),
            "lease_registry_offset": lease_offset,
            "lease_registry_length": _read_int64(region, 80),
            "slot_metadata_offset": slot_offset,
            "slot_metadata_length": _read_int64(region, 96),
            "descriptor_storage_offset": _read_int64(region, 104),
            "descriptor_storage_length": _read_int64(region, 112),
            "payload_storage_offset": _read_int64(region, 120),
            "payload_storage_length": _read_int64(region, 128),
            "store_id_hex": f"{_read_uint64(region, 136):016x}",
            "store_state": _read_int32(region, 144),
            "sequence": _read_int64(region, 152),
        },
        "index_entries": index_entries,
        "lease_records": lease_records,
        "slots": slots,
    }


class StoreHeader(ctypes.LittleEndianStructure):
    _pack_ = 8
    _fields_ = [
        ("magic", ctypes.c_int32),
        ("layout_major_version", ctypes.c_int32),
        ("layout_minor_version", ctypes.c_int32),
        ("header_length", ctypes.c_int32),
        ("total_bytes", ctypes.c_int64),
        ("slot_count", ctypes.c_int32),
        ("lease_record_count", ctypes.c_int32),
        ("max_key_bytes", ctypes.c_int32),
        ("max_descriptor_bytes", ctypes.c_int32),
        ("max_value_bytes", ctypes.c_int32),
        ("index_entry_count", ctypes.c_int32),
        ("index_entry_size", ctypes.c_int32),
        ("index_offset", ctypes.c_int64),
        ("index_length", ctypes.c_int64),
        ("lease_registry_offset", ctypes.c_int64),
        ("lease_registry_length", ctypes.c_int64),
        ("slot_metadata_offset", ctypes.c_int64),
        ("slot_metadata_length", ctypes.c_int64),
        ("descriptor_storage_offset", ctypes.c_int64),
        ("descriptor_storage_length", ctypes.c_int64),
        ("payload_storage_offset", ctypes.c_int64),
        ("payload_storage_length", ctypes.c_int64),
        ("store_id", ctypes.c_int64),
        ("store_state", ctypes.c_int32),
        ("reserved", ctypes.c_int32),
        ("sequence", ctypes.c_int64),
    ]


class IndexEntryHeader(ctypes.LittleEndianStructure):
    _pack_ = 8
    _fields_ = [
        ("state", ctypes.c_int32),
        ("key_length", ctypes.c_int32),
        ("key_hash", ctypes.c_uint64),
        ("slot_index", ctypes.c_int32),
        ("slot_generation", ctypes.c_int32),
        ("slot_reuse_epoch", ctypes.c_int64),
    ]


class SlotMetadata(ctypes.LittleEndianStructure):
    _pack_ = 8
    _fields_ = [
        ("state", ctypes.c_int32),
        ("generation", ctypes.c_int32),
        ("reuse_epoch", ctypes.c_int64),
        ("usage_count", ctypes.c_int32),
        ("key_length", ctypes.c_int32),
        ("descriptor_length", ctypes.c_int32),
        ("value_length", ctypes.c_int32),
        ("publisher_process_id", ctypes.c_int32),
        ("reserved", ctypes.c_int32),
        ("key_hash", ctypes.c_uint64),
        ("descriptor_offset", ctypes.c_int64),
        ("payload_offset", ctypes.c_int64),
        ("committed_sequence", ctypes.c_int64),
    ]


class LeaseRecord(ctypes.LittleEndianStructure):
    _pack_ = 8
    _fields_ = [
        ("state", ctypes.c_int32),
        ("lease_record_id", ctypes.c_int32),
        ("slot_index", ctypes.c_int32),
        ("slot_generation", ctypes.c_int32),
        ("slot_reuse_epoch", ctypes.c_int64),
        ("owner_process_id", ctypes.c_int32),
        ("reserved", ctypes.c_int32),
        ("acquire_sequence", ctypes.c_int64),
    ]


class ProtocolManifestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))

    def test_protocol_identity_and_magic_are_exact(self) -> None:
        protocol = self.manifest["protocol"]
        self.assertEqual(1, self.manifest["format_version"])
        self.assertEqual(1, protocol["layout_major"])
        self.assertEqual(2, protocol["layout_minor"])
        self.assertEqual(1, protocol["resource_naming_version"])
        self.assertEqual("little", protocol["byte_order"])
        self.assertEqual(8, protocol["max_field_alignment"])

        magic = protocol["magic"]
        self.assertEqual("SMS1", magic["ascii"])
        self.assertEqual(0x31534D53, magic["integer_value"])
        self.assertEqual(f"{magic['integer_value']:08x}", magic["integer_hex"])
        self.assertEqual(
            magic["ascii"].encode("ascii"),
            bytes.fromhex(magic["little_endian_bytes_hex"]),
        )
        self.assertEqual(
            magic["integer_value"],
            int.from_bytes(bytes.fromhex(magic["little_endian_bytes_hex"]), "little"),
        )

    def test_every_record_size_type_offset_and_padding(self) -> None:
        structures = {
            "store_header": StoreHeader,
            "index_entry_header": IndexEntryHeader,
            "slot_metadata": SlotMetadata,
            "lease_record": LeaseRecord,
        }
        type_names = {
            ctypes.c_int32: "int32",
            ctypes.c_int64: "int64",
            ctypes.c_uint64: "uint64",
        }

        self.assertEqual(set(structures), set(self.manifest["records"]))
        for record_name, structure in structures.items():
            with self.subTest(record=record_name):
                record = self.manifest["records"][record_name]
                self.assertEqual(record["size"], ctypes.sizeof(structure))
                declared_fields = dict(structure._fields_)
                self.assertEqual(set(record["fields"]), set(declared_fields))
                occupied: set[int] = set()
                for field_name, field_type in structure._fields_:
                    expected = record["fields"][field_name]
                    descriptor = getattr(structure, field_name)
                    self.assertEqual(expected["offset"], descriptor.offset)
                    self.assertEqual(expected["type"], type_names[field_type])
                    occupied.update(range(descriptor.offset, descriptor.offset + ctypes.sizeof(field_type)))

                actual_padding = sorted(set(range(ctypes.sizeof(structure))) - occupied)
                expected_padding = sorted(
                    byte
                    for padding in record["padding"]
                    for byte in range(padding["offset"], padding["offset"] + padding["length"])
                )
                self.assertEqual(expected_padding, actual_padding)

        self.assertEqual(
            self.manifest["records"]["index_entry_header"]["size"],
            self.manifest["records"]["index_entry_header"]["inline_key_offset"],
        )

    def test_state_open_mode_and_status_assignments_are_exact(self) -> None:
        self.assertEqual(
            {
                "store": {"Initializing": 0, "Ready": 1, "Disposing": 2, "Corrupt": 3, "Unsupported": 4},
                "index": {"Empty": 0, "Occupied": 1, "Tombstone": 2},
                "slot": {"Free": 0, "Publishing": 1, "Published": 2, "RemoveRequested": 3, "Reclaiming": 4},
                "lease": {"Free": 0, "Active": 1, "Released": 2, "Abandoned": 3},
            },
            self.manifest["states"],
        )
        self.assertEqual(
            {"CreateNew": 0, "OpenExisting": 1, "CreateOrOpen": 2},
            self.manifest["open_modes"],
        )
        self.assertEqual(
            {
                "Success": 0,
                "AlreadyExists": 1,
                "NotFound": 2,
                "InvalidOptions": 3,
                "IncompatibleLayout": 4,
                "UnsupportedPlatform": 5,
                "InsufficientCapacity": 6,
                "AccessDenied": 7,
                "MappingFailed": 8,
                "StoreBusy": 9,
                "OperationCanceled": 10,
            },
            self.manifest["status_values"]["store_open_status"],
        )
        self.assertEqual(
            {
                "Success": 0,
                "DuplicateKey": 1,
                "NotFound": 2,
                "KeyTooLarge": 3,
                "ValueTooLarge": 4,
                "DescriptorTooLarge": 5,
                "StoreFull": 6,
                "LeaseTableFull": 7,
                "InvalidLease": 8,
                "LeaseAlreadyReleased": 9,
                "RemovePending": 10,
                "UnsupportedPlatform": 11,
                "StoreDisposed": 12,
                "CorruptStore": 13,
                "AccessDenied": 14,
                "UnknownFailure": 15,
                "InvalidReservation": 16,
                "ReservationIncomplete": 17,
                "ReservationAlreadyCompleted": 18,
                "ReservationWriteOutOfRange": 19,
                "InvalidKey": 20,
                "StoreBusy": 21,
                "OperationCanceled": 22,
            },
            self.manifest["status_values"]["store_status"],
        )

    def test_every_fnv1a_vector(self) -> None:
        specification = self.manifest["fnv1a_64"]
        self.assertEqual(0xCBF29CE484222325, int(specification["offset_basis_hex"], 16))
        self.assertEqual(0x00000100000001B3, int(specification["prime_hex"], 16))
        names: set[str] = set()
        for vector in specification["vectors"]:
            with self.subTest(vector=vector["name"]):
                self.assertNotIn(vector["name"], names)
                names.add(vector["name"])
                value = bytes.fromhex(vector["bytes_hex"])
                self.assertEqual(bool(value), vector["valid_store_key"])
                self.assertEqual(16, len(vector["expected_hash_hex"]))
                self.assertEqual(
                    int(vector["expected_hash_hex"], 16),
                    _fnv1a_64(value),
                )

    def test_every_successful_layout_vector(self) -> None:
        names: set[str] = set()
        for vector in self.manifest["layout_calculation"]["vectors"]:
            with self.subTest(vector=vector["name"]):
                self.assertNotIn(vector["name"], names)
                names.add(vector["name"])
                self.assertEqual(vector["expected"], _calculate_layout(**vector["input"]))

    def test_every_layout_error_vector(self) -> None:
        error_types = {
            "invalid_argument": LayoutInvalidArgument,
            "arithmetic_overflow": LayoutArithmeticOverflow,
        }
        names: set[str] = set()
        for vector in self.manifest["layout_calculation"]["error_vectors"]:
            with self.subTest(vector=vector["name"]):
                self.assertNotIn(vector["name"], names)
                names.add(vector["name"])
                expected_error = vector["expected_error"]
                self.assertIn(expected_error, error_types)
                with self.assertRaises(error_types[expected_error]):
                    _calculate_layout(**vector["input"])

    def test_every_offline_mapped_region_fixture_and_snapshot(self) -> None:
        expected_names = {
            "empty",
            "published",
            "pending-reservation",
            "pending-removal",
            "reused-slot",
        }
        fixtures = self.manifest["mapped_region_fixtures"]
        self.assertEqual(expected_names, {fixture["name"] for fixture in fixtures})

        for fixture in fixtures:
            with self.subTest(fixture=fixture["name"]):
                self.assertTrue(fixture["offline_only"])
                binary_path = MANIFEST_PATH.parent / fixture["binary_file"]
                snapshot_path = MANIFEST_PATH.parent / fixture["snapshot_file"]
                region = binary_path.read_bytes()
                snapshot_bytes = snapshot_path.read_bytes()

                self.assertEqual(fixture["byte_length"], len(region))
                self.assertEqual(
                    fixture["binary_sha256_hex"], hashlib.sha256(region).hexdigest()
                )
                self.assertEqual(
                    fixture["snapshot_sha256_hex"],
                    hashlib.sha256(snapshot_bytes).hexdigest(),
                )

                snapshot = json.loads(snapshot_bytes)
                self.assertEqual(
                    snapshot,
                    _normalize_mapped_region(fixture["name"], region),
                )

    def test_every_resource_name_vector(self) -> None:
        specification = self.manifest["resource_naming"]
        self.assertEqual(1, specification["version"])
        self.assertEqual("0700", specification["linux_directory_mode_octal"])
        self.assertEqual("0600", specification["linux_file_mode_octal"])
        names: set[str] = set()
        linux_fragments: set[str] = set()
        for vector in specification["vectors"]:
            with self.subTest(vector=vector["name"]):
                self.assertNotIn(vector["name"], names)
                names.add(vector["name"])
                derived = _derive_resource_names(vector["public_name"])
                self.assertEqual(vector["utf16_code_units"], derived.pop("utf16_code_units"))
                self.assertLessEqual(
                    vector["utf16_code_units"],
                    specification["maximum_public_name_utf16_code_units"],
                )
                self.assertEqual(vector["expected"], derived)
                fragment = vector["expected"]["linux_fragment"]
                self.assertNotIn(fragment, linux_fragments)
                linux_fragments.add(fragment)
                readable = fragment.removeprefix("sms-").rsplit("-", 1)[0]
                self.assertLessEqual(
                    len(_utf16_code_units(readable)),
                    specification["maximum_linux_readable_utf16_code_units"],
                )


if __name__ == "__main__":
    unittest.main()
