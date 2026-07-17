"""Private ``ctypes`` declarations and deterministic native-library loader."""

from __future__ import annotations

import atexit
import ctypes
from contextlib import ExitStack
from importlib.resources import as_file, files
from pathlib import Path
import platform
import sys
import threading
from typing import Optional


ABI_VERSION = 0x00020000
LAYOUT_MAJOR_VERSION = 2
LAYOUT_MINOR_VERSION = 0
RESOURCE_PROTOCOL_VERSION = 2
REQUIRED_FEATURES = 7
OPTIONAL_FEATURES = 0
WAIT_INFINITE = -1
STATUS_COUNT = 23

UInt8Pointer = ctypes.POINTER(ctypes.c_uint8)
CancellationHandle = ctypes.c_void_p


class Bytes(ctypes.Structure):
    _fields_ = [("data", UInt8Pointer), ("length", ctypes.c_uint64)]


class MutableBytes(ctypes.Structure):
    _fields_ = [("data", UInt8Pointer), ("length", ctypes.c_uint64)]


class WaitOptions(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("timeout_milliseconds", ctypes.c_int64),
        ("cancellation", CancellationHandle),
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
        ("participant_record_count", ctypes.c_int32),
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
        ("layout_major", ctypes.c_int32),
        ("layout_minor", ctypes.c_int32),
        ("resource_protocol", ctypes.c_int32),
        ("reserved", ctypes.c_int32),
        ("required_features", ctypes.c_uint64),
        ("optional_features", ctypes.c_uint64),
        ("total_bytes", ctypes.c_int64),
        ("slot_count", ctypes.c_int32),
        ("free_slot_count", ctypes.c_int32),
        ("initializing_slot_count", ctypes.c_int32),
        ("reserved_slot_count", ctypes.c_int32),
        ("published_slot_count", ctypes.c_int32),
        ("pending_removal_count", ctypes.c_int32),
        ("reclaiming_slot_count", ctypes.c_int32),
        ("retired_slot_count", ctypes.c_int32),
        ("active_reservation_count", ctypes.c_int32),
        ("active_lease_count", ctypes.c_int32),
        ("claiming_lease_count", ctypes.c_int32),
        ("recovering_lease_count", ctypes.c_int32),
        ("free_lease_count", ctypes.c_int32),
        ("retired_lease_count", ctypes.c_int32),
        ("participant_record_count", ctypes.c_int32),
        ("free_participant_count", ctypes.c_int32),
        ("registering_participant_count", ctypes.c_int32),
        ("active_participant_count", ctypes.c_int32),
        ("closing_participant_count", ctypes.c_int32),
        ("recovering_participant_count", ctypes.c_int32),
        ("reclaiming_participant_count", ctypes.c_int32),
        ("retired_participant_count", ctypes.c_int32),
        ("index_entry_count", ctypes.c_int32),
        ("occupied_index_entry_count", ctypes.c_int32),
        ("empty_index_entry_count", ctypes.c_int32),
        ("usable_index_capacity", ctypes.c_int32),
        ("primary_directory_occupancy", ctypes.c_int32),
        ("spilled_bucket_count", ctypes.c_int32),
        ("overflow_directory_occupancy", ctypes.c_int32),
        ("last_observed_probe_length", ctypes.c_int32),
        ("max_observed_probe_length", ctypes.c_int32),
        ("max_observed_overflow_scan_length", ctypes.c_int32),
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
        ("overflow_scan_count", ctypes.c_int64),
        ("cas_retry_count", ctypes.c_int64),
        ("helped_transition_count", ctypes.c_int64),
        ("contention_budget_exhaustion_count", ctypes.c_int64),
        ("invalid_token_count", ctypes.c_int64),
        ("stale_token_count", ctypes.c_int64),
        ("recovery_attempt_count", ctypes.c_int64),
        ("recovered_transition_count", ctypes.c_int64),
        ("current_owner_classification_count", ctypes.c_int64),
        ("live_owner_classification_count", ctypes.c_int64),
        ("stale_owner_classification_count", ctypes.c_int64),
        ("unsupported_owner_classification_count", ctypes.c_int64),
        ("inconsistent_owner_classification_count", ctypes.c_int64),
        ("changing_owner_classification_count", ctypes.c_int64),
        ("failure_counts", ctypes.c_int64 * STATUS_COUNT),
    ]


class ProtocolInfo(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("layout_major", ctypes.c_int32),
        ("layout_minor", ctypes.c_int32),
        ("resource_protocol", ctypes.c_int32),
        ("reserved", ctypes.c_int32),
        ("required_features", ctypes.c_uint64),
        ("optional_features", ctypes.c_uint64),
        ("store_header_size", ctypes.c_int32),
        ("participant_record_size", ctypes.c_int32),
        ("primary_directory_bucket_size", ctypes.c_int32),
        ("overflow_binding_size", ctypes.c_int32),
        ("lease_record_size", ctypes.c_int32),
        ("value_slot_size", ctypes.c_int32),
    ]


