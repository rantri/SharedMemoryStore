"""Pythonic ownership wrappers over the versioned SharedMemoryStore C ABI."""

from __future__ import annotations

import ctypes
from contextlib import contextmanager
from dataclasses import dataclass
import sys
import threading
import time
from typing import Any, ClassVar, Iterable, Iterator, Optional
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


class _CancellationClosedError(RuntimeError):
    pass


class CancellationSource:
    """Own one native cancellation flag and keep it alive while calls borrow it."""

    __slots__ = (
        "_active_borrows",
        "_closing",
        "_condition",
        "_handle",
        "_lib",
        "__weakref__",
    )

    def __init__(self) -> None:
        lib = _native.library()
        handle = _native.CancellationHandle()
        status = _store_status(int(lib.sms_create_cancellation(ctypes.byref(handle))))
        if status is not StoreStatus.SUCCESS or not handle.value:
            raise RuntimeError(f"could not create a cancellation source ({status.name})")
        self._lib = lib
        self._handle: Optional[_native.CancellationHandle] = handle
        self._condition = threading.Condition(threading.RLock())
        self._active_borrows = 0
        self._closing = False

    @contextmanager
    def _borrow(self) -> Iterator[_native.CancellationHandle]:
        """Borrow the opaque handle for exactly one synchronous native call."""

        with self._condition:
            if self._handle is None or self._closing:
                raise _CancellationClosedError("the CancellationSource is closed")
            self._active_borrows += 1
            handle = self._handle
        try:
            yield handle
        finally:
            with self._condition:
                self._active_borrows -= 1
                if self._active_borrows == 0:
                    self._condition.notify_all()

    def signal(self) -> StoreStatus:
        """Signal cancellation. Repeated calls are safe and remain signaled."""

        try:
            with self._borrow() as handle:
                return _store_status(int(self._lib.sms_signal_cancellation(handle)))
        except _CancellationClosedError:
            return StoreStatus.UNKNOWN_FAILURE

    @property
    def is_signaled(self) -> bool:
        try:
            with self._borrow() as handle:
                return bool(self._lib.sms_cancellation_is_signaled(handle))
        except _CancellationClosedError:
            return False

    @property
    def is_closed(self) -> bool:
        with self._condition:
            return self._handle is None or self._closing

    def close(self) -> None:
        with self._condition:
            while self._closing:
                self._condition.wait()
            if self._handle is None:
                return
            self._closing = True
            while self._active_borrows:
                self._condition.wait()
            handle, self._handle = self._handle, None

        try:
            self._lib.sms_destroy_cancellation(handle)
        finally:
            with self._condition:
                self._closing = False
                self._condition.notify_all()

    def __enter__(self) -> "CancellationSource":
        if self.is_closed:
            raise RuntimeError("the CancellationSource is closed")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except BaseException:
            pass


@dataclass(frozen=True, slots=True)
class WaitOptions:
    """Caller-controlled wait policy with an optional owned cancellation borrow."""

    timeout_milliseconds: int = 1000
    cancellation: Optional[CancellationSource] = None

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
        if self.cancellation is not None and not isinstance(self.cancellation, CancellationSource):
            raise TypeError("cancellation must be a CancellationSource or None")

    @classmethod
    def default(cls, cancellation: Optional[CancellationSource] = None) -> "WaitOptions":
        return cls.DEFAULT if cancellation is None else cls(1000, cancellation)

    @classmethod
    def defaults(cls, cancellation: Optional[CancellationSource] = None) -> "WaitOptions":
        return cls.default(cancellation)

    @classmethod
    def no_wait(cls, cancellation: Optional[CancellationSource] = None) -> "WaitOptions":
        return cls.NO_WAIT if cancellation is None else cls(0, cancellation)

    @classmethod
    def infinite(cls, cancellation: Optional[CancellationSource] = None) -> "WaitOptions":
        return cls.INFINITE if cancellation is None else cls(_native.WAIT_INFINITE, cancellation)


WaitOptions.DEFAULT = WaitOptions(1000)
WaitOptions.NO_WAIT = WaitOptions(0)
WaitOptions.INFINITE = WaitOptions(_native.WAIT_INFINITE)


@contextmanager
def _native_wait(value: WaitOptions) -> Iterator[_native.WaitOptions]:
    if not isinstance(value, WaitOptions):
        raise TypeError("wait must be a WaitOptions instance")
    result = _native.WaitOptions()
    result.struct_size = ctypes.sizeof(_native.WaitOptions)
    result.abi_version = _native.ABI_VERSION
    result.timeout_milliseconds = value.timeout_milliseconds
    result.cancellation = None
    if value.cancellation is None:
        yield result
        return
    with value.cancellation._borrow() as handle:
        result.cancellation = handle
        yield result


