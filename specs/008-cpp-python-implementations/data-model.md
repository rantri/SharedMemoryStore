# Data Model: Native and Python Implementations

## Shared Protocol Version

- `layout_major`: 1
- `layout_minor`: 2
- `resource_naming_version`: 1
- `c_abi_major`: 1
- `c_abi_minor`: starts at 0 and may gain backward-compatible functions or
  trailing versioned-struct fields.

Package versions are independent of these protocol versions.

## Canonical Mapped Records

All records are little-endian, sequential, and packed to a maximum alignment of
8 bytes. Every implementation must assert both total sizes and field offsets.

| Record | Size | Key offsets |
|--------|-----:|-------------|
| Store header | 160 | magic 0, total bytes 16, index offset 56, store id 136, store state 144, sequence 152 |
| Index entry header | 32 | state 0, key length 4, hash 8, slot index 16, generation 20, reuse epoch 24 |
| Slot metadata | 72 | state 0, generation 4, reuse epoch 8, usage 16, publisher PID 32, hash 40, descriptor offset 48, payload offset 56, sequence 64 |
| Lease record | 40 | state 0, id 4, slot 8, generation 12, reuse epoch 16, owner PID 24, acquire sequence 32 |

The index entry stride is `align8(32 + max_key_bytes)`. Section calculations
must exactly match the canonical algorithm in `StoreLayout`.

## Store Options

- UTF-8 public name, 1 through 240 Unicode scalar/code-unit-compatible input
  characters with no NUL.
- open mode: create new, open existing, or create-or-open.
- total mapped bytes: positive and at least the calculated requirement.
- slot count, maximum value bytes, maximum key bytes, and lease records: positive.
- maximum descriptor bytes: non-negative.
- lease recovery enabled: process-local policy, not stored in the mapping.
- ABI struct size and ABI version: required for native forward compatibility.

## Store Handle

Owns one process-local mapping view, shared synchronization handle, platform
owner registration, operation gate, diagnostics counters, and disposed flag.
Closing one handle unregisters only that owner and does not mutate a live store's
header state.

## Slot Lifecycle Identity

`(generation: int32, reuse_epoch: int64)` uniquely identifies one use of a slot.
Initial value is `(1, 0)`. Reclaim increments generation; generation rollover
sets generation to 1 and increments reuse epoch. Exhausting both fields marks
the store corrupt rather than making stale tokens valid.

## Slot State Machine

```text
Free -> Publishing -> Published -> RemoveRequested -> Reclaiming -> Free
          |              |                                  ^
          +--------------+----------------------------------+
             abort/recovery or unleased remove
```

- Publishing is invisible but owns its key in the index.
- Commit requires exact reservation progress and publishes metadata after bytes.
- RemoveRequested remains readable to existing leases but not acquirable anew.
- Reclaim removes every matching index entry before advancing identity.

## Lease

Contains an owning store reference, slot index, lifecycle identity, and lease
record id. It exposes read-only descriptor and payload bytes only while the
record remains active and the slot identity matches. Release is idempotently
status-returning; a second release returns the documented already-released
outcome.

## Reservation

Contains an owning store reference, slot index, lifecycle identity, announced
payload length, and shared progress. It exposes only the currently remaining
writable range. Commit requires progress equal to announced length. Abort,
commit, recovery, close, or identity change invalidates borrowed views.

## Platform Resource Identity

Windows:

- region name: exact public name.
- mutex: scope plus `SharedMemoryStore-` and character-for-character sanitized
  public name.

Linux:

- root: `/dev/shm/SharedMemoryStore` when available, otherwise the system temp
  directory plus `SharedMemoryStore`.
- fragment: `sms-<readable up to 80>-<first 8 SHA-256 bytes as lowercase hex>`.
- files: `.region`, `.lock`, `.owners`, `.lifecycle`.
- directory mode 0700 and files mode 0600.
- owner line: `<pid>:<start-token>:<unique-token>`.

## Diagnostics Snapshot

Shared facts include total bytes, slot capacity/state counts, active leases,
index occupancy/tombstones/probe observations, and compaction count. Runtime
local facts include last observed failure and per-status failure counters.
Snapshots never own a telemetry sink and never print.

## Compatibility Matrix Entry

- distribution name and version range.
- supported ABI range where applicable.
- readable/creatable layout versions.
- resource naming version.
- validated operating systems and architectures.
- ordered-pair interoperability evidence.
