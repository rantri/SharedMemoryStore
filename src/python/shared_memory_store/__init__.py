"""Interoperable named shared-memory values for Python."""

from __future__ import annotations

from ._native import (
    ABI_VERSION,
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    RESOURCE_NAMING_VERSION,
    native_library_path,
)
from .enums import OpenMode, StoreOpenStatus, StoreStatus
from .store import (
    DiagnosticsSnapshot,
    MemoryStore,
    RecoveryReport,
    StoreOptions,
    ValueLease,
    ValueReservation,
    WaitOptions,
    calculate_required_bytes,
)


__version__ = "0.1.0"

__all__ = [
    "__version__",
    "ABI_VERSION",
    "LAYOUT_MAJOR_VERSION",
    "LAYOUT_MINOR_VERSION",
    "RESOURCE_NAMING_VERSION",
    "OpenMode",
    "StoreOpenStatus",
    "StoreStatus",
    "WaitOptions",
    "StoreOptions",
    "RecoveryReport",
    "DiagnosticsSnapshot",
    "MemoryStore",
    "ValueLease",
    "ValueReservation",
    "calculate_required_bytes",
    "native_library_path",
]
