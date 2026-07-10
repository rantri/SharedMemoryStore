"""Pythonic ownership wrappers over the versioned SharedMemoryStore C ABI."""

from __future__ import annotations

import ctypes
from dataclasses import dataclass
import sys
import threading
from typing import Any, ClassVar, Iterable, Optional
import weakref

from . import _native
from .enums import OpenMode, StoreOpenStatus, StoreStatus


_INT32_MIN = -(1 << 31)
_INT32_MAX = (1 << 31) - 1
_INT64_MAX = (1 << 63) - 1


def _require_integer(value: object, name: str, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer")
    if value < minimum or value > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return value


def _fits_integer(value: object, minimum: int, maximum: int) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and minimum <= value <= maximum


def _store_status(value: int) -> StoreStatus:
    try:
        return StoreStatus(value)
    except ValueError:
        return StoreStatus.UNKNOWN_FAILURE


def _open_status(value: int) -> StoreOpenStatus:
    try:
        return StoreOpenStatus(value)
    except ValueError:
        return StoreOpenStatus.MAPPING_FAILED


@dataclass(frozen=True, slots=True)
class WaitOptions:
    """Caller-controlled shared-lock wait policy in milliseconds."""

    timeout_milliseconds: int = 1000

    DEFAULT: ClassVar["WaitOptions"]
    NO_WAIT: ClassVar["WaitOptions"]
    INFINITE: ClassVar["WaitOptions"]

    def __post_init__(self) -> None:
        _require_integer(
            self.timeout_milliseconds,
            "timeout_milliseconds",
            _native.WAIT_INFINITE,
            _INT64_MAX,
        )

    @classmethod
    def default(cls) -> "WaitOptions":
        return cls.DEFAULT

    @classmethod
    def defaults(cls) -> "WaitOptions":
        return cls.DEFAULT

    @classmethod
    def no_wait(cls) -> "WaitOptions":
        return cls.NO_WAIT

    @classmethod
    def infinite(cls) -> "WaitOptions":
        return cls.INFINITE


WaitOptions.DEFAULT = WaitOptions(1000)
WaitOptions.NO_WAIT = WaitOptions(0)
WaitOptions.INFINITE = WaitOptions(_native.WAIT_INFINITE)


def _native_wait(value: WaitOptions) -> _native.WaitOptions:
    if not isinstance(value, WaitOptions):
        raise TypeError("wait must be a WaitOptions instance")
    result = _native.WaitOptions()
    result.struct_size = ctypes.sizeof(_native.WaitOptions)
    result.abi_version = _native.ABI_VERSION
    result.timeout_milliseconds = value.timeout_milliseconds
    return result


def calculate_required_bytes(
    *,
    slot_count: int,
    max_value_bytes: int,
    max_descriptor_bytes: int,
    max_key_bytes: int,
    lease_record_count: int,
) -> int:
    """Calculate the exact mapped capacity for the supplied protocol limits."""

    arguments = {
        "slot_count": slot_count,
        "max_value_bytes": max_value_bytes,
        "max_descriptor_bytes": max_descriptor_bytes,
        "max_key_bytes": max_key_bytes,
        "lease_record_count": lease_record_count,
    }
    for name, value in arguments.items():
        _require_integer(value, name, _INT32_MIN, _INT32_MAX)

    required = ctypes.c_int64()
    status = _open_status(
        int(
            _native.library().sms_calculate_required_bytes(
                slot_count,
                max_value_bytes,
                max_descriptor_bytes,
                max_key_bytes,
                lease_record_count,
                ctypes.byref(required),
            )
        )
    )
    if status is not StoreOpenStatus.SUCCESS:
        raise ValueError(f"invalid SharedMemoryStore capacities ({status.name})")
    return int(required.value)


@dataclass(frozen=True, slots=True, kw_only=True)
class StoreOptions:
    """Immutable capacities and open behavior for one named store handle."""

    name: str
    total_bytes: int
    slot_count: int
    max_value_bytes: int
    max_descriptor_bytes: int
    max_key_bytes: int
    lease_record_count: int
    open_mode: OpenMode = OpenMode.CREATE_OR_OPEN
    enable_lease_recovery: bool = False

    @classmethod
    def create(
        cls,
        name: str,
        *,
        slot_count: int,
        max_value_bytes: int,
        max_descriptor_bytes: int,
        max_key_bytes: int,
        lease_record_count: int,
        open_mode: OpenMode = OpenMode.CREATE_OR_OPEN,
        enable_lease_recovery: bool = False,
    ) -> "StoreOptions":
        total_bytes = calculate_required_bytes(
            slot_count=slot_count,
            max_value_bytes=max_value_bytes,
            max_descriptor_bytes=max_descriptor_bytes,
            max_key_bytes=max_key_bytes,
            lease_record_count=lease_record_count,
        )
        return cls(
            name=name,
            open_mode=open_mode,
            total_bytes=total_bytes,
            slot_count=slot_count,
            max_value_bytes=max_value_bytes,
            max_descriptor_bytes=max_descriptor_bytes,
            max_key_bytes=max_key_bytes,
            lease_record_count=lease_record_count,
            enable_lease_recovery=enable_lease_recovery,
        )


@dataclass(frozen=True, slots=True)
class RecoveryReport:
    scanned_count: int
    recovered_count: int
    active_count: int
    unsupported_count: int
    failed_count: int


@dataclass(frozen=True, slots=True)
class DiagnosticsSnapshot:
    total_bytes: int
    slot_count: int
    free_slot_count: int
    published_slot_count: int
    pending_removal_count: int
    active_lease_count: int
    active_reservation_count: int
    index_entry_count: int
    occupied_index_entry_count: int
    tombstone_index_entry_count: int
    empty_index_entry_count: int
    usable_index_capacity: int
    last_observed_probe_length: int
    max_observed_probe_length: int
    last_failure_status: StoreStatus
    aborted_reservation_count: int
    recovered_lease_count: int
    active_lease_recovery_count: int
    unsupported_lease_recovery_count: int
    failed_lease_recovery_count: int
    recovered_reservation_count: int
    active_reservation_recovery_count: int
    unsupported_reservation_recovery_count: int
    failed_reservation_recovery_count: int
    capacity_pressure_count: int
    index_compaction_count: int
    failure_counts: tuple[int, ...]

    def failure_count(self, status: StoreStatus) -> int:
        try:
            index = int(StoreStatus(status))
        except (TypeError, ValueError):
            return 0
        return self.failure_counts[index] if 0 <= index < len(self.failure_counts) else 0

    get_failure_count = failure_count


def _coerce_bytes(value: Any, name: str) -> bytes:
    try:
        view = memoryview(value)
    except TypeError as error:
        raise TypeError(f"{name} must support the buffer protocol") from error
    try:
        return view.tobytes()
    finally:
        view.release()


class _InputBuffer:
    __slots__ = ("_array", "native")

    def __init__(self, value: Any, name: str) -> None:
        data = _coerce_bytes(value, name)
        if data:
            array = (ctypes.c_uint8 * len(data)).from_buffer_copy(data)
            pointer = ctypes.cast(array, _native.UInt8Pointer)
            self._array: Optional[ctypes.Array[Any]] = array
        else:
            pointer = _native.UInt8Pointer()
            self._array = None
        self.native = _native.Bytes(pointer, len(data))

    def segment(self) -> _native.Segment:
        return _native.Segment(self.native.data, self.native.length)


def _borrowed_view(
    pointer: _native.UInt8Pointer,
    length: int,
    owner: Any,
    *,
    readonly: bool,
) -> memoryview:
    if length < 0 or length > sys.maxsize:
        raise RuntimeError(f"native library returned an invalid byte length: {length}")
    array_type = ctypes.c_uint8 * length
    if length:
        address = ctypes.cast(pointer, ctypes.c_void_p).value
        if not address:
            raise RuntimeError("native library returned a null pointer for a non-empty byte view")
        array = array_type.from_address(address)
    else:
        array = array_type()
    # A memoryview retains its ctypes exporter; the exporter retains the token;
    # the token retains the store. This makes the native lifetime explicit.
    array._shared_memory_store_owner = owner  # type: ignore[attr-defined]
    view = memoryview(array).cast("B")
    if readonly:
        result = view.toreadonly()
        view.release()
        return result
    return view


class _ViewOwner:
    __slots__ = ("_view_refs",)

    def _initialize_views(self) -> None:
        self._view_refs: list[weakref.ReferenceType[memoryview]] = []

    def _track_view(self, view: memoryview) -> memoryview:
        self._view_refs.append(weakref.ref(view))
        return view

    def _release_views(self) -> None:
        references, self._view_refs = self._view_refs, []
        for reference in references:
            view = reference()
            if view is not None:
                try:
                    view.release()
                except (BufferError, ValueError):
                    pass


class MemoryStore:
    """Context-managed owner of one process-local native store handle."""

    __slots__ = ("_handle", "_lib", "_lock", "_children", "__weakref__")

    def __init__(self, handle: Optional[ctypes.c_void_p] = None, lib: Any = None) -> None:
        if (handle is None) != (lib is None):
            raise ValueError("handle and lib must either both be supplied or both be omitted")
        self._handle = handle
        self._lib = lib
        self._lock = threading.RLock()
        self._children: weakref.WeakSet[Any] = weakref.WeakSet()

    @classmethod
    def open(
        cls,
        options: StoreOptions,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreOpenStatus, Optional["MemoryStore"]]:
        if not isinstance(options, StoreOptions):
            raise TypeError("options must be a StoreOptions instance")
        native_wait = _native_wait(wait)
        if not isinstance(options.name, str):
            return StoreOpenStatus.INVALID_OPTIONS, None
        try:
            name_utf8 = options.name.encode("utf-8", errors="strict")
        except UnicodeEncodeError:
            return StoreOpenStatus.INVALID_OPTIONS, None
        integer_fields = (
            (options.total_bytes, _INT64_MAX),
            (options.slot_count, _INT32_MAX),
            (options.max_value_bytes, _INT32_MAX),
            (options.max_descriptor_bytes, _INT32_MAX),
            (options.max_key_bytes, _INT32_MAX),
            (options.lease_record_count, _INT32_MAX),
        )
        if not all(_fits_integer(value, _INT32_MIN if maximum == _INT32_MAX else -(1 << 63), maximum)
                   for value, maximum in integer_fields):
            return StoreOpenStatus.INVALID_OPTIONS, None
        if not (type(options.open_mode) is int or isinstance(options.open_mode, OpenMode)) or not _fits_integer(
            options.open_mode, _INT32_MIN, _INT32_MAX
        ):
            return StoreOpenStatus.INVALID_OPTIONS, None
        open_mode = int(options.open_mode)
        if not isinstance(options.enable_lease_recovery, bool):
            return StoreOpenStatus.INVALID_OPTIONS, None

        native = _native.StoreOptions()
        native.struct_size = ctypes.sizeof(_native.StoreOptions)
        native.abi_version = _native.ABI_VERSION
        native.name_utf8 = name_utf8
        native.name_length = len(name_utf8)
        native.open_mode = open_mode
        native.total_bytes = options.total_bytes
        native.slot_count = options.slot_count
        native.max_value_bytes = options.max_value_bytes
        native.max_descriptor_bytes = options.max_descriptor_bytes
        native.max_key_bytes = options.max_key_bytes
        native.lease_record_count = options.lease_record_count
        native.enable_lease_recovery = 1 if options.enable_lease_recovery else 0

        lib = _native.library()
        handle = ctypes.c_void_p()
        status = _open_status(int(lib.sms_open_store(ctypes.byref(native), ctypes.byref(native_wait), ctypes.byref(handle))))
        if status is not StoreOpenStatus.SUCCESS or not handle.value:
            return status, None
        return status, cls(handle, lib)

    @property
    def is_open(self) -> bool:
        with self._lock:
            return self._handle is not None

    @property
    def is_valid(self) -> bool:
        return self.is_open

    def __enter__(self) -> "MemoryStore":
        if not self.is_open:
            raise RuntimeError("the MemoryStore is closed")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def close(self) -> None:
        with self._lock:
            handle = self._handle
            if handle is None:
                return
            for child in list(self._children):
                child._close_from_store_locked()
            self._children.clear()
            self._handle = None
            self._lib.sms_close_store(handle)

    def __del__(self) -> None:
        try:
            self.close()
        except BaseException:
            pass

    def publish(
        self,
        key: Any,
        value: Any,
        descriptor: Any = b"",
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> StoreStatus:
        native_wait = _native_wait(wait)
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED
            native_key = _InputBuffer(key, "key")
            native_value = _InputBuffer(value, "value")
            native_descriptor = _InputBuffer(descriptor, "descriptor")
            return _store_status(
                int(
                    self._lib.sms_publish(
                        self._handle,
                        native_key.native,
                        native_value.native,
                        native_descriptor.native,
                        ctypes.byref(native_wait),
                    )
                )
            )

    def publish_segments(
        self,
        key: Any,
        segments: Iterable[Any],
        descriptor: Any = b"",
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, int]:
        native_wait = _native_wait(wait)
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED, 0
            native_key = _InputBuffer(key, "key")
            native_descriptor = _InputBuffer(descriptor, "descriptor")
            try:
                buffers = [_InputBuffer(value, f"segments[{index}]") for index, value in enumerate(segments)]
            except TypeError as error:
                if "not iterable" in str(error):
                    raise TypeError("segments must be an iterable of bytes-like objects") from error
                raise
            if len(buffers) > (1 << 64) - 1:
                raise OverflowError("too many payload segments")
            if buffers:
                array = (_native.Segment * len(buffers))(*(buffer.segment() for buffer in buffers))
                pointer = array
            else:
                array = None
                pointer = None
            copied = ctypes.c_int64()
            status = _store_status(
                int(
                    self._lib.sms_publish_segments(
                        self._handle,
                        native_key.native,
                        pointer,
                        len(buffers),
                        native_descriptor.native,
                        ctypes.byref(native_wait),
                        ctypes.byref(copied),
                    )
                )
            )
            return status, int(copied.value)

    def acquire(
        self,
        key: Any,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, Optional["ValueLease"]]:
        native_wait = _native_wait(wait)
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED, None
            native_key = _InputBuffer(key, "key")
            handle = ctypes.c_void_p()
            status = _store_status(
                int(self._lib.sms_acquire(self._handle, native_key.native, ctypes.byref(native_wait), ctypes.byref(handle)))
            )
            if status is not StoreStatus.SUCCESS or not handle.value:
                return status, None
            lease = ValueLease(self, handle)
            self._children.add(lease)
            return status, lease

    def remove(
        self,
        key: Any,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> StoreStatus:
        native_wait = _native_wait(wait)
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED
            native_key = _InputBuffer(key, "key")
            return _store_status(int(self._lib.sms_remove(self._handle, native_key.native, ctypes.byref(native_wait))))

    def reserve(
        self,
        key: Any,
        payload_length: int,
        descriptor: Any = b"",
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, Optional["ValueReservation"]]:
        native_wait = _native_wait(wait)
        if isinstance(payload_length, bool) or not isinstance(payload_length, int):
            raise TypeError("payload_length must be an integer")
        if payload_length < _INT32_MIN or payload_length > _INT32_MAX:
            return StoreStatus.VALUE_TOO_LARGE, None
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED, None
            native_key = _InputBuffer(key, "key")
            native_descriptor = _InputBuffer(descriptor, "descriptor")
            handle = ctypes.c_void_p()
            status = _store_status(
                int(
                    self._lib.sms_reserve(
                        self._handle,
                        native_key.native,
                        payload_length,
                        native_descriptor.native,
                        ctypes.byref(native_wait),
                        ctypes.byref(handle),
                    )
                )
            )
            if status is not StoreStatus.SUCCESS or not handle.value:
                return status, None
            reservation = ValueReservation(self, handle)
            self._children.add(reservation)
            return status, reservation

    def recover_leases(
        self,
        recover_current_process: bool = False,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, RecoveryReport]:
        return self._recover("sms_recover_leases", recover_current_process, wait)

    def recover_reservations(
        self,
        recover_current_process: bool = False,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, RecoveryReport]:
        return self._recover("sms_recover_reservations", recover_current_process, wait)

    def _recover(
        self,
        function_name: str,
        recover_current_process: bool,
        wait: WaitOptions,
    ) -> tuple[StoreStatus, RecoveryReport]:
        if not isinstance(recover_current_process, bool):
            raise TypeError("recover_current_process must be a bool")
        native_wait = _native_wait(wait)
        native_report = _native.RecoveryReport()
        native_report.struct_size = ctypes.sizeof(_native.RecoveryReport)
        native_report.abi_version = _native.ABI_VERSION
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED, RecoveryReport(0, 0, 0, 0, 0)
            function = getattr(self._lib, function_name)
            status = _store_status(
                int(
                    function(
                        self._handle,
                        1 if recover_current_process else 0,
                        ctypes.byref(native_wait),
                        ctypes.byref(native_report),
                    )
                )
            )
            if status is StoreStatus.SUCCESS and recover_current_process:
                for child in list(self._children):
                    child._invalidate_views_locked()
        report = RecoveryReport(
            native_report.scanned_count,
            native_report.recovered_count,
            native_report.active_count,
            native_report.unsupported_count,
            native_report.failed_count,
        )
        return status, report

    def diagnostics(
        self,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, Optional[DiagnosticsSnapshot]]:
        native_wait = _native_wait(wait)
        native = _native.Diagnostics()
        native.struct_size = ctypes.sizeof(_native.Diagnostics)
        native.abi_version = _native.ABI_VERSION
        with self._lock:
            if self._handle is None:
                return StoreStatus.STORE_DISPOSED, None
            status = _store_status(
                int(self._lib.sms_get_diagnostics(self._handle, ctypes.byref(native_wait), ctypes.byref(native)))
            )
        if status is not StoreStatus.SUCCESS:
            return status, None
        return status, DiagnosticsSnapshot(
            total_bytes=native.total_bytes,
            slot_count=native.slot_count,
            free_slot_count=native.free_slot_count,
            published_slot_count=native.published_slot_count,
            pending_removal_count=native.pending_removal_count,
            active_lease_count=native.active_lease_count,
            active_reservation_count=native.active_reservation_count,
            index_entry_count=native.index_entry_count,
            occupied_index_entry_count=native.occupied_index_entry_count,
            tombstone_index_entry_count=native.tombstone_index_entry_count,
            empty_index_entry_count=native.empty_index_entry_count,
            usable_index_capacity=native.usable_index_capacity,
            last_observed_probe_length=native.last_observed_probe_length,
            max_observed_probe_length=native.max_observed_probe_length,
            last_failure_status=_store_status(native.last_failure_status),
            aborted_reservation_count=native.aborted_reservation_count,
            recovered_lease_count=native.recovered_lease_count,
            active_lease_recovery_count=native.active_lease_recovery_count,
            unsupported_lease_recovery_count=native.unsupported_lease_recovery_count,
            failed_lease_recovery_count=native.failed_lease_recovery_count,
            recovered_reservation_count=native.recovered_reservation_count,
            active_reservation_recovery_count=native.active_reservation_recovery_count,
            unsupported_reservation_recovery_count=native.unsupported_reservation_recovery_count,
            failed_reservation_recovery_count=native.failed_reservation_recovery_count,
            capacity_pressure_count=native.capacity_pressure_count,
            index_compaction_count=native.index_compaction_count,
            failure_counts=tuple(int(value) for value in native.failure_counts),
        )

    get_diagnostics = diagnostics


class ValueLease(_ViewOwner):
    """Owning token for read-only, zero-copy value and descriptor views.

    Directly returned views are actively released with this token. Any slice or
    derived view created by the caller is subject to the same lifetime and must
    not be retained beyond release, recovery, or store close.
    """

    __slots__ = ("_store", "_handle", "_lock", "__weakref__")

    def __init__(self, store: MemoryStore, handle: ctypes.c_void_p) -> None:
        self._store = store
        self._handle: Optional[ctypes.c_void_p] = handle
        self._lock = threading.RLock()
        self._initialize_views()

    def _valid_locked(self) -> bool:
        if self._handle is None:
            return False
        valid = bool(self._store._lib.sms_lease_is_valid(self._handle))
        if not valid:
            self._release_views()
        return valid

    @property
    def is_valid(self) -> bool:
        with self._store._lock:
            with self._lock:
                return self._valid_locked()

    @property
    def value(self) -> memoryview:
        return self._bytes_view("sms_lease_value")

    @property
    def descriptor(self) -> memoryview:
        return self._bytes_view("sms_lease_descriptor")

    def _bytes_view(self, function_name: str) -> memoryview:
        with self._store._lock:
            with self._lock:
                if not self._valid_locked():
                    raise RuntimeError("the value lease is no longer valid")
                native = getattr(self._store._lib, function_name)(self._handle)
                view = _borrowed_view(native.data, int(native.length), self, readonly=True)
                return self._track_view(view)

    def release(self, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        native_wait = _native_wait(wait)
        with self._store._lock:
            with self._lock:
                self._release_views()
                if self._handle is None:
                    return StoreStatus.INVALID_LEASE
                return _store_status(
                    int(self._store._lib.sms_release_lease(self._handle, ctypes.byref(native_wait)))
                )

    def __enter__(self) -> "ValueLease":
        if not self.is_valid:
            raise RuntimeError("the value lease is no longer valid")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def close(self) -> None:
        with self._store._lock:
            self._close_from_store_locked()
            self._store._children.discard(self)

    def _close_from_store_locked(self) -> None:
        with self._lock:
            self._release_views()
            if self._handle is not None:
                handle, self._handle = self._handle, None
                self._store._lib.sms_destroy_lease(handle)

    def _invalidate_views_locked(self) -> None:
        with self._lock:
            self._release_views()

    def __del__(self) -> None:
        try:
            self.close()
        except BaseException:
            pass


class ValueReservation(_ViewOwner):
    """Owning token for an announced-length, zero-copy writable payload.

    A writable view is immediate-use memory and is released by the next
    reservation operation. Caller-created slices share that same lifetime.
    """

    __slots__ = ("_store", "_handle", "_lock", "__weakref__")

    def __init__(self, store: MemoryStore, handle: ctypes.c_void_p) -> None:
        self._store = store
        self._handle: Optional[ctypes.c_void_p] = handle
        self._lock = threading.RLock()
        self._initialize_views()

    def _valid_locked(self) -> bool:
        if self._handle is None:
            return False
        valid = bool(self._store._lib.sms_reservation_is_valid(self._handle))
        if not valid:
            self._release_views()
        return valid

    @property
    def is_valid(self) -> bool:
        with self._store._lock:
            with self._lock:
                return self._valid_locked()

    @property
    def payload_length(self) -> int:
        with self._store._lock:
            with self._lock:
                if not self._valid_locked():
                    return 0
                return int(self._store._lib.sms_reservation_payload_length(self._handle))

    @property
    def bytes_written(self) -> int:
        with self._store._lock:
            with self._lock:
                if not self._valid_locked():
                    return 0
                return int(self._store._lib.sms_reservation_bytes_written(self._handle))

    @property
    def remaining_bytes(self) -> int:
        with self._store._lock:
            with self._lock:
                if not self._valid_locked():
                    return 0
                return max(0, int(self._store._lib.sms_reservation_remaining_bytes(self._handle)))

    def buffer(self, size_hint: int = 0) -> memoryview:
        size_hint = _require_integer(size_hint, "size_hint", _INT32_MIN, _INT32_MAX)
        with self._store._lock:
            with self._lock:
                self._release_views()
                if not self._valid_locked():
                    raise RuntimeError("the value reservation is no longer valid")
                native = self._store._lib.sms_reservation_buffer(self._handle, size_hint)
                view = _borrowed_view(native.data, int(native.length), self, readonly=False)
                return self._track_view(view)

    @property
    def view(self) -> memoryview:
        return self.buffer()

    def advance(self, byte_count: int, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        native_wait = _native_wait(wait)
        if isinstance(byte_count, bool) or not isinstance(byte_count, int):
            raise TypeError("byte_count must be an integer")
        if byte_count < _INT32_MIN or byte_count > _INT32_MAX:
            return StoreStatus.RESERVATION_WRITE_OUT_OF_RANGE
        with self._store._lock:
            with self._lock:
                self._release_views()
                if self._handle is None:
                    return StoreStatus.INVALID_RESERVATION
                return _store_status(
                    int(
                        self._store._lib.sms_advance_reservation(
                            self._handle, byte_count, ctypes.byref(native_wait)
                        )
                    )
                )

    def commit(self, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        return self._complete("sms_commit_reservation", wait)

    def abort(self, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        return self._complete("sms_abort_reservation", wait)

    def _complete(self, function_name: str, wait: WaitOptions) -> StoreStatus:
        native_wait = _native_wait(wait)
        with self._store._lock:
            with self._lock:
                self._release_views()
                if self._handle is None:
                    return StoreStatus.INVALID_RESERVATION
                function = getattr(self._store._lib, function_name)
                return _store_status(int(function(self._handle, ctypes.byref(native_wait))))

    def __enter__(self) -> "ValueReservation":
        if not self.is_valid:
            raise RuntimeError("the value reservation is no longer valid")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def close(self) -> None:
        with self._store._lock:
            self._close_from_store_locked()
            self._store._children.discard(self)

    def _close_from_store_locked(self) -> None:
        with self._lock:
            self._release_views()
            if self._handle is not None:
                handle, self._handle = self._handle, None
                self._store._lib.sms_destroy_reservation(handle)

    def _invalidate_views_locked(self) -> None:
        with self._lock:
            self._release_views()

    def __del__(self) -> None:
        try:
            self.close()
        except BaseException:
            pass


__all__ = [
    "WaitOptions",
    "StoreOptions",
    "RecoveryReport",
    "DiagnosticsSnapshot",
    "MemoryStore",
    "ValueLease",
    "ValueReservation",
    "calculate_required_bytes",
]
