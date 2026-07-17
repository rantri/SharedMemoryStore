"""Test-only raw SMS2 fault and cold-lock helpers for the Python agent."""

from __future__ import annotations

from contextlib import contextmanager
import ctypes
import hashlib
import mmap
import os
from pathlib import Path
import stat
import struct
import sys
import tempfile
from typing import Any, Iterator
import unicodedata

from shared_memory_store import (
    ABI_VERSION,
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    MemoryStore,
    OPTIONAL_FEATURES,
    REQUIRED_FEATURES,
    RESOURCE_PROTOCOL_VERSION,
    StoreOptions,
    StoreStatus,
)
from shared_memory_store import _native


class UnsupportedFaultPlatform(RuntimeError):
    """Raised when a test-only platform fault primitive is unavailable."""


def supports_platform_faults() -> bool:
    return sys.platform in {"linux", "win32"}


def _utf16_code_units(value: str) -> list[int]:
    encoded = value.encode("utf-16-le", errors="strict")
    return [
        int.from_bytes(encoded[offset : offset + 2], "little")
        for offset in range(0, len(encoded), 2)
    ]


def _resource_fragment(public_name: str) -> str:
    readable = "".join(
        chr(code_unit)
        if code_unit < 128
        and (
            ord("A") <= code_unit <= ord("Z")
            or ord("a") <= code_unit <= ord("z")
            or ord("0") <= code_unit <= ord("9")
            or code_unit in (ord("-"), ord("_"), ord("."))
        )
        else "_"
        for code_unit in _utf16_code_units(public_name)
    ).strip("_.")
    readable = (readable or "store")[:80]
    digest = hashlib.sha256(public_name.encode("utf-8", errors="strict")).hexdigest()[:16]
    return f"sms-{readable}-{digest}"


def linux_resource_paths(public_name: str) -> dict[str, Path]:
    fragment = _resource_fragment(public_name)
    root = Path("/dev/shm") if Path("/dev/shm").is_dir() else Path(tempfile.gettempdir())
    directory = root / "SharedMemoryStore"
    return {
        "directory": directory,
        "region": directory / f"{fragment}.region",
        "lock": directory / f"{fragment}.lock",
        "owners": directory / f"{fragment}.owners",
        "lifecycle": directory / f"{fragment}.lifecycle",
    }


def _windows_synchronization_name(public_name: str) -> str:
    scope = "Global\\" if public_name[:7].lower() == "global\\" else "Local\\"
    sanitized = "".join(
        chr(code_unit)
        if unicodedata.category(chr(code_unit)).startswith("L")
        or unicodedata.category(chr(code_unit)) == "Nd"
        or code_unit in (ord("-"), ord("_"))
        else "_"
        for code_unit in _utf16_code_units(public_name)
    )
    return f"{scope}SharedMemoryStore-{sanitized}"


def _store_layout(store: MemoryStore) -> _native.StoreLayout:
    layout = _native.StoreLayout()
    layout.struct_size = ctypes.sizeof(_native.StoreLayout)
    layout.abi_version = ABI_VERSION
    wait = _native.WaitOptions()
    wait.struct_size = ctypes.sizeof(_native.WaitOptions)
    wait.abi_version = ABI_VERSION
    wait.timeout_milliseconds = 1000
    wait.cancellation = None
    with store._entered_operation() as entry:  # type: ignore[attr-defined]
        if entry is None:
            raise RuntimeError("the store is closed")
        status = StoreStatus(int(entry.lib.sms_get_store_layout(
            entry.handle,
            ctypes.byref(wait),
            ctypes.byref(layout),
        )))
    if status is not StoreStatus.SUCCESS:
        raise RuntimeError(f"native SMS2 layout query failed: {status.name}")
    return layout


def _validate_directory(path: Path) -> None:
    information = path.lstat()
    if not stat.S_ISDIR(information.st_mode) or stat.S_ISLNK(information.st_mode):
        raise RuntimeError(f"raw SMS2 resource root is not a real directory: {path}")