class _WaitBudget:
    """One caller wait budget shared by the Python gate and native operation."""

    __slots__ = (
        "_cancellation_handle",
        "_cancellation_library",
        "_deadline",
        "_timeout_milliseconds",
    )

    def __init__(
        self,
        value: WaitOptions,
        cancellation_handle: Optional[_native.CancellationHandle],
    ) -> None:
        self._timeout_milliseconds = value.timeout_milliseconds
        self._deadline = (
            None
            if value.timeout_milliseconds == _native.WAIT_INFINITE
            else time.monotonic() + (value.timeout_milliseconds / 1000.0)
        )
        self._cancellation_handle = cancellation_handle
        self._cancellation_library = (
            value.cancellation._lib if value.cancellation is not None else None
        )

    @property
    def is_cancellation_signaled(self) -> bool:
        return bool(
            self._cancellation_handle
            and self._cancellation_library.sms_cancellation_is_signaled(
                self._cancellation_handle
            )
        )

    @property
    def remaining_seconds(self) -> Optional[float]:
        if self._deadline is None:
            return None
        return max(0.0, self._deadline - time.monotonic())

    def native_options(self) -> _native.WaitOptions:
        result = _native.WaitOptions()
        result.struct_size = ctypes.sizeof(_native.WaitOptions)
        result.abi_version = _native.ABI_VERSION
        if self._deadline is None:
            result.timeout_milliseconds = _native.WAIT_INFINITE
        else:
            remaining = self.remaining_seconds
            assert remaining is not None
            result.timeout_milliseconds = min(
                self._timeout_milliseconds,
                max(0, int(remaining * 1000.0)),
            )
        result.cancellation = self._cancellation_handle
        return result


@contextmanager
def _wait_budget(value: WaitOptions) -> Iterator[_WaitBudget]:
    if not isinstance(value, WaitOptions):
        raise TypeError("wait must be a WaitOptions instance")
    if value.cancellation is None:
        yield _WaitBudget(value, None)
        return
    with value.cancellation._borrow() as handle:
        yield _WaitBudget(value, handle)


def calculate_required_bytes(
    *,
    slot_count: int,
    max_value_bytes: int,
    max_descriptor_bytes: int,
    max_key_bytes: int,
    lease_record_count: int,
    participant_record_count: int = 64,
) -> int:
    """Calculate the exact mapped capacity for the supplied protocol limits."""

    arguments = {
        "slot_count": slot_count,
        "max_value_bytes": max_value_bytes,
        "max_descriptor_bytes": max_descriptor_bytes,
        "max_key_bytes": max_key_bytes,
        "lease_record_count": lease_record_count,
        "participant_record_count": participant_record_count,
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
                participant_record_count,
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
    participant_record_count: int = 64
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
        participant_record_count: int = 64,
        open_mode: OpenMode = OpenMode.CREATE_OR_OPEN,
        enable_lease_recovery: bool = False,
    ) -> "StoreOptions":
        total_bytes = calculate_required_bytes(
            slot_count=slot_count,
            max_value_bytes=max_value_bytes,
            max_descriptor_bytes=max_descriptor_bytes,
            max_key_bytes=max_key_bytes,
            lease_record_count=lease_record_count,
            participant_record_count=participant_record_count,
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
            participant_record_count=participant_record_count,
            enable_lease_recovery=enable_lease_recovery,
        )


@dataclass(frozen=True, slots=True)
class ProtocolInfo:
    """Immutable identity of the one mapped protocol understood by this package."""

    layout_major_version: int
    layout_minor_version: int
    resource_protocol_version: int
    required_features: int
    optional_features: int


@dataclass(frozen=True, slots=True)
class RecoveryReport:
    scanned_count: int
    recovered_count: int
    active_count: int
    unsupported_count: int
    failed_count: int


