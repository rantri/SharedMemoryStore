"""Private ``ctypes`` declarations and deterministic native-library loader."""

from __future__ import annotations

import atexit
import ctypes
from contextlib import ExitStack
from importlib.resources import as_file, files
from pathlib import Path
import sys
import threading
from typing import Optional


ABI_VERSION = 0x00010000
LAYOUT_MAJOR_VERSION = 1
LAYOUT_MINOR_VERSION = 2
RESOURCE_NAMING_VERSION = 1
WAIT_INFINITE = -1
STATUS_COUNT = 23

UInt8Pointer = ctypes.POINTER(ctypes.c_uint8)


class Bytes(ctypes.Structure):
    _fields_ = [("data", UInt8Pointer), ("length", ctypes.c_uint64)]


class MutableBytes(ctypes.Structure):
    _fields_ = [("data", UInt8Pointer), ("length", ctypes.c_uint64)]


class WaitOptions(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("timeout_milliseconds", ctypes.c_int64),
    ]


class StoreOptions(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("name_utf8", ctypes.c_char_p),
        ("name_length", ctypes.c_uint64),
        ("open_mode", ctypes.c_int32),
        ("total_bytes", ctypes.c_int64),
        ("slot_count", ctypes.c_int32),
        ("max_value_bytes", ctypes.c_int32),
        ("max_descriptor_bytes", ctypes.c_int32),
        ("max_key_bytes", ctypes.c_int32),
        ("lease_record_count", ctypes.c_int32),
        ("enable_lease_recovery", ctypes.c_uint8),
        ("reserved", ctypes.c_uint8 * 7),
    ]


class Segment(ctypes.Structure):
    _fields_ = [("data", UInt8Pointer), ("length", ctypes.c_uint64)]


class RecoveryReport(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("scanned_count", ctypes.c_int32),
        ("recovered_count", ctypes.c_int32),
        ("active_count", ctypes.c_int32),
        ("unsupported_count", ctypes.c_int32),
        ("failed_count", ctypes.c_int32),
        ("reserved", ctypes.c_int32),
    ]


class Diagnostics(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("total_bytes", ctypes.c_int64),
        ("slot_count", ctypes.c_int32),
        ("free_slot_count", ctypes.c_int32),
        ("published_slot_count", ctypes.c_int32),
        ("pending_removal_count", ctypes.c_int32),
        ("active_lease_count", ctypes.c_int32),
        ("active_reservation_count", ctypes.c_int32),
        ("index_entry_count", ctypes.c_int32),
        ("occupied_index_entry_count", ctypes.c_int32),
        ("tombstone_index_entry_count", ctypes.c_int32),
        ("empty_index_entry_count", ctypes.c_int32),
        ("usable_index_capacity", ctypes.c_int32),
        ("last_observed_probe_length", ctypes.c_int32),
        ("max_observed_probe_length", ctypes.c_int32),
        ("last_failure_status", ctypes.c_int32),
        ("aborted_reservation_count", ctypes.c_int64),
        ("recovered_lease_count", ctypes.c_int64),
        ("active_lease_recovery_count", ctypes.c_int64),
        ("unsupported_lease_recovery_count", ctypes.c_int64),
        ("failed_lease_recovery_count", ctypes.c_int64),
        ("recovered_reservation_count", ctypes.c_int64),
        ("active_reservation_recovery_count", ctypes.c_int64),
        ("unsupported_reservation_recovery_count", ctypes.c_int64),
        ("failed_reservation_recovery_count", ctypes.c_int64),
        ("capacity_pressure_count", ctypes.c_int64),
        ("index_compaction_count", ctypes.c_int64),
        ("failure_counts", ctypes.c_int64 * STATUS_COUNT),
    ]


class ProtocolInfo(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("layout_major", ctypes.c_int32),
        ("layout_minor", ctypes.c_int32),
        ("resource_naming_version", ctypes.c_int32),
        ("store_header_size", ctypes.c_int32),
        ("index_entry_header_size", ctypes.c_int32),
        ("slot_metadata_size", ctypes.c_int32),
        ("lease_record_size", ctypes.c_int32),
    ]


class StoreLayout(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("total_bytes", ctypes.c_int64),
        ("slot_count", ctypes.c_int32),
        ("lease_record_count", ctypes.c_int32),
        ("max_value_bytes", ctypes.c_int32),
        ("max_descriptor_bytes", ctypes.c_int32),
        ("max_key_bytes", ctypes.c_int32),
        ("header_length", ctypes.c_int32),
        ("index_entry_count", ctypes.c_int32),
        ("index_entry_size", ctypes.c_int32),
        ("index_offset", ctypes.c_int64),
        ("index_length", ctypes.c_int64),
        ("lease_registry_offset", ctypes.c_int64),
        ("lease_registry_length", ctypes.c_int64),
        ("slot_metadata_offset", ctypes.c_int64),
        ("slot_metadata_length", ctypes.c_int64),
        ("descriptor_stride", ctypes.c_int32),
        ("payload_stride", ctypes.c_int32),
        ("descriptor_storage_offset", ctypes.c_int64),
        ("descriptor_storage_length", ctypes.c_int64),
        ("payload_storage_offset", ctypes.c_int64),
        ("payload_storage_length", ctypes.c_int64),
        ("required_bytes", ctypes.c_int64),
    ]


