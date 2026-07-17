from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from dataclasses import FrozenInstanceError, fields, replace
import gc
import hashlib
import inspect
import mmap
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import uuid
import weakref

import shared_memory_store as sms
from shared_memory_store import (
    MemoryStore,
    OpenMode,
    StoreOptions,
    StoreOpenStatus,
    StoreStatus,
    calculate_required_bytes,
)
from shared_memory_store import _native

from _support import create_options, require_native, unique_store_name


def _linux_process_start_token() -> str:
    stat = Path("/proc/self/stat").read_text(encoding="ascii")
    command_end = stat.rfind(")")
    return "proc-" + stat[command_end + 2 :].split()[19]


@contextmanager
def _raw_named_mapping(name: str, content: bytes | bytearray):
    raw = bytes(content)
    if sys.platform == "win32":
        mapping = mmap.mmap(-1, len(raw), tagname=name, access=mmap.ACCESS_WRITE)
        mapping.write(raw)
        mapping.flush()

        def read() -> bytes:
            mapping.seek(0)
            return mapping.read()

        try:
            yield read
        finally:
            mapping.close()
        return

    root = Path("/dev/shm") if Path("/dev/shm").is_dir() else Path(tempfile.gettempdir())
    directory = root / "SharedMemoryStore"
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(directory, 0o700)
    readable = "".join(character if character.isascii() and (character.isalnum() or character in "-_.") else "_" for character in name)
    readable = readable.strip("_.") or "store"
    digest = hashlib.sha256(name.encode("utf-8")).hexdigest()[:16]
    fragment = f"sms-{readable[:80]}-{digest}"
    region = directory / f"{fragment}.region"
    lock = directory / f"{fragment}.lock"
    owners = directory / f"{fragment}.owners"
    lifecycle = directory / f"{fragment}.lifecycle"
    paths = (region, lock, owners, lifecycle)
    region.write_bytes(raw)
    lock.touch()
    lifecycle.touch()
    owner = f"{os.getpid()}:{_linux_process_start_token()}:{uuid.uuid4().hex}\n"
    owners.write_text(owner, encoding="ascii")
    for path in paths:
        os.chmod(path, 0o600)
    try:
        yield region.read_bytes
    finally:
        for path in paths:
            path.unlink(missing_ok=True)


class MemoryStoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        require_native()

    def test_required_bytes_and_all_open_modes(self) -> None:
        options = create_options("open")
        self.assertEqual(
            options.total_bytes,
            calculate_required_bytes(
                slot_count=options.slot_count,
                max_value_bytes=options.max_value_bytes,
                max_descriptor_bytes=options.max_descriptor_bytes,
                max_key_bytes=options.max_key_bytes,
                lease_record_count=options.lease_record_count,
            ),
        )
        with self.assertRaises(ValueError):
            calculate_required_bytes(
                slot_count=0,
                max_value_bytes=1,
                max_descriptor_bytes=0,
                max_key_bytes=1,
                lease_record_count=1,
            )

        create_new = replace(options, open_mode=OpenMode.CREATE_NEW)
        status, creator = MemoryStore.open(create_new)
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        self.assertIsNotNone(creator)
        assert creator is not None
        try:
            duplicate, absent = MemoryStore.open(create_new)
            self.assertEqual(StoreOpenStatus.ALREADY_EXISTS, duplicate)
            self.assertIsNone(absent)
            opened, peer = MemoryStore.open(replace(options, open_mode=OpenMode.OPEN_EXISTING))
            self.assertEqual(StoreOpenStatus.SUCCESS, opened)
            self.assertIsNotNone(peer)
            assert peer is not None
            peer.close()
            peer.close()

            incompatible, incompatible_store = MemoryStore.open(
                replace(options, open_mode=OpenMode.OPEN_EXISTING, max_value_bytes=64)
            )
            self.assertEqual(StoreOpenStatus.INCOMPATIBLE_LAYOUT, incompatible)
            self.assertIsNone(incompatible_store)
        finally:
            creator.close()

        insufficient, no_store = MemoryStore.open(replace(options, total_bytes=options.total_bytes - 1))
        self.assertEqual(StoreOpenStatus.INSUFFICIENT_CAPACITY, insufficient)
        self.assertIsNone(no_store)
        for invalid_name in ("", "   ", "embedded\x00nul", "\ud800"):
            invalid, no_store = MemoryStore.open(replace(options, name=invalid_name))
            self.assertEqual(StoreOpenStatus.INVALID_OPTIONS, invalid)
            self.assertIsNone(no_store)

    def test_binary_publish_acquire_release_remove_and_reuse(self) -> None:
        status, store = MemoryStore.open(create_options("basic"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            key = bytearray(b"key\x00\xff")
            value = bytearray(b"payload\x00\xfe")
            descriptor = bytearray(b"descriptor\x00")
            self.assertEqual(StoreStatus.SUCCESS, store.publish(key, value, descriptor))
            value[:] = b"X" * len(value)
            self.assertEqual(StoreStatus.DUPLICATE_KEY, store.publish(key, b"other"))

            acquired, lease = store.acquire(memoryview(key))
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            value_view = lease.value
            descriptor_view = lease.descriptor
            self.assertTrue(value_view.readonly)
            self.assertTrue(descriptor_view.readonly)
            self.assertEqual(b"payload\x00\xfe", bytes(value_view))
            self.assertEqual(b"descriptor\x00", bytes(descriptor_view))
            with self.assertRaises(TypeError):
                value_view[0] = 1

            self.assertEqual(StoreStatus.REMOVE_PENDING, store.remove(key))
            self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(key)[0])
            self.assertEqual(StoreStatus.SUCCESS, lease.release())
            self.assertEqual(StoreStatus.LEASE_ALREADY_RELEASED, lease.release())
            with self.assertRaises(ValueError):
                bytes(value_view)
            lease.close()

            self.assertEqual(StoreStatus.SUCCESS, store.publish(key, b"replacement"))
            self.assertEqual(StoreStatus.SUCCESS, store.remove(key))

    def test_view_retains_lease_and_store_ownership(self) -> None:
        status, store = MemoryStore.open(create_options("ownership"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"k", b"owned"))
        acquired, lease = store.acquire(b"k")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None
        reference = weakref.ref(lease)
        view = lease.value
        del lease
        gc.collect()
        self.assertIsNotNone(reference())
        self.assertEqual(b"owned", bytes(view))
        view.release()
        del view
        gc.collect()
        self.assertIsNone(reference())
        diagnostic_status, snapshot = store.diagnostics()
        self.assertEqual(StoreStatus.SUCCESS, diagnostic_status)
        assert snapshot is not None
        self.assertEqual(0, snapshot.active_lease_count)
        store.close()

    def test_store_close_invalidates_children_and_views(self) -> None:
        status, store = MemoryStore.open(create_options("close"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"k", b"value"))
        acquired, lease = store.acquire(b"k")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None
        view = lease.value
        store.close()
        store.close()
        self.assertFalse(store.is_open)
        self.assertFalse(lease.is_valid)
        self.assertEqual(StoreStatus.STORE_DISPOSED, store.publish(b"x", b"y"))
        self.assertEqual(StoreStatus.STORE_DISPOSED, store.remove(b"x"))
        self.assertEqual(StoreStatus.STORE_DISPOSED, store.acquire(b"x")[0])
        with self.assertRaises(ValueError):
            bytes(view)

    def test_invalid_and_oversized_inputs_return_contract_statuses(self) -> None:
        options = create_options("validation")
        status, store = MemoryStore.open(options)
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            self.assertEqual(StoreStatus.INVALID_KEY, store.publish(b"", b"value"))
            self.assertEqual(StoreStatus.KEY_TOO_LARGE, store.publish(b"k" * 33, b"value"))
            self.assertEqual(StoreStatus.VALUE_TOO_LARGE, store.publish(b"k", b"v" * 129))
            self.assertEqual(StoreStatus.DESCRIPTOR_TOO_LARGE, store.publish(b"k", b"v", b"d" * 33))
            with self.assertRaises(TypeError):
                store.publish("not bytes", b"value")

    def test_concurrent_duplicate_publish_has_one_winner(self) -> None:
        status, store = MemoryStore.open(create_options("duplicate-race"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store, ThreadPoolExecutor(max_workers=8) as executor:
            results = list(executor.map(lambda value: store.publish(b"same", bytes([value])), range(8)))
        self.assertEqual(1, results.count(StoreStatus.SUCCESS))
        self.assertEqual(7, results.count(StoreStatus.DUPLICATE_KEY))


class CanonicalSms2StoreTests(unittest.TestCase):
    def test_sizing_and_store_options_are_participant_aware_with_default_64(self) -> None:
        sizing = inspect.signature(calculate_required_bytes).parameters
        creation = inspect.signature(StoreOptions.create).parameters
        self.assertIn("participant_record_count", sizing)
        self.assertEqual(64, sizing["participant_record_count"].default)
        self.assertIn("participant_record_count", creation)
        self.assertEqual(64, creation["participant_record_count"].default)
        self.assertIn("participant_record_count", {field.name for field in fields(StoreOptions)})

        dimensions = {
            "slot_count": 3,
            "max_value_bytes": 17,
            "max_descriptor_bytes": 5,
            "max_key_bytes": 9,
            "lease_record_count": 4,
        }
        default_size = calculate_required_bytes(**dimensions)
        explicit_default = calculate_required_bytes(**dimensions, participant_record_count=64)
        one_participant = calculate_required_bytes(**dimensions, participant_record_count=1)
        two_participants = calculate_required_bytes(**dimensions, participant_record_count=2)
        self.assertEqual(default_size, explicit_default)
        self.assertEqual(64, two_participants - one_participant)

        options = StoreOptions.create(
            unique_store_name("canonical-sizing"),
            **dimensions,
            participant_record_count=2,
        )
        self.assertEqual(2, options.participant_record_count)
        self.assertEqual(two_participants, options.total_bytes)

    def test_successful_handle_exposes_one_immutable_five_field_protocol_identity(self) -> None:
        self.assertTrue(hasattr(sms, "ProtocolInfo"))
        self.assertIn("participant_record_count", inspect.signature(StoreOptions.create).parameters)
        options = StoreOptions.create(
            unique_store_name("protocol-info"),
            slot_count=2,
            max_value_bytes=16,
            max_descriptor_bytes=4,
            max_key_bytes=8,
            lease_record_count=2,
            participant_record_count=2,
            open_mode=OpenMode.CREATE_NEW,
        )
        status, store = MemoryStore.open(options)
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        self.assertIsNotNone(store)
        assert store is not None
        try:
            expected = sms.ProtocolInfo(2, 0, 2, 7, 0)
            self.assertEqual(expected, store.protocol_info)
            observed = store.protocol_info
            self.assertEqual(observed, store.protocol_info)
            with self.assertRaises((FrozenInstanceError, AttributeError)):
                store.protocol_info.required_features = 0
            self.assertEqual(observed, store.protocol_info)
        finally:
            store.close()

    def test_participant_capacity_exhausts_without_mutation_and_reuses_closed_record(self) -> None:
        self.assertIn("participant_record_count", inspect.signature(StoreOptions.create).parameters)
        self.assertTrue(hasattr(StoreOpenStatus, "PARTICIPANT_TABLE_FULL"))
        options = StoreOptions.create(
            unique_store_name("participants"),
            slot_count=2,
            max_value_bytes=16,
            max_descriptor_bytes=4,
            max_key_bytes=8,
            lease_record_count=2,
            participant_record_count=2,
            open_mode=OpenMode.CREATE_NEW,
        )
        first_status, anchor = MemoryStore.open(options)
        self.assertEqual(StoreOpenStatus.SUCCESS, first_status)
        assert anchor is not None
        peer = None
        reused = None
        try:
            open_existing = replace(options, open_mode=OpenMode.OPEN_EXISTING)
            peer_status, peer = MemoryStore.open(open_existing)
            self.assertEqual(StoreOpenStatus.SUCCESS, peer_status)
            assert peer is not None

            full_status, absent = MemoryStore.open(open_existing)
            self.assertEqual(StoreOpenStatus.PARTICIPANT_TABLE_FULL, full_status)
            self.assertIsNone(absent)
            self.assertTrue(anchor.is_open)
            self.assertTrue(peer.is_open)

            peer.close()
            peer = None
            reused_status, reused = MemoryStore.open(open_existing)
            self.assertEqual(StoreOpenStatus.SUCCESS, reused_status)
            self.assertIsNotNone(reused)
        finally:
            if reused is not None:
                reused.close()
            if peer is not None:
                peer.close()
            anchor.close()

    def test_retired_sms1_mapping_is_rejected_without_mutating_fixture_bytes(self) -> None:
        self.assertIn("participant_record_count", {field.name for field in fields(StoreOptions)})
        name = unique_store_name("retired-sms1")
        options = StoreOptions.create(
            name=name,
            slot_count=3,
            max_value_bytes=17,
            max_descriptor_bytes=5,
            max_key_bytes=9,
            lease_record_count=4,
            participant_record_count=64,
            open_mode=OpenMode.OPEN_EXISTING,
        )
        fixture = bytearray(options.total_bytes)
        struct.pack_into("<I", fixture, 0, 0x31534D53)
        struct.pack_into("<i", fixture, 4, 1)
        struct.pack_into("<i", fixture, 8, 2)
        before = hashlib.sha256(fixture).digest()
        with _raw_named_mapping(name, fixture) as read_mapping:
            status, store = MemoryStore.open(options)
            self.assertEqual(StoreOpenStatus.INCOMPATIBLE_LAYOUT, status)
            self.assertIsNone(store)
            if store is not None:
                store.close()
            self.assertEqual(before, hashlib.sha256(read_mapping()).digest())

    def test_malformed_sms2_header_is_rejected_before_any_payload_projection(self) -> None:
        self.assertIn("participant_record_count", {field.name for field in fields(StoreOptions)})
        fixture = bytearray(4096)
        struct.pack_into("<I", fixture, 0, 0x32534D53)
        struct.pack_into("<H", fixture, 4, 2)
        struct.pack_into("<H", fixture, 6, 0)
        struct.pack_into("<i", fixture, 8, 512)
        struct.pack_into("<i", fixture, 12, 2)
        struct.pack_into("<Q", fixture, 16, 7)
        struct.pack_into("<Q", fixture, 24, 0)
        struct.pack_into("<q", fixture, 32, len(fixture))
        struct.pack_into("<Q", fixture, 40, 1)
        struct.pack_into("<q", fixture, 48, 2)
        struct.pack_into("<iii", fixture, 64, 1, 1, 1)
        struct.pack_into("<iii", fixture, 76, 8, 4, 16)
        struct.pack_into("<q", fixture, 96, 513)  # Deliberately misaligned participant section.
        before = hashlib.sha256(fixture).digest()
        name = unique_store_name("malformed-sms2")
        with _raw_named_mapping(name, fixture) as read_mapping:
            options = StoreOptions(
                name=name,
                total_bytes=len(fixture),
                slot_count=1,
                max_value_bytes=16,
                max_descriptor_bytes=4,
                max_key_bytes=8,
                lease_record_count=1,
                participant_record_count=1,
                open_mode=OpenMode.OPEN_EXISTING,
            )
            status, store = MemoryStore.open(options)
            self.assertEqual(StoreOpenStatus.INCOMPATIBLE_LAYOUT, status)
            self.assertIsNone(store)
            if store is not None:
                store.close()
            self.assertEqual(before, hashlib.sha256(read_mapping()).digest())

    def test_architecture_gate_accepts_only_little_endian_64_bit_x86(self) -> None:
        helper = getattr(_native, "_is_supported_architecture", None)
        self.assertTrue(callable(helper))
        self.assertTrue(helper("AMD64", "little", 8))
        self.assertTrue(helper("x86_64", "little", 8))
        self.assertFalse(helper("arm64", "little", 8))
        self.assertFalse(helper("x86", "little", 4))
        self.assertFalse(helper("x86_64", "big", 8))


if __name__ == "__main__":
    unittest.main()
