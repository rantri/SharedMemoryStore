from __future__ import annotations

import base64
import unittest

from interop_agent import Agent

from _support import require_native, unique_store_name


def encoded(value: bytes) -> str:
    return base64.b64encode(value).decode("ascii")


class InteropAgentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        require_native()

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
                "enableLeaseRecovery": True,
            }
            self.assert_success(agent.handle({"id": "1", "command": "ping"}))
            self.assert_success(agent.handle({"id": "2", "command": "open", "arguments": common}))
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


if __name__ == "__main__":
    unittest.main()