_LIBRARY_LOCK = threading.Lock()
_LIBRARY: Optional[ctypes.CDLL] = None
_LIBRARY_PATH: Optional[Path] = None
_RESOURCE_CONTEXTS = ExitStack()
atexit.register(_RESOURCE_CONTEXTS.close)


def _library_filename() -> str:
    if sys.platform == "win32":
        return "shared_memory_store.dll"
    if sys.platform.startswith("linux"):
        return "libshared_memory_store.so"
    raise OSError(
        "SharedMemoryStore supports native loading only on Windows and Linux; "
        f"the current platform is {sys.platform!r}."
    )


def _bundled_library_path(filename: str) -> Path:
    resource = files("shared_memory_store").joinpath(filename)
    if not resource.is_file():
        raise OSError(
            f"The packaged native library {filename!r} is missing from the "
            "shared_memory_store package. Build or install a platform wheel; the "
            "loader never searches the current directory or system library path."
        )
    return Path(_RESOURCE_CONTEXTS.enter_context(as_file(resource)))


def _configure_signatures(lib: ctypes.CDLL) -> None:
    store_handle = ctypes.c_void_p
    lease_handle = ctypes.c_void_p
    reservation_handle = ctypes.c_void_p

    lib.sms_abi_version.argtypes = []
    lib.sms_abi_version.restype = ctypes.c_uint32
    lib.sms_get_protocol_info.argtypes = [ctypes.POINTER(ProtocolInfo)]
    lib.sms_get_protocol_info.restype = ctypes.c_int32
    lib.sms_get_layout_field_offset.argtypes = [ctypes.c_int32, ctypes.POINTER(ctypes.c_uint32)]
    lib.sms_get_layout_field_offset.restype = ctypes.c_int32
    lib.sms_calculate_required_bytes.argtypes = [
        ctypes.c_int32,
        ctypes.c_int32,
        ctypes.c_int32,
        ctypes.c_int32,
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_int64),
    ]
    lib.sms_calculate_required_bytes.restype = ctypes.c_int32
    lib.sms_open_store.argtypes = [
        ctypes.POINTER(StoreOptions),
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(store_handle),
    ]
    lib.sms_open_store.restype = ctypes.c_int32
    lib.sms_close_store.argtypes = [store_handle]
    lib.sms_close_store.restype = None
    lib.sms_get_store_layout.argtypes = [
        store_handle,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(StoreLayout),
    ]
    lib.sms_get_store_layout.restype = ctypes.c_int32
    lib.sms_publish.argtypes = [store_handle, Bytes, Bytes, Bytes, ctypes.POINTER(WaitOptions)]
    lib.sms_publish.restype = ctypes.c_int32
    lib.sms_publish_segments.argtypes = [
        store_handle,
        Bytes,
        ctypes.POINTER(Segment),
        ctypes.c_uint64,
        Bytes,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(ctypes.c_int64),
    ]
    lib.sms_publish_segments.restype = ctypes.c_int32
    lib.sms_acquire.argtypes = [
        store_handle,
        Bytes,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(lease_handle),
    ]
    lib.sms_acquire.restype = ctypes.c_int32
    lib.sms_lease_is_valid.argtypes = [lease_handle]
    lib.sms_lease_is_valid.restype = ctypes.c_int32
    lib.sms_lease_value.argtypes = [lease_handle]
    lib.sms_lease_value.restype = Bytes
    lib.sms_lease_descriptor.argtypes = [lease_handle]
    lib.sms_lease_descriptor.restype = Bytes
    lib.sms_release_lease.argtypes = [lease_handle, ctypes.POINTER(WaitOptions)]
    lib.sms_release_lease.restype = ctypes.c_int32
    lib.sms_destroy_lease.argtypes = [lease_handle]
    lib.sms_destroy_lease.restype = None
    lib.sms_remove.argtypes = [store_handle, Bytes, ctypes.POINTER(WaitOptions)]
    lib.sms_remove.restype = ctypes.c_int32
    lib.sms_reserve.argtypes = [
        store_handle,
        Bytes,
        ctypes.c_int32,
        Bytes,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(reservation_handle),
    ]
    lib.sms_reserve.restype = ctypes.c_int32
    lib.sms_reservation_is_valid.argtypes = [reservation_handle]
    lib.sms_reservation_is_valid.restype = ctypes.c_int32
    lib.sms_reservation_payload_length.argtypes = [reservation_handle]
    lib.sms_reservation_payload_length.restype = ctypes.c_int32
    lib.sms_reservation_bytes_written.argtypes = [reservation_handle]
    lib.sms_reservation_bytes_written.restype = ctypes.c_int32
    lib.sms_reservation_remaining_bytes.argtypes = [reservation_handle]
    lib.sms_reservation_remaining_bytes.restype = ctypes.c_int32
    lib.sms_reservation_buffer.argtypes = [reservation_handle, ctypes.c_int32]
    lib.sms_reservation_buffer.restype = MutableBytes
    lib.sms_advance_reservation.argtypes = [
        reservation_handle,
        ctypes.c_int32,
        ctypes.POINTER(WaitOptions),
    ]
    lib.sms_advance_reservation.restype = ctypes.c_int32
    lib.sms_commit_reservation.argtypes = [reservation_handle, ctypes.POINTER(WaitOptions)]
    lib.sms_commit_reservation.restype = ctypes.c_int32
    lib.sms_abort_reservation.argtypes = [reservation_handle, ctypes.POINTER(WaitOptions)]
    lib.sms_abort_reservation.restype = ctypes.c_int32
    lib.sms_destroy_reservation.argtypes = [reservation_handle]
    lib.sms_destroy_reservation.restype = None
    lib.sms_recover_leases.argtypes = [
        store_handle,
        ctypes.c_int32,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(RecoveryReport),
    ]
    lib.sms_recover_leases.restype = ctypes.c_int32
    lib.sms_recover_reservations.argtypes = [
        store_handle,
        ctypes.c_int32,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(RecoveryReport),
    ]
    lib.sms_recover_reservations.restype = ctypes.c_int32
    lib.sms_get_diagnostics.argtypes = [
        store_handle,
        ctypes.POINTER(WaitOptions),
        ctypes.POINTER(Diagnostics),
    ]
    lib.sms_get_diagnostics.restype = ctypes.c_int32