@dataclass(frozen=True, slots=True)
class DiagnosticsSnapshot:
    protocol_info: ProtocolInfo
    total_bytes: int
    slot_count: int
    free_slot_count: int
    initializing_slot_count: int
    reserved_slot_count: int
    published_slot_count: int
    pending_removal_count: int
    reclaiming_slot_count: int
    retired_slot_count: int
    active_reservation_count: int
    active_lease_count: int
    claiming_lease_count: int
    recovering_lease_count: int
    free_lease_count: int
    retired_lease_count: int
    participant_record_count: int
    free_participant_count: int
    registering_participant_count: int
    active_participant_count: int
    closing_participant_count: int
    recovering_participant_count: int
    reclaiming_participant_count: int
    retired_participant_count: int
    index_entry_count: int
    occupied_index_entry_count: int
    empty_index_entry_count: int
    usable_index_capacity: int
    primary_directory_occupancy: int
    spilled_bucket_count: int
    overflow_directory_occupancy: int
    last_observed_probe_length: int
    max_observed_probe_length: int
    max_observed_overflow_scan_length: int
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
    overflow_scan_count: int
    cas_retry_count: int
    helped_transition_count: int
    contention_budget_exhaustion_count: int
    invalid_token_count: int
    stale_token_count: int
    recovery_attempt_count: int
    recovered_transition_count: int
    current_owner_classification_count: int
    live_owner_classification_count: int
    stale_owner_classification_count: int
    unsupported_owner_classification_count: int
    inconsistent_owner_classification_count: int
    changing_owner_classification_count: int
    failure_counts: tuple[int, ...]

    @property
    def is_participant_table_exhausted(self) -> bool:
        return self.participant_record_count > 0 and self.free_participant_count == 0

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
    __slots__ = ("_exporter_refs", "_view_condition", "_view_refs")

    def _initialize_views(self) -> None:
        # The concrete token creates ``_lock`` before initializing views. Use
        # that same lock so token state and borrowed-exporter lifetime change
        # atomically from the wrapper's point of view.
        self._view_condition = threading.Condition(self._lock)
        self._view_refs: list[weakref.ReferenceType[memoryview]] = []
        self._exporter_refs: list[weakref.ReferenceType[Any]] = []

    def _track_view(self, view: memoryview) -> memoryview:
        exporter = view.obj
        condition = self._view_condition

        def exporter_released(_: weakref.ReferenceType[Any]) -> None:
            with condition:
                condition.notify_all()

        with condition:
            self._view_refs.append(weakref.ref(view))
            # ctypes arrays are weak-referenceable. Every caller-derived
            # memoryview retains this exporter even after the directly returned
            # view is released, so its lifetime is the authoritative signal
            # that mapped bytes are no longer borrowed.
            self._exporter_refs.append(weakref.ref(exporter, exporter_released))
        return view

    def _release_views(self) -> None:
        with self._view_condition:
            references, self._view_refs = self._view_refs, []
            for reference in references:
                view = reference()
                if view is not None:
                    try:
                        view.release()
                    except BufferError:
                        # A legal secondary buffer export (for example
                        # pickle.PickleBuffer) can temporarily prevent release
                        # of the direct root. Retain it so close/transition
                        # retry can revoke the root after that export ends.
                        self._view_refs.append(reference)
                    except ValueError:
                        pass

    def _has_live_exporters_locked(self) -> bool:
        live: list[weakref.ReferenceType[Any]] = []
        for reference in self._exporter_refs:
            if reference() is not None:
                live.append(reference)
        self._exporter_refs = live
        return bool(live)

    def _drain_views(self, budget: _WaitBudget) -> StoreStatus:
        """Release direct views and wait until every derived borrow is gone."""

        with self._view_condition:
            self._release_views()
            while self._has_live_exporters_locked():
                if budget.is_cancellation_signaled:
                    return StoreStatus.OPERATION_CANCELED
                remaining = budget.remaining_seconds
                if remaining is not None and remaining <= 0.0:
                    return StoreStatus.STORE_BUSY
                poll_seconds = 0.01
                self._view_condition.wait(
                    poll_seconds if remaining is None else min(poll_seconds, remaining)
                )
            return StoreStatus.SUCCESS

    def _prepare_close(self) -> bool:
        """Release direct roots and fail fast if a derived export remains."""

        with self._view_condition:
            self._release_views()
            return not self._has_live_exporters_locked()


class _OperationEntry:
    """Stable native references held while one wrapper operation is entered."""

    __slots__ = ("_wait_budget", "gate_status", "handle", "lib")

    def __init__(self, handle: ctypes.c_void_p, lib: Any) -> None:
        self.handle = handle
        self.lib = lib
        self.gate_status = StoreStatus.SUCCESS
        self._wait_budget: Optional[_WaitBudget] = None

    def remaining_native_wait(self) -> _native.WaitOptions:
        if self._wait_budget is None:
            raise RuntimeError("this operation has no caller wait budget")
        return self._wait_budget.native_options()


class _MappingOperationGroup:
    """Coordinate destructive recovery across Python handles for one mapping.

    Normal store calls take a shared entry and therefore remain concurrent.
    Current-process recovery takes the exclusive entry so every direct borrowed
    view can be revoked before the native runtime makes its slot reusable.
    """

    __slots__ = (
        "_condition",
        "_readers",
        "_writer",
        "_waiting_writers",
        "stores",
    )

    def __init__(self) -> None:
        self._condition = threading.Condition(threading.RLock())
        self._readers = 0
        self._writer = False
        self._waiting_writers = 0
        self.stores: weakref.WeakSet[MemoryStore] = weakref.WeakSet()

    @contextmanager
    def entered(
        self,
        *,
        exclusive: bool,
        budget: Optional[_WaitBudget],
    ) -> Iterator[StoreStatus]:
        acquired = False
        with self._condition:
            if exclusive:
                self._waiting_writers += 1
                try:
                    status = self._wait_until_available_locked(
                        lambda: not self._writer and not self._readers,
                        budget,
                    )
                    if status is StoreStatus.SUCCESS:
                        self._writer = True
                        acquired = True
                finally:
                    self._waiting_writers -= 1
                    if not acquired:
                        self._condition.notify_all()
            else:
                status = self._wait_until_available_locked(
                    lambda: not self._writer and not self._waiting_writers,
                    budget,
                )
                if status is StoreStatus.SUCCESS:
                    self._readers += 1
                    acquired = True
        try:
            yield status
        finally:
            if acquired:
                with self._condition:
                    if exclusive:
                        self._writer = False
                    else:
                        self._readers -= 1
                    self._condition.notify_all()

    def _wait_until_available_locked(
        self,
        available: Any,
        budget: Optional[_WaitBudget],
    ) -> StoreStatus:
        while True:
            if budget is not None and budget.is_cancellation_signaled:
                return StoreStatus.OPERATION_CANCELED
            if available():
                return StoreStatus.SUCCESS
            if budget is None:
                self._condition.wait()
                continue
            remaining = budget.remaining_seconds
            if remaining is not None and remaining <= 0.0:
                return StoreStatus.STORE_BUSY
            # Native cancellation is a shared atomic flag rather than a Python
            # event, so poll it while still bounding finite waits precisely.
            poll_seconds = 0.01
            self._condition.wait(
                poll_seconds if remaining is None else min(poll_seconds, remaining)
            )


