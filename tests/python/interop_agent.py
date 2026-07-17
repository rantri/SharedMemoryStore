#!/usr/bin/env python3
"""Line-delimited JSON interoperability participant for the Python runtime."""

from __future__ import annotations

import base64
import ctypes
import json
import os
from pathlib import Path
import sys
import threading
import time
from typing import Any, Callable, Optional

from interop_checkpoint_catalog import CHECKPOINTS, CHECKPOINTS_BY_ID
from interop_faults import (
    ColdLock,
    UnsupportedFaultPlatform,
    inject_raw_fault,
    supports_platform_faults,
)

from shared_memory_store import (
    CancellationSource,
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    MemoryStore,
    OpenMode,
    OPTIONAL_FEATURES,
    REQUIRED_FEATURES,
    RESOURCE_PROTOCOL_VERSION,
    StoreOpenStatus,
    StoreOptions,
    StoreStatus,
    ValueLease,
    ValueReservation,
    WaitOptions,
)
from shared_memory_store import _native


AGENT_PROTOCOL_VERSION = 2
CHECKPOINT_CATALOG_VERSION = 1
ABRUPT_EXIT_CODE = 97
FNV1A64_OFFSET_BASIS = 14_695_981_039_346_656_037
FNV1A64_PRIME = 1_099_511_628_211
UINT64_MASK = (1 << 64) - 1


class _CheckpointHooks:
    """Private ctypes bridge into the test-only instrumented native runtime."""

    _Callback = ctypes.CFUNCTYPE(None, ctypes.c_int32, ctypes.c_void_p)

    def __init__(self, library: ctypes.CDLL) -> None:
        self.library = library
        self.library.sms_test_checkpoint_bridge_version.argtypes = []
        self.library.sms_test_checkpoint_bridge_version.restype = ctypes.c_uint32
        self.library.sms_test_set_thread_checkpoint_callback.argtypes = [
            self._Callback,
            ctypes.c_void_p,
        ]
        self.library.sms_test_set_thread_checkpoint_callback.restype = None
        if int(self.library.sms_test_checkpoint_bridge_version()) != 1:
            raise RuntimeError("unsupported Python checkpoint bridge version")

    def set_thread_callback(self, callback: Optional[Callable[[int], None]]) -> Any:
        if callback is None:
            self.library.sms_test_set_thread_checkpoint_callback(
                self._Callback(),
                None,
            )
            return None

        native_callback = self._Callback(
            lambda checkpoint_id, context: callback(int(checkpoint_id))
        )
        self.library.sms_test_set_thread_checkpoint_callback(
            native_callback,
            None,
        )
        return native_callback


def _load_checkpoint_hooks(arguments: list[str]) -> Optional[_CheckpointHooks]:
    if "--checkpoint-library" not in arguments:
        return None
    index = arguments.index("--checkpoint-library")
    if index + 1 >= len(arguments) or index + 2 != len(arguments):
        raise ValueError("--checkpoint-library requires exactly one final path")
    path = Path(arguments[index + 1]).resolve(strict=True)
    candidate = ctypes.CDLL(str(path))
    _native._configure_signatures(candidate)
    _native._verify_contract(candidate, path)
    hooks = _CheckpointHooks(candidate)
    # The worker and ordinary agent commands must use this exact private test
    # runtime. Production package loading remains unchanged when the command-
    # line switch is absent.
    _native._LIBRARY = candidate
    _native._LIBRARY_PATH = path
    return hooks


