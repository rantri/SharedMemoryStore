from __future__ import annotations

import base64
import inspect
import json
from pathlib import Path
import re
import subprocess
import sys
import unittest

from interop_agent import Agent

from _support import unique_store_name


PROTOCOL_IDENTITY = {
    "layoutMajorVersion": 2,
    "layoutMinorVersion": 0,
    "resourceProtocolVersion": 2,
    "requiredFeatures": 7,
    "optionalFeatures": 0,
}


def encoded(value: bytes) -> str:
    return base64.b64encode(value).decode("ascii")


class InteropAgentTests(unittest.TestCase):
    def test_agent_executes_binary_value_and_reservation_lifecycles(self) -> None:
        agent = Agent()
        try:
            common = {
                "storeId": "store",
                "name": unique_store_name("agent"),
                "openMode": 0,
                "slotCount": 3,
                "maxValueBytes": 32,
                "maxDescriptorBytes": 8,
                "maxKeyBytes": 8,
                "leaseRecordCount": 4,
                "participantRecordCount": 2,
                "enableLeaseRecovery": True,
            }
            ping = agent.handle({"id": "1", "command": "ping"})
            self.assert_success(ping)
            self.assert_protocol_identity(ping["result"])
            opened = agent.handle({"id": "2", "command": "open", "arguments": common})
            self.assert_success(opened)
            self.assert_open_result(opened, "store", participant_record_count=2)
            self.assert_success(
                agent.handle(
                    {
                        "id": "3",
                        "command": "publish",
                        "arguments": {
                            "storeId": "store",
                            "key": encoded(b"k\x00"),
                            "value": encoded(b"v\x00\xff"),
                            "descriptor": encoded(b"d"),
                        },
                    }
                )
            )
            acquired = agent.handle(
                {
                    "id": "4",
                    "command": "acquire",
                    "arguments": {"storeId": "store", "leaseId": "lease", "key": encoded(b"k\x00")},
                }
            )
            self.assert_success(acquired)
            self.assertEqual(encoded(b"v\x00\xff"), acquired["result"]["value"])
            self.assert_success(
                agent.handle({"id": "5", "command": "release", "arguments": {"leaseId": "lease"}})
            )

            self.assert_success(
                agent.handle(
                    {
                        "id": "6",
                        "command": "reserve",
                        "arguments": {
                            "storeId": "store",
                            "reservationId": "reservation",
                            "key": encoded(b"r"),
                            "payloadLength": 3,
                            "descriptor": "",
                        },
                    }
                )
            )
            self.assert_success(
                agent.handle(
                    {
                        "id": "7",
                        "command": "reservationWrite",
                        "arguments": {"reservationId": "reservation", "data": encoded(b"a\x00b")},
                    }
                )
            )
            self.assert_success(
                agent.handle(
                    {
                        "id": "8",
                        "command": "advance",
                        "arguments": {"reservationId": "reservation", "byteCount": 3},
                    }
                )
            )
            self.assert_success(
                agent.handle({"id": "9", "command": "commit", "arguments": {"reservationId": "reservation"}})
            )
            diagnostics = agent.handle(
                {"id": "10", "command": "diagnostics", "arguments": {"storeId": "store"}}
            )
            self.assert_success(diagnostics)
            self.assertEqual(2, diagnostics["result"]["publishedSlotCount"])
        finally:
            agent.close()

    def test_ping_reports_agent_protocol_two_and_the_fixed_sms2_identity(self) -> None:
        agent = Agent()
        try:
            first = agent.handle({"id": "ping-1", "command": "ping"})
            second = agent.handle({"id": "ping-2", "command": "ping"})
            self.assert_success(first)
            self.assert_success(second)
            self.assert_protocol_identity(first["result"])
            self.assertEqual(first["result"], second["result"])

            first["result"]["layoutMajorVersion"] = 99
            third = agent.handle({"id": "ping-3", "command": "ping"})
            self.assert_protocol_identity(third["result"])
        finally:
            agent.close()

    def test_checkpoint_catalog_exactly_matches_managed_metadata(self) -> None:
        source_path = (
            Path(__file__).resolve().parents[2]
            / "src"
            / "SharedMemoryStore"
            / "LockFree"
            / "LockFreeCheckpoint.cs"
        )
        source = source_path.read_text(encoding="utf-8-sig")
        enum_match = re.search(
            r"internal enum LockFreeCheckpointId\s*\{(?P<body>.*?)\}",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(enum_match)
        enum_ids = {
            name: int(identifier)
            for name, identifier in re.findall(
                r"^\s*(\w+)\s*=\s*(\d+)\s*,?\s*$",
                enum_match.group("body"),  # type: ignore[union-attr]
                re.MULTILINE,
            )
        }
        catalog_pattern = re.compile(
            r"\b(?P<position>Before|After)\(\s*"
            r"LockFreeCheckpointId\.(?P<name>\w+)\s*,\s*"
            r"LockFreeCheckpointFamily\.(?P<family>\w+)\s*,\s*"
            r"LockFreePauseClassification\.(?P<pause>\w+)\s*,\s*"
            r"LockFreeCrashClassification\.(?P<crash>\w+)\s*,\s*"
            r"LockFreeRaceClassification\.(?P<race>\w+)\s*,\s*"
            r"(?:orderingPoint:\s*(?P<ordering>true|false)\s*,\s*)?"
            r'"(?P<description>[^"\r\n]+)"\s*\)',
            re.DOTALL,
        )
        expected = []
        for match in catalog_pattern.finditer(source):
            name = match.group("name")
            expected.append({
                "id": enum_ids[name],
                "name": name,
                "family": match.group("family"),
                "position": match.group("position"),
                "pause": match.group("pause"),
                "crash": match.group("crash"),
                "race": match.group("race"),
                "isPublicOrderingPoint": match.group("ordering") == "true",
                "description": match.group("description"),
            })

        self.assertEqual(67, len(expected))
        self.assertEqual(list(range(1, 68)), [entry["id"] for entry in expected])
        agent = Agent()
        try:
            first = agent.handle({"id": "catalog-1", "command": "checkpointCatalog"})
            self.assert_success(first)
            self.assertEqual(1, first["result"]["checkpointCatalogVersion"])
            self.assertEqual(expected, first["result"]["checkpoints"])

            first["result"]["checkpoints"][0]["name"] = "mutated"
            second = agent.handle({"id": "catalog-2", "command": "checkpointCatalog"})
            self.assertEqual(expected, second["result"]["checkpoints"])
        finally:
            agent.close()

    def test_named_checkpoint_controls_fail_closed_without_native_hooks(self) -> None:
        agent = Agent()
        try:
            for command in ("pauseAtCheckpoint", "crashAtCheckpoint"):
                response = agent.handle({
                    "id": command,
                    "command": command,
                    "arguments": {
                        "checkpointId": 1,
                        "occurrence": 1,
                        "operation": "publish",
                    },
                })
                self.assertTrue(response["ok"])
                self.assertEqual(
                    {"code": 11, "name": "UnsupportedPlatform"},
                    response["status"],
                )
                self.assertEqual(False, response["result"]["supported"])
                self.assertEqual(
                    "native_checkpoint_hooks_unavailable",
                    response["result"]["reason"],
                )
                self.assertEqual(1, response["result"]["checkpointId"])
                self.assertEqual(
                    "PublishBeforeSlotClaim",
                    response["result"]["checkpointName"],
                )

            for command in ("resumeCheckpoint", "cancelCheckpoint"):
                response = agent.handle({"id": command, "command": command})
                self.assertEqual(
                    {"code": 11, "name": "UnsupportedPlatform"},
                    response["status"],
                )
                self.assertFalse(response["result"]["supported"])

            invalid = agent.handle({
                "id": "invalid-checkpoint",
                "command": "pauseAtCheckpoint",
                "arguments": {"checkpointId": 68, "operation": "publish"},
            })
            self.assertFalse(invalid["ok"])
            self.assertEqual(
                {"code": -1, "name": "ProtocolError"},
                invalid["status"],
            )
            self.assertEqual("invalid_arguments", invalid["error"]["code"])
            self.assert_success(agent.handle({"id": "still-alive", "command": "ping"}))
        finally:
            agent.close()

    def test_raw_directory_fault_is_a_real_persistent_sms2_mutation(self) -> None:
        if sys.platform not in {"linux", "win32"}:
            self.skipTest("raw SMS2 mapping fault injection supports Windows and Linux")
        agent = Agent()
        try:
            name = unique_store_name("python-raw-fault")
            self.assert_success(agent.handle(
                self.open_request("open", "store", name, participants=2, open_mode=0)
            ))
            arguments = {"storeId": "store", "target": "directoryMutation"}
            first = agent.handle({
                "id": "fault-1",
                "command": "injectRawFault",
                "arguments": arguments,
            })
            self.assert_success(first)
            expected_malformed = (1 << 31) | 3
            self.assertEqual("directoryMutation", first["result"]["target"])
            self.assertEqual(expected_malformed, first["result"]["replacementRaw"])

            second = agent.handle({
                "id": "fault-2",
                "command": "injectRawFault",
                "arguments": arguments,
            })
            self.assert_success(second)
            self.assertEqual(expected_malformed, second["result"]["originalRaw"])

            diagnostics = agent.handle({
                "id": "diagnostics",
                "command": "diagnostics",
                "arguments": {"storeId": "store"},
            })
            self.assertEqual({"code": 13, "name": "CorruptStore"}, diagnostics["status"])
        finally:
            agent.close()

    def test_raw_identity_faults_make_new_open_reject_the_mapping(self) -> None:
        if sys.platform not in {"linux", "win32"}:
            self.skipTest("raw SMS2 mapping fault injection supports Windows and Linux")
        cases = (
            (
                "layoutMajorVersion",
                {"replacementLayoutMajorVersion": 1},
                2,
                1,
            ),
            (
                "requiredFeatures",
                {"replacementRequiredFeatures": 7 | (1 << 63)},
                7,
                -(1 << 63) + 7,
            ),
        )
        for target, replacement_arguments, original, replacement in cases:
            with self.subTest(target=target):
                agent = Agent()
                try:
                    name = unique_store_name(f"python-raw-{target}")
                    self.assert_success(agent.handle(
                        self.open_request("open", "injector", name, participants=3, open_mode=0)
                    ))
                    response = agent.handle({
                        "id": "fault",
                        "command": "injectRawFault",
                        "arguments": {
                            "storeId": "injector",
                            "target": target,
                            **replacement_arguments,
                        },
                    })
                    self.assert_success(response)
                    self.assertEqual(original, response["result"]["originalRaw"])
                    self.assertEqual(replacement, response["result"]["replacementRaw"])

                    rejected = agent.handle(
                        self.open_request("reopen", "newcomer", name, participants=3, open_mode=1)
                    )
                    self.assertEqual(
                        {"code": 4, "name": "IncompatibleLayout"},
                        rejected["status"],
                    )
                finally:
                    agent.close()

    def test_real_cold_lock_blocks_cold_open_but_not_hot_publish(self) -> None:
        if sys.platform not in {"linux", "win32"}:
            self.skipTest("cold-lock fault injection supports Windows and Linux")
        agent = Agent()
        child: subprocess.Popen[str] | None = None
        try:
            name = unique_store_name("python-cold-lock")
            open_arguments = self.open_request(
                "open",
                "store",
                name,
                participants=4,
                open_mode=0,
            )["arguments"]
            self.assert_success(agent.handle({
                "id": "open",
                "command": "open",
                "arguments": open_arguments,
            }))
            held = agent.handle({
                "id": "hold",
                "command": "holdColdLock",
                "arguments": {"name": name},
            })
            self.assert_success(held)
            repeated = agent.handle({
                "id": "hold-again",
                "command": "holdColdLock",
                "arguments": {"name": name},
            })
            self.assertFalse(repeated["ok"])
            self.assertEqual(
                {"code": -6, "name": "ColdLockAlreadyHeld"},
                repeated["status"],
            )

            self.assert_success(agent.handle({
                "id": "hot-publish",
                "command": "publish",
                "arguments": {
                    "storeId": "store",
                    "key": encoded(b"hot"),
                    "value": encoded(b"value"),
                    "descriptor": "",
                    "timeoutMs": 0,
                },
            }))

            child = self.start_fault_agent()
            child_open = dict(open_arguments, storeId="child", openMode=1, timeoutMs=0)
            blocked = self.send_child(child, "blocked-open", "open", child_open)
            self.assertEqual({"code": 9, "name": "StoreBusy"}, blocked["status"])

            released = agent.handle({"id": "release-lock", "command": "releaseColdLock"})
            self.assert_success(released)
            self.assertEqual({"released": True}, released["result"])
            missing = agent.handle({"id": "release-again", "command": "releaseColdLock"})
            self.assertFalse(missing["ok"])
            self.assertEqual(
                {"code": -8, "name": "ColdLockNotHeld"},
                missing["status"],
            )

            child_open["timeoutMs"] = 1000
            self.assert_success(self.send_child(child, "open-after-release", "open", child_open))
            self.assert_success(self.send_child(
                child,
                "close-child",
                "close",
                {"storeId": "child"},
            ))
        finally:
            if child is not None:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=5)
                for stream in (child.stdin, child.stdout, child.stderr):
                    if stream is not None:
                        stream.close()
            agent.close()

    def test_exact_lifecycle_status_commands_cover_segments_hold_remove_and_reuse(self) -> None:
        agent = Agent()
        try:
            opened = agent.handle(
                self.open_request(
                    "open",
                    "store",
                    unique_store_name("agent-exact-status"),
                    participants=2,
                    open_mode=0,
                )
            )
            self.assert_success(opened)

            segmented = agent.handle({
                "id": "segments",
                "command": "publishSegments",
                "arguments": {
                    "storeId": "store",
                    "key": encoded(b"segments"),
                    "segments": [encoded(b"a\x00"), encoded(b""), encoded(b"b\xff")],
                    "descriptor": encoded(b"meta"),
                },
            })
            self.assert_success(segmented)
            self.assertEqual(4, segmented["result"]["copiedBytes"])

            held = agent.handle({
                "id": "hold",
                "command": "acquire",
                "arguments": {
                    "storeId": "store",
                    "leaseId": "held",
                    "key": encoded(b"segments"),
                },
            })
            self.assert_success(held)
            self.assertEqual("held", held["result"]["leaseId"])
            self.assertEqual(encoded(b"a\x00b\xff"), held["result"]["value"])

            checksum = agent.handle({
                "id": "checksum",
                "command": "checksum",
                "arguments": {"leaseId": "held"},
            })
            self.assert_success(checksum)
            self.assertEqual({
                "leaseId": "held",
                "valueLength": 4,
                "descriptorLength": 4,
                "valueChecksum": "ab4072820d3fd4d7",
                "descriptorChecksum": "4320e9a2e32eac38",
            }, checksum["result"])

            pending = agent.handle({
                "id": "remove",
                "command": "remove",
                "arguments": {"storeId": "store", "key": encoded(b"segments")},
            })
            self.assertEqual({"code": 10, "name": "RemovePending"}, pending["status"])
            retained = agent.handle({
                "id": "read",
                "command": "read",
                "arguments": {"leaseId": "held"},
            })
            self.assert_success(retained)
            self.assertEqual(encoded(b"a\x00b\xff"), retained["result"]["value"])
            retained_checksum = agent.handle({
                "id": "checksum-retained",
                "command": "checksum",
                "arguments": {"leaseId": "held"},
            })
            self.assert_success(retained_checksum)
            self.assertEqual(checksum["result"], retained_checksum["result"])

            released = agent.handle({
                "id": "release",
                "command": "release",
                "arguments": {"leaseId": "held"},
            })
            self.assert_success(released)
            self.assertFalse(released["result"]["valid"])
            repeated = agent.handle({
                "id": "release-again",
                "command": "release",
                "arguments": {"leaseId": "held"},
            })
            self.assertEqual({"code": 9, "name": "LeaseAlreadyReleased"}, repeated["status"])
            stale_checksum = agent.handle({
                "id": "checksum-stale",
                "command": "checksum",
                "arguments": {"leaseId": "held"},
            })
            self.assertEqual({"code": 8, "name": "InvalidLease"}, stale_checksum["status"])
            missing = agent.handle({
                "id": "release-missing",
                "command": "release",
                "arguments": {"leaseId": "missing"},
            })
            self.assertEqual({"code": 8, "name": "InvalidLease"}, missing["status"])

            reused = agent.handle({
                "id": "reuse",
                "command": "publish",
                "arguments": {
                    "storeId": "store",
                    "key": encoded(b"segments"),
                    "value": encoded(b"new"),
                    "descriptor": "",
                },
            })
            self.assert_success(reused)
            duplicate = agent.handle({
                "id": "duplicate",
                "command": "publish",
                "arguments": {
                    "storeId": "store",
                    "key": encoded(b"segments"),
                    "value": encoded(b"other"),
                    "descriptor": "",
                },
            })
            self.assertEqual({"code": 1, "name": "DuplicateKey"}, duplicate["status"])

            reserved = agent.handle({
                "id": "reserve",
                "command": "reserve",
                "arguments": {
                    "storeId": "store",
                    "reservationId": "reservation",
                    "key": encoded(b"reserve"),
                    "payloadLength": 3,
                    "descriptor": "",
                },
            })
            self.assert_success(reserved)
            self.assertEqual(3, reserved["result"]["remainingBytes"])
            written = agent.handle({
                "id": "write",
                "command": "write",
                "arguments": {
                    "reservationId": "reservation",
                    "data": encoded(b"x\x00y"),
                },
            })
            self.assert_success(written)
            self.assertEqual(3, written["result"]["written"])
            advanced = agent.handle({
                "id": "advance",
                "command": "advance",
                "arguments": {"reservationId": "reservation", "byteCount": 3},
            })
            self.assert_success(advanced)
            self.assertEqual(0, advanced["result"]["remainingBytes"])
            committed = agent.handle({
                "id": "commit",
                "command": "commit",
                "arguments": {"reservationId": "reservation"},
            })
            self.assert_success(committed)
            self.assertFalse(committed["result"]["valid"])

            first_close = agent.handle({
                "id": "close",
                "command": "close",
                "arguments": {"storeId": "store"},
            })
            second_close = agent.handle({
                "id": "close-again",
                "command": "close",
                "arguments": {"storeId": "store"},
            })
            self.assert_success(first_close)
            self.assert_success(second_close)
        finally:
            agent.close()

    def test_abrupt_python_fault_agent_leaves_exact_lease_and_reservation_for_recovery(self) -> None:
        survivor = Agent()
        children: list[subprocess.Popen[str]] = []
        try:
            name = unique_store_name("python-fault-recovery")
            arguments = self.open_request(
                "survivor-open",
                "survivor",
                name,
                participants=8,
                open_mode=0,
            )["arguments"]
            arguments.update(slotCount=4, leaseRecordCount=8)
            self.assert_success(survivor.handle({
                "id": "survivor-open",
                "command": "open",
                "arguments": arguments,
            }))

            lease_owner = self.start_fault_agent()
            children.append(lease_owner)
            lease_open = dict(arguments, storeId="lease-owner", openMode=1)
            self.assert_success(self.send_child(lease_owner, "lease-open", "open", lease_open))
            self.assert_success(self.send_child(lease_owner, "publish", "publish", {
                "storeId": "lease-owner",
                "key": encoded(b"leased"),
                "value": encoded(b"value"),
                "descriptor": "",
            }))
            self.assert_success(self.send_child(lease_owner, "acquire", "acquire", {
                "storeId": "lease-owner",
                "leaseId": "abandoned",
                "key": encoded(b"leased"),
            }))
            self.crash_child(lease_owner, "crash-lease")

            recovered = survivor.handle({
                "id": "recover-lease",
                "command": "recoverLeases",
                "arguments": {"storeId": "survivor", "recoverCurrentProcess": False},
            })
            self.assert_success(recovered)
            self.assertEqual(1, recovered["result"]["recoveredLeaseCount"])
            self.assert_success(survivor.handle({
                "id": "remove-leased",
                "command": "remove",
                "arguments": {"storeId": "survivor", "key": encoded(b"leased")},
            }))

            reservation_owner = self.start_fault_agent()
            children.append(reservation_owner)
            reservation_open = dict(arguments, storeId="reservation-owner", openMode=1)
            self.assert_success(self.send_child(
                reservation_owner,
                "reservation-open",
                "open",
                reservation_open,
            ))
            self.assert_success(self.send_child(reservation_owner, "reserve", "reserve", {
                "storeId": "reservation-owner",
                "reservationId": "abandoned",
                "key": encoded(b"reserved"),
                "payloadLength": 4,
                "descriptor": "",
            }))
            self.assert_success(self.send_child(reservation_owner, "write", "reservationWrite", {
                "reservationId": "abandoned",
                "data": encoded(b"ab"),
            }))
            self.assert_success(self.send_child(reservation_owner, "advance", "advance", {
                "reservationId": "abandoned",
                "byteCount": 2,
            }))
            self.crash_child(reservation_owner, "crash-reservation")

            recovered = survivor.handle({
                "id": "recover-reservation",
                "command": "recoverReservations",
                "arguments": {"storeId": "survivor", "recoverCurrentProcess": False},
            })
            self.assert_success(recovered)
            self.assertEqual(1, recovered["result"]["recoveredReservationCount"])
            self.assert_success(survivor.handle({
                "id": "reuse-reserved",
                "command": "publish",
                "arguments": {
                    "storeId": "survivor",
                    "key": encoded(b"reserved"),
                    "value": encoded(b"replacement"),
                    "descriptor": "",
                },
            }))
        finally:
            for child in children:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=5)
                if child.stdin is not None:
                    child.stdin.close()
                if child.stdout is not None:
                    child.stdout.close()
                if child.stderr is not None:
                    child.stderr.close()
            survivor.close()

    def test_open_requires_participant_capacity_and_returns_handle_protocol_identity(self) -> None:
        self.assert_agent_open_contract()
        agent = Agent()
        try:
            name = unique_store_name("agent-contract")
            request = self.open_request("open-1", "store", name, participants=3, open_mode=0)
            opened = agent.handle(request)
            self.assert_success(opened)
            self.assert_open_result(opened, "store", participant_record_count=3)

            mismatched = agent.handle(
                self.open_request("open-2", "mismatch", name, participants=4, open_mode=1)
            )
            self.assertEqual(4, mismatched["status"]["code"])
            self.assertEqual("IncompatibleLayout", mismatched["status"]["name"])
            self.assertNotIn("mismatch", agent.stores)

            ping = agent.handle({"id": "ping-after", "command": "ping"})
            self.assert_protocol_identity(ping["result"])
            self.assertEqual(opened["result"]["protocolInfo"], PROTOCOL_IDENTITY)
        finally:
            agent.close()

    def test_open_honors_total_bytes_and_replaces_an_existing_store_id_first(self) -> None:
        agent = Agent()
        try:
            name = unique_store_name("agent-total-bytes")
            anchor = self.open_request(
                "open-anchor", "anchor", name, participants=3, open_mode=0
            )
            self.assert_success(agent.handle(anchor))
            initial = self.open_request(
                "open-initial", "store", name, participants=3, open_mode=1
            )
            self.assert_success(agent.handle(initial))

            replacement = self.open_request(
                "open-replacement", "store", name, participants=3, open_mode=1
            )
            replacement["arguments"]["totalBytes"] = 1
            rejected = agent.handle(replacement)

            self.assertEqual(
                {"code": 6, "name": "InsufficientCapacity"},
                rejected["status"],
            )
            self.assertNotIn("store", agent.stores)
            self.assertNotIn("store", agent.store_options)

            reopened = self.open_request(
                "open-after-rejection", "store", name, participants=3, open_mode=1
            )
            self.assert_success(agent.handle(reopened))
        finally:
            agent.close()

    def test_participant_table_full_is_status_11_and_closed_capacity_is_reused(self) -> None:
        self.assert_agent_open_contract()
        agent = Agent()
        try:
            name = unique_store_name("agent-participants")
            anchor = agent.handle(
                self.open_request("open-anchor", "anchor", name, participants=2, open_mode=0)
            )
            peer = agent.handle(
                self.open_request("open-peer", "peer", name, participants=2, open_mode=1)
            )
            self.assert_success(anchor)
            self.assert_success(peer)

            full_request = self.open_request(
                "open-full", "reused", name, participants=2, open_mode=1
            )
            full = agent.handle(full_request)
            self.assertTrue(full["ok"])
            self.assertEqual(11, full["status"]["code"])
            self.assertEqual("ParticipantTableFull", full["status"]["name"])
            self.assertNotIn("reused", agent.stores)

            closed = agent.handle(
                {"id": "close-peer", "command": "close", "arguments": {"storeId": "peer"}}
            )
            self.assert_success(closed)
            reused = agent.handle(full_request)
            self.assert_success(reused)
            self.assert_open_result(reused, "reused", participant_record_count=2)
        finally:
            agent.close()

    def test_canonical_store_outcomes_remain_protocol_responses(self) -> None:
        self.assert_agent_open_contract()
        agent = Agent()
        try:
            invalid = agent.handle(
                self.open_request(
                    "invalid-open",
                    "invalid",
                    unique_store_name("invalid-participants"),
                    participants=0,
                    open_mode=0,
                )
            )
            self.assertTrue(invalid["ok"])
            self.assertEqual({"code": 3, "name": "InvalidOptions"}, invalid["status"])

            name = unique_store_name("canonical-status")
            opened = agent.handle(
                self.open_request("valid-open", "store", name, participants=1, open_mode=0)
            )
            self.assert_success(opened)
            missing = agent.handle(
                {
                    "id": "missing-key",
                    "command": "acquire",
                    "arguments": {
                        "storeId": "store",
                        "leaseId": "missing",
                        "key": encoded(b"absent"),
                    },
                }
            )
            self.assertTrue(missing["ok"])
            self.assertEqual({"code": 2, "name": "NotFound"}, missing["status"])
        finally:
            agent.close()

    def test_unknown_command_is_a_protocol_failure(self) -> None:
        agent = Agent()
        try:
            response = agent.handle({"id": "1", "command": "unknown"})
            self.assertFalse(response["ok"])
            self.assertEqual("unsupported_command", response["error"]["code"])
        finally:
            agent.close()

    def assert_success(self, response: dict) -> None:
        self.assertTrue(response["ok"], response.get("error"))
        self.assertEqual(0, response["status"]["code"])
        self.assertEqual("Success", response["status"]["name"])

    def assert_protocol_identity(self, result: dict) -> None:
        self.assertEqual("python", result["runtime"])
        self.assertEqual(2, result["protocolVersion"])
        for field, expected in PROTOCOL_IDENTITY.items():
            self.assertEqual(expected, result[field], field)

    def assert_open_result(
        self,
        response: dict,
        store_id: str,
        *,
        participant_record_count: int,
    ) -> None:
        self.assertEqual(store_id, response["result"]["storeId"])
        self.assertEqual(
            participant_record_count,
            response["result"]["participantRecordCount"],
        )
        self.assertEqual(PROTOCOL_IDENTITY, response["result"]["protocolInfo"])

    def assert_agent_open_contract(self) -> None:
        source = inspect.getsource(Agent.command_open)
        self.assertIn("participantRecordCount", source)
        self.assertIn("participant_record_count", source)

    @staticmethod
    def start_fault_agent() -> subprocess.Popen[str]:
        return subprocess.Popen(
            [sys.executable, str(Path(__file__).with_name("interop_agent.py"))],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )

    def send_child(
        self,
        child: subprocess.Popen[str],
        request_id: str,
        command: str,
        arguments: dict,
    ) -> dict:
        assert child.stdin is not None and child.stdout is not None
        child.stdin.write(json.dumps({
            "id": request_id,
            "command": command,
            "arguments": arguments,
        }) + "\n")
        child.stdin.flush()
        response = child.stdout.readline()
        if not response:
            child.wait(timeout=5)
            stderr = child.stderr.read() if child.stderr is not None else ""
            self.fail(f"fault agent exited before responding: {stderr}")
        return json.loads(response)

    def crash_child(self, child: subprocess.Popen[str], request_id: str) -> None:
        assert child.stdin is not None
        child.stdin.write(json.dumps({
            "id": request_id,
            "command": "crash",
            "arguments": {},
        }) + "\n")
        child.stdin.flush()
        self.assertEqual(97, child.wait(timeout=10))

    @staticmethod
    def open_request(
        request_id: str,
        store_id: str,
        name: str,
        *,
        participants: int,
        open_mode: int,
    ) -> dict:
        return {
            "id": request_id,
            "command": "open",
            "arguments": {
                "storeId": store_id,
                "name": name,
                "openMode": open_mode,
                "slotCount": 2,
                "maxValueBytes": 32,
                "maxDescriptorBytes": 8,
                "maxKeyBytes": 8,
                "leaseRecordCount": 2,
                "participantRecordCount": participants,
                "enableLeaseRecovery": True,
            },
        }


if __name__ == "__main__":
    unittest.main()
