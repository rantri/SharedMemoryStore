from __future__ import annotations

import ctypes
from dataclasses import FrozenInstanceError
import unittest

from shared_memory_store import (
    ABI_VERSION,
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    RESOURCE_NAMING_VERSION,
    OpenMode,
    StoreOpenStatus,
    StoreStatus,
    WaitOptions,
)
from shared_memory_store import _native


class NativeAbiTests(unittest.TestCase):
    def test_protocol_and_status_numbers_are_exact(self) -> None:
        self.assertEqual(0x00010000, ABI_VERSION)
        self.assertEqual((1, 2, 1), (LAYOUT_MAJOR_VERSION, LAYOUT_MINOR_VERSION, RESOURCE_NAMING_VERSION))
        self.assertEqual([0, 1, 2], [int(value) for value in OpenMode])
        self.assertEqual(list(range(11)), [int(value) for value in StoreOpenStatus])
        self.assertEqual(list(range(23)), [int(value) for value in StoreStatus])

    def test_every_ctypes_structure_size_and_critical_offset(self) -> None:
        expected = {
            _native.Bytes: (16, {"data": 0, "length": 8}),
            _native.MutableBytes: (16, {"data": 0, "length": 8}),
            _native.Segment: (16, {"data": 0, "length": 8}),
            _native.WaitOptions: (16, {"struct_size": 0, "abi_version": 4, "timeout_milliseconds": 8}),
            _native.StoreOptions: (
                72,
                {
                    "struct_size": 0,
                    "abi_version": 4,
                    "name_utf8": 8,
                    "name_length": 16,
                    "open_mode": 24,
                    "total_bytes": 32,
                    "slot_count": 40,
                    "lease_record_count": 56,
                    "enable_lease_recovery": 60,
                },
            ),
            _native.RecoveryReport: (32, {"scanned_count": 8, "failed_count": 24}),
            _native.Diagnostics: (
                344,
                {
                    "total_bytes": 8,
                    "slot_count": 16,
                    "last_failure_status": 68,
                    "aborted_reservation_count": 72,
                    "failure_counts": 160,
                },
            ),
            _native.ProtocolInfo: (36, {"layout_major": 8, "lease_record_size": 32}),
            _native.StoreLayout: (
                144,
                {
                    "total_bytes": 8,
                    "slot_count": 16,
                    "index_offset": 48,
                    "descriptor_stride": 96,
                    "descriptor_storage_offset": 104,
                    "required_bytes": 136,
                },
            ),
        }
        for structure, (size, offsets) in expected.items():
            with self.subTest(structure=structure.__name__):
                self.assertEqual(size, ctypes.sizeof(structure))
                for field, offset in offsets.items():
                    self.assertEqual(offset, getattr(structure, field).offset, field)

        self.assertIs(ctypes.c_uint64, dict(_native.Bytes._fields_)["length"])
        self.assertIs(ctypes.c_uint64, dict(_native.StoreOptions._fields_)["name_length"])
        self.assertIs(ctypes.c_uint64, dict(_native.Segment._fields_)["length"])

    def test_wait_options_are_validated_and_immutable(self) -> None:
        self.assertEqual(1000, WaitOptions.default().timeout_milliseconds)
        self.assertEqual(0, WaitOptions.no_wait().timeout_milliseconds)
        self.assertEqual(-1, WaitOptions.infinite().timeout_milliseconds)
        with self.assertRaises(ValueError):
            WaitOptions(-2)
        with self.assertRaises(TypeError):
            WaitOptions(True)
        with self.assertRaises(FrozenInstanceError):
            WaitOptions.DEFAULT.timeout_milliseconds = 1  # type: ignore[misc]

    def test_loader_has_one_platform_specific_package_artifact_name(self) -> None:
        filename = _native._library_filename()
        self.assertIn(filename, {"shared_memory_store.dll", "libshared_memory_store.so"})


if __name__ == "__main__":
    unittest.main()