class _CheckpointOperation:
    """Run one real native operation while the JSON request loop stays live."""

    def __init__(
        self,
        hooks: _CheckpointHooks,
        checkpoint_id: int,
        occurrence: int,
        operation: str,
        options: StoreOptions,
        key: bytes,
        value: bytes,
        descriptor: bytes,
        crash: bool,
    ) -> None:
        self.hooks = hooks
        self.checkpoint_id = checkpoint_id
        self.occurrence = occurrence
        self.operation = operation
        self.options = options
        self.key = key
        self.value = value
        self.descriptor = descriptor
        self.crash = crash
        self.paused = threading.Event()
        self.resume = threading.Event()
        self.completed = threading.Event()
        self.cancellation = CancellationSource()
        self.reached: Optional[int] = None
        self.status = StoreStatus.UNKNOWN_FAILURE
        self.open_status = StoreOpenStatus.MAPPING_FAILED
        self.error: Optional[BaseException] = None
        self._observed_occurrences = 0
        self._callback: Any = None
        self._thread = threading.Thread(
            target=self._run,
            name="sms-python-checkpoint",
            daemon=True,
        )
        self._thread.start()

    def wait_until_paused(self, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.paused.wait(0.01):
                return True
            if self.completed.is_set():
                return False
        return self.paused.is_set()

    def complete(self, cancel: bool, timeout: float = 10.0) -> tuple[StoreStatus, StoreOpenStatus]:
        if cancel:
            self.cancellation.signal()
        self.resume.set()
        if not self.completed.wait(timeout):
            return StoreStatus.STORE_BUSY, self.open_status
        if self.error is not None:
            return StoreStatus.UNKNOWN_FAILURE, StoreOpenStatus.MAPPING_FAILED
        return self.status, self.open_status

    def _observe(self, checkpoint_id: int) -> None:
        if checkpoint_id != self.checkpoint_id:
            return
        self._observed_occurrences += 1
        if self._observed_occurrences != self.occurrence:
            return
        self.reached = checkpoint_id
        self.paused.set()
        if self.crash:
            os._exit(ABRUPT_EXIT_CODE)
        self.resume.wait()

    def _run(self) -> None:
        store: Optional[MemoryStore] = None
        try:
            self._callback = self.hooks.set_thread_callback(self._observe)
            wait = WaitOptions.infinite(self.cancellation)
            self.open_status, store = MemoryStore.open(self.options, wait=wait)
            if self.open_status is not StoreOpenStatus.SUCCESS or store is None:
                self.status = StoreStatus.UNKNOWN_FAILURE
                return
            self.status = self._execute(store, wait)
        except BaseException as error:
            self.error = error
            self.status = StoreStatus.UNKNOWN_FAILURE
            self.open_status = StoreOpenStatus.MAPPING_FAILED
        finally:
            if store is not None:
                try:
                    store.close()
                except BaseException as error:
                    if self.error is None:
                        self.error = error
            try:
                self.hooks.set_thread_callback(None)
            finally:
                self._callback = None
                self.cancellation.close()
                self.completed.set()

    def _execute(self, store: MemoryStore, wait: WaitOptions) -> StoreStatus:
        if self.operation == "noop":
            return StoreStatus.SUCCESS
        if self.operation == "publish":
            return store.publish(self.key, self.value, self.descriptor, wait=wait)
        if self.operation in {"reserve", "abort"}:
            status, reservation = store.reserve(
                self.key,
                len(self.value),
                self.descriptor,
                wait=wait,
            )
            if status is not StoreStatus.SUCCESS or reservation is None:
                return status
            return reservation.abort(wait=wait)
        if self.operation == "commit":
            status, reservation = store.reserve(
                self.key,
                len(self.value),
                self.descriptor,
                wait=wait,
            )
            if status is not StoreStatus.SUCCESS or reservation is None:
                return status
            if self.value:
                projected = reservation.buffer(len(self.value))
                projected[:] = self.value
            status = reservation.advance(len(self.value), wait=wait)
            return reservation.commit(wait=wait) if status is StoreStatus.SUCCESS else status
        if self.operation in {"acquire", "release"}:
            status, lease = store.acquire(self.key, wait=wait)
            if status is not StoreStatus.SUCCESS or lease is None:
                return status
            # Drive the genuine projection validation boundaries too.
            _ = lease.descriptor
            _ = lease.value
            return lease.release(wait=wait)
        if self.operation == "remove":
            return store.remove(self.key, wait=wait)
        if self.operation == "diagnostics":
            status, _ = store.diagnostics(wait=wait)
            return status
        if self.operation == "recoverLeases":
            status, _ = store.acquire(self.key, wait=wait)
            if status is not StoreStatus.SUCCESS:
                return status
            status, _ = store.recover_leases(True, wait=wait)
            return status
        if self.operation == "recoverReservations":
            status, _ = store.reserve(
                self.key,
                len(self.value),
                self.descriptor,
                wait=wait,
            )
            if status is not StoreStatus.SUCCESS:
                return status
            status, _ = store.recover_reservations(True, wait=wait)
            return status
        return StoreStatus.UNKNOWN_FAILURE


class AgentFailure(Exception):
    """A deterministic non-success response in the interop protocol."""

    def __init__(
        self,
        status_code: int,
        status_name: str,
        error_code: str,
        message: str,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.status_name = status_name
        self.error_code = error_code


def _protocol_identity(value: Any = None) -> dict[str, int]:
    """Project a public store identity into the canonical agent vocabulary."""

    if value is None:
        return {
            "layoutMajorVersion": LAYOUT_MAJOR_VERSION,
            "layoutMinorVersion": LAYOUT_MINOR_VERSION,
            "resourceProtocolVersion": RESOURCE_PROTOCOL_VERSION,
            "requiredFeatures": REQUIRED_FEATURES,
            "optionalFeatures": OPTIONAL_FEATURES,
        }
    return {
        "layoutMajorVersion": value.layout_major_version,
        "layoutMinorVersion": value.layout_minor_version,
        "resourceProtocolVersion": value.resource_protocol_version,
        "requiredFeatures": value.required_features,
        "optionalFeatures": value.optional_features,
    }


def _symbolic_name(value: Any) -> str:
    return "".join(part.title() for part in value.name.split("_"))


def _bytes(arguments: dict[str, Any], name: str, default: Optional[bytes] = None) -> bytes:
    value = arguments.get(name)
    if value is None and default is not None:
        return default
    if not isinstance(value, str):
        raise ValueError(f"argument {name!r} must be a base64 string")
    return base64.b64decode(value, validate=True)


def _encoded(value: bytes) -> str:
    return base64.b64encode(value).decode("ascii")


def _fnv1a64(value: memoryview) -> str:
    """Return the canonical portable FNV-1a 64-bit lowercase hex digest."""

    checksum = FNV1A64_OFFSET_BASIS
    for item in value.cast("B"):
        checksum = ((checksum ^ item) * FNV1A64_PRIME) & UINT64_MASK
    return f"{checksum:016x}"


def _wait(arguments: dict[str, Any]) -> WaitOptions:
    timeout = arguments.get("timeoutMs", arguments.get("timeoutMilliseconds", 1000))
    return WaitOptions(timeout)


class Agent:
    def __init__(self, checkpoint_hooks: Optional[_CheckpointHooks] = None) -> None:
        self.stores: dict[str, MemoryStore] = {}
        self.store_options: dict[str, StoreOptions] = {}
        self.leases: dict[str, ValueLease] = {}
        self.reservations: dict[str, ValueReservation] = {}
        self.cold_lock: Optional[ColdLock] = None
        self.checkpoint_hooks = checkpoint_hooks
        self.checkpoint_operation: Optional[_CheckpointOperation] = None

    def close(self) -> None:
        checkpoint, self.checkpoint_operation = self.checkpoint_operation, None
        if checkpoint is not None:
            checkpoint.complete(cancel=True, timeout=2.0)
        cold_lock, self.cold_lock = self.cold_lock, None
        if cold_lock is not None:
            cold_lock.close()
        for lease in list(self.leases.values()):
            lease.close()
        for reservation in list(self.reservations.values()):
            reservation.close()
        for store in list(self.stores.values()):
            store.close()
        self.leases.clear()
        self.reservations.clear()
        self.stores.clear()
        self.store_options.clear()

    def handle(self, request: dict[str, Any]) -> dict[str, Any]:
        request_id = request.get("id")
        command = request.get("command")
        arguments = request.get("arguments") or {}
        if not isinstance(request_id, str) or not request_id.strip():
            raise ValueError("the request id is required")
        if not isinstance(command, str) or not command.strip():
            raise ValueError("the request command is required")
        if not isinstance(arguments, dict):
            raise ValueError("request arguments must be an object")

        method = getattr(self, f"command_{command}", None)
        if method is None:
            return {
                "id": request_id,
                "ok": False,
                "status": {"code": -2, "name": "UnsupportedCommand"},
                "error": {
                    "code": "unsupported_command",
                    "message": f"The command {command!r} is not implemented by this agent.",
                },
            }
        try:
            status, result = method(arguments)
        except AgentFailure as error:
            return {
                "id": request_id,
                "ok": False,
                "status": {"code": error.status_code, "name": error.status_name},
                "error": {"code": error.error_code, "message": str(error)},
            }
        response: dict[str, Any] = {
            "id": request_id,
            "ok": True,
            "status": {"code": int(status), "name": _symbolic_name(status)},
        }
        if result is not None:
            response["result"] = result
        return response

    def _store(self, arguments: dict[str, Any]) -> MemoryStore:
        store_id = arguments.get("storeId")
        if not isinstance(store_id, str) or store_id not in self.stores:
            raise ValueError(f"unknown storeId: {store_id!r}")
        return self.stores[store_id]

    def _lease(self, arguments: dict[str, Any]) -> ValueLease:
        lease_id = arguments.get("leaseId")
        if not isinstance(lease_id, str) or lease_id not in self.leases:
            raise ValueError(f"unknown leaseId: {lease_id!r}")
        return self.leases[lease_id]

    def _reservation(self, arguments: dict[str, Any]) -> ValueReservation:
        reservation_id = arguments.get("reservationId")
        if not isinstance(reservation_id, str) or reservation_id not in self.reservations:
            raise ValueError(f"unknown reservationId: {reservation_id!r}")
        return self.reservations[reservation_id]

    @staticmethod
    def _reservation_result(
        reservation_id: str,
        reservation: ValueReservation,
        written: int = 0,
    ) -> dict[str, Any]:
        return {
            "reservationId": reservation_id,
            "payloadLength": reservation.payload_length,
            "bytesWritten": reservation.bytes_written,
            "remainingBytes": reservation.remaining_bytes,
            "written": written,
            "bytesCopied": written,
            "valid": reservation.is_valid,
        }

    def command_ping(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        del arguments
        return StoreStatus.SUCCESS, {
            "runtime": "python",
            "protocolVersion": AGENT_PROTOCOL_VERSION,
            "checkpointCatalogVersion": CHECKPOINT_CATALOG_VERSION,
            **_protocol_identity(),
        }

    def command_open(self, arguments: dict[str, Any]) -> tuple[StoreOpenStatus, Optional[dict[str, Any]]]:
        try:
            store_id = arguments["storeId"]
            if not isinstance(store_id, str) or not store_id:
                return StoreOpenStatus.INVALID_OPTIONS, None
        except (KeyError, TypeError, ValueError):
            return StoreOpenStatus.INVALID_OPTIONS, None

        prior = self.stores.pop(store_id, None)
        self.store_options.pop(store_id, None)
        if prior is not None:
            prior.close()

        try:
            fields = {
                "name": arguments["name"],
                "slot_count": arguments["slotCount"],
                "max_value_bytes": arguments["maxValueBytes"],
                "max_descriptor_bytes": arguments["maxDescriptorBytes"],
                "max_key_bytes": arguments["maxKeyBytes"],
                "lease_record_count": arguments["leaseRecordCount"],
                "participant_record_count": arguments["participantRecordCount"],
                "open_mode": OpenMode(arguments.get("openMode", int(OpenMode.CREATE_OR_OPEN))),
                "enable_lease_recovery": arguments.get("enableLeaseRecovery", False),
            }
            if "totalBytes" in arguments and arguments["totalBytes"] is not None:
                options = StoreOptions(total_bytes=arguments["totalBytes"], **fields)
            else:
                options = StoreOptions.create(**fields)
        except (KeyError, TypeError, ValueError):
            return StoreOpenStatus.INVALID_OPTIONS, None
        status, store = MemoryStore.open(options, wait=_wait(arguments))
        if status is StoreOpenStatus.SUCCESS:
            assert store is not None
            self.stores[store_id] = store
            self.store_options[store_id] = options
            return status, {
                "storeId": store_id,
                "participantRecordCount": options.participant_record_count,
                "protocolInfo": _protocol_identity(store.protocol_info),
            }
        return status, None

    def command_close(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        store_id = arguments.get("storeId")
        store = self.stores.pop(store_id, None) if isinstance(store_id, str) else None
        if isinstance(store_id, str):
            self.store_options.pop(store_id, None)
        if store is not None:
            store.close()
        return StoreStatus.SUCCESS, {"storeId": store_id, "closed": True}

    def command_publish(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        status = self._store(arguments).publish(
            _bytes(arguments, "key"),
            _bytes(arguments, "value"),
            _bytes(arguments, "descriptor", b""),
            wait=_wait(arguments),
        )
        return status, None

    def command_publishSegments(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, int]]:
        encoded_segments = arguments.get("segments")
        if not isinstance(encoded_segments, list):
            raise ValueError("segments must be an array")
        segments = [base64.b64decode(value, validate=True) for value in encoded_segments]
        status, copied = self._store(arguments).publish_segments(
            _bytes(arguments, "key"),
            segments,
            _bytes(arguments, "descriptor", b""),
            wait=_wait(arguments),
        )
        return status, {"copiedBytes": copied}

    def command_acquire(self, arguments: dict[str, Any]) -> tuple[StoreStatus, Optional[dict[str, str]]]:
        status, lease = self._store(arguments).acquire(_bytes(arguments, "key"), wait=_wait(arguments))
        if status is not StoreStatus.SUCCESS:
            return status, None
        lease_id = arguments.get("leaseId")
        if not isinstance(lease_id, str) or not lease_id:
            assert lease is not None
            lease.close()
            return StoreStatus.INVALID_LEASE, None
        prior = self.leases.pop(lease_id, None)
        if prior is not None:
            prior.close()
        assert lease is not None
        self.leases[lease_id] = lease
        return status, {
            "leaseId": lease_id,
            "value": _encoded(bytes(lease.value)),
            "descriptor": _encoded(bytes(lease.descriptor)),
        }

    def command_read(self, arguments: dict[str, Any]) -> tuple[StoreStatus, Optional[dict[str, str]]]:
        lease = self._lease(arguments)
        if not lease.is_valid:
            return StoreStatus.INVALID_LEASE, None
        return StoreStatus.SUCCESS, {
            "value": _encoded(bytes(lease.value)),
            "descriptor": _encoded(bytes(lease.descriptor)),
        }

    def command_checksum(self, arguments: dict[str, Any]) -> tuple[StoreStatus, Optional[dict[str, Any]]]:
        lease_id = arguments.get("leaseId")
        lease = self.leases.get(lease_id) if isinstance(lease_id, str) else None
        if lease is None or not lease.is_valid:
            return StoreStatus.INVALID_LEASE, None
        value = lease.value
        descriptor = lease.descriptor
        return StoreStatus.SUCCESS, {
            "leaseId": lease_id,
            "valueLength": len(value),
            "descriptorLength": len(descriptor),
            "valueChecksum": _fnv1a64(value),
            "descriptorChecksum": _fnv1a64(descriptor),
        }

    def command_release(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        lease_id = arguments.get("leaseId")
        lease = self.leases.get(lease_id) if isinstance(lease_id, str) else None
        if lease is None:
            return StoreStatus.INVALID_LEASE, {"leaseId": lease_id, "valid": False}
        status = lease.release(wait=_wait(arguments))
        return status, {"leaseId": lease_id, "valid": lease.is_valid}

    def command_remove(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        return self._store(arguments).remove(_bytes(arguments, "key"), wait=_wait(arguments)), None

    def command_reserve(self, arguments: dict[str, Any]) -> tuple[StoreStatus, Optional[dict[str, Any]]]:
        status, reservation = self._store(arguments).reserve(
            _bytes(arguments, "key"),
            arguments.get("payloadLength"),
            _bytes(arguments, "descriptor", b""),
            wait=_wait(arguments),
        )
        if status is not StoreStatus.SUCCESS:
            return status, None
        reservation_id = arguments.get("reservationId")
        if not isinstance(reservation_id, str) or not reservation_id:
            assert reservation is not None
            reservation.close()
            return StoreStatus.INVALID_RESERVATION, None
        prior = self.reservations.pop(reservation_id, None)
        if prior is not None:
            prior.close()
        assert reservation is not None
        self.reservations[reservation_id] = reservation
        return status, self._reservation_result(reservation_id, reservation)

    def command_reservationWrite(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        reservation_id = arguments.get("reservationId")
        reservation = (
            self.reservations.get(reservation_id)
            if isinstance(reservation_id, str)
            else None
        )
        if reservation is None:
            return StoreStatus.INVALID_RESERVATION, {
                "reservationId": reservation_id,
                "written": 0,
                "bytesCopied": 0,
                "valid": False,
            }
        data = _bytes(arguments, "data")
        view = reservation.buffer(len(data))
        try:
            if len(view) < len(data):
                return (
                    StoreStatus.RESERVATION_WRITE_OUT_OF_RANGE,
                    self._reservation_result(reservation_id, reservation),
                )
            view[: len(data)] = data
            return StoreStatus.SUCCESS, self._reservation_result(
                reservation_id,
                reservation,
                len(data),
            )
        finally:
            view.release()

    def command_advance(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        reservation_id = arguments.get("reservationId")
        reservation = self._reservation(arguments)
        status = reservation.advance(arguments.get("byteCount"), wait=_wait(arguments))
        return status, self._reservation_result(reservation_id, reservation)

    def command_commit(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        reservation_id = arguments.get("reservationId")
        reservation = self._reservation(arguments)
        status = reservation.commit(wait=_wait(arguments))
        return status, self._reservation_result(reservation_id, reservation)

    def command_abort(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        reservation_id = arguments.get("reservationId")
        reservation = self._reservation(arguments)
        status = reservation.abort(wait=_wait(arguments))
        return status, self._reservation_result(reservation_id, reservation)

    def command_recoverLeases(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        status, report = self._store(arguments).recover_leases(
            arguments.get("recoverCurrentProcess", False), wait=_wait(arguments)
        )
        return status, _report(report, reservations=False, store_id=arguments.get("storeId"))

    def command_recoverReservations(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        status, report = self._store(arguments).recover_reservations(
            arguments.get("recoverCurrentProcess", False), wait=_wait(arguments)
        )
        return status, _report(report, reservations=True, store_id=arguments.get("storeId"))

    def command_diagnostics(self, arguments: dict[str, Any]) -> tuple[StoreStatus, Optional[dict[str, Any]]]:
        status, snapshot = self._store(arguments).diagnostics(wait=_wait(arguments))
        if snapshot is None:
            return status, None
        return status, {
            "storeId": arguments.get("storeId"),
            "protocolInfo": _protocol_identity(snapshot.protocol_info),
            "totalBytes": snapshot.total_bytes,
            "slotCount": snapshot.slot_count,
            "freeSlotCount": snapshot.free_slot_count,
            "initializingSlotCount": snapshot.initializing_slot_count,
            "reservedSlotCount": snapshot.reserved_slot_count,
            "publishedSlotCount": snapshot.published_slot_count,
            "pendingRemovalCount": snapshot.pending_removal_count,
            "reclaimingSlotCount": snapshot.reclaiming_slot_count,
            "retiredSlotCount": snapshot.retired_slot_count,
            "activeReservationCount": snapshot.active_reservation_count,
            "activeLeaseCount": snapshot.active_lease_count,
            "claimingLeaseCount": snapshot.claiming_lease_count,
            "recoveringLeaseCount": snapshot.recovering_lease_count,
            "freeLeaseCount": snapshot.free_lease_count,
            "retiredLeaseCount": snapshot.retired_lease_count,
            "participantRecordCount": snapshot.participant_record_count,
            "freeParticipantCount": snapshot.free_participant_count,
            "registeringParticipantCount": snapshot.registering_participant_count,
            "activeParticipantCount": snapshot.active_participant_count,
            "closingParticipantCount": snapshot.closing_participant_count,
            "recoveringParticipantCount": snapshot.recovering_participant_count,
            "reclaimingParticipantCount": snapshot.reclaiming_participant_count,
            "retiredParticipantCount": snapshot.retired_participant_count,
            "indexEntryCount": snapshot.index_entry_count,
            "occupiedIndexEntryCount": snapshot.occupied_index_entry_count,
            "emptyIndexEntryCount": snapshot.empty_index_entry_count,
            "usableIndexCapacity": snapshot.usable_index_capacity,
            "primaryDirectoryOccupancy": snapshot.primary_directory_occupancy,
            "spilledBucketCount": snapshot.spilled_bucket_count,
            "overflowDirectoryOccupancy": snapshot.overflow_directory_occupancy,
            "lastObservedProbeLength": snapshot.last_observed_probe_length,
            "maxObservedProbeLength": snapshot.max_observed_probe_length,
            "maxObservedOverflowScanLength": snapshot.max_observed_overflow_scan_length,
            "lastFailureStatus": int(snapshot.last_failure_status),
            "abortedReservationCount": snapshot.aborted_reservation_count,
            "recoveredLeaseCount": snapshot.recovered_lease_count,
            "activeLeaseRecoveryCount": snapshot.active_lease_recovery_count,
            "unsupportedLeaseRecoveryCount": snapshot.unsupported_lease_recovery_count,
            "failedLeaseRecoveryCount": snapshot.failed_lease_recovery_count,
            "recoveredReservationCount": snapshot.recovered_reservation_count,
            "activeReservationRecoveryCount": snapshot.active_reservation_recovery_count,
            "unsupportedReservationRecoveryCount": snapshot.unsupported_reservation_recovery_count,
            "failedReservationRecoveryCount": snapshot.failed_reservation_recovery_count,
            "capacityPressureCount": snapshot.capacity_pressure_count,
            "overflowScanCount": snapshot.overflow_scan_count,
            "casRetryCount": snapshot.cas_retry_count,
            "helpedTransitionCount": snapshot.helped_transition_count,
            "contentionBudgetExhaustionCount": snapshot.contention_budget_exhaustion_count,
            "invalidTokenCount": snapshot.invalid_token_count,
            "staleTokenCount": snapshot.stale_token_count,
            "recoveryAttemptCount": snapshot.recovery_attempt_count,
            "recoveredTransitionCount": snapshot.recovered_transition_count,
            "currentOwnerClassificationCount": snapshot.current_owner_classification_count,
            "liveOwnerClassificationCount": snapshot.live_owner_classification_count,
            "staleOwnerClassificationCount": snapshot.stale_owner_classification_count,
            "unsupportedOwnerClassificationCount": snapshot.unsupported_owner_classification_count,
            "inconsistentOwnerClassificationCount": snapshot.inconsistent_owner_classification_count,
            "changingOwnerClassificationCount": snapshot.changing_owner_classification_count,
            "failureCounts": list(snapshot.failure_counts),
        }

    def command_checkpointCatalog(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        del arguments
        return StoreStatus.SUCCESS, {
            "checkpointCatalogVersion": CHECKPOINT_CATALOG_VERSION,
            "checkpoints": [entry.protocol_result() for entry in CHECKPOINTS],
        }

    @staticmethod
    def _checkpoint_unavailable(
        command: str,
        arguments: dict[str, Any],
        *,
        requires_checkpoint: bool,
    ) -> tuple[StoreStatus, dict[str, Any]]:
        result: dict[str, Any] = {
            "command": command,
            "checkpointCatalogVersion": CHECKPOINT_CATALOG_VERSION,
            "supported": False,
            "reason": "native_checkpoint_hooks_unavailable",
        }
        if requires_checkpoint:
            checkpoint_id = arguments.get("checkpointId")
            if isinstance(checkpoint_id, bool) or not isinstance(checkpoint_id, int):
                raise AgentFailure(
                    -1,
                    "ProtocolError",
                    "invalid_arguments",
                    "argument 'checkpointId' must be an integer",
                )
            checkpoint = CHECKPOINTS_BY_ID.get(checkpoint_id)
            if checkpoint is None:
                raise AgentFailure(
                    -1,
                    "ProtocolError",
                    "invalid_arguments",
                    f"unknown checkpointId: {checkpoint_id}",
                )
            operation = arguments.get("operation")
            if not isinstance(operation, str) or not operation.strip():
                raise AgentFailure(
                    -1,
                    "ProtocolError",
                    "invalid_arguments",
                    "argument 'operation' must be a non-empty string",
                )
            occurrence = arguments.get("occurrence", 1)
            if isinstance(occurrence, bool) or not isinstance(occurrence, int) or occurrence < 1:
                raise AgentFailure(
                    -1,
                    "ProtocolError",
                    "invalid_arguments",
                    "argument 'occurrence' must be an integer greater than zero",
                )
            result.update(
                checkpointId=checkpoint.id,
                checkpointName=checkpoint.name,
                operation=operation,
                occurrence=occurrence,
            )
        return StoreStatus.UNSUPPORTED_PLATFORM, result

    def command_pauseAtCheckpoint(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        return self._begin_checkpoint(arguments, crash=False)

    def command_resumeCheckpoint(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        del arguments
        if self.checkpoint_hooks is None:
            return self._checkpoint_unavailable(
                "resumeCheckpoint",
                {},
                requires_checkpoint=False,
            )
        return self._complete_checkpoint(cancel=False)

    def command_cancelCheckpoint(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        del arguments
        if self.checkpoint_hooks is None:
            return self._checkpoint_unavailable(
                "cancelCheckpoint",
                {},
                requires_checkpoint=False,
            )
        return self._complete_checkpoint(cancel=True)

    def command_crashAtCheckpoint(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        return self._begin_checkpoint(arguments, crash=True)

    @staticmethod
    def _checkpoint_options(arguments: dict[str, Any]) -> StoreOptions:
        fields = {
            "name": arguments["name"],
            "slot_count": arguments["slotCount"],
            "max_value_bytes": arguments["maxValueBytes"],
            "max_descriptor_bytes": arguments["maxDescriptorBytes"],
            "max_key_bytes": arguments["maxKeyBytes"],
            "lease_record_count": arguments["leaseRecordCount"],
            "participant_record_count": arguments["participantRecordCount"],
            "open_mode": OpenMode(arguments.get("openMode", int(OpenMode.OPEN_EXISTING))),
            "enable_lease_recovery": arguments.get("enableLeaseRecovery", False),
        }
        if "totalBytes" in arguments and arguments["totalBytes"] is not None:
            return StoreOptions(total_bytes=arguments["totalBytes"], **fields)
        return StoreOptions.create(**fields)

    def _begin_checkpoint(
        self,
        arguments: dict[str, Any],
        *,
        crash: bool,
    ) -> tuple[StoreStatus, dict[str, Any]]:
        if self.checkpoint_hooks is None:
            return self._checkpoint_unavailable(
                "crashAtCheckpoint" if crash else "pauseAtCheckpoint",
                arguments,
                requires_checkpoint=True,
            )
        if self.checkpoint_operation is not None:
            raise AgentFailure(
                -3,
                "CheckpointAlreadyArmed",
                "checkpoint_already_armed",
                "One Python checkpoint operation is already paused.",
            )

        checkpoint_id = arguments.get("checkpointId")
        if isinstance(checkpoint_id, bool) or not isinstance(checkpoint_id, int):
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                "argument 'checkpointId' must be an integer",
            )
        checkpoint = CHECKPOINTS_BY_ID.get(checkpoint_id)
        if checkpoint is None:
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                f"unknown checkpointId: {checkpoint_id}",
            )
        occurrence = arguments.get("occurrence", 1)
        if isinstance(occurrence, bool) or not isinstance(occurrence, int) or occurrence < 1:
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                "argument 'occurrence' must be an integer greater than zero",
            )
        operation_name = arguments.get("operation")
        if not isinstance(operation_name, str) or not operation_name.strip():
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                "argument 'operation' must be a non-empty string",
            )
        try:
            options = self._checkpoint_options(arguments)
            key = _bytes(arguments, "key", b"")
            value = _bytes(arguments, "value", b"")
            descriptor = _bytes(arguments, "descriptor", b"")
        except (KeyError, TypeError, ValueError) as error:
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                str(error),
            ) from error

        operation = _CheckpointOperation(
            self.checkpoint_hooks,
            checkpoint_id,
            occurrence,
            operation_name,
            options,
            key,
            value,
            descriptor,
            crash,
        )
        self.checkpoint_operation = operation
        if not operation.wait_until_paused(10.0):
            status, open_status = operation.complete(cancel=True)
            self.checkpoint_operation = None
            detail = str(operation.error) if operation.error is not None else "not reached"
            raise AgentFailure(
                -4,
                "CheckpointNotReached",
                "checkpoint_not_reached",
                f"Checkpoint {checkpoint.name} was not reached; "
                f"open={_symbolic_name(open_status)}, "
                f"operation={_symbolic_name(status)}; {detail}.",
            )
        return StoreStatus.SUCCESS, {
            "checkpointId": checkpoint.id,
            "checkpointName": checkpoint.name,
            "family": checkpoint.family,
            "position": checkpoint.position,
            "operation": operation_name,
            "processId": os.getpid(),
        }

    def _complete_checkpoint(
        self,
        *,
        cancel: bool,
    ) -> tuple[StoreStatus, dict[str, Any]]:
        operation, self.checkpoint_operation = self.checkpoint_operation, None
        if operation is None:
            raise AgentFailure(
                -5,
                "CheckpointNotArmed",
                "checkpoint_not_armed",
                "No Python checkpoint operation is currently paused.",
            )
        checkpoint = CHECKPOINTS_BY_ID[operation.reached or operation.checkpoint_id]
        status, open_status = operation.complete(cancel=cancel)
        return status, {
            "checkpoint": {
                "checkpointId": checkpoint.id,
                "checkpointName": checkpoint.name,
                "family": checkpoint.family,
                "position": checkpoint.position,
                "operation": None,
                "processId": os.getpid(),
            },
            "canceled": cancel,
            "openStatus": {
                "code": int(open_status),
                "name": _symbolic_name(open_status),
            },
        }

    def command_injectRawFault(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        store_id = arguments.get("storeId")
        store = self.stores.get(store_id) if isinstance(store_id, str) else None
        options = self.store_options.get(store_id) if isinstance(store_id, str) else None
        if store is None or options is None:
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                f"unknown storeId: {store_id!r}",
            )
        target = arguments.get("target")
        if not isinstance(target, str) or not target:
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                "argument 'target' must be a non-empty string",
            )
        if not supports_platform_faults():
            return StoreStatus.UNSUPPORTED_PLATFORM, {
                "target": target,
                "supported": False,
                "reason": "raw_fault_platform_unavailable",
            }
        try:
            return StoreStatus.SUCCESS, inject_raw_fault(store, options, target, arguments)
        except UnsupportedFaultPlatform as error:
            return StoreStatus.UNSUPPORTED_PLATFORM, {
                "target": target,
                "supported": False,
                "reason": str(error),
            }
        except ValueError as error:
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                str(error),
            ) from error
        except Exception as error:
            raise AgentFailure(
                -9,
                "RawFaultFailed",
                "raw_fault_failed",
                str(error) or type(error).__name__,
            ) from error

    def command_holdColdLock(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        if self.cold_lock is not None:
            raise AgentFailure(
                -6,
                "ColdLockAlreadyHeld",
                "cold_lock_already_held",
                "This agent already holds a cold synchronization resource.",
            )
        name = arguments.get("name")
        if not isinstance(name, str) or not name.strip():
            raise AgentFailure(
                -1,
                "ProtocolError",
                "invalid_arguments",
                "argument 'name' must be a non-empty string",
            )
        try:
            self.cold_lock = ColdLock.acquire(name)
        except UnsupportedFaultPlatform as error:
            return StoreStatus.UNSUPPORTED_PLATFORM, {
                "name": name,
                "supported": False,
                "reason": str(error),
            }
        except Exception as error:
            raise AgentFailure(
                -7,
                "ColdLockFailed",
                "cold_lock_failed",
                str(error) or type(error).__name__,
            ) from error
        return StoreStatus.SUCCESS, {"name": name}

    def command_releaseColdLock(
        self,
        arguments: dict[str, Any],
    ) -> tuple[StoreStatus, dict[str, Any]]:
        del arguments
        cold_lock, self.cold_lock = self.cold_lock, None
        if cold_lock is None:
            raise AgentFailure(
                -8,
                "ColdLockNotHeld",
                "cold_lock_not_held",
                "This agent does not hold a cold synchronization resource.",
            )
        try:
            cold_lock.close()
        except Exception as error:
            raise AgentFailure(
                -7,
                "ColdLockFailed",
                "cold_lock_failed",
                str(error) or type(error).__name__,
            ) from error
        return StoreStatus.SUCCESS, {"released": True}

    def command_crash(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        del arguments
        os._exit(ABRUPT_EXIT_CODE)

    command_publishSegmented = command_publishSegments
    command_segmentedPublish = command_publishSegments
    command_write = command_reservationWrite


def _report(report: Any, *, reservations: bool, store_id: Any) -> dict[str, Any]:
    result: dict[str, Any] = {
        "storeId": store_id,
        "scannedCount": report.scanned_count,
        "recoveredCount": report.recovered_count,
        "activeCount": report.active_count,
        "unsupportedCount": report.unsupported_count,
        "failedCount": report.failed_count,
        "failedRecoveryCount": report.failed_count,
    }
    if reservations:
        result.update(
            scannedReservationCount=report.scanned_count,
            recoveredReservationCount=report.recovered_count,
            activeReservationCount=report.active_count,
            unsupportedReservationCount=report.unsupported_count,
        )
    else:
        result.update(
            scannedRecordCount=report.scanned_count,
            recoveredLeaseCount=report.recovered_count,
            activeLeaseCount=report.active_count,
            unsupportedLeaseCount=report.unsupported_count,
        )
    return result


def main() -> int:
    checkpoint_hooks = _load_checkpoint_hooks(sys.argv[1:])
    agent = Agent(checkpoint_hooks)
    try:
        for line in sys.stdin:
            request_id = ""
            try:
                if "\n" in line[:-1] or "\r" in line:
                    raise ValueError("an agent protocol frame must contain exactly one LF-delimited line")
                request = json.loads(line)
                if not isinstance(request, dict):
                    raise ValueError("an agent protocol frame must be a JSON object")
                request_id = request.get("id") if isinstance(request.get("id"), str) else ""
                response = agent.handle(request)
            except Exception as error:
                response = {
                    "id": request_id,
                    "ok": False,
                    "status": {"code": -1, "name": "ProtocolError"},
                    "error": {"code": "invalid_request", "message": str(error) or type(error).__name__},
                }
            sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
            sys.stdout.flush()
    finally:
        agent.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