@contextmanager
def _raw_mapping(options: StoreOptions, layout: _native.StoreLayout) -> Iterator[mmap.mmap]:
    if sys.platform == "linux":
        paths = linux_resource_paths(options.name)
        _validate_directory(paths["directory"])
        flags = os.O_RDWR | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(paths["region"], flags)
        try:
            information = os.fstat(descriptor)
            if not stat.S_ISREG(information.st_mode) or information.st_size != layout.total_bytes:
                raise RuntimeError("raw SMS2 region is not the exact expected regular file")
            mapped = mmap.mmap(descriptor, layout.total_bytes, access=mmap.ACCESS_WRITE)
            try:
                yield mapped
            finally:
                mapped.close()
        finally:
            os.close(descriptor)
        return

    if sys.platform == "win32":
        mapped = mmap.mmap(
            -1,
            layout.total_bytes,
            tagname=options.name,
            access=mmap.ACCESS_WRITE,
        )
        try:
            yield mapped
        finally:
            mapped.close()
        return

    raise UnsupportedFaultPlatform("raw fault injection supports Windows and Linux only")


def _read(mapped: mmap.mmap, format_string: str, offset: int) -> int:
    return int(struct.unpack_from(format_string, mapped, offset)[0])


def _validate_mapping(
    mapped: mmap.mmap,
    options: StoreOptions,
    layout: _native.StoreLayout,
) -> None:
    expected_fields = {
        ("<I", 0): 0x32534D53,
        ("<H", 4): LAYOUT_MAJOR_VERSION,
        ("<H", 6): LAYOUT_MINOR_VERSION,
        ("<i", 8): layout.header_length,
        ("<i", 12): RESOURCE_PROTOCOL_VERSION,
        ("<Q", 16): REQUIRED_FEATURES,
        ("<Q", 24): OPTIONAL_FEATURES,
        ("<q", 32): layout.total_bytes,
        ("<i", 64): layout.slot_count,
        ("<i", 68): layout.lease_record_count,
        ("<i", 72): layout.participant_record_count,
        ("<i", 76): options.max_key_bytes,
        ("<i", 80): options.max_descriptor_bytes,
        ("<i", 84): options.max_value_bytes,
        ("<i", 88): layout.participant_index_bits,
        ("<i", 92): layout.participant_generation_bits,
        ("<q", 96): layout.participant_offset,
        ("<q", 104): layout.participant_length,
        ("<i", 112): layout.participant_stride,
        ("<i", 116): layout.primary_lane_count,
        ("<i", 120): layout.primary_bucket_count,
        ("<i", 124): layout.primary_bucket_stride,
        ("<q", 128): layout.primary_directory_offset,
        ("<q", 136): layout.primary_directory_length,
        ("<q", 144): layout.overflow_directory_offset,
        ("<q", 152): layout.overflow_directory_length,
        ("<i", 160): layout.overflow_stride,
        ("<i", 164): layout.lease_stride,
        ("<q", 168): layout.lease_registry_offset,
        ("<q", 176): layout.lease_registry_length,
        ("<i", 184): layout.slot_metadata_stride,
        ("<i", 188): layout.key_stride,
        ("<q", 192): layout.slot_metadata_offset,
        ("<q", 200): layout.slot_metadata_length,
        ("<q", 208): layout.key_storage_offset,
        ("<q", 216): layout.key_storage_length,
        ("<i", 224): layout.descriptor_stride,
        ("<i", 228): layout.payload_stride,
        ("<q", 232): layout.descriptor_storage_offset,
        ("<q", 240): layout.descriptor_storage_length,
        ("<q", 248): layout.payload_storage_offset,
        ("<q", 256): layout.payload_storage_length,
    }
    if options.total_bytes != layout.total_bytes or layout.required_bytes != layout.total_bytes:
        raise RuntimeError("raw SMS2 layout size does not match the opened options")
    for (format_string, offset), expected in expected_fields.items():
        actual = _read(mapped, format_string, offset)
        if actual != expected:
            raise RuntimeError(
                f"raw SMS2 header mismatch at offset {offset}: expected {expected}, actual {actual}"
            )
    if _read(mapped, "<Q", 40) == 0 or (_read(mapped, "<q", 48) & 0x7) != 2:
        raise RuntimeError("raw SMS2 mapping is not a ready nonzero store incarnation")


def _signed64(value: int) -> int:
    return value if value < (1 << 63) else value - (1 << 64)