class StoreLayout(ctypes.Structure):
    _fields_ = [
        ("struct_size", ctypes.c_uint32),
        ("abi_version", ctypes.c_uint32),
        ("total_bytes", ctypes.c_int64),
        ("slot_count", ctypes.c_int32),
        ("lease_record_count", ctypes.c_int32),
        ("participant_record_count", ctypes.c_int32),
        ("max_value_bytes", ctypes.c_int32),
        ("max_descriptor_bytes", ctypes.c_int32),
        ("max_key_bytes", ctypes.c_int32),
        ("header_length", ctypes.c_int32),
        ("participant_index_bits", ctypes.c_int32),
        ("participant_generation_bits", ctypes.c_int32),
        ("participant_stride", ctypes.c_int32),
        ("participant_offset", ctypes.c_int64),
        ("participant_length", ctypes.c_int64),
        ("primary_lane_count", ctypes.c_int32),
        ("primary_bucket_count", ctypes.c_int32),
        ("primary_bucket_stride", ctypes.c_int32),
        ("primary_directory_offset", ctypes.c_int64),
        ("primary_directory_length", ctypes.c_int64),
        ("overflow_stride", ctypes.c_int32),
        ("overflow_directory_offset", ctypes.c_int64),
        ("overflow_directory_length", ctypes.c_int64),
        ("lease_stride", ctypes.c_int32),
        ("lease_registry_offset", ctypes.c_int64),
        ("lease_registry_length", ctypes.c_int64),
        ("slot_metadata_stride", ctypes.c_int32),
        ("key_stride", ctypes.c_int32),
        ("slot_metadata_offset", ctypes.c_int64),
        ("slot_metadata_length", ctypes.c_int64),
        ("key_storage_offset", ctypes.c_int64),
        ("key_storage_length", ctypes.c_int64),
        ("descriptor_stride", ctypes.c_int32),
        ("payload_stride", ctypes.c_int32),
        ("descriptor_storage_offset", ctypes.c_int64),
        ("descriptor_storage_length", ctypes.c_int64),
        ("payload_storage_offset", ctypes.c_int64),
        ("payload_storage_length", ctypes.c_int64),
        ("required_bytes", ctypes.c_int64),
    ]


# The native ABI exposes offsets by stable numeric query id.  Keeping the full
# catalog here makes the loader reject a binary whose mapped-record contract is
# even subtly different from the one understood by this package.
LAYOUT_FIELD_QUERIES = {
    "header.magic": (0, 0),
    "header.layout_major_version": (1, 4),
    "header.layout_minor_version": (2, 6),
    "header.header_length": (3, 8),
    "header.resource_protocol_version": (4, 12),
    "header.required_features": (5, 16),
    "header.optional_features": (6, 24),
    "header.total_bytes": (7, 32),
    "header.store_id": (8, 40),
    "header.control": (9, 48),
    "header.sequence": (10, 56),
    "header.slot_count": (11, 64),
    "header.lease_record_count": (12, 68),
    "header.participant_record_count": (13, 72),
    "header.max_key_bytes": (14, 76),
    "header.max_descriptor_bytes": (15, 80),
    "header.max_value_bytes": (16, 84),
    "header.participant_index_bits": (17, 88),
    "header.participant_generation_bits": (18, 92),
    "header.participant_offset": (19, 96),
    "header.participant_length": (20, 104),
    "header.participant_stride": (21, 112),
    "header.primary_lane_count": (22, 116),
    "header.primary_bucket_count": (23, 120),
    "header.primary_bucket_stride": (24, 124),
    "header.primary_directory_offset": (25, 128),
    "header.primary_directory_length": (26, 136),
    "header.overflow_directory_offset": (27, 144),
    "header.overflow_directory_length": (28, 152),
    "header.overflow_stride": (29, 160),
    "header.lease_stride": (30, 164),
    "header.lease_registry_offset": (31, 168),
    "header.lease_registry_length": (32, 176),
    "header.slot_metadata_stride": (33, 184),
    "header.key_stride": (34, 188),
    "header.slot_metadata_offset": (35, 192),
    "header.slot_metadata_length": (36, 200),
    "header.key_storage_offset": (37, 208),
    "header.key_storage_length": (38, 216),
    "header.descriptor_stride": (39, 224),
    "header.payload_stride": (40, 228),
    "header.descriptor_storage_offset": (41, 232),
    "header.descriptor_storage_length": (42, 240),
    "header.payload_storage_offset": (43, 248),
    "header.payload_storage_length": (44, 256),
    "header.pid_namespace_id": (45, 264),
    "header.pid_namespace_mode": (46, 272),
    "participant.control": (100, 0),
    "participant.identity_kind": (101, 8),
    "participant.reserved": (102, 12),
    "participant.process_start_value": (103, 16),
    "participant.open_sequence": (104, 24),
    "participant.pid_namespace_id": (105, 32),
    "primary_directory_bucket.spill_summary": (200, 0),
    "primary_directory_bucket.mutation": (201, 8),
    "primary_directory_bucket.lanes": (202, 16),
    "overflow_binding.binding": (300, 0),
    "lease.control": (400, 0),
    "lease.slot_binding": (401, 8),
    "lease.acquire_sequence": (402, 16),
    "value_slot.control": (500, 0),
    "value_slot.directory_binding": (501, 8),
    "value_slot.directory_location": (502, 16),
    "value_slot.directory_operation": (503, 24),
    "value_slot.key_hash": (504, 32),
    "value_slot.key_length": (505, 40),
    "value_slot.descriptor_length": (506, 44),
    "value_slot.value_length": (507, 48),
    "value_slot.publication_intent": (508, 52),
    "value_slot.bytes_advanced": (509, 56),
    "value_slot.commit_sequence": (510, 64),
    "value_slot.key_offset": (511, 72),
    "value_slot.descriptor_offset": (512, 80),
    "value_slot.payload_offset": (513, 88),
}


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


