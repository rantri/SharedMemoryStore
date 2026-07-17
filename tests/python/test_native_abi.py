from __future__ import annotations

import ctypes
from dataclasses import FrozenInstanceError
import inspect
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import shared_memory_store as sms
from shared_memory_store import _native


EXPECTED_LAYOUT_FIELD_QUERIES = {
    "header.magic": (0, 0),
    "header.layout_major_version": (1, 4),
    "header.layout_minor_version": (2, 6),
    "header.header_length": (3, 8),
    "header.resource_protocol_version": (4, 12),
    "header.required_features": (5, 16),
    "header.optional_features": (6, 24),
    "header.total_bytes": (7, 32),
    "header.store_id": (8, 40),
    "header.control": (9, 48),
    "header.sequence": (10, 56),
    "header.slot_count": (11, 64),
    "header.lease_record_count": (12, 68),
    "header.participant_record_count": (13, 72),
    "header.max_key_bytes": (14, 76),
    "header.max_descriptor_bytes": (15, 80),
    "header.max_value_bytes": (16, 84),
    "header.participant_index_bits": (17, 88),
    "header.participant_generation_bits": (18, 92),
    "header.participant_offset": (19, 96),
    "header.participant_length": (20, 104),
    "header.participant_stride": (21, 112),
    "header.primary_lane_count": (22, 116),
    "header.primary_bucket_count": (23, 120),
    "header.primary_bucket_stride": (24, 124),
    "header.primary_directory_offset": (25, 128),
    "header.primary_directory_length": (26, 136),
    "header.overflow_directory_offset": (27, 144),
    "header.overflow_directory_length": (28, 152),
    "header.overflow_stride": (29, 160),
    "header.lease_stride": (30, 164),
    "header.lease_registry_offset": (31, 168),
    "header.lease_registry_length": (32, 176),
    "header.slot_metadata_stride": (33, 184),
    "header.key_stride": (34, 188),
    "header.slot_metadata_offset": (35, 192),
    "header.slot_metadata_length": (36, 200),
    "header.key_storage_offset": (37, 208),
    "header.key_storage_length": (38, 216),
    "header.descriptor_stride": (39, 224),
    "header.payload_stride": (40, 228),
    "header.descriptor_storage_offset": (41, 232),
    "header.descriptor_storage_length": (42, 240),
    "header.payload_storage_offset": (43, 248),
    "header.payload_storage_length": (44, 256),
    "header.pid_namespace_id": (45, 264),
    "header.pid_namespace_mode": (46, 272),
    "participant.control": (100, 0),
    "participant.identity_kind": (101, 8),
    "participant.reserved": (102, 12),
    "participant.process_start_value": (103, 16),
    "participant.open_sequence": (104, 24),
    "participant.pid_namespace_id": (105, 32),
    "primary_directory_bucket.spill_summary": (200, 0),
    "primary_directory_bucket.mutation": (201, 8),
    "primary_directory_bucket.lanes": (202, 16),
    "overflow_binding.binding": (300, 0),
    "lease.control": (400, 0),
    "lease.slot_binding": (401, 8),
    "lease.acquire_sequence": (402, 16),
    "value_slot.control": (500, 0),
    "value_slot.directory_binding": (501, 8),
    "value_slot.directory_location": (502, 16),
    "value_slot.directory_operation": (503, 24),
    "value_slot.key_hash": (504, 32),
    "value_slot.key_length": (505, 40),
    "value_slot.descriptor_length": (506, 44),
    "value_slot.value_length": (507, 48),
    "value_slot.publication_intent": (508, 52),
    "value_slot.bytes_advanced": (509, 56),
    "value_slot.commit_sequence": (510, 64),
    "value_slot.key_offset": (511, 72),
    "value_slot.descriptor_offset": (512, 80),
    "value_slot.payload_offset": (513, 88),
}


class _Function:
    pass


class _SignatureLibrary:
    def __init__(self) -> None:
        self._functions: dict[str, _Function] = {}

    def __getattr__(self, name: str) -> _Function:
        return self._functions.setdefault(name, _Function())


