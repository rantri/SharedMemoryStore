#!/usr/bin/env python3
"""Line-delimited JSON interoperability participant for the Python runtime."""

from __future__ import annotations

import base64
import json
import os
import sys
from typing import Any, Optional

from shared_memory_store import (
    MemoryStore,
    OpenMode,
    StoreOpenStatus,
    StoreOptions,
    StoreStatus,
    ValueLease,
    ValueReservation,
    WaitOptions,
)


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


def _wait(arguments: dict[str, Any]) -> WaitOptions:
    timeout = arguments.get("timeoutMs", arguments.get("timeoutMilliseconds", 1000))
    return WaitOptions(timeout)


class Agent:
    def __init__(self) -> None:
        self.stores: dict[str, MemoryStore] = {}
        self.leases: dict[str, ValueLease] = {}
        self.reservations: dict[str, ValueReservation] = {}

    def close(self) -> None:
        for lease in list(self.leases.values()):
            lease.close()
        for reservation in list(self.reservations.values()):
            reservation.close()
        for store in list(self.stores.values()):
            store.close()
        self.leases.clear()
        self.reservations.clear()
        self.stores.clear()

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
        status, result = method(arguments)
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

    def command_ping(self, arguments: dict[str, Any]) -> tuple[StoreStatus, dict[str, Any]]:
        del arguments
        return StoreStatus.SUCCESS, {"runtime": "python", "protocolVersion": 1}

    def command_open(self, arguments: dict[str, Any]) -> tuple[StoreOpenStatus, None]:
        try:
            options = StoreOptions.create(
                arguments["name"],
                slot_count=arguments["slotCount"],
                max_value_bytes=arguments["maxValueBytes"],
                max_descriptor_bytes=arguments["maxDescriptorBytes"],
                max_key_bytes=arguments["maxKeyBytes"],
                lease_record_count=arguments["leaseRecordCount"],
                open_mode=OpenMode(arguments.get("openMode", int(OpenMode.CREATE_OR_OPEN))),
                enable_lease_recovery=arguments.get("enableLeaseRecovery", False),
            )
        except (KeyError, TypeError, ValueError):
            return StoreOpenStatus.INVALID_OPTIONS, None
        status, store = MemoryStore.open(options, wait=_wait(arguments))
        if status is StoreOpenStatus.SUCCESS:
            store_id = arguments.get("storeId")
            if not isinstance(store_id, str) or not store_id:
                assert store is not None
                store.close()
                return StoreOpenStatus.INVALID_OPTIONS, None
            prior = self.stores.pop(store_id, None)
            if prior is not None:
                prior.close()
            assert store is not None
            self.stores[store_id] = store
        return status, None

    def command_close(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        store_id = arguments.get("storeId")
        store = self.stores.pop(store_id, None) if isinstance(store_id, str) else None
        if store is None:
            return StoreStatus.STORE_DISPOSED, None
        store.close()
        return StoreStatus.SUCCESS, None

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
        return status, {"value": _encoded(bytes(lease.value)), "descriptor": _encoded(bytes(lease.descriptor))}

    def command_read(self, arguments: dict[str, Any]) -> tuple[StoreStatus, Optional[dict[str, str]]]:
        lease = self._lease(arguments)
        if not lease.is_valid:
            return StoreStatus.INVALID_LEASE, None
        return StoreStatus.SUCCESS, {
            "value": _encoded(bytes(lease.value)),
            "descriptor": _encoded(bytes(lease.descriptor)),
        }

    def command_release(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        return self._lease(arguments).release(wait=_wait(arguments)), None

    def command_remove(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        return self._store(arguments).remove(_bytes(arguments, "key"), wait=_wait(arguments)), None

    def command_reserve(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
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
        return status, None

    def command_reservationWrite(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        reservation = self._reservation(arguments)
        data = _bytes(arguments, "data")
        view = reservation.buffer(len(data))
        try:
            if len(view) < len(data):
                return StoreStatus.RESERVATION_WRITE_OUT_OF_RANGE, None
            view[: len(data)] = data
            return StoreStatus.SUCCESS, None
        finally:
            view.release()

    def command_advance(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        return self._reservation(arguments).advance(arguments.get("byteCount"), wait=_wait(arguments)), None

    def command_commit(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        return self._reservation(arguments).commit(wait=_wait(arguments)), None

    def command_abort(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        return self._reservation(arguments).abort(wait=_wait(arguments)), None

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
            "totalBytes": snapshot.total_bytes,
            "slotCount": snapshot.slot_count,
            "freeSlotCount": snapshot.free_slot_count,
            "publishedSlotCount": snapshot.published_slot_count,
            "pendingRemovalCount": snapshot.pending_removal_count,
            "activeLeaseCount": snapshot.active_lease_count,
            "activeReservationCount": snapshot.active_reservation_count,
            "indexEntryCount": snapshot.index_entry_count,
            "occupiedIndexEntryCount": snapshot.occupied_index_entry_count,
            "tombstoneIndexEntryCount": snapshot.tombstone_index_entry_count,
            "emptyIndexEntryCount": snapshot.empty_index_entry_count,
            "usableIndexCapacity": snapshot.usable_index_capacity,
            "lastObservedProbeLength": snapshot.last_observed_probe_length,
            "maxObservedProbeLength": snapshot.max_observed_probe_length,
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
            "indexCompactionCount": snapshot.index_compaction_count,
            "failureCounts": list(snapshot.failure_counts),
        }

    def command_crash(self, arguments: dict[str, Any]) -> tuple[StoreStatus, None]:
        del arguments
        os._exit(97)

    command_publishSegmented = command_publishSegments
    command_segmentedPublish = command_publishSegments


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
    agent = Agent()
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
