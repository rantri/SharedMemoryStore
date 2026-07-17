from __future__ import annotations

from dataclasses import FrozenInstanceError, fields
import unittest

from shared_memory_store import (
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    OPTIONAL_FEATURES,
    REQUIRED_FEATURES,
    RESOURCE_PROTOCOL_VERSION,
    DiagnosticsSnapshot,
    MemoryStore,
    ProtocolInfo,
    StoreOpenStatus,
    StoreStatus,
)

from _support import create_options, require_native


EXPECTED_DIAGNOSTIC_FIELDS = (
    "protocol_info",
    "total_bytes",
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
    "failure_counts",
)


class DiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        require_native()

    def test_snapshot_surface_is_complete_immutable_and_has_no_legacy_index_fields(self) -> None:
        self.assertEqual(EXPECTED_DIAGNOSTIC_FIELDS, tuple(field.name for field in fields(DiagnosticsSnapshot)))
        self.assertFalse(hasattr(DiagnosticsSnapshot, "tombstone_index_entry_count"))
        self.assertFalse(hasattr(DiagnosticsSnapshot, "index_compaction_count"))

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

            self.assertEqual(
                ProtocolInfo(
                    LAYOUT_MAJOR_VERSION,
                    LAYOUT_MINOR_VERSION,
                    RESOURCE_PROTOCOL_VERSION,
                    REQUIRED_FEATURES,
                    OPTIONAL_FEATURES,
                ),
                snapshot.protocol_info,
            )
            self.assertEqual(store.protocol_info, snapshot.protocol_info)

            self.assertEqual(3, snapshot.slot_count)
            self.assertEqual(1, snapshot.free_slot_count)
            self.assertEqual(0, snapshot.initializing_slot_count)
            self.assertEqual(1, snapshot.reserved_slot_count)
            self.assertEqual(0, snapshot.published_slot_count)
            self.assertEqual(1, snapshot.pending_removal_count)
            self.assertEqual(0, snapshot.reclaiming_slot_count)
            self.assertEqual(0, snapshot.retired_slot_count)
            self.assertEqual(1, snapshot.active_reservation_count)
            self.assertEqual(
                snapshot.slot_count,
                snapshot.free_slot_count
                + snapshot.initializing_slot_count
                + snapshot.reserved_slot_count
                + snapshot.published_slot_count
                + snapshot.pending_removal_count
                + snapshot.reclaiming_slot_count
                + snapshot.retired_slot_count,
            )

            self.assertEqual(1, snapshot.active_lease_count)
            self.assertEqual(
                8,
                snapshot.active_lease_count
                + snapshot.claiming_lease_count
                + snapshot.recovering_lease_count
                + snapshot.free_lease_count
                + snapshot.retired_lease_count,
            )

            self.assertEqual(64, snapshot.participant_record_count)
            self.assertEqual(1, snapshot.active_participant_count)
            self.assertEqual(
                snapshot.participant_record_count,
                snapshot.free_participant_count
                + snapshot.registering_participant_count
                + snapshot.active_participant_count
                + snapshot.closing_participant_count
                + snapshot.recovering_participant_count
                + snapshot.reclaiming_participant_count
                + snapshot.retired_participant_count,
            )
            self.assertFalse(snapshot.is_participant_table_exhausted)

            self.assertEqual(
                snapshot.index_entry_count,
                snapshot.occupied_index_entry_count + snapshot.empty_index_entry_count,
            )
            self.assertEqual(snapshot.empty_index_entry_count, snapshot.usable_index_capacity)
            self.assertEqual(
                snapshot.occupied_index_entry_count,
                snapshot.primary_directory_occupancy + snapshot.overflow_directory_occupancy,
            )
            self.assertGreaterEqual(snapshot.spilled_bucket_count, 0)
            self.assertGreaterEqual(snapshot.last_observed_probe_length, 0)
            self.assertGreaterEqual(snapshot.max_observed_probe_length, snapshot.last_observed_probe_length)
            self.assertGreaterEqual(snapshot.max_observed_overflow_scan_length, 0)

            self.assertEqual(StoreStatus.REMOVE_PENDING, snapshot.last_failure_status)
            self.assertEqual(1, snapshot.failure_count(StoreStatus.NOT_FOUND))
            self.assertEqual(1, snapshot.get_failure_count(StoreStatus.REMOVE_PENDING))
            self.assertEqual(23, len(snapshot.failure_counts))
            for name in EXPECTED_DIAGNOSTIC_FIELDS[35:-1]:
                with self.subTest(counter=name):
                    self.assertGreaterEqual(getattr(snapshot, name), 0)

            with self.assertRaises(FrozenInstanceError):
                snapshot.slot_count = 4  # type: ignore[misc]

            lease.close()
            reservation.close()


if __name__ == "__main__":
    unittest.main()
