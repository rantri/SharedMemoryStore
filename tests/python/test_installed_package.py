from __future__ import annotations

from importlib.resources import files
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import shared_memory_store
from shared_memory_store import _native
from shared_memory_store import MemoryStore, StoreOpenStatus, StoreStatus

from _support import create_options


class PackageMetadataTests(unittest.TestCase):
    def test_version_and_explicit_sdist_inputs_are_lock_free_abi2(self) -> None:
        repository_root = Path(__file__).resolve().parents[2]
        project_text = (repository_root / "pyproject.toml").read_text(encoding="utf-8")

        self.assertIn('version = "1.0.0"', project_text)
        self.assertEqual("1.0.0", shared_memory_store.__version__)
        self.assertIn('wheel.py-api = "py3"', project_text)
        self.assertIn("wheel.packages = []", project_text)
        for required_input in (
            "/pyproject.toml",
            "/CMakeLists.txt",
            "/cmake/**",
            "/src/cpp/**",
            "/src/python/shared_memory_store/*.py",
            "/protocol/compatibility.json",
            "/LICENSE",
            "/README.md",
        ):
            with self.subTest(build_input=required_input):
                self.assertIn(f'"{required_input}"', project_text)

    def test_missing_adjacent_native_artifact_is_rejected_without_searching(self) -> None:
        filename = _native._library_filename()
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(_native, "files", return_value=Path(directory)):
                with self.assertRaisesRegex(OSError, "packaged native library"):
                    _native._bundled_library_path(filename)

    def test_wrong_native_abi_is_rejected_before_protocol_use(self) -> None:
        class WrongAbiLibrary:
            @staticmethod
            def sms_abi_version() -> int:
                return 0x00010000

        with patch.object(_native, "_is_supported_architecture", return_value=True):
            with self.assertRaisesRegex(ImportError, "not compatible"):
                _native._verify_contract(WrongAbiLibrary(), Path("wrong-abi"))  # type: ignore[arg-type]


class InstalledPackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if os.environ.get("SMS_TEST_INSTALLED_PACKAGE") != "1":
            raise unittest.SkipTest("set SMS_TEST_INSTALLED_PACKAGE=1 in the clean wheel environment")

    def test_import_and_native_load_do_not_resolve_from_repository_source(self) -> None:
        self.assertIn(os.environ.get("PYTHONPATH"), (None, ""))
        self.assertEqual("1.0.0", shared_memory_store.__version__)
        package_file = Path(shared_memory_store.__file__).resolve()
        repository_root = Path(__file__).resolve().parents[2]
        repository_source = repository_root / "src" / "python"
        current_directory = Path.cwd().resolve()
        self.assertNotEqual(repository_root.resolve(), current_directory)
        self.assertFalse(current_directory.is_relative_to(repository_root.resolve()))
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
