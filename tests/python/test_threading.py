from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, TimeoutError as FutureTimeout
import ctypes
import gc
import pickle
import threading
import time
import unittest
import weakref

from shared_memory_store import (
    CancellationSource,
    MemoryStore,
    ProtocolInfo,
    StoreStatus,
    WaitOptions,
)
from shared_memory_store import _native


def _store(library: object) -> MemoryStore:
    return MemoryStore(
        ctypes.c_void_p(1),
        library,
        ProtocolInfo(2, 0, 2, 7, 0),
        (id(library), "threading-tests"),
    )


def _store_pair(library: object) -> tuple[MemoryStore, MemoryStore]:
    key = (id(library), "mapping-gate-tests")
    protocol = ProtocolInfo(2, 0, 2, 7, 0)
    return (
        MemoryStore(ctypes.c_void_p(11), library, protocol, key),
        MemoryStore(ctypes.c_void_p(12), library, protocol, key),
    )


def _wait_until(predicate: object, timeout: float = 2.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.001)
    return bool(predicate())


class _ConcurrentPublishLibrary:
    def __init__(self) -> None:
        self.barrier = threading.Barrier(2)
        self.lock = threading.Lock()
        self.active = 0
        self.maximum_active = 0
        self.closed = 0
        self.destroyed = 0
        self.events: list[str] = []

    def sms_publish(self, *arguments: object) -> int:
        del arguments
        with self.lock:
            self.active += 1
            self.maximum_active = max(self.maximum_active, self.active)
        try:
            self.barrier.wait(timeout=2)
            return int(StoreStatus.SUCCESS)
        finally:
            with self.lock:
                self.active -= 1

    def sms_publish_segments(
        self,
        store: object,
        key: object,
        segments: object,
        segment_count: int,
        descriptor: object,
        wait: object,
        copied: object,
    ) -> int:
        del store, key, segments, descriptor, wait
        copied._obj.value = segment_count
        return int(StoreStatus.SUCCESS)

    def sms_close_store(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.closed += 1
        self.events.append("close_store")

    def sms_destroy_store(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.destroyed += 1
        self.events.append("destroy_store")


class _BlockingPublishLibrary:
    def __init__(self) -> None:
        self.entered = threading.Event()
        self.allow_return = threading.Event()
        self.native_closed = threading.Event()
        self.events: list[str] = []

    def sms_publish(self, *arguments: object) -> int:
        del arguments
        self.events.append("publish_enter")
        self.entered.set()
        if not self.allow_return.wait(timeout=5):
            return int(StoreStatus.UNKNOWN_FAILURE)
        self.events.append("publish_exit")
        return int(StoreStatus.SUCCESS)

    def sms_close_store(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.events.append("close_store")
        self.native_closed.set()

    def sms_destroy_store(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.events.append("destroy_store")


class _BlockingLeaseLibrary:
    def __init__(self, *, blocked: bool = True) -> None:
        self.value_entered = threading.Event()
        self.allow_value = threading.Event()
        if not blocked:
            self.allow_value.set()
        self.events: list[str] = []
        self.destroyed = False
        self._buffer = (ctypes.c_uint8 * 5).from_buffer_copy(b"value")

    def sms_acquire(self, store: object, key: object, wait: object, output: object) -> int:
        del store, key, wait
        output._obj.value = 7
        return int(StoreStatus.SUCCESS)

    def sms_lease_is_valid(self, handle: ctypes.c_void_p) -> int:
        del handle
        return int(not self.destroyed)

    def sms_lease_value(self, handle: ctypes.c_void_p) -> _native.Bytes:
        del handle
        self.events.append("lease_value_enter")
        self.value_entered.set()
        if not self.allow_value.wait(timeout=5):
            raise TimeoutError("test lease projection was not released")
        self.events.append("lease_value_exit")
        return _native.Bytes(ctypes.cast(self._buffer, _native.UInt8Pointer), len(self._buffer))

    def sms_lease_descriptor(self, handle: ctypes.c_void_p) -> _native.Bytes:
        del handle
        return _native.Bytes(_native.UInt8Pointer(), 0)

    def sms_release_lease(self, handle: ctypes.c_void_p, wait: object) -> int:
        del handle, wait
        self.destroyed = True
        return int(StoreStatus.SUCCESS)

    def sms_destroy_lease(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.destroyed = True
        self.events.append("destroy_lease")

    def sms_close_store(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.events.append("close_store")

    def sms_destroy_store(self, handle: ctypes.c_void_p) -> None:
        del handle
        self.events.append("destroy_store")


class _BlockingRecoveryLibrary(_BlockingLeaseLibrary):
    def __init__(self) -> None:
        super().__init__(blocked=False)
        self.recovery_entered = threading.Event()
        self.allow_recovery = threading.Event()
        self.recovered = False
        self.recovery_timeouts: list[int] = []

    def sms_lease_is_valid(self, handle: ctypes.c_void_p) -> int:
        del handle
        return int(not self.destroyed and not self.recovered)

    def sms_recover_leases(
        self,
        store: ctypes.c_void_p,
        recover_current_process: int,
        wait: object,
        report: object,
    ) -> int:
        del store, recover_current_process
        self.recovery_timeouts.append(int(wait._obj.timeout_milliseconds))
        self.events.append("recovery_enter")
        self.recovery_entered.set()
        if not self.allow_recovery.wait(timeout=5):
            return int(StoreStatus.UNKNOWN_FAILURE)
        self.recovered = True
        report._obj.scanned_count = 1
        report._obj.recovered_count = 1
        report._obj.active_count = 0
        report._obj.unsupported_count = 0
        report._obj.failed_count = 0
        self.events.append("recovery_exit")
        return int(StoreStatus.SUCCESS)


class _MappingGateLibrary:
    """Deterministic native fake used only to hold either side of the gate."""

    def __init__(self) -> None:
        self.publish_entered = threading.Event()
        self.allow_publish = threading.Event()
        self.allow_publish.set()
        self.recovery_entered = threading.Event()
        self.allow_recovery = threading.Event()
        self.allow_recovery.set()
        self.block_publish = False
        self.block_recovery = False
        self.publish_timeouts: list[int] = []
        self.recovery_timeouts: list[int] = []
        self.publish_calls = 0
        self.recovery_calls = 0
        self._cancellations: dict[int, bool] = {}
        self._next_cancellation = 101

    @staticmethod
    def _handle_value(handle: ctypes.c_void_p) -> int:
        return int(handle.value or 0)

    def cancellation(self) -> CancellationSource:
        handle = self._next_cancellation
        self._next_cancellation += 1
        self._cancellations[handle] = False
        source = CancellationSource.__new__(CancellationSource)
        source._lib = self
        source._handle = ctypes.c_void_p(handle)
        source._condition = threading.Condition(threading.RLock())
        source._active_borrows = 0
        source._closing = False
        return source

    def sms_signal_cancellation(self, handle: ctypes.c_void_p) -> int:
        self._cancellations[self._handle_value(handle)] = True
        return int(StoreStatus.SUCCESS)

    def sms_cancellation_is_signaled(self, handle: ctypes.c_void_p) -> int:
        return int(self._cancellations.get(self._handle_value(handle), False))

    def sms_destroy_cancellation(self, handle: ctypes.c_void_p) -> None:
        self._cancellations.pop(self._handle_value(handle), None)

    def sms_publish(
        self,
        store: object,
        key: object,
        value: object,
        descriptor: object,
        wait: object,
    ) -> int:
        del store, key, value, descriptor
        self.publish_calls += 1
        self.publish_timeouts.append(int(wait._obj.timeout_milliseconds))
        self.publish_entered.set()
        if self.block_publish and not self.allow_publish.wait(timeout=5):
            return int(StoreStatus.UNKNOWN_FAILURE)
        return int(StoreStatus.SUCCESS)

    def sms_recover_reservations(
        self,
        store: object,
        recover_current_process: object,
        wait: object,
        report: object,
    ) -> int:
        del store, recover_current_process
        self.recovery_calls += 1
        self.recovery_timeouts.append(int(wait._obj.timeout_milliseconds))
        self.recovery_entered.set()
        if self.block_recovery and not self.allow_recovery.wait(timeout=5):
            return int(StoreStatus.UNKNOWN_FAILURE)
        report._obj.scanned_count = 0
        report._obj.recovered_count = 0
        report._obj.active_count = 0
        report._obj.unsupported_count = 0
        report._obj.failed_count = 0
        return int(StoreStatus.SUCCESS)

    def sms_close_store(self, handle: ctypes.c_void_p) -> None:
        del handle

    def sms_destroy_store(self, handle: ctypes.c_void_p) -> None:
        del handle


class ThreadingAdapterTests(unittest.TestCase):
    def test_same_handle_native_calls_are_not_broadly_serialized(self) -> None:
        library = _ConcurrentPublishLibrary()
        store = _store(library)
        with ThreadPoolExecutor(max_workers=2) as executor:
            first = executor.submit(store.publish, b"first", b"1")
            second = executor.submit(store.publish, b"second", b"2")
            self.assertEqual(StoreStatus.SUCCESS, first.result(timeout=3))
            self.assertEqual(StoreStatus.SUCCESS, second.result(timeout=3))
        self.assertEqual(2, library.maximum_active)
        store.close()

    def test_publish_segments_materializes_caller_iterable_before_mapping_entry(self) -> None:
        library = _ConcurrentPublishLibrary()
        store = _store(library)
        observed_active_operations: list[int] = []

        def segments() -> object:
            observed_active_operations.append(store._active_operations)
            yield b"a"
            observed_active_operations.append(store._active_operations)
            yield b"b"

        status, copied = store.publish_segments(b"key", segments())

        self.assertEqual(StoreStatus.SUCCESS, status)
        self.assertEqual(2, copied)
        self.assertEqual([0, 0], observed_active_operations)
        store.close()

    def test_close_rejects_new_calls_and_waits_for_an_entered_native_call(self) -> None:
        library = _BlockingPublishLibrary()
        store = _store(library)
        with ThreadPoolExecutor(max_workers=3) as executor:
            entered = executor.submit(store.publish, b"entered", b"value")
            self.assertTrue(library.entered.wait(timeout=2))
            closing = executor.submit(store.close)
            self.assertTrue(_wait_until(lambda: store._closing))
            self.assertFalse(store.is_open)
            self.assertFalse(closing.done())
            self.assertFalse(library.native_closed.is_set())

            rejected = executor.submit(store.publish, b"late", b"value")
            try:
                self.assertEqual(StoreStatus.STORE_DISPOSED, rejected.result(timeout=0.5))
            except FutureTimeout:
                self.fail("an operation entering after close began was not rejected immediately")
            finally:
                library.allow_return.set()

            self.assertEqual(StoreStatus.SUCCESS, entered.result(timeout=2))
            closing.result(timeout=2)

        self.assertEqual(
            ["publish_enter", "publish_exit", "close_store", "destroy_store"],
            library.events,
        )

    def test_store_close_drains_child_projection_then_invalidates_before_native_close(self) -> None:
        library = _BlockingLeaseLibrary()
        store = _store(library)
        acquired, lease = store.acquire(b"key")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None

        with ThreadPoolExecutor(max_workers=2) as executor:
            projected = executor.submit(lambda: lease.value)
            self.assertTrue(library.value_entered.wait(timeout=2))
            closing = executor.submit(store.close)
            self.assertTrue(_wait_until(lambda: store._closing))
            self.assertFalse(closing.done())
            library.allow_value.set()
            view = projected.result(timeout=2)
            closing.result(timeout=2)

        with self.assertRaises(ValueError):
            bytes(view)
        self.assertFalse(lease.is_valid)
        self.assertLess(library.events.index("lease_value_exit"), library.events.index("destroy_lease"))
        self.assertLess(library.events.index("destroy_lease"), library.events.index("close_store"))
        self.assertLess(library.events.index("close_store"), library.events.index("destroy_store"))

    def test_derived_memoryview_retains_token_until_caller_releases_the_borrow(self) -> None:
        library = _BlockingLeaseLibrary(blocked=False)
        store = _store(library)
        _, lease = store.acquire(b"key")
        assert lease is not None
        reference = weakref.ref(lease)
        direct = lease.value
        derived = direct[1:4]
        self.assertTrue(derived.readonly)

        del lease
        gc.collect()
        self.assertIsNotNone(reference())
        direct.release()
        del direct
        gc.collect()
        self.assertIsNotNone(reference())

        derived.release()
        del derived
        gc.collect()
        self.assertIsNone(reference())
        self.assertTrue(library.destroyed)
        store.close()

    def test_close_retry_releases_direct_root_after_secondary_export_ends(self) -> None:
        library = _BlockingLeaseLibrary(blocked=False)
        store = _store(library)
        _, lease = store.acquire(b"key")
        assert lease is not None
        root = lease.value
        secondary = pickle.PickleBuffer(root)

        with self.assertRaises(BufferError):
            store.close()
        self.assertTrue(store.is_open)
        self.assertFalse(library.destroyed)

        secondary.release()
        store.close()
        with self.assertRaises(ValueError):
            bytes(root)
        self.assertTrue(library.destroyed)

    def test_concurrent_failed_closes_do_not_report_success_for_an_open_store(self) -> None:
        library = _BlockingLeaseLibrary(blocked=False)
        store = _store(library)
        _, lease = store.acquire(b"key")
        assert lease is not None
        root = lease.value
        derived = root[:]

        def close_result() -> type[BaseException] | None:
            try:
                store.close()
            except BaseException as error:
                return type(error)
            return None

        with ThreadPoolExecutor(max_workers=2) as executor:
            first = executor.submit(close_result)
            second = executor.submit(close_result)
            results = [first.result(timeout=2), second.result(timeout=2)]

        self.assertEqual([BufferError, BufferError], results)
        self.assertTrue(store.is_open)
        derived.release()
        store.close()

    def test_recovery_forwards_only_the_wait_budget_remaining_after_view_drain(self) -> None:
        library = _BlockingRecoveryLibrary()
        library.allow_recovery.set()
        owner, peer = _store_pair(library)
        _, lease = owner.acquire(b"key")
        assert lease is not None
        root = lease.value
        derived = root[:]

        def root_released() -> bool:
            try:
                bytes(root)
                return False
            except ValueError:
                return True

        with ThreadPoolExecutor(max_workers=1) as executor:
            recovering = executor.submit(
                peer.recover_leases,
                True,
                wait=WaitOptions(300),
            )
            self.assertTrue(_wait_until(root_released))
            time.sleep(0.12)
            derived.release()
            status, _ = recovering.result(timeout=2)

        self.assertEqual(StoreStatus.SUCCESS, status)
        self.assertEqual(1, len(library.recovery_timeouts))
        self.assertLess(library.recovery_timeouts[0], 250)
        owner.close()
        peer.close()

    def test_concurrent_close_is_idempotent_and_closes_native_handle_once(self) -> None:
        library = _ConcurrentPublishLibrary()
        store = _store(library)
        with ThreadPoolExecutor(max_workers=2) as executor:
            first = executor.submit(store.close)
            second = executor.submit(store.close)
            first.result(timeout=2)
            second.result(timeout=2)
        self.assertEqual(1, library.closed)
        self.assertEqual(1, library.destroyed)
        self.assertEqual(["close_store", "destroy_store"], library.events)

    def test_close_waits_for_recovery_to_invalidate_a_borrowed_view_before_native_close(self) -> None:
        library = _BlockingRecoveryLibrary()
        store = _store(library)
        acquired, lease = store.acquire(b"key")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None
        view = lease.value
        self.assertEqual(b"value", bytes(view))

        with ThreadPoolExecutor(max_workers=2) as executor:
            recovering = executor.submit(store.recover_leases, True)
            self.assertTrue(library.recovery_entered.wait(timeout=2))

            projection_started = time.monotonic()
            self.assertFalse(lease.is_valid)
            with self.assertRaises(RuntimeError):
                _ = lease.value
            self.assertLess(time.monotonic() - projection_started, 0.25)

            closing = executor.submit(store.close)
            self.assertTrue(_wait_until(lambda: store._closing))
            self.assertFalse(closing.done())

            library.allow_recovery.set()
            status, report = recovering.result(timeout=2)
            self.assertEqual(StoreStatus.SUCCESS, status)
            self.assertEqual(1, report.recovered_count)
            closing.result(timeout=2)

        with self.assertRaises(ValueError):
            bytes(view)
        self.assertFalse(lease.is_valid)
        self.assertLess(library.events.index("recovery_exit"), library.events.index("destroy_lease"))
        self.assertLess(library.events.index("destroy_lease"), library.events.index("close_store"))
        self.assertLess(library.events.index("close_store"), library.events.index("destroy_store"))

    def test_peer_close_drains_exclusive_mapping_recovery_before_destroy(self) -> None:
        library = _BlockingRecoveryLibrary()
        owner, peer = _store_pair(library)
        acquired, lease = owner.acquire(b"key")
        self.assertEqual(StoreStatus.SUCCESS, acquired)
        assert lease is not None

        try:
            with ThreadPoolExecutor(max_workers=2) as executor:
                recovering = executor.submit(peer.recover_leases, True)
                self.assertTrue(library.recovery_entered.wait(timeout=2))
                closing = executor.submit(owner.close)
                self.assertTrue(_wait_until(lambda: owner._closing))
                self.assertFalse(closing.done())
                self.assertNotIn("destroy_lease", library.events)
                self.assertNotIn("close_store", library.events)

                library.allow_recovery.set()
                self.assertEqual(StoreStatus.SUCCESS, recovering.result(timeout=2)[0])
                closing.result(timeout=2)

            self.assertLess(library.events.index("recovery_exit"), library.events.index("destroy_lease"))
            self.assertLess(library.events.index("destroy_lease"), library.events.index("close_store"))
            self.assertLess(library.events.index("close_store"), library.events.index("destroy_store"))
        finally:
            library.allow_recovery.set()
            peer.close()

    def test_shared_gate_waits_honor_no_wait_timeout_and_cancellation(self) -> None:
        library = _MappingGateLibrary()
        owner, peer = _store_pair(library)
        library.block_recovery = True
        library.allow_recovery.clear()
        try:
            with ThreadPoolExecutor(max_workers=3) as executor:
                recovery = executor.submit(
                    owner.recover_reservations,
                    True,
                    wait=WaitOptions.INFINITE,
                )
                self.assertTrue(library.recovery_entered.wait(timeout=2))

                cancellation = library.cancellation()
                self.assertEqual(StoreStatus.SUCCESS, cancellation.signal())
                try:
                    started = time.monotonic()
                    self.assertEqual(
                        StoreStatus.OPERATION_CANCELED,
                        peer.publish(
                            b"pre-canceled",
                            b"value",
                            wait=WaitOptions.infinite(cancellation),
                        ),
                    )
                    self.assertLess(time.monotonic() - started, 0.25)
                finally:
                    cancellation.close()

                started = time.monotonic()
                self.assertEqual(
                    StoreStatus.STORE_BUSY,
                    peer.publish(b"no-wait", b"value", wait=WaitOptions.NO_WAIT),
                )
                self.assertLess(time.monotonic() - started, 0.25)

                started = time.monotonic()
                self.assertEqual(
                    StoreStatus.STORE_BUSY,
                    peer.publish(b"finite", b"value", wait=WaitOptions(50)),
                )
                elapsed = time.monotonic() - started
                self.assertGreaterEqual(elapsed, 0.03)
                self.assertLess(elapsed, 0.5)

                cancellation = library.cancellation()
                try:
                    canceled_publish = executor.submit(
                        peer.publish,
                        b"cancel-while-parked",
                        b"value",
                        wait=WaitOptions.infinite(cancellation),
                    )
                    time.sleep(0.05)
                    self.assertFalse(canceled_publish.done())
                    signaled_at = time.monotonic()
                    self.assertEqual(StoreStatus.SUCCESS, cancellation.signal())
                    self.assertEqual(
                        StoreStatus.OPERATION_CANCELED,
                        canceled_publish.result(timeout=1),
                    )
                    self.assertLess(time.monotonic() - signaled_at, 0.25)
                finally:
                    cancellation.close()

                remaining_publish = executor.submit(
                    peer.publish,
                    b"remaining",
                    b"value",
                    wait=WaitOptions(500),
                )
                time.sleep(0.1)
                self.assertFalse(remaining_publish.done())
                library.allow_recovery.set()
                self.assertEqual(StoreStatus.SUCCESS, recovery.result(timeout=2)[0])
                self.assertEqual(StoreStatus.SUCCESS, remaining_publish.result(timeout=2))

            self.assertEqual(1, library.publish_calls)
            self.assertEqual(1, len(library.publish_timeouts))
            self.assertGreaterEqual(library.publish_timeouts[0], 0)
            self.assertLess(library.publish_timeouts[0], 480)
        finally:
            library.allow_recovery.set()
            peer.close()
            owner.close()

    def test_exclusive_gate_waits_honor_no_wait_timeout_and_cancellation(self) -> None:
        library = _MappingGateLibrary()
        owner, peer = _store_pair(library)
        library.block_publish = True
        library.allow_publish.clear()
        try:
            with ThreadPoolExecutor(max_workers=3) as executor:
                publish = executor.submit(
                    owner.publish,
                    b"holder",
                    b"value",
                    wait=WaitOptions.INFINITE,
                )
                self.assertTrue(library.publish_entered.wait(timeout=2))

                cancellation = library.cancellation()
                self.assertEqual(StoreStatus.SUCCESS, cancellation.signal())
                try:
                    started = time.monotonic()
                    status, _ = peer.recover_reservations(
                        True,
                        wait=WaitOptions.infinite(cancellation),
                    )
                    self.assertEqual(StoreStatus.OPERATION_CANCELED, status)
                    self.assertLess(time.monotonic() - started, 0.25)
                finally:
                    cancellation.close()

                started = time.monotonic()
                status, _ = peer.recover_reservations(True, wait=WaitOptions.NO_WAIT)
                self.assertEqual(StoreStatus.STORE_BUSY, status)
                self.assertLess(time.monotonic() - started, 0.25)

                started = time.monotonic()
                status, _ = peer.recover_reservations(True, wait=WaitOptions(50))
                self.assertEqual(StoreStatus.STORE_BUSY, status)
                elapsed = time.monotonic() - started
                self.assertGreaterEqual(elapsed, 0.03)
                self.assertLess(elapsed, 0.5)

                cancellation = library.cancellation()
                try:
                    canceled_recovery = executor.submit(
                        peer.recover_reservations,
                        True,
                        wait=WaitOptions.infinite(cancellation),
                    )
                    time.sleep(0.05)
                    self.assertFalse(canceled_recovery.done())
                    signaled_at = time.monotonic()
                    self.assertEqual(StoreStatus.SUCCESS, cancellation.signal())
                    self.assertEqual(
                        StoreStatus.OPERATION_CANCELED,
                        canceled_recovery.result(timeout=1)[0],
                    )
                    self.assertLess(time.monotonic() - signaled_at, 0.25)
                finally:
                    cancellation.close()

                remaining_recovery = executor.submit(
                    peer.recover_reservations,
                    True,
                    wait=WaitOptions(500),
                )
                time.sleep(0.1)
                self.assertFalse(remaining_recovery.done())
                library.allow_publish.set()
                self.assertEqual(StoreStatus.SUCCESS, publish.result(timeout=2))
                self.assertEqual(
                    StoreStatus.SUCCESS,
                    remaining_recovery.result(timeout=2)[0],
                )

            self.assertEqual(1, library.recovery_calls)
            self.assertEqual(1, len(library.recovery_timeouts))
            self.assertGreaterEqual(library.recovery_timeouts[0], 0)
            self.assertLess(library.recovery_timeouts[0], 480)
        finally:
            library.allow_publish.set()
            peer.close()
            owner.close()


if __name__ == "__main__":
    unittest.main()