def _write_u64(mapped: mmap.mmap, offset: int, replacement: int) -> tuple[int, int]:
    if offset < 0 or offset % 8 != 0 or offset > len(mapped) - 8:
        raise RuntimeError(f"raw SMS2 qword offset is invalid: {offset}")
    original = _read(mapped, "<Q", offset)
    replacement &= (1 << 64) - 1
    struct.pack_into("<Q", mapped, offset, replacement)
    mapped.flush()
    return _signed64(original), _signed64(replacement)


def _require_integer(
    arguments: dict[str, Any],
    name: str,
    minimum: int,
    maximum: int,
) -> int:
    value = arguments.get(name)
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise ValueError(f"argument {name!r} must be an integer in [{minimum}, {maximum}]")
    return value


def _empty_fault_result(target: str) -> dict[str, int | str]:
    return {
        "target": target,
        "participantIndex": -1,
        "originalProcessId": 0,
        "replacementProcessId": 0,
        "originalPidNamespaceId": 0,
        "replacementPidNamespaceId": 0,
        "originalRaw": 0,
        "replacementRaw": 0,
    }


def inject_raw_fault(
    store: MemoryStore,
    options: StoreOptions,
    target: str,
    arguments: dict[str, Any],
) -> dict[str, int | str]:
    if not supports_platform_faults():
        raise UnsupportedFaultPlatform("raw fault injection supports Windows and Linux only")
    layout = _store_layout(store)
    with _raw_mapping(options, layout) as mapped:
        _validate_mapping(mapped, options, layout)
        result = _empty_fault_result(target)

        if target == "directoryMutation":
            malformed = (1 << 31) | (layout.slot_count + 1)
            original, replacement = _write_u64(
                mapped,
                layout.primary_directory_offset + 8,
                malformed,
            )
            result.update(originalRaw=original, replacementRaw=replacement)
            return result

        if target == "participantProcessId":
            target_pid = _require_integer(arguments, "targetProcessId", 1, (1 << 31) - 1)
            replacement_pid = _require_integer(
                arguments,
                "replacementProcessId",
                1,
                (1 << 31) - 1,
            )
            for index in range(layout.participant_record_count):
                offset = layout.participant_offset + (index * layout.participant_stride)
                original_unsigned = _read(mapped, "<Q", offset)
                state = original_unsigned & 0x7
                process_id = original_unsigned >> 31
                if process_id == target_pid and state in {1, 2, 3}:
                    incarnation = (original_unsigned >> 3) & 0x0FFF_FFFF
                    replacement_unsigned = state | (incarnation << 3) | (replacement_pid << 31)
                    original, replacement = _write_u64(mapped, offset, replacement_unsigned)
                    namespace_id = _read(mapped, "<Q", offset + 32)
                    result.update(
                        participantIndex=index,
                        originalProcessId=target_pid,
                        replacementProcessId=replacement_pid,
                        originalPidNamespaceId=namespace_id,
                        replacementPidNamespaceId=namespace_id,
                        originalRaw=original,
                        replacementRaw=replacement,
                    )
                    return result
            raise RuntimeError(f"no live participant record owned by PID {target_pid} was found")

        if target == "participantNamespace":
            target_pid = _require_integer(arguments, "targetProcessId", 1, (1 << 31) - 1)
            replacement_namespace = _require_integer(
                arguments,
                "replacementPidNamespaceId",
                0,
                (1 << 64) - 1,
            )
            for index in range(layout.participant_record_count):
                offset = layout.participant_offset + (index * layout.participant_stride)
                control = _read(mapped, "<Q", offset)
                state = control & 0x7
                process_id = control >> 31
                if process_id == target_pid and state in {1, 2, 3}:
                    original_namespace = _read(mapped, "<Q", offset + 32)
                    struct.pack_into("<Q", mapped, offset + 32, replacement_namespace)
                    mapped.flush()
                    result.update(
                        participantIndex=index,
                        originalProcessId=target_pid,
                        replacementProcessId=target_pid,
                        originalPidNamespaceId=original_namespace,
                        replacementPidNamespaceId=replacement_namespace,
                        originalRaw=_signed64(control),
                        replacementRaw=_signed64(control),
                    )
                    return result
            raise RuntimeError(f"no live participant record owned by PID {target_pid} was found")

        if target == "headerNamespace":
            replacement_namespace = _require_integer(
                arguments,
                "replacementPidNamespaceId",
                0,
                (1 << 64) - 1,
            )
            original_namespace = _read(mapped, "<Q", 264)
            struct.pack_into("<Q", mapped, 264, replacement_namespace)
            mapped.flush()
            result.update(
                originalPidNamespaceId=original_namespace,
                replacementPidNamespaceId=replacement_namespace,
            )
            return result

        if target == "layoutMajorVersion":
            replacement_major = _require_integer(
                arguments,
                "replacementLayoutMajorVersion",
                0,
                (1 << 16) - 1,
            )
            original_major = _read(mapped, "<H", 4)
            struct.pack_into("<H", mapped, 4, replacement_major)
            mapped.flush()
            result.update(originalRaw=original_major, replacementRaw=replacement_major)
            return result

        if target == "requiredFeatures":
            replacement_features = _require_integer(
                arguments,
                "replacementRequiredFeatures",
                0,
                (1 << 64) - 1,
            )
            original, replacement = _write_u64(mapped, 16, replacement_features)
            result.update(originalRaw=original, replacementRaw=replacement)
            return result

        raise ValueError(f"unknown raw fault target {target!r}")


