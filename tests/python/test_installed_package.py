from __future__ import annotations

from importlib.resources import files
import os
from pathlib import Path
import sys
import unittest

import shared_memory_store
from shared_memory_store import MemoryStore, StoreOpenStatus, StoreStatus

from _support import create_options


class InstalledPackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if os.environ.get("SMS_TEST_INSTALLED_PACKAGE") != "1":
            raise unittest.SkipTest("set SMS_TEST_INSTALLED_PACKAGE=1 in the clean wheel environment")

    def test_import_and_native_load_do_not_resolve_from_repository_source(self) -> None:
        package_file = Path(shared_memory_store.__file__).resolve()
        repository_source = Path(__file__).resolve().parents[2] / "src" / "python"
        self.assertFalse(package_file.is_relative_to(repository_source))
        filename = "shared_memory_store.dll" if sys.platform == "win32" else "libshared_memory_store.so"
        self.assertTrue(files("shared_memory_store").joinpath(filename).is_file())
        loaded = shared_memory_store.native_library_path().resolve()
        self.assertEqual(filename, loaded.name)
        self.assertEqual(package_file.parent, loaded.parent)

    def test_clean_wheel_executes_store_lifecycle(self) -> None:
        status, store = MemoryStore.open(create_options("installed"))
        self.assertEqual(StoreOpenStatus.SUCCESS, status)
        assert store is not None
        with store:
            self.assertEqual(StoreStatus.SUCCESS, store.publish(b"key", b"value"))
            acquired, lease = store.acquire(b"key")
            self.assertEqual(StoreStatus.SUCCESS, acquired)
            assert lease is not None
            with lease:
                self.assertEqual(b"value", bytes(lease.value))


if __name__ == "__main__":
    unittest.main()