class NativeAbiTests(unittest.TestCase):
    def test_public_protocol_constants_are_abi2_sms2_only(self) -> None:
        self.assertEqual(0x00020000, sms.ABI_VERSION)
        self.assertEqual(2, sms.LAYOUT_MAJOR_VERSION)
        self.assertEqual(0, sms.LAYOUT_MINOR_VERSION)
        self.assertEqual(2, getattr(sms, "RESOURCE_PROTOCOL_VERSION", None))
        self.assertEqual(7, getattr(sms, "REQUIRED_FEATURES", None))
        self.assertEqual(0, getattr(sms, "OPTIONAL_FEATURES", None))

    def test_open_and_operation_status_numbers_are_exact(self) -> None:
        self.assertEqual([0, 1, 2], [int(value) for value in sms.OpenMode])
        self.assertEqual(list(range(12)), [int(value) for value in sms.StoreOpenStatus])
        self.assertEqual(
            11,
            int(getattr(sms.StoreOpenStatus, "PARTICIPANT_TABLE_FULL", -1)),
        )
        self.assertEqual(list(range(23)), [int(value) for value in sms.StoreStatus])

    def test_v1_constants_and_layout_fields_are_not_exposed(self) -> None:
        for module in (sms, _native):
            with self.subTest(module=module.__name__):
                self.assertFalse(hasattr(module, "RESOURCE_NAMING_VERSION"))
                self.assertFalse(hasattr(module, "INDEX_ENTRY_HEADER_SIZE"))

        protocol_fields = {name for name, _ in _native.ProtocolInfo._fields_}
        layout_fields = {name for name, _ in _native.StoreLayout._fields_}
        for retired in ("resource_naming_version", "index_entry_header_size"):
            self.assertNotIn(retired, protocol_fields)
        for retired in ("index_entry_count", "index_entry_size", "index_offset", "index_length"):
            self.assertNotIn(retired, layout_fields)

    def test_wait_options_carries_one_borrowed_opaque_cancellation_pointer(self) -> None:
        self.assertEqual(
            [
                ("struct_size", ctypes.c_uint32),
                ("abi_version", ctypes.c_uint32),
                ("timeout_milliseconds", ctypes.c_int64),
                ("cancellation", ctypes.c_void_p),
            ],
            _native.WaitOptions._fields_,
        )
        self.assertEqual(24, ctypes.sizeof(_native.WaitOptions))
        self.assertEqual(16, _native.WaitOptions.cancellation.offset)
        cancellation_handle = getattr(_native, "CancellationHandle", None)
        self.assertIs(ctypes.c_void_p, cancellation_handle)
        self.assertEqual(ctypes.sizeof(ctypes.c_void_p), ctypes.sizeof(cancellation_handle))

    def test_diagnostics_is_the_exact_complete_abi2_structure(self) -> None:
        int32_fields = (
            "layout_major",
            "layout_minor",
            "resource_protocol",
            "reserved",
            "slot_count",
            "free_slot_count",
            "initializing_slot_count",
            "reserved_slot_count",
            "published_slot_count",
            "pending_removal_count",
            "reclaiming_slot_count",
            "retired_slot_count",
            "active_reservation_count",
            "active_lease_count",
            "claiming_lease_count",
            "recovering_lease_count",
            "free_lease_count",
            "retired_lease_count",
            "participant_record_count",
            "free_participant_count",
            "registering_participant_count",
            "active_participant_count",
            "closing_participant_count",
            "recovering_participant_count",
            "reclaiming_participant_count",
            "retired_participant_count",
            "index_entry_count",
            "occupied_index_entry_count",
            "empty_index_entry_count",
            "usable_index_capacity",
            "primary_directory_occupancy",
            "spilled_bucket_count",
            "overflow_directory_occupancy",
            "last_observed_probe_length",
            "max_observed_probe_length",
            "max_observed_overflow_scan_length",
            "last_failure_status",
        )
        int64_fields = (
            "total_bytes",
            "aborted_reservation_count",
            "recovered_lease_count",
            "active_lease_recovery_count",
            "unsupported_lease_recovery_count",
            "failed_lease_recovery_count",
            "recovered_reservation_count",
            "active_reservation_recovery_count",
            "unsupported_reservation_recovery_count",
            "failed_reservation_recovery_count",
            "capacity_pressure_count",
            "overflow_scan_count",
            "cas_retry_count",
            "helped_transition_count",
            "contention_budget_exhaustion_count",
            "invalid_token_count",
            "stale_token_count",
            "recovery_attempt_count",
            "recovered_transition_count",
            "current_owner_classification_count",
            "live_owner_classification_count",
            "stale_owner_classification_count",
            "unsupported_owner_classification_count",
            "inconsistent_owner_classification_count",
            "changing_owner_classification_count",
        )
        expected_field_names = (
            "struct_size",
            "abi_version",
            *int32_fields[:4],
            "required_features",
            "optional_features",
            "total_bytes",
            *int32_fields[4:],
            *int64_fields[1:],
            "failure_counts",
        )
        self.assertEqual(
            expected_field_names,
            tuple(name for name, _ in _native.Diagnostics._fields_),
        )
        field_types = dict(_native.Diagnostics._fields_)
        self.assertIs(ctypes.c_uint32, field_types["struct_size"])
        self.assertIs(ctypes.c_uint32, field_types["abi_version"])
        for name in int32_fields:
            with self.subTest(field=name):
                self.assertIs(ctypes.c_int32, field_types[name])
        self.assertIs(ctypes.c_uint64, field_types["required_features"])
        self.assertIs(ctypes.c_uint64, field_types["optional_features"])
        for name in int64_fields:
            with self.subTest(field=name):
                self.assertIs(ctypes.c_int64, field_types[name])
        self.assertEqual(ctypes.c_int64 * _native.STATUS_COUNT, field_types["failure_counts"])

        self.assertEqual(560, ctypes.sizeof(_native.Diagnostics))
        expected_offsets = {
            "layout_major": 8,
            "required_features": 24,
            "total_bytes": 40,
            "slot_count": 48,
            "active_lease_count": 84,
            "participant_record_count": 104,
            "index_entry_count": 136,
            "last_failure_status": 176,
            "aborted_reservation_count": 184,
            "changing_owner_classification_count": 368,
            "failure_counts": 376,
        }
        for field, offset in expected_offsets.items():
            self.assertEqual(offset, getattr(_native.Diagnostics, field).offset)
        for retired in ("tombstone_index_entry_count", "index_compaction_count"):
            self.assertNotIn(retired, field_types)

    def test_store_options_has_required_participant_capacity_at_the_abi2_offset(self) -> None:
        expected_fields = [
            ("struct_size", ctypes.c_uint32),
            ("abi_version", ctypes.c_uint32),
            ("name_utf8", ctypes.c_char_p),
            ("name_length", ctypes.c_uint64),
            ("open_mode", ctypes.c_int32),
            ("total_bytes", ctypes.c_int64),
            ("slot_count", ctypes.c_int32),
            ("max_value_bytes", ctypes.c_int32),
            ("max_descriptor_bytes", ctypes.c_int32),
            ("max_key_bytes", ctypes.c_int32),
            ("lease_record_count", ctypes.c_int32),
            ("participant_record_count", ctypes.c_int32),
            ("enable_lease_recovery", ctypes.c_uint8),
            ("reserved", ctypes.c_uint8 * 7),
        ]
        self.assertEqual(expected_fields, _native.StoreOptions._fields_)
        self.assertEqual(72, ctypes.sizeof(_native.StoreOptions))
        self.assertEqual(60, _native.StoreOptions.participant_record_count.offset)
        self.assertEqual(64, _native.StoreOptions.enable_lease_recovery.offset)
        self.assertEqual(65, _native.StoreOptions.reserved.offset)

    def test_protocol_info_is_the_exact_five_field_identity_and_record_catalog(self) -> None:
        expected_fields = [
            ("struct_size", ctypes.c_uint32),
            ("abi_version", ctypes.c_uint32),
            ("layout_major", ctypes.c_int32),
            ("layout_minor", ctypes.c_int32),
            ("resource_protocol", ctypes.c_int32),
            ("reserved", ctypes.c_int32),
            ("required_features", ctypes.c_uint64),
            ("optional_features", ctypes.c_uint64),
            ("store_header_size", ctypes.c_int32),
            ("participant_record_size", ctypes.c_int32),
            ("primary_directory_bucket_size", ctypes.c_int32),
            ("overflow_binding_size", ctypes.c_int32),
            ("lease_record_size", ctypes.c_int32),
            ("value_slot_size", ctypes.c_int32),
        ]
        self.assertEqual(expected_fields, _native.ProtocolInfo._fields_)
        self.assertEqual(64, ctypes.sizeof(_native.ProtocolInfo))
        expected_offsets = {
            "struct_size": 0,
            "abi_version": 4,
            "layout_major": 8,
            "layout_minor": 12,
            "resource_protocol": 16,
            "reserved": 20,
            "required_features": 24,
            "optional_features": 32,
            "store_header_size": 40,
            "participant_record_size": 44,
            "primary_directory_bucket_size": 48,
            "overflow_binding_size": 52,
            "lease_record_size": 56,
            "value_slot_size": 60,
        }
        for field, offset in expected_offsets.items():
            self.assertEqual(offset, getattr(_native.ProtocolInfo, field).offset)

    def test_buffer_and_recovery_structures_keep_fixed_width_abi_types(self) -> None:
        for structure in (_native.Bytes, _native.MutableBytes, _native.Segment):
            with self.subTest(structure=structure.__name__):
                self.assertEqual(16, ctypes.sizeof(structure))
                self.assertEqual(0, structure.data.offset)
                self.assertEqual(8, structure.length.offset)
                self.assertIs(ctypes.c_uint64, dict(structure._fields_)["length"])
        self.assertEqual(32, ctypes.sizeof(_native.RecoveryReport))
        self.assertEqual(8, _native.RecoveryReport.scanned_count.offset)
        self.assertEqual(24, _native.RecoveryReport.failed_count.offset)

    def test_layout_query_catalog_covers_every_canonical_mapped_record_field(self) -> None:
        actual = getattr(_native, "LAYOUT_FIELD_QUERIES", None)
        self.assertEqual(EXPECTED_LAYOUT_FIELD_QUERIES, actual)
        query_ids = [query_id for query_id, _ in actual.values()]
        self.assertEqual(len(query_ids), len(set(query_ids)))

    def test_abi2_signatures_include_participant_sizing_cancellation_and_store_lifetime_symbols(self) -> None:
        library = _SignatureLibrary()
        _native._configure_signatures(library)  # type: ignore[arg-type]
        self.assertEqual(
            [
                ctypes.c_int32,
                ctypes.c_int32,
                ctypes.c_int32,
                ctypes.c_int32,
                ctypes.c_int32,
                ctypes.c_int32,
                ctypes.POINTER(ctypes.c_int64),
            ],
            library.sms_calculate_required_bytes.argtypes,
        )
        self.assertEqual(
            [ctypes.POINTER(ctypes.c_void_p)],
            library.sms_create_cancellation.argtypes,
        )
        self.assertEqual([ctypes.c_void_p], library.sms_signal_cancellation.argtypes)
        self.assertEqual([ctypes.c_void_p], library.sms_cancellation_is_signaled.argtypes)
        self.assertEqual([ctypes.c_void_p], library.sms_destroy_cancellation.argtypes)
        self.assertEqual([ctypes.c_void_p], library.sms_close_store.argtypes)
        self.assertIsNone(library.sms_close_store.restype)
        self.assertEqual([ctypes.c_void_p], library.sms_destroy_store.argtypes)
        self.assertIsNone(library.sms_destroy_store.restype)

    def test_wait_options_are_validated_and_immutable(self) -> None:
        self.assertEqual(1000, sms.WaitOptions.default().timeout_milliseconds)
        self.assertEqual(0, sms.WaitOptions.no_wait().timeout_milliseconds)
        self.assertEqual(-1, sms.WaitOptions.infinite().timeout_milliseconds)
        with self.assertRaises(ValueError):
            sms.WaitOptions(-2)
        with self.assertRaises(TypeError):
            sms.WaitOptions(True)
        with self.assertRaises(TypeError):
            sms.WaitOptions(cancellation=object())  # type: ignore[arg-type]
        with self.assertRaises(FrozenInstanceError):
            sms.WaitOptions.DEFAULT.timeout_milliseconds = 1  # type: ignore[misc]

    def test_loader_uses_only_the_adjacent_platform_package_artifact(self) -> None:
        filename = _native._library_filename()
        self.assertIn(filename, {"shared_memory_store.dll", "libshared_memory_store.so"})
        loader_source = inspect.getsource(_native._bundled_library_path)
        self.assertIn('files("shared_memory_store")', loader_source)
        self.assertNotIn("find_library", inspect.getsource(_native))

        with tempfile.TemporaryDirectory() as directory:
            packaged = Path(directory) / "shared_memory_store" / filename
            packaged.parent.mkdir()
            packaged.touch()
            load_error = OSError("deliberate test load failure")
            with (
                patch.object(_native, "_LIBRARY", None),
                patch.object(_native, "_LIBRARY_PATH", None),
                patch.object(_native, "_bundled_library_path", return_value=packaged) as locate,
                patch.object(_native.ctypes, "CDLL", side_effect=load_error) as load,
            ):
                with self.assertRaises(OSError):
                    _native.library()
            locate.assert_called_once_with(filename)
            load.assert_called_once_with(str(packaged.resolve(strict=True)))


if __name__ == "__main__":
    unittest.main()
