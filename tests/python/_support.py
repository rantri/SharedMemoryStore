from __future__ import annotations

import os
import unittest
import uuid


def unique_store_name(label: str) -> str:
    return f"sms-python-{label}-{os.getpid()}-{uuid.uuid4().hex}"


def require_native() -> None:
    from shared_memory_store import calculate_required_bytes

    try:
        calculate_required_bytes(
            slot_count=1,
            max_value_bytes=1,
            max_descriptor_bytes=0,
            max_key_bytes=1,
            lease_record_count=1,
        )
    except (ImportError, OSError) as error:
        raise unittest.SkipTest(f"native SharedMemoryStore artifact is unavailable: {error}") from error


def create_options(label: str, *, slots: int = 3, recovery: bool = True):
    from shared_memory_store import StoreOptions

    return StoreOptions.create(
        unique_store_name(label),
        slot_count=slots,
        max_value_bytes=128,
        max_descriptor_bytes=32,
        max_key_bytes=32,
        lease_record_count=8,
        enable_lease_recovery=recovery,
    )