class ColdLock:
    """One real test-only hold on the inherited cold synchronization resource."""

    __slots__ = ("_handle", "_kind")

    def __init__(self, kind: str, handle: Any) -> None:
        self._kind = kind
        self._handle = handle

    @classmethod
    def acquire(cls, public_name: str) -> "ColdLock":
        if sys.platform == "linux":
            return cls._acquire_linux(public_name)
        if sys.platform == "win32":
            return cls._acquire_windows(public_name)
        raise UnsupportedFaultPlatform("cold-lock injection supports Windows and Linux only")

    @classmethod
    def _acquire_linux(cls, public_name: str) -> "ColdLock":
        import fcntl

        paths = linux_resource_paths(public_name)
        _validate_directory(paths["directory"])
        flags = (
            os.O_RDWR
            | getattr(os, "O_CLOEXEC", 0)
            | getattr(os, "O_NOFOLLOW", 0)
            | getattr(os, "O_NONBLOCK", 0)
        )
        descriptor = os.open(paths["lock"], flags)
        try:
            information = os.fstat(descriptor)
            if not stat.S_ISREG(information.st_mode):
                raise RuntimeError("the Linux cold synchronization resource is not a regular file")
            fcntl.lockf(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB, 1, 0, os.SEEK_SET)
            return cls("linux", descriptor)
        except BaseException:
            os.close(descriptor)
            raise

    @classmethod
    def _acquire_windows(cls, public_name: str) -> "ColdLock":
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
        kernel32.CreateMutexW.restype = wintypes.HANDLE
        kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel32.WaitForSingleObject.restype = wintypes.DWORD
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.restype = wintypes.BOOL
        handle = kernel32.CreateMutexW(None, False, _windows_synchronization_name(public_name))
        if not handle:
            raise OSError(ctypes.get_last_error(), "CreateMutexW failed")
        wait_result = int(kernel32.WaitForSingleObject(handle, 5000))
        if wait_result not in {0, 0x80}:
            kernel32.CloseHandle(handle)
            if wait_result == 0x102:
                raise TimeoutError("could not acquire the Windows cold mutex")
            raise OSError(ctypes.get_last_error(), "WaitForSingleObject failed")
        return cls("windows", (kernel32, handle))

    def close(self) -> None:
        if self._handle is None:
            return
        handle, self._handle = self._handle, None
        if self._kind == "linux":
            import fcntl

            try:
                fcntl.lockf(handle, fcntl.LOCK_UN, 1, 0, os.SEEK_SET)
            finally:
                os.close(handle)
            return

        kernel32, native_handle = handle
        try:
            if not kernel32.ReleaseMutex(native_handle):
                raise OSError(ctypes.get_last_error(), "ReleaseMutex failed")
        finally:
            kernel32.CloseHandle(native_handle)

    def __enter__(self) -> "ColdLock":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


__all__ = [
    "ColdLock",
    "UnsupportedFaultPlatform",
    "inject_raw_fault",
    "linux_resource_paths",
    "supports_platform_faults",
]
