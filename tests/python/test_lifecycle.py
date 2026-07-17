from __future__ import annotations

import ctypes
import threading
import time
import unittest

from shared_memory_store import (
    CancellationSource,
    MemoryStore,
    OpenMode,
    ProtocolInfo,
    StoreOpenStatus,
    StoreOptions,
    StoreStatus,
    WaitOptions,
)
from shared_memory_store import _native

from _support import create_options, require_native


def _native_bytes(value: _native.Bytes) -> bytes:
    return ctypes.string_at(value.data, int(value.length)) if value.length else b""


class _LifecycleLibrary:
    """Process-local ABI fake; it tests wrapper ownership, not mapped atomics."""

    def __init__(self) -> None:
        self.records: dict[bytes, dict[str, object]] = {}
        self.leases: dict[int, dict[str, object]] = {}
        self.reservations: dict[int, dict[str, object]] = {}
        self.closed_handles: list[int] = []
        self.destroyed_handles: list[int] = []
        self.events: list[str] = []
        self._next_lease = 1
        self._next_reservation = 1
        self._buffers: list[ctypes.Array] = []

    @staticmethod
    def _handle_value(handle: ctypes.c_void_p) -> int:
        return int(handle.value or 0)

    def _bytes_view(self, value: bytes) -> _native.Bytes:
        array = (ctypes.c_uint8 * len(value)).from_buffer_copy(value)
        self._buffers.append(array)
        pointer = ctypes.cast(array, _native.UInt8Pointer) if value else _native.UInt8Pointer()
        return _native.Bytes(pointer, len(value))

    def sms_publish(
        self,
        store: ctypes.c_void_p,
        key: _native.Bytes,
        value: _native.Bytes,
        descriptor: _native.Bytes,
        wait: object,
    ) -> int:
        del store, wait
        logical_key = _native_bytes(key)
        if logical_key in self.records:
            return int(StoreStatus.DUPLICATE_KEY)
        self.records[logical_key] = {
            "value": _native_bytes(value),
            "descriptor": _native_bytes(descriptor),
            "removed": False,
        }
        return int(StoreStatus.SUCCESS)

    def sms_publish_segments(
        self,
        store: ctypes.c_void_p,
        key: _native.Bytes,
        segments: object,
        segment_count: int,
        descriptor: _native.Bytes,
        wait: object,
        copied: object,
    ) -> int:
        payload = b"".join(
            ctypes.string_at(segments[index].data, int(segments[index].length))
            for index in range(segment_count)
        )
        status = self.sms_publish(
            store,
            key,
            self._bytes_view(payload),
            descriptor,
            wait,
        )
        copied._obj.value = len(payload)
        return status

    def sms_acquire(
        self,
        store: ctypes.c_void_p,
        key: _native.Bytes,
        wait: object,
        output: object,
    ) -> int:
        del store, wait
        logical_key = _native_bytes(key)
        record = self.records.get(logical_key)
        if record is None or record["removed"]:
            return int(StoreStatus.NOT_FOUND)
        handle = self._next_lease
        self._next_lease += 1
        self.leases[handle] = {"key": logical_key, "active": True}
        output._obj.value = handle
        return int(StoreStatus.SUCCESS)

    def sms_lease_is_valid(self, handle: ctypes.c_void_p) -> int:
        lease = self.leases.get(self._handle_value(handle))
        return int(lease is not None and lease["active"])

    def sms_lease_value(self, handle: ctypes.c_void_p) -> _native.Bytes:
        lease = self.leases[self._handle_value(handle)]
        return self._bytes_view(self.records[lease["key"]]["value"])

    def sms_lease_descriptor(self, handle: ctypes.c_void_p) -> _native.Bytes:
        lease = self.leases[self._handle_value(handle)]
        return self._bytes_view(self.records[lease["key"]]["descriptor"])

    def sms_release_lease(self, handle: ctypes.c_void_p, wait: object) -> int:
        del wait
        lease = self.leases.get(self._handle_value(handle))
        if lease is None:
            return int(StoreStatus.INVALID_LEASE)
        if not lease["active"]:
            return int(StoreStatus.LEASE_ALREADY_RELEASED)
        lease["active"] = False
        key = lease["key"]
        if self.records[key]["removed"]:
            del self.records[key]
        return int(StoreStatus.SUCCESS)

    def sms_destroy_lease(self, handle: ctypes.c_void_p) -> None:
        numeric = self._handle_value(handle)
        lease = self.leases.get(numeric)
        if lease is not None and lease["active"]:
            self.sms_release_lease(handle, None)
        self.leases.pop(numeric, None)
        self.events.append("destroy_lease")

    def sms_remove(
        self,
        store: ctypes.c_void_p,
        key: _native.Bytes,
        wait: object,
    ) -> int:
        del store, wait
        logical_key = _native_bytes(key)
        record = self.records.get(logical_key)
        if record is None or record["removed"]:
            return int(StoreStatus.NOT_FOUND)
        record["removed"] = True
        active = any(lease["key"] == logical_key and lease["active"] for lease in self.leases.values())
        if active:
            return int(StoreStatus.REMOVE_PENDING)
        del self.records[logical_key]
        return int(StoreStatus.SUCCESS)

    def sms_reserve(
        self,
        store: ctypes.c_void_p,
        key: _native.Bytes,
        payload_length: int,
        descriptor: _native.Bytes,
        wait: object,
        output: object,
    ) -> int:
        del store, wait
        logical_key = _native_bytes(key)
        if logical_key in self.records or any(
            reservation["key"] == logical_key and reservation["state"] == "active"
            for reservation in self.reservations.values()
        ):
            return int(StoreStatus.DUPLICATE_KEY)
        handle = self._next_reservation
        self._next_reservation += 1
        buffer = (ctypes.c_uint8 * payload_length)()
        self.reservations[handle] = {
            "key": logical_key,
            "descriptor": _native_bytes(descriptor),
            "buffer": buffer,
            "length": payload_length,
            "written": 0,
            "state": "active",
        }
        output._obj.value = handle
        return int(StoreStatus.SUCCESS)

    def sms_reservation_is_valid(self, handle: ctypes.c_void_p) -> int:
        value = self.reservations.get(self._handle_value(handle))
        return int(value is not None and value["state"] == "active")

    def sms_reservation_payload_length(self, handle: ctypes.c_void_p) -> int:
        return int(self.reservations[self._handle_value(handle)]["length"])

    def sms_reservation_bytes_written(self, handle: ctypes.c_void_p) -> int:
        return int(self.reservations[self._handle_value(handle)]["written"])

    def sms_reservation_remaining_bytes(self, handle: ctypes.c_void_p) -> int:
        reservation = self.reservations[self._handle_value(handle)]
        return int(reservation["length"]) - int(reservation["written"])

    def sms_reservation_buffer(self, handle: ctypes.c_void_p, size_hint: int) -> _native.MutableBytes:
        reservation = self.reservations[self._handle_value(handle)]
        remaining = int(reservation["length"]) - int(reservation["written"])
        length = remaining if size_hint <= 0 else min(size_hint, remaining)
        if length:
            pointer = ctypes.cast(
                ctypes.byref(reservation["buffer"], int(reservation["written"])),
                _native.UInt8Pointer,
            )
        else:
            pointer = _native.UInt8Pointer()
        return _native.MutableBytes(pointer, length)

    def sms_advance_reservation(self, handle: ctypes.c_void_p, count: int, wait: object) -> int:
        del wait
        reservation = self.reservations.get(self._handle_value(handle))
        if reservation is None or reservation["state"] != "active":
            return int(StoreStatus.INVALID_RESERVATION)
        remaining = int(reservation["length"]) - int(reservation["written"])
        if count < 0 or count > remaining:
            return int(StoreStatus.RESERVATION_WRITE_OUT_OF_RANGE)
        reservation["written"] = int(reservation["written"]) + count
        return int(StoreStatus.SUCCESS)

    def sms_commit_reservation(self, handle: ctypes.c_void_p, wait: object) -> int:
        del wait
        reservation = self.reservations.get(self._handle_value(handle))
        if reservation is None:
            return int(StoreStatus.INVALID_RESERVATION)
        if reservation["state"] != "active":
            return int(StoreStatus.RESERVATION_ALREADY_COMPLETED)
        if reservation["written"] != reservation["length"]:
            return int(StoreStatus.RESERVATION_INCOMPLETE)
        reservation["state"] = "committed"
        self.records[reservation["key"]] = {
            "value": bytes(reservation["buffer"]),
            "descriptor": reservation["descriptor"],
            "removed": False,
        }
        return int(StoreStatus.SUCCESS)

    def sms_abort_reservation(self, handle: ctypes.c_void_p, wait: object) -> int:
        del wait
        reservation = self.reservations.get(self._handle_value(handle))
        if reservation is None:
            return int(StoreStatus.INVALID_RESERVATION)
        if reservation["state"] != "active":
            return int(StoreStatus.RESERVATION_ALREADY_COMPLETED)
        reservation["state"] = "aborted"
        return int(StoreStatus.SUCCESS)

    def sms_destroy_reservation(self, handle: ctypes.c_void_p) -> None:
        numeric = self._handle_value(handle)
        reservation = self.reservations.get(numeric)
        if reservation is not None and reservation["state"] == "active":
            reservation["state"] = "aborted"
        self.reservations.pop(numeric, None)
        self.events.append("destroy_reservation")

    def sms_close_store(self, handle: ctypes.c_void_p) -> None:
        self.closed_handles.append(self._handle_value(handle))
        self.events.append("close_store")

    def sms_destroy_store(self, handle: ctypes.c_void_p) -> None:
        self.destroyed_handles.append(self._handle_value(handle))
        self.events.append("destroy_store")