def _is_supported_architecture(machine: str, byte_order: str, pointer_size: int) -> bool:
    """Return whether this process can execute the qualified SMS2 atomics."""

    normalized = machine.strip().lower().replace("-", "_")
    return normalized in {"amd64", "x86_64"} and byte_order == "little" and pointer_size == 8


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
    lib.sms_create_cancellation.argtypes = [ctypes.POINTER(CancellationHandle)]
    lib.sms_create_cancellation.restype = ctypes.c_int32
    lib.sms_signal_cancellation.argtypes = [CancellationHandle]
    lib.sms_signal_cancellation.restype = ctypes.c_int32
    lib.sms_cancellation_is_signaled.argtypes = [CancellationHandle]
    lib.sms_cancellation_is_signaled.restype = ctypes.c_int32
    lib.sms_destroy_cancellation.argtypes = [CancellationHandle]
    lib.sms_destroy_cancellation.restype = None
    lib.sms_calculate_required_bytes.argtypes = [
        ctypes.c_int32,
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
    lib.sms_destroy_store.argtypes = [store_handle]
    lib.sms_destroy_store.restype = None
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
    if not _is_supported_architecture(platform.machine(), sys.byteorder, ctypes.sizeof(ctypes.c_void_p)):
        raise ImportError(
            "SharedMemoryStore SMS2 requires a little-endian x86-64 process; "
            f"current machine={platform.machine()!r}, byte_order={sys.byteorder!r}, "
            f"pointer_size={ctypes.sizeof(ctypes.c_void_p)}."
        )

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
        RESOURCE_PROTOCOL_VERSION,
        REQUIRED_FEATURES,
        OPTIONAL_FEATURES,
        512,
        64,
        128,
        8,
        64,
        128,
    )
    actual = (
        info.layout_major,
        info.layout_minor,
        info.resource_protocol,
        info.required_features,
        info.optional_features,
        info.store_header_size,
        info.participant_record_size,
        info.primary_directory_bucket_size,
        info.overflow_binding_size,
        info.lease_record_size,
        info.value_slot_size,
    )
    if status != 0 or actual != expected:
        raise ImportError(
            f"Native SharedMemoryStore protocol metadata at {path} is incompatible: "
            f"status={status}, expected={expected}, actual={actual}."
        )

    for field_name, (field, expected_offset) in LAYOUT_FIELD_QUERIES.items():
        actual_offset = ctypes.c_uint32()
        offset_status = int(lib.sms_get_layout_field_offset(field, ctypes.byref(actual_offset)))
        if offset_status != 0 or actual_offset.value != expected_offset:
            raise ImportError(
                f"Native SharedMemoryStore layout field {field_name!r} ({field}) at {path} "
                "is incompatible: "
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
    "RESOURCE_PROTOCOL_VERSION",
    "REQUIRED_FEATURES",
    "OPTIONAL_FEATURES",
    "WAIT_INFINITE",
    "STATUS_COUNT",
    "CancellationHandle",
    "Bytes",
    "MutableBytes",
    "WaitOptions",
    "StoreOptions",
    "Segment",
    "RecoveryReport",
    "Diagnostics",
    "ProtocolInfo",
    "StoreLayout",
    "LAYOUT_FIELD_QUERIES",
    "library",
    "native_library_path",
]
