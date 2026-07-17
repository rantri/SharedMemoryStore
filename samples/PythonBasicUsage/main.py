from __future__ import annotations

import os
import uuid

from shared_memory_store import (
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    MemoryStore,
    OPTIONAL_FEATURES,
    REQUIRED_FEATURES,
    RESOURCE_PROTOCOL_VERSION,
    StoreOpenStatus,
    StoreOptions,
    StoreStatus,
)


def require(actual: object, expected: object, operation: str) -> None:
    if actual != expected:
        raise RuntimeError(f"{operation} returned {actual!r}; expected {expected!r}")


def main() -> None:
    options = StoreOptions.create(
        f"sms-python-sample-{os.getpid()}-{uuid.uuid4().hex}",
        slot_count=2,
        max_value_bytes=64,
        max_descriptor_bytes=16,
        max_key_bytes=16,
        lease_record_count=4,
        participant_record_count=4,
        enable_lease_recovery=True,
    )
    open_status, store = MemoryStore.open(options)
    require(open_status, StoreOpenStatus.SUCCESS, "open")
    assert store is not None

    key = b"frame-1"
    with store:
        protocol = store.protocol_info
        expected_protocol = (
            LAYOUT_MAJOR_VERSION,
            LAYOUT_MINOR_VERSION,
            RESOURCE_PROTOCOL_VERSION,
            REQUIRED_FEATURES,
            OPTIONAL_FEATURES,
        )
        actual_protocol = (
            protocol.layout_major_version,
            protocol.layout_minor_version,
            protocol.resource_protocol_version,
            protocol.required_features,
            protocol.optional_features,
        )
        require(actual_protocol, expected_protocol, "protocol identity")
        print(
            "protocol=SMS2 "
            f"layout={protocol.layout_major_version}.{protocol.layout_minor_version} "
            f"resource={protocol.resource_protocol_version} "
            f"required=0x{protocol.required_features:x} "
            f"optional=0x{protocol.optional_features:x} "
            f"participants={options.participant_record_count}"
        )

        require(store.publish(key, b"hello from Python\x00", b"sample"), StoreStatus.SUCCESS, "publish")
        acquire_status, lease = store.acquire(key)
        require(acquire_status, StoreStatus.SUCCESS, "acquire")
        assert lease is not None
        with lease:
            print(f"value={bytes(lease.value)!r} descriptor={bytes(lease.descriptor)!r}")
        require(store.remove(key), StoreStatus.SUCCESS, "remove")

        diagnostics_status, diagnostics = store.diagnostics()
        require(diagnostics_status, StoreStatus.SUCCESS, "diagnostics")
        assert diagnostics is not None
        print(f"free slots: {diagnostics.free_slot_count}/{diagnostics.slot_count}")


if __name__ == "__main__":
    main()
