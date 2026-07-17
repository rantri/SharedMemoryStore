"""Interoperable named shared-memory values for Python."""

from __future__ import annotations

from ._native import (
    ABI_VERSION,
    LAYOUT_MAJOR_VERSION,
    LAYOUT_MINOR_VERSION,
    OPTIONAL_FEATURES,
    REQUIRED_FEATURES,
    RESOURCE_PROTOCOL_VERSION,
    native_library_path,
)
from .enums import OpenMode, StoreOpenStatus, StoreStatus
from .store import (
    CancellationSource,
    DiagnosticsSnapshot,
    MemoryStore,
    ProtocolInfo,
    RecoveryReport,
    StoreOptions,
    ValueLease,
    ValueReservation,
    WaitOptions,
    calculate_required_bytes,
)


__version__ = "1.0.0"

__all__ = [
    "__version__",
    "ABI_VERSION",
    "LAYOUT_MAJOR_VERSION",
    "LAYOUT_MINOR_VERSION",
    "RESOURCE_PROTOCOL_VERSION",
    "REQUIRED_FEATURES",
    "OPTIONAL_FEATURES",
    "OpenMode",
    "StoreOpenStatus",
    "StoreStatus",
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
    "native_library_path",
]