def _verify_contract(lib: ctypes.CDLL, path: Path) -> None:
    actual_abi = int(lib.sms_abi_version())
    if actual_abi >> 16 != ABI_VERSION >> 16 or actual_abi < ABI_VERSION:
        raise ImportError(
            f"Native SharedMemoryStore ABI 0x{actual_abi:08x} at {path} is not "
            f"compatible with required ABI 0x{ABI_VERSION:08x}."
        )

    info = ProtocolInfo()
    info.struct_size = ctypes.sizeof(ProtocolInfo)
    info.abi_version = ABI_VERSION
    status = int(lib.sms_get_protocol_info(ctypes.byref(info)))
    expected = (
        LAYOUT_MAJOR_VERSION,
        LAYOUT_MINOR_VERSION,
        RESOURCE_NAMING_VERSION,
        160,
        32,
        72,
        40,
    )
    actual = (
        info.layout_major,
        info.layout_minor,
        info.resource_naming_version,
        info.store_header_size,
        info.index_entry_header_size,
        info.slot_metadata_size,
        info.lease_record_size,
    )
    if status != 0 or actual != expected:
        raise ImportError(
            f"Native SharedMemoryStore protocol metadata at {path} is incompatible: "
            f"status={status}, expected={expected}, actual={actual}."
        )

    expected_offsets = {
        0: 0,
        1: 56,
        2: 144,
        3: 152,
        100: 0,
        101: 8,
        102: 24,
        200: 0,
        201: 8,
        202: 16,
        203: 40,
        204: 64,
        300: 0,
        301: 16,
        302: 24,
        303: 32,
    }
    for field, expected_offset in expected_offsets.items():
        actual_offset = ctypes.c_uint32()
        offset_status = int(lib.sms_get_layout_field_offset(field, ctypes.byref(actual_offset)))
        if offset_status != 0 or actual_offset.value != expected_offset:
            raise ImportError(
                f"Native SharedMemoryStore layout field {field} at {path} is incompatible: "
                f"status={offset_status}, expected offset={expected_offset}, "
                f"actual offset={actual_offset.value}."
            )


def library() -> ctypes.CDLL:
    """Return the validated process-wide native ABI library."""

    global _LIBRARY, _LIBRARY_PATH
    if _LIBRARY is not None:
        return _LIBRARY
    with _LIBRARY_LOCK:
        if _LIBRARY is not None:
            return _LIBRARY
        filename = _library_filename()
        path = _bundled_library_path(filename)
        try:
            candidate = ctypes.CDLL(str(path.resolve(strict=True)))
            _configure_signatures(candidate)
            _verify_contract(candidate, path)
        except (AttributeError, OSError) as error:
            raise OSError(
                f"Unable to load the SharedMemoryStore native library from {path}. "
                f"Expected platform artifact {filename!r}."
            ) from error
        _LIBRARY = candidate
        _LIBRARY_PATH = path
        return candidate


def native_library_path() -> Path:
    """Return the exact loaded artifact path, primarily for package diagnostics."""

    library()
    assert _LIBRARY_PATH is not None
    return _LIBRARY_PATH


__all__ = [
    "ABI_VERSION",
    "LAYOUT_MAJOR_VERSION",
    "LAYOUT_MINOR_VERSION",
    "RESOURCE_NAMING_VERSION",
    "WAIT_INFINITE",
    "STATUS_COUNT",
    "Bytes",
    "MutableBytes",
    "WaitOptions",
    "StoreOptions",
    "Segment",
    "RecoveryReport",
    "Diagnostics",
    "ProtocolInfo",
    "StoreLayout",
    "library",
    "native_library_path",
]
