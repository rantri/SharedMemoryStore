from __future__ import annotations

from dataclasses import FrozenInstanceError
import unittest

from shared_memory_store import MemoryStore, StoreOpenStatus, StoreStatus

from _support import create_options, require_native


class DiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        require_native()

    def test_snapshot_reports_shared_state_and_local_failures(self) -> None:
        status, store = MemoryStore.open(create_options("diagnostics", slots=3))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"missing")[0])
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"one", b"1"))
            acquired, lease = store.acquire(b"one")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            self.assertEqual(StoreStatus.REMOVE_PENDING, store.remove(b"one"))
            reserved, reservation = store.reserve(b"two", 2)
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None

            diagnostic_status, snapshot = store.get_diagnostics()
            self.assertEqual(StoreStatus.SUCCESS, diagnostic_status)
            assert snapshot is not None
            self.assertEqual(3, snapshot.slot_count)
            self.assertEqual(1, snapshot.free_slot_count)
            self.assertEqual(0, snapshot.published_slot_count)
            self.assertEqual(1, snapshot.pending_removal_count)
            self.assertEqual(1, snapshot.active_lease_count)
            self.assertEqual(1, snapshot.active_reservation_count)
            self.assertEqual(StoreStatus.REMOVE_PENDING, snapshot.last_failure_status)
            self.assertEqual(1, snapshot.failure_count(StoreStatus.NOT_FOUND))
            self.assertEqual(1, snapshot.get_failure_count(StoreStatus.REMOVE_PENDING))
            self.assertEqual(23, len(snapshot.failure_counts))
            with self.assertRaises(FrozenInstanceError):
                snapshot.slot_count = 4  # type: ignore[misc]

            lease.close()
            reservation.close()


if __name__ == "__main__":
    unittest.main()
