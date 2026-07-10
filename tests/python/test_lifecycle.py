from __future__ import annotations

import unittest

from shared_memory_store import MemoryStore, StoreOpenStatus, StoreStatus

from _support import create_options, require_native


class LifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        require_native()

    def test_segmented_publish_preserves_logical_bytes(self) -> None:
        status, store = MemoryStore.open(create_options("segments"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            source = memoryview(bytearray(b"abcdef"))[::2]
            published, copied = store.publish_segments(
                b"key",
                [b"\x00\x01", bytearray(b"\xfe"), source, memoryview(b"\xff")],
                b"meta",
            )
            source.release()
            self.assertEqual(StoreStatus.SUCCESS, published)
            self.assertEqual(7, copied)
            acquired, lease = store.acquire(b"key")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            with lease:
                self.assertEqual(b"\x00\x01\xfeace\xff", bytes(lease.value))
                self.assertEqual(b"meta", bytes(lease.descriptor))

            empty_status, empty_copied = store.publish_segments(b"empty", [])
            self.assertEqual(StoreStatus.SUCCESS, empty_status)
            self.assertEqual(0, empty_copied)
            acquired, empty_lease = store.acquire(b"empty")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert empty_lease is not None
            with empty_lease:
                self.assertEqual(b"", bytes(empty_lease.value))

    def test_reservation_progress_commit_and_view_invalidation(self) -> None:
        status, store = MemoryStore.open(create_options("reservation"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            reserved, reservation = store.reserve(b"key", 5, b"desc")
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None
            self.assertEqual(5, reservation.payload_length)
            self.assertEqual(0, reservation.bytes_written)
            self.assertEqual(5, reservation.remaining_bytes)
            self.assertEqual(StoreStatus.RESERVATION_INCOMPLETE, reservation.commit())

            first = reservation.buffer(2)
            self.assertFalse(first.readonly)
            first[:2] = b"ab"
            self.assertEqual(StoreStatus.SUCCESS, reservation.advance(2))
            with self.assertRaises(ValueError):
                bytes(first)
            self.assertEqual(2, reservation.bytes_written)
            self.assertEqual(3, reservation.remaining_bytes)
            second = reservation.buffer(3)
            self.assertEqual(3, len(second))
            second[:] = b"c\x00d"
            self.assertEqual(StoreStatus.RESERVATION_WRITE_OUT_OF_RANGE, reservation.advance(4))
            with self.assertRaises(ValueError):
                bytes(second)
            # A failed advance invalidates the immediate view, but not progress.
            second = reservation.buffer(3)
            second[:] = b"c\x00d"
            self.assertEqual(StoreStatus.SUCCESS, reservation.advance(3))
            self.assertEqual(StoreStatus.SUCCESS, reservation.commit())
            self.assertFalse(reservation.is_valid)
            self.assertEqual(StoreStatus.RESERVATION_ALREADY_COMPLETED, reservation.commit())

            acquired, lease = store.acquire(b"key")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            with lease:
                self.assertEqual(b"abc\x00d", bytes(lease.value))
                self.assertEqual(b"desc", bytes(lease.descriptor))

    def test_context_exit_aborts_and_allows_key_reuse(self) -> None:
        status, store = MemoryStore.open(create_options("abort"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            reserved, reservation = store.reserve(b"key", 3)
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None
            with reservation:
                view = reservation.buffer()
                view[:] = b"abc"
            with self.assertRaises(ValueError):
                bytes(view)
            self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"key")[0])
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"key", b"replacement"))

    def test_current_process_recovery_invalidates_owned_tokens(self) -> None:
        status, store = MemoryStore.open(create_options("recovery", recovery=True))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"lease", b"value"))
            acquired, lease = store.acquire(b"lease")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            lease_view = lease.value
            recovery_status, lease_report = store.recover_leases(True)
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, lease_report.recovered_count)
            self.assertFalse(lease.is_valid)
            with self.assertRaises(ValueError):
                bytes(lease_view)

            reserved, reservation = store.reserve(b"reservation", 2)
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None
            reservation_view = reservation.buffer()
            reservation_view[:] = b"no"
            recovery_status, reservation_report = store.recover_reservations(True)
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, reservation_report.recovered_count)
            self.assertFalse(reservation.is_valid)
            with self.assertRaises(ValueError):
                bytes(reservation_view)
            self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"reservation")[0])


if __name__ == "__main__":
    unittest.main()