def _fake_store(library: _LifecycleLibrary, handle: int = 1) -> MemoryStore:
    return MemoryStore(
        ctypes.c_void_p(handle),
        library,
        ProtocolInfo(2, 0, 2, 7, 0),
    )


class LifecycleAdapterTests(unittest.TestCase):
    def test_publish_segments_reservation_visibility_commit_and_abort(self) -> None:
        library = _LifecycleLibrary()
        store = _fake_store(library)
        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"direct", b"a\x00b", b"meta"))
        status, copied = store.publish_segments(b"segments", [b"ab", b"", b"c\xff"])
        self.assertEqual((StoreStatus.SUCCESS, 4), (status, copied))

        reserved, reservation = store.reserve(b"reserved", 4, b"d")
        self.assertEqual(StoreStatus.SUCCESS, reserved)
        assert reservation is not None
        self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"reserved")[0])
        view = reservation.buffer()
        view[:] = b"v\x00x!"
        self.assertEqual(StoreStatus.SUCCESS, reservation.advance(4))
        with self.assertRaises(ValueError):
            bytes(view)
        self.assertEqual(StoreStatus.SUCCESS, reservation.commit())
        acquired, lease = store.acquire(b"reserved")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None
        self.assertEqual(b"v\x00x!", bytes(lease.value))

        aborted, token = store.reserve(b"aborted", 2)
        self.assertEqual(StoreStatus.SUCCESS, aborted)
        assert token is not None
        self.assertEqual(StoreStatus.SUCCESS, token.abort())
        self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"aborted")[0])
        token.close()
        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"aborted", b"reuse"))
        store.close()

    def test_remove_preserves_lease_then_reuses_key_and_fences_stale_token(self) -> None:
        library = _LifecycleLibrary()
        store = _fake_store(library)
        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"key", b"old"))
        acquired, old_lease = store.acquire(b"key")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert old_lease is not None
        view = old_lease.value
        self.assertEqual(StoreStatus.REMOVE_PENDING, store.remove(b"key"))
        self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"key")[0])
        self.assertEqual(b"old", bytes(view))
        self.assertEqual(StoreStatus.SUCCESS, old_lease.release())
        with self.assertRaises(ValueError):
            bytes(view)
        old_lease.close()

        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"key", b"new"))
        acquired, current_lease = store.acquire(b"key")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert current_lease is not None
        self.assertEqual(StoreStatus.INVALID_LEASE, old_lease.release())
        self.assertEqual(b"new", bytes(current_lease.value))
        current_lease.close()
        store.close()

    def test_closing_one_participant_does_not_close_peer_or_its_children(self) -> None:
        library = _LifecycleLibrary()
        first = _fake_store(library, 11)
        peer = _fake_store(library, 12)
        self.assertEqual(StoreStatus.SUCCESS, peer.publish(b"peer", b"alive"))
        acquired, lease = peer.acquire(b"peer")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None
        view = lease.value

        first.close()
        self.assertEqual([11], library.closed_handles)
        self.assertEqual([11], library.destroyed_handles)
        self.assertTrue(peer.is_open)
        self.assertTrue(lease.is_valid)
        self.assertEqual(b"alive", bytes(view))
        peer.close()
        with self.assertRaises(ValueError):
            bytes(view)
        self.assertEqual([11, 12], library.closed_handles)
        self.assertEqual([11, 12], library.destroyed_handles)

    def test_store_close_invalidates_direct_lease_and_reservation_views_before_unmap(self) -> None:
        library = _LifecycleLibrary()
        store = _fake_store(library)
        self.assertEqual(StoreStatus.SUCCESS, store.publish(b"lease", b"value"))
        _, lease = store.acquire(b"lease")
        _, reservation = store.reserve(b"reservation", 2)
        assert lease is not None and reservation is not None
        lease_view = lease.value
        reservation_view = reservation.buffer()

        store.close()

        with self.assertRaises(ValueError):
            bytes(lease_view)
        with self.assertRaises(ValueError):
            bytes(reservation_view)
        self.assertFalse(lease.is_valid)
        self.assertFalse(reservation.is_valid)
        self.assertEqual(["close_store", "destroy_store"], library.events[-2:])
        self.assertCountEqual(
            ["destroy_lease", "destroy_reservation"],
            library.events[:-2],
        )


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

    def test_derived_views_pin_token_transitions_until_released(self) -> None:
        status, store = MemoryStore.open(
            create_options("derived-token-transitions", slots=2)
        )
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"lease", b"data"))
            acquired, lease = store.acquire(b"lease")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            lease_root = lease.value
            lease_derived = lease_root[:]
            self.assertEqual(
                StoreStatus.STORE_BUSY,
                lease.release(wait=WaitOptions.NO_WAIT),
            )
            with self.assertRaises(BufferError):
                lease.close()
            self.assertTrue(lease.is_valid)
            self.assertEqual(b"data", bytes(lease_derived))
            lease_derived.release()
            self.assertEqual(StoreStatus.SUCCESS, lease.release())

            reserved, reservation = store.reserve(b"reservation", 4)
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None
            reservation_root = reservation.buffer()
            reservation_derived = reservation_root[:]
            reservation_derived[:] = b"safe"
            self.assertEqual(
                StoreStatus.STORE_BUSY,
                reservation.advance(4, wait=WaitOptions.NO_WAIT),
            )
            self.assertEqual(
                StoreStatus.STORE_BUSY,
                reservation.commit(wait=WaitOptions.NO_WAIT),
            )
            with self.assertRaises(BufferError):
                reservation.buffer()
            with self.assertRaises(BufferError):
                reservation.close()
            self.assertTrue(reservation.is_valid)
            self.assertEqual(0, reservation.bytes_written)
            reservation_derived.release()
            replacement = reservation.buffer()
            replacement[:] = b"safe"
            self.assertEqual(StoreStatus.SUCCESS, reservation.advance(4))
            self.assertEqual(StoreStatus.SUCCESS, reservation.commit())

            acquired, committed = store.acquire(b"reservation")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert committed is not None
            with committed:
                self.assertEqual(b"safe", bytes(committed.value))

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

            with CancellationSource() as cancellation:
                self.assertEqual(StoreStatus.SUCCESS, cancellation.signal())
                canceled, canceled_report = store.recover_leases(
                    True,
                    wait=WaitOptions.infinite(cancellation),
                )
                self.assertEqual(StoreStatus.OPERATION_CANCELED, canceled)
                self.assertEqual((0, 0, 0, 0, 0), (
                    canceled_report.scanned_count,
                    canceled_report.recovered_count,
                    canceled_report.active_count,
                    canceled_report.unsupported_count,
                    canceled_report.failed_count,
                ))
                self.assertTrue(lease.is_valid)
                self.assertEqual(b"value", bytes(lease_view))

            recovery_status, lease_report = store.recover_leases(True)
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, lease_report.recovered_count)
            self.assertFalse(lease.is_valid)
            with self.assertRaises(ValueError):
                bytes(lease_view)
            self.assertEqual(StoreStatus.LEASE_ALREADY_RELEASED, lease.release())
            self.assertEqual(StoreStatus.SUCCESS, store.remove(b"lease"))
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"lease", b"replacement"))
            self.assertEqual(StoreStatus.LEASE_ALREADY_RELEASED, lease.release())
            acquired, replacement = store.acquire(b"lease")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert replacement is not None
            with replacement:
                self.assertEqual(b"replacement", bytes(replacement.value))

            reserved, reservation = store.reserve(b"reservation", 2)
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None
            reservation_view = reservation.buffer()
            reservation_view[:] = b"no"

            with CancellationSource() as cancellation:
                self.assertEqual(StoreStatus.SUCCESS, cancellation.signal())
                canceled, canceled_report = store.recover_reservations(
                    True,
                    wait=WaitOptions.infinite(cancellation),
                )
                self.assertEqual(StoreStatus.OPERATION_CANCELED, canceled)
                self.assertEqual(0, canceled_report.scanned_count)
                self.assertTrue(reservation.is_valid)
                self.assertEqual(b"no", bytes(reservation_view))

            recovery_status, reservation_report = store.recover_reservations(True)
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, reservation_report.recovered_count)
            self.assertFalse(reservation.is_valid)
            with self.assertRaises(ValueError):
                bytes(reservation_view)
            self.assertEqual(StoreStatus.INVALID_RESERVATION, reservation.advance(0))
            self.assertEqual(StoreStatus.INVALID_RESERVATION, reservation.commit())
            self.assertEqual(StoreStatus.INVALID_RESERVATION, reservation.abort())
            self.assertEqual(StoreStatus.NOT_FOUND, store.acquire(b"reservation")[0])
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"reservation", b"ok"))
            self.assertEqual(StoreStatus.INVALID_RESERVATION, reservation.abort())
            acquired, replacement = store.acquire(b"reservation")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert replacement is not None
            with replacement:
                self.assertEqual(b"ok", bytes(replacement.value))

    def test_derived_reservation_view_blocks_recovery_and_close_before_reuse(self) -> None:
        options = create_options("derived-view-recovery", slots=1, recovery=True)
        owner_status, owner = MemoryStore.open(options)
        self.assertEqual(StoreOpenStatus.SUCCESS, owner_status)
        assert owner is not None
        peer_status, peer = MemoryStore.open(
            StoreOptions(
                name=options.name,
                total_bytes=options.total_bytes,
                slot_count=options.slot_count,
                max_value_bytes=options.max_value_bytes,
                max_descriptor_bytes=options.max_descriptor_bytes,
                max_key_bytes=options.max_key_bytes,
                lease_record_count=options.lease_record_count,
                participant_record_count=options.participant_record_count,
                open_mode=OpenMode.OPEN_EXISTING,
                enable_lease_recovery=True,
            )
        )
        self.assertEqual(StoreOpenStatus.SUCCESS, peer_status)
        assert peer is not None

        derived = None
        try:
            reserved, reservation = owner.reserve(b"key", 4)
            self.assertEqual(StoreStatus.SUCCESS, reserved)
            assert reservation is not None
            direct = reservation.buffer()
            derived = direct[:]
            derived[:] = b"EVIL"

            recovery_status, report = peer.recover_reservations(
                True,
                wait=WaitOptions.NO_WAIT,
            )
            self.assertEqual(StoreStatus.STORE_BUSY, recovery_status)
            self.assertEqual((0, 0, 0, 0, 0), (
                report.scanned_count,
                report.recovered_count,
                report.active_count,
                report.unsupported_count,
                report.failed_count,
            ))
            self.assertTrue(reservation.is_valid)
            self.assertEqual(b"EVIL", bytes(derived))

            with self.assertRaises(BufferError):
                owner.close()
            self.assertTrue(owner.is_open)

            derived.release()
            derived = None
            recovery_status, report = peer.recover_reservations(True)
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, report.recovered_count)
            self.assertEqual(StoreStatus.SUCCESS, peer.publish(b"key", b"GOOD"))
            acquired, lease = peer.acquire(b"key")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            with lease:
                self.assertEqual(b"GOOD", bytes(lease.value))
        finally:
            if derived is not None:
                derived.release()
            owner.close()
            peer.close()

    def test_cross_handle_recovery_invalidates_all_direct_views_before_reuse(self) -> None:
        options = create_options("cross-handle-recovery", slots=4, recovery=True)
        owner_status, owner = MemoryStore.open(options)
        self.assertEqual(StoreOpenStatus.SUCCESS, owner_status)
        assert owner is not None
        peer_status, peer = MemoryStore.open(
            StoreOptions(
                name=options.name,
                total_bytes=options.total_bytes,
                slot_count=options.slot_count,
                max_value_bytes=options.max_value_bytes,
                max_descriptor_bytes=options.max_descriptor_bytes,
                max_key_bytes=options.max_key_bytes,
                lease_record_count=options.lease_record_count,
                participant_record_count=options.participant_record_count,
                open_mode=OpenMode.OPEN_EXISTING,
                enable_lease_recovery=True,
            )
        )
        self.assertEqual(StoreOpenStatus.SUCCESS, peer_status)
        assert peer is not None
        try:
            self.assertEqual(StoreStatus.SUCCESS, owner.publish(b"lease", b"old!"))
            lease_status, lease = owner.acquire(b"lease")
            self.assertEqual(StoreStatus.SUCCESS, lease_status)
            assert lease is not None
            lease_view = lease.value

            recovery_status, lease_report = peer.recover_leases(True)
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, lease_report.recovered_count)
            with self.assertRaises(ValueError):
                bytes(lease_view)
            self.assertEqual(StoreStatus.SUCCESS, peer.remove(b"lease"))

            reservation_status, reservation = owner.reserve(b"reservation", 4)
            self.assertEqual(StoreStatus.SUCCESS, reservation_status)
            assert reservation is not None
            reservation_view = reservation.buffer()
            reservation_view[:] = b"old!"

            native_recovery_returned = threading.Event()
            allow_recovery_wrapper_return = threading.Event()
            publisher_started = threading.Event()
            publisher_entered_native = threading.Event()

            class _DelayedRecoveryLibrary:
                def __init__(self, inner: object) -> None:
                    self._inner = inner

                def __getattr__(self, name: str) -> object:
                    return getattr(self._inner, name)

                def sms_recover_reservations(self, *args: object) -> int:
                    result = self._inner.sms_recover_reservations(*args)
                    native_recovery_returned.set()
                    if not allow_recovery_wrapper_return.wait(5):
                        raise TimeoutError("test did not release the delayed recovery wrapper")
                    return int(result)

                def sms_publish(self, *args: object) -> int:
                    publisher_entered_native.set()
                    return int(self._inner.sms_publish(*args))

            original_library = peer._lib
            peer._lib = _DelayedRecoveryLibrary(original_library)
            recovery_results: list[tuple[StoreStatus, object]] = []
            recovery_errors: list[BaseException] = []
            publish_results: list[StoreStatus] = []
            publish_errors: list[BaseException] = []

            def recover() -> None:
                try:
                    recovery_results.append(peer.recover_reservations(True))
                except BaseException as error:
                    recovery_errors.append(error)

            def publish_replacement() -> None:
                publisher_started.set()
                try:
                    publish_results.append(peer.publish(b"reservation", b"GOOD"))
                except BaseException as error:
                    publish_errors.append(error)

            recovery_thread = threading.Thread(target=recover)
            publish_thread = threading.Thread(target=publish_replacement)
            recovery_thread.start()
            self.assertTrue(native_recovery_returned.wait(5))

            projection_started = time.monotonic()
            self.assertFalse(reservation.is_valid)
            self.assertEqual(0, reservation.payload_length)
            self.assertEqual(0, reservation.bytes_written)
            self.assertEqual(0, reservation.remaining_bytes)
            with self.assertRaises(RuntimeError):
                reservation.buffer()
            self.assertLess(time.monotonic() - projection_started, 0.25)

            publish_thread.start()
            self.assertTrue(publisher_started.wait(5))
            try:
                with self.assertRaises(ValueError):
                    reservation_view[:] = b"EVIL"
                self.assertFalse(
                    publisher_entered_native.wait(0.25),
                    "slot reuse entered native code before Python revoked stale views",
                )
            finally:
                allow_recovery_wrapper_return.set()
                recovery_thread.join(5)
                publish_thread.join(5)
                peer._lib = original_library

            self.assertFalse(recovery_thread.is_alive())
            self.assertFalse(publish_thread.is_alive())
            self.assertEqual([], recovery_errors)
            self.assertEqual([], publish_errors)
            self.assertEqual(1, len(recovery_results))
            recovery_status, reservation_report = recovery_results[0]
            self.assertEqual(StoreStatus.SUCCESS, recovery_status)
            self.assertEqual(1, reservation_report.recovered_count)
            self.assertEqual([StoreStatus.SUCCESS], publish_results)
            acquired_status, replacement = peer.acquire(b"reservation")
            self.assertEqual(StoreStatus.SUCCESS, acquired_status)
            assert replacement is not None
            with replacement:
                self.assertEqual(b"GOOD", bytes(replacement.value))
        finally:
            peer.close()
            owner.close()


if __name__ == "__main__":
    unittest.main()
