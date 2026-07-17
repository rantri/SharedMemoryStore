from __future__ import annotations

import ctypes
import gc
import threading
import unittest
import weakref

from shared_memory_store import (
    CancellationSource,
    MemoryStore,
    StoreOpenStatus,
    StoreStatus,
    WaitOptions,
)
from shared_memory_store.store import _native_wait

from _support import create_options, require_native


class CancellationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        require_native()

    def test_source_is_idempotently_signaled_and_context_managed(self) -> None:
        source = CancellationSource()
        with source:
            self.assertFalse(source.is_closed)
            self.assertFalse(source.is_signaled)
            self.assertEqual(StoreStatus.SUCCESS, source.signal())
            self.assertEqual(StoreStatus.SUCCESS, source.signal())
            self.assertTrue(source.is_signaled)

        self.assertTrue(source.is_closed)
        source.close()
        self.assertEqual(StoreStatus.UNKNOWN_FAILURE, source.signal())
        self.assertFalse(source.is_signaled)
        with self.assertRaises(RuntimeError):
            with _native_wait(WaitOptions.default(source)):
                pass

    def test_signaled_source_cancels_open_without_transferring_ownership(self) -> None:
        options = create_options("canceled-open")
        with CancellationSource() as source:
            self.assertEqual(StoreStatus.SUCCESS, source.signal())
            status, store = MemoryStore.open(options, wait=WaitOptions.infinite(source))
            self.assertEqual(StoreOpenStatus.OPERATION_CANCELED, status)
            self.assertIsNone(store)
            self.assertTrue(source.is_signaled)

    def test_wait_options_strongly_retain_the_source(self) -> None:
        source = CancellationSource()
        reference = weakref.ref(source)
        wait = WaitOptions(25, source)
        del source
        gc.collect()

        self.assertIsNotNone(reference())
        self.assertIs(reference(), wait.cancellation)
        assert wait.cancellation is not None
        wait.cancellation.close()

    def test_close_waits_until_native_wait_releases_its_borrow(self) -> None:
        source = CancellationSource()
        wait = WaitOptions.infinite(source)
        borrowed = threading.Event()
        release_borrow = threading.Event()
        close_started = threading.Event()
        closed = threading.Event()

        def borrower() -> None:
            with _native_wait(wait) as native_wait:
                self.assertNotEqual(0, ctypes.cast(native_wait.cancellation, ctypes.c_void_p).value)
                borrowed.set()
                self.assertTrue(release_borrow.wait(5))

        def closer() -> None:
            close_started.set()
            source.close()
            closed.set()

        borrow_thread = threading.Thread(target=borrower)
        close_thread = threading.Thread(target=closer)
        borrow_thread.start()
        self.assertTrue(borrowed.wait(5))
        close_thread.start()
        self.assertTrue(close_started.wait(5))
        self.assertFalse(closed.wait(0.05))

        release_borrow.set()
        borrow_thread.join(5)
        close_thread.join(5)
        self.assertFalse(borrow_thread.is_alive())
        self.assertFalse(close_thread.is_alive())
        self.assertTrue(closed.is_set())
        self.assertTrue(source.is_closed)


if __name__ == "__main__":
    unittest.main()
