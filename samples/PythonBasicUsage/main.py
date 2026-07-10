from __future__ import annotations

import os
import uuid

from shared_memory_store import MemoryStore, StoreOpenStatus, StoreOptions, StoreStatus


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
        enable_lease_recovery=True,
    )
    open_status, store = MemoryStore.open(options)
    require(open_status, StoreOpenStatus.SUCCESS, "open")
    assert store is not None

    key = b"frame-1"
    with store:
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