class MemoryStore:
    """Context-managed owner of one process-local native store handle."""

    _registry_lock: ClassVar[Any] = threading.RLock()
    _registry: ClassVar[
        dict[tuple[int, str], _MappingOperationGroup]
    ] = {}

    __slots__ = (
        "_handle",
        "_lib",
        "_protocol_info",
        "_condition",
        "_active_operations",
        "_closing",
        "_children",
        "_registry_key",
        "_operation_group",
        "__weakref__",
    )

    def __init__(
        self,
        handle: Optional[ctypes.c_void_p] = None,
        lib: Any = None,
        protocol_info: Optional[ProtocolInfo] = None,
        registry_key: Optional[tuple[int, str]] = None,
    ) -> None:
        if (handle is None) != (lib is None):
            raise ValueError("handle and lib must either both be supplied or both be omitted")
        if (handle is None) != (protocol_info is None):
            raise ValueError("protocol_info must be supplied exactly when a native handle is supplied")
        self._handle = handle
        self._lib = lib
        self._protocol_info = protocol_info
        self._condition = threading.Condition(threading.RLock())
        self._active_operations = 0
        self._closing = False
        self._children: weakref.WeakSet[Any] = weakref.WeakSet()
        self._registry_key = registry_key
        self._operation_group: Optional[_MappingOperationGroup] = None
        if registry_key is not None:
            self._register_store()

    def _register_store(self) -> None:
        key = self._registry_key
        if key is None:
            return
        with self._registry_lock:
            group = self._registry.get(key)
            if group is None:
                group = _MappingOperationGroup()
                self._registry[key] = group
            group.stores.add(self)
            self._operation_group = group

    def _unregister_store(self) -> None:
        key = self._registry_key
        if key is None:
            return
        with self._registry_lock:
            group = self._operation_group
            if group is None:
                return
            group.stores.discard(self)
            self._operation_group = None
            if not group.stores and self._registry.get(key) is group:
                self._registry.pop(key, None)

    def _mapping_children_snapshot(self) -> list[Any]:
        """Snapshot children from every Python handle for this native mapping."""

        key = self._registry_key
        if key is None:
            return self._children_snapshot()
        with self._registry_lock:
            group = self._operation_group
            stores = list(group.stores) if group is not None else []
        children: list[Any] = []
        for store in stores:
            children.extend(store._children_snapshot())
        return children

    @contextmanager
    def _entered_operation(
        self,
        *,
        allow_during_close: bool = False,
        exclusive_mapping: bool = False,
        wait: Optional[WaitOptions] = None,
    ) -> Iterator[Optional[_OperationEntry]]:
        """Enter one local handle lifetime without serializing native work."""

        with self._condition:
            if self._handle is None or (self._closing and not allow_during_close):
                entry = None
            else:
                self._active_operations += 1
                entry = _OperationEntry(self._handle, self._lib)
                group = self._operation_group
        try:
            if entry is None:
                yield entry
            elif wait is None:
                if group is None:
                    yield entry
                else:
                    with group.entered(
                        exclusive=exclusive_mapping,
                        budget=None,
                    ) as gate_status:
                        entry.gate_status = gate_status
                        yield entry
            else:
                with _wait_budget(wait) as budget:
                    if group is None:
                        entry.gate_status = (
                            StoreStatus.OPERATION_CANCELED
                            if budget.is_cancellation_signaled
                            else StoreStatus.SUCCESS
                        )
                        if entry.gate_status is StoreStatus.SUCCESS:
                            entry._wait_budget = budget
                        yield entry
                    else:
                        with group.entered(
                            exclusive=exclusive_mapping,
                            budget=budget,
                        ) as gate_status:
                            entry.gate_status = gate_status
                            if gate_status is StoreStatus.SUCCESS:
                                entry._wait_budget = budget
                            yield entry
        finally:
            if entry is not None:
                with self._condition:
                    self._active_operations -= 1
                    if self._active_operations == 0:
                        self._condition.notify_all()

    def _register_child(self, child: Any) -> None:
        with self._condition:
            self._children.add(child)

    def _discard_child(self, child: Any) -> None:
        with self._condition:
            self._children.discard(child)

    def _children_snapshot(self) -> list[Any]:
        with self._condition:
            return list(self._children)

    @classmethod
    def open(
        cls,
        options: StoreOptions,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreOpenStatus, Optional["MemoryStore"]]:
        if not isinstance(options, StoreOptions):
            raise TypeError("options must be a StoreOptions instance")
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
            (options.participant_record_count, _INT32_MAX),
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
        native.participant_record_count = options.participant_record_count
        native.enable_lease_recovery = 1 if options.enable_lease_recovery else 0

        lib = _native.library()
        handle = ctypes.c_void_p()
        with _native_wait(wait) as native_wait:
            status = _open_status(
                int(
                    lib.sms_open_store(
                        ctypes.byref(native),
                        ctypes.byref(native_wait),
                        ctypes.byref(handle),
                    )
                )
            )
        if status is not StoreOpenStatus.SUCCESS or not handle.value:
            return status, None
        protocol_info = ProtocolInfo(
            _native.LAYOUT_MAJOR_VERSION,
            _native.LAYOUT_MINOR_VERSION,
            _native.RESOURCE_PROTOCOL_VERSION,
            _native.REQUIRED_FEATURES,
            _native.OPTIONAL_FEATURES,
        )
        return status, cls(
            handle,
            lib,
            protocol_info,
            (id(lib), options.name),
        )

    @property
    def is_open(self) -> bool:
        with self._condition:
            return self._handle is not None and not self._closing

    @property
    def is_valid(self) -> bool:
        return self.is_open

    @property
    def protocol_info(self) -> ProtocolInfo:
        """Return the immutable protocol identity captured for this handle."""

        if self._protocol_info is None:
            raise RuntimeError("the MemoryStore has no native protocol identity")
        return self._protocol_info

    def __enter__(self) -> "MemoryStore":
        if not self.is_open:
            raise RuntimeError("the MemoryStore is closed")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def close(self) -> None:
        with self._condition:
            while self._closing:
                self._condition.wait()
            if self._handle is None:
                return
            self._closing = True
            while self._active_operations:
                self._condition.wait()
            handle = self._handle
            lib = self._lib
            children = list(self._children)
            group = self._operation_group

        close_error: Optional[BaseException] = None
        detached = False

        @contextmanager
        def mapping_drain() -> Iterator[None]:
            if group is None:
                yield
                return
            # Store close is an explicit lifetime drain. It intentionally has
            # no operation timeout and waits for peer recovery to leave its
            # exclusive mapping section before any token or handle is freed.
            with group.entered(exclusive=False, budget=None):
                yield

        try:
            with mapping_drain():
                # Match Python's exported-buffer safety precedent: direct roots
                # are revoked, but an arbitrary derived memoryview cannot be
                # invalidated. Fail without detaching any native handle so the
                # caller can release that borrow and retry close.
                if not all(child._prepare_close() for child in children):
                    raise BufferError(
                        "cannot close SharedMemoryStore while a derived "
                        "memoryview is still active"
                    )
                with self._condition:
                    self._handle = None
                    detached = True
                for child in children:
                    try:
                        child._close_from_store_drained(lib)
                    except BaseException as error:
                        if close_error is None:
                            close_error = error
                try:
                    lib.sms_close_store(handle)
                except BaseException as error:
                    if close_error is None:
                        close_error = error
                try:
                    lib.sms_destroy_store(handle)
                except BaseException as error:
                    if close_error is None:
                        close_error = error
        finally:
            with self._condition:
                if detached:
                    self._children.clear()
                self._closing = False
                self._condition.notify_all()
            if detached:
                self._unregister_store()
        if close_error is not None:
            raise close_error

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
        with self._entered_operation(wait=wait) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status
            native_key = _InputBuffer(key, "key")
            native_value = _InputBuffer(value, "value")
            native_descriptor = _InputBuffer(descriptor, "descriptor")
            native_wait = entry.remaining_native_wait()
            return _store_status(
                int(
                    entry.lib.sms_publish(
                        entry.handle,
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
        # Materialize arbitrary iterables before entering the mapping gate.
        # A generator is caller code and may re-enter this mapping; executing it
        # while a writer-preferred shared entry is held can deadlock with a
        # queued current-process recovery.
        try:
            buffers = [
                _InputBuffer(value, f"segments[{index}]")
                for index, value in enumerate(segments)
            ]
        except TypeError as error:
            if "not iterable" in str(error):
                raise TypeError(
                    "segments must be an iterable of bytes-like objects"
                ) from error
            raise
        if len(buffers) > (1 << 64) - 1:
            raise OverflowError("too many payload segments")

        with self._entered_operation(wait=wait) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED, 0
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status, 0
            native_key = _InputBuffer(key, "key")
            native_descriptor = _InputBuffer(descriptor, "descriptor")
            if buffers:
                array = (_native.Segment * len(buffers))(*(buffer.segment() for buffer in buffers))
                pointer = array
            else:
                array = None
                pointer = None
            copied = ctypes.c_int64()
            native_wait = entry.remaining_native_wait()
            status = _store_status(
                int(
                    entry.lib.sms_publish_segments(
                        entry.handle,
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
        with self._entered_operation(wait=wait) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED, None
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status, None
            native_key = _InputBuffer(key, "key")
            handle = ctypes.c_void_p()
            native_wait = entry.remaining_native_wait()
            status = _store_status(
                int(
                    entry.lib.sms_acquire(
                        entry.handle,
                        native_key.native,
                        ctypes.byref(native_wait),
                        ctypes.byref(handle),
                    )
                )
            )
            if status is not StoreStatus.SUCCESS or not handle.value:
                return status, None
            lease = ValueLease(self, handle)
            self._register_child(lease)
            return status, lease

    def remove(
        self,
        key: Any,
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> StoreStatus:
        with self._entered_operation(wait=wait) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status
            native_key = _InputBuffer(key, "key")
            native_wait = entry.remaining_native_wait()
            return _store_status(
                int(
                    entry.lib.sms_remove(
                        entry.handle,
                        native_key.native,
                        ctypes.byref(native_wait),
                    )
                )
            )

    def reserve(
        self,
        key: Any,
        payload_length: int,
        descriptor: Any = b"",
        *,
        wait: WaitOptions = WaitOptions.DEFAULT,
    ) -> tuple[StoreStatus, Optional["ValueReservation"]]:
        if isinstance(payload_length, bool) or not isinstance(payload_length, int):
            raise TypeError("payload_length must be an integer")
        if payload_length < _INT32_MIN or payload_length > _INT32_MAX:
            return StoreStatus.VALUE_TOO_LARGE, None
        with self._entered_operation(wait=wait) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED, None
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status, None
            native_key = _InputBuffer(key, "key")
            native_descriptor = _InputBuffer(descriptor, "descriptor")
            handle = ctypes.c_void_p()
            native_wait = entry.remaining_native_wait()
            status = _store_status(
                int(
                    entry.lib.sms_reserve(
                        entry.handle,
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
            self._register_child(reservation)
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
        if not isinstance(wait, WaitOptions):
            raise TypeError("wait must be a WaitOptions instance")
        native_report = _native.RecoveryReport()
        native_report.struct_size = ctypes.sizeof(_native.RecoveryReport)
        native_report.abi_version = _native.ABI_VERSION
        with self._entered_operation(
            exclusive_mapping=recover_current_process,
            wait=wait,
        ) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED, RecoveryReport(0, 0, 0, 0, 0)
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status, RecoveryReport(0, 0, 0, 0, 0)
            function = getattr(entry.lib, function_name)
            if recover_current_process:
                recovered_type = (
                    ValueLease
                    if function_name == "sms_recover_leases"
                    else ValueReservation
                )
                for child in self._mapping_children_snapshot():
                    if isinstance(child, recovered_type):
                        drain_status = child._drain_views(entry._wait_budget)
                        if drain_status is not StoreStatus.SUCCESS:
                            return drain_status, RecoveryReport(0, 0, 0, 0, 0)
            native_wait = entry.remaining_native_wait()
            status = _store_status(
                int(
                    function(
                        entry.handle,
                        1 if recover_current_process else 0,
                        ctypes.byref(native_wait),
                        ctypes.byref(native_report),
                    )
                )
            )
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
        native = _native.Diagnostics()
        native.struct_size = ctypes.sizeof(_native.Diagnostics)
        native.abi_version = _native.ABI_VERSION
        with self._entered_operation(wait=wait) as entry:
            if entry is None:
                return StoreStatus.STORE_DISPOSED, None
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status, None
            native_wait = entry.remaining_native_wait()
            status = _store_status(
                int(
                    entry.lib.sms_get_diagnostics(
                        entry.handle,
                        ctypes.byref(native_wait),
                        ctypes.byref(native),
                    )
                )
            )
        if status is not StoreStatus.SUCCESS:
            return status, None
        return status, DiagnosticsSnapshot(
            protocol_info=ProtocolInfo(
                native.layout_major,
                native.layout_minor,
                native.resource_protocol,
                native.required_features,
                native.optional_features,
            ),
            total_bytes=native.total_bytes,
            slot_count=native.slot_count,
            free_slot_count=native.free_slot_count,
            initializing_slot_count=native.initializing_slot_count,
            reserved_slot_count=native.reserved_slot_count,
            published_slot_count=native.published_slot_count,
            pending_removal_count=native.pending_removal_count,
            reclaiming_slot_count=native.reclaiming_slot_count,
            retired_slot_count=native.retired_slot_count,
            active_reservation_count=native.active_reservation_count,
            active_lease_count=native.active_lease_count,
            claiming_lease_count=native.claiming_lease_count,
            recovering_lease_count=native.recovering_lease_count,
            free_lease_count=native.free_lease_count,
            retired_lease_count=native.retired_lease_count,
            participant_record_count=native.participant_record_count,
            free_participant_count=native.free_participant_count,
            registering_participant_count=native.registering_participant_count,
            active_participant_count=native.active_participant_count,
            closing_participant_count=native.closing_participant_count,
            recovering_participant_count=native.recovering_participant_count,
            reclaiming_participant_count=native.reclaiming_participant_count,
            retired_participant_count=native.retired_participant_count,
            index_entry_count=native.index_entry_count,
            occupied_index_entry_count=native.occupied_index_entry_count,
            empty_index_entry_count=native.empty_index_entry_count,
            usable_index_capacity=native.usable_index_capacity,
            primary_directory_occupancy=native.primary_directory_occupancy,
            spilled_bucket_count=native.spilled_bucket_count,
            overflow_directory_occupancy=native.overflow_directory_occupancy,
            last_observed_probe_length=native.last_observed_probe_length,
            max_observed_probe_length=native.max_observed_probe_length,
            max_observed_overflow_scan_length=native.max_observed_overflow_scan_length,
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
            overflow_scan_count=native.overflow_scan_count,
            cas_retry_count=native.cas_retry_count,
            helped_transition_count=native.helped_transition_count,
            contention_budget_exhaustion_count=native.contention_budget_exhaustion_count,
            invalid_token_count=native.invalid_token_count,
            stale_token_count=native.stale_token_count,
            recovery_attempt_count=native.recovery_attempt_count,
            recovered_transition_count=native.recovered_transition_count,
            current_owner_classification_count=native.current_owner_classification_count,
            live_owner_classification_count=native.live_owner_classification_count,
            stale_owner_classification_count=native.stale_owner_classification_count,
            unsupported_owner_classification_count=native.unsupported_owner_classification_count,
            inconsistent_owner_classification_count=native.inconsistent_owner_classification_count,
            changing_owner_classification_count=native.changing_owner_classification_count,
            failure_counts=tuple(int(value) for value in native.failure_counts),
        )

    get_diagnostics = diagnostics


class ValueLease(_ViewOwner):
    """Owning token for read-only, zero-copy value and descriptor views.

    Directly returned views are actively released with this token. A slice or
    other derived view pins the native token and mapping until the caller
    releases that derived borrow; bounded release/recovery reports busy or
    cancellation instead of recycling bytes that are still projected.
    """

    __slots__ = ("_store", "_handle", "_lock", "__weakref__")

    def __init__(self, store: MemoryStore, handle: ctypes.c_void_p) -> None:
        self._store = store
        self._handle: Optional[ctypes.c_void_p] = handle
        self._lock = threading.RLock()
        self._initialize_views()

    def _valid_locked(self, lib: Any) -> bool:
        if self._handle is None:
            return False
        valid = bool(lib.sms_lease_is_valid(self._handle))
        if not valid:
            self._release_views()
        return valid

    @property
    def is_valid(self) -> bool:
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                return False
            if entry.gate_status is not StoreStatus.SUCCESS:
                return False
            with self._lock:
                return self._valid_locked(entry.lib)

    @property
    def value(self) -> memoryview:
        return self._bytes_view("sms_lease_value")

    @property
    def descriptor(self) -> memoryview:
        return self._bytes_view("sms_lease_descriptor")

    def _bytes_view(self, function_name: str) -> memoryview:
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                raise RuntimeError("the value lease is no longer valid")
            if entry.gate_status is not StoreStatus.SUCCESS:
                raise RuntimeError("the value lease is no longer valid")
            with self._lock:
                if not self._valid_locked(entry.lib):
                    raise RuntimeError("the value lease is no longer valid")
                native = getattr(entry.lib, function_name)(self._handle)
                view = _borrowed_view(native.data, int(native.length), self, readonly=True)
                return self._track_view(view)

    def release(self, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        with self._store._entered_operation(wait=wait) as entry:
            if entry is None:
                self._invalidate_views()
                return StoreStatus.INVALID_LEASE
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status
            with self._lock:
                drain_status = self._drain_views(entry._wait_budget)
                if drain_status is not StoreStatus.SUCCESS:
                    return drain_status
                if self._handle is None:
                    return StoreStatus.INVALID_LEASE
                native_wait = entry.remaining_native_wait()
                return _store_status(
                    int(
                        entry.lib.sms_release_lease(
                            self._handle,
                            ctypes.byref(native_wait),
                        )
                    )
                )

    def __enter__(self) -> "ValueLease":
        if not self.is_valid:
            raise RuntimeError("the value lease is no longer valid")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def close(self) -> None:
        # Destruction coordinates with mapping recovery, then fails fast if a
        # caller-derived view still exports mapped bytes.
        with self._store._entered_operation(allow_during_close=True) as entry:
            if entry is None:
                self._invalidate_views()
                return
            self._close_from_store_drained(entry.lib)
            self._store._discard_child(self)

    def _close_from_store_drained(self, lib: Any) -> None:
        with self._lock:
            if not self._prepare_close():
                raise BufferError(
                    "cannot close a value lease while a derived memoryview "
                    "is still active"
                )
            if self._handle is not None:
                handle, self._handle = self._handle, None
                lib.sms_destroy_lease(handle)

    def _invalidate_views(self) -> None:
        with self._lock:
            self._release_views()

    def __del__(self) -> None:
        try:
            self.close()
        except BaseException:
            pass


class ValueReservation(_ViewOwner):
    """Owning token for an announced-length, zero-copy writable payload.

    A directly returned writable view is released by the next reservation
    operation. Caller-derived views pin the reservation and mapped bytes until
    released; bounded transitions fail busy/canceled rather than publishing or
    recycling memory that is still writable.
    """

    __slots__ = ("_store", "_handle", "_lock", "__weakref__")

    def __init__(self, store: MemoryStore, handle: ctypes.c_void_p) -> None:
        self._store = store
        self._handle: Optional[ctypes.c_void_p] = handle
        self._lock = threading.RLock()
        self._initialize_views()

    def _valid_locked(self, lib: Any) -> bool:
        if self._handle is None:
            return False
        valid = bool(lib.sms_reservation_is_valid(self._handle))
        if not valid:
            self._release_views()
        return valid

    @property
    def is_valid(self) -> bool:
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                return False
            if entry.gate_status is not StoreStatus.SUCCESS:
                return False
            with self._lock:
                return self._valid_locked(entry.lib)

    @property
    def payload_length(self) -> int:
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                return 0
            if entry.gate_status is not StoreStatus.SUCCESS:
                return 0
            with self._lock:
                if not self._valid_locked(entry.lib):
                    return 0
                return int(entry.lib.sms_reservation_payload_length(self._handle))

    @property
    def bytes_written(self) -> int:
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                return 0
            if entry.gate_status is not StoreStatus.SUCCESS:
                return 0
            with self._lock:
                if not self._valid_locked(entry.lib):
                    return 0
                return int(entry.lib.sms_reservation_bytes_written(self._handle))

    @property
    def remaining_bytes(self) -> int:
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                return 0
            if entry.gate_status is not StoreStatus.SUCCESS:
                return 0
            with self._lock:
                if not self._valid_locked(entry.lib):
                    return 0
                return max(0, int(entry.lib.sms_reservation_remaining_bytes(self._handle)))

    def buffer(self, size_hint: int = 0) -> memoryview:
        size_hint = _require_integer(size_hint, "size_hint", _INT32_MIN, _INT32_MAX)
        with self._store._entered_operation(wait=WaitOptions.NO_WAIT) as entry:
            if entry is None:
                self._invalidate_views()
                raise RuntimeError("the value reservation is no longer valid")
            if entry.gate_status is not StoreStatus.SUCCESS:
                raise RuntimeError("the value reservation is no longer valid")
            with self._lock:
                drain_status = self._drain_views(entry._wait_budget)
                if drain_status is not StoreStatus.SUCCESS:
                    raise BufferError(
                        "a derived reservation view is still active"
                    )
                if not self._valid_locked(entry.lib):
                    raise RuntimeError("the value reservation is no longer valid")
                native = entry.lib.sms_reservation_buffer(self._handle, size_hint)
                view = _borrowed_view(native.data, int(native.length), self, readonly=False)
                return self._track_view(view)

    @property
    def view(self) -> memoryview:
        return self.buffer()

    def advance(self, byte_count: int, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        if isinstance(byte_count, bool) or not isinstance(byte_count, int):
            raise TypeError("byte_count must be an integer")
        if byte_count < _INT32_MIN or byte_count > _INT32_MAX:
            return StoreStatus.RESERVATION_WRITE_OUT_OF_RANGE
        with self._store._entered_operation(wait=wait) as entry:
            if entry is None:
                self._invalidate_views()
                return StoreStatus.INVALID_RESERVATION
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status
            with self._lock:
                drain_status = self._drain_views(entry._wait_budget)
                if drain_status is not StoreStatus.SUCCESS:
                    return drain_status
                if self._handle is None:
                    return StoreStatus.INVALID_RESERVATION
                native_wait = entry.remaining_native_wait()
                return _store_status(
                    int(
                        entry.lib.sms_advance_reservation(
                            self._handle,
                            byte_count,
                            ctypes.byref(native_wait),
                        )
                    )
                )

    def commit(self, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        return self._complete("sms_commit_reservation", wait)

    def abort(self, *, wait: WaitOptions = WaitOptions.DEFAULT) -> StoreStatus:
        return self._complete("sms_abort_reservation", wait)

    def _complete(self, function_name: str, wait: WaitOptions) -> StoreStatus:
        with self._store._entered_operation(wait=wait) as entry:
            if entry is None:
                self._invalidate_views()
                return StoreStatus.INVALID_RESERVATION
            if entry.gate_status is not StoreStatus.SUCCESS:
                return entry.gate_status
            with self._lock:
                drain_status = self._drain_views(entry._wait_budget)
                if drain_status is not StoreStatus.SUCCESS:
                    return drain_status
                if self._handle is None:
                    return StoreStatus.INVALID_RESERVATION
                function = getattr(entry.lib, function_name)
                native_wait = entry.remaining_native_wait()
                return _store_status(
                    int(function(self._handle, ctypes.byref(native_wait)))
                )

    def __enter__(self) -> "ValueReservation":
        if not self.is_valid:
            raise RuntimeError("the value reservation is no longer valid")
        return self

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        self.close()

    def close(self) -> None:
        # See ValueLease.close: coordinate with recovery and fail fast while a
        # caller-derived view still exports mapped bytes.
        with self._store._entered_operation(allow_during_close=True) as entry:
            if entry is None:
                self._invalidate_views()
                return
            self._close_from_store_drained(entry.lib)
            self._store._discard_child(self)

    def _close_from_store_drained(self, lib: Any) -> None:
        with self._lock:
            if not self._prepare_close():
                raise BufferError(
                    "cannot close a value reservation while a derived "
                    "memoryview is still active"
                )
            if self._handle is not None:
                handle, self._handle = self._handle, None
                lib.sms_destroy_reservation(handle)

    def _invalidate_views(self) -> None:
        with self._lock:
            self._release_views()

    def __del__(self) -> None:
        try:
            self.close()
        except BaseException:
            pass


__all__ = [
    "CancellationSource",
    "WaitOptions",
    "StoreOptions",
    "ProtocolInfo",
    "RecoveryReport",
    "DiagnosticsSnapshot",
    "MemoryStore",
    "ValueLease",
    "ValueReservation",
    "calculate_required_bytes",
]
