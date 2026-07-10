from __future__ import annotations

from dataclasses import replace
from concurrent.futures import ThreadPoolExecutor
import gc
import unittest
import weakref

from shared_memory_store import (
    MemoryStore,
    OpenMode,
    StoreOpenStatus,
    StoreStatus,
    calculate_required_bytes,
)

from _support import create_options, require_native


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


if __name__ == "__main__":
    unittest.main()
