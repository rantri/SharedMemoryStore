"""Numeric public contracts shared by every SharedMemoryStore runtime."""

from __future__ import annotations

from enum import IntEnum


class OpenMode(IntEnum):
    """Controls whether a named store is created, opened, or either."""

    CREATE_NEW = 0
    OPEN_EXISTING = 1
    CREATE_OR_OPEN = 2



class StoreOpenStatus(IntEnum):
    """Outcome of opening a named store."""

    SUCCESS = 0
    ALREADY_EXISTS = 1
    NOT_FOUND = 2
    INVALID_OPTIONS = 3
    INCOMPATIBLE_LAYOUT = 4
    UNSUPPORTED_PLATFORM = 5
    INSUFFICIENT_CAPACITY = 6
    ACCESS_DENIED = 7
    MAPPING_FAILED = 8
    STORE_BUSY = 9
    OPERATION_CANCELED = 10



class StoreStatus(IntEnum):
    """Outcome of a store lifecycle operation."""

    SUCCESS = 0
    DUPLICATE_KEY = 1
    NOT_FOUND = 2
    KEY_TOO_LARGE = 3
    VALUE_TOO_LARGE = 4
    DESCRIPTOR_TOO_LARGE = 5
    STORE_FULL = 6
    LEASE_TABLE_FULL = 7
    INVALID_LEASE = 8
    LEASE_ALREADY_RELEASED = 9
    REMOVE_PENDING = 10
    UNSUPPORTED_PLATFORM = 11
    STORE_DISPOSED = 12
    CORRUPT_STORE = 13
    ACCESS_DENIED = 14
    UNKNOWN_FAILURE = 15
    INVALID_RESERVATION = 16
    RESERVATION_INCOMPLETE = 17
    RESERVATION_ALREADY_COMPLETED = 18
    RESERVATION_WRITE_OUT_OF_RANGE = 19
    INVALID_KEY = 20
    STORE_BUSY = 21
    OPERATION_CANCELED = 22

__all__ = ["OpenMode", "StoreOpenStatus", "StoreStatus"]
