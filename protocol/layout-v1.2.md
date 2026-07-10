# Mapped Layout 1.2

This document defines the canonical major-1, minor-2 mapped representation. All
multi-byte integers are little-endian, and signed integers use two's-complement
representation. Records use sequential field order with a maximum field
alignment of 8 bytes; the offsets below are part of the protocol and must be
asserted rather than inferred from a compiler ABI. Keys, descriptors, and
payloads are opaque bytes.

The 32-bit magic integer is `0x31534d53`, whose little-endian bytes spell
`SMS1`. All offsets are absolute byte offsets from the beginning of the mapped
region.

## Region order

```text
store header
shared key index
lease registry
slot metadata table
descriptor storage
payload storage
```

There is no section directory outside the 160-byte header. Index entries have a
variable stride because the fixed 32-byte header is followed by inline key
capacity. Lease and slot records have fixed strides of 40 and 72 bytes.

## Store header: 160 bytes

| Field | Type | Offset | Meaning |
|---|---:|---:|---|
| `magic` | `int32` | 0 | `0x31534d53` |
| `layout_major_version` | `int32` | 4 | `1` |
| `layout_minor_version` | `int32` | 8 | `2` |
| `header_length` | `int32` | 12 | `160` |
| `total_bytes` | `int64` | 16 | Full mapped-region length supplied at creation |
| `slot_count` | `int32` | 24 | Number of slot records and storage strides |
| `lease_record_count` | `int32` | 28 | Number of lease records |
| `max_key_bytes` | `int32` | 32 | Inline key capacity in each index entry |
| `max_descriptor_bytes` | `int32` | 36 | Per-slot descriptor capacity |
| `max_value_bytes` | `int32` | 40 | Per-slot payload capacity |
| `index_entry_count` | `int32` | 44 | Power-of-two index capacity |
| `index_entry_size` | `int32` | 48 | Aligned fixed header plus key capacity |
| padding | 4 bytes | 52 | Must not be interpreted |
| `index_offset` | `int64` | 56 | Start of the index |
| `index_length` | `int64` | 64 | Index bytes |
| `lease_registry_offset` | `int64` | 72 | Start of lease records |
| `lease_registry_length` | `int64` | 80 | Lease-record bytes |
| `slot_metadata_offset` | `int64` | 88 | Start of slot records |
| `slot_metadata_length` | `int64` | 96 | Slot-record bytes |
| `descriptor_storage_offset` | `int64` | 104 | Start of descriptor strides |
| `descriptor_storage_length` | `int64` | 112 | Descriptor-storage bytes |
| `payload_storage_offset` | `int64` | 120 | Start of payload strides |
| `payload_storage_length` | `int64` | 128 | Payload-storage bytes |
| `store_id` | `int64` | 136 | Opaque identity assigned at initialization |
| `store_state` | `int32` | 144 | Store state value |
| `reserved` | `int32` | 148 | Reserved; readers must not attach meaning |
| `sequence` | `int64` | 152 | Monotonic commit/acquire sequence source |

## Index entry header: 32 bytes

| Field | Type | Offset |
|---|---:|---:|
| `state` | `int32` | 0 |
| `key_length` | `int32` | 4 |
| `key_hash` | `uint64` | 8 |
| `slot_index` | `int32` | 16 |
| `slot_generation` | `int32` | 20 |
| `slot_reuse_epoch` | `int64` | 24 |

The inline key starts at offset 32 and occupies `max_key_bytes` bytes inside the
entry stride. Writers clear the full inline capacity, copy exactly `key_length`
bytes, and publish `Occupied` last. Exact equality requires both hash and bytes;
a hash match alone is never a key match.

Keys use unsigned 64-bit FNV-1a with offset basis
`0xcbf29ce484222325` and prime `0x00000100000001b3`; multiplication wraps modulo
2^64 after every byte. Probe start is
`key_hash & (index_entry_count - 1)`, followed by linear probing with wraparound.
An `Empty` entry terminates lookup; a `Tombstone` does not. Insertions reuse the
first tombstone encountered. Removal marks every matching copy tombstone so an
interrupted index compaction cannot leave a stale duplicate.

## Slot metadata: 72 bytes

| Field | Type | Offset | Meaning |
|---|---:|---:|---|
| `state` | `int32` | 0 | Slot state value |
| `generation` | `int32` | 4 | Positive lifecycle generation |
| `reuse_epoch` | `int64` | 8 | Non-negative generation-rollover epoch |
| `usage_count` | `int32` | 16 | Active leases protecting this lifecycle |
| `key_length` | `int32` | 20 | Key bytes in the corresponding index entry |
| `descriptor_length` | `int32` | 24 | Committed or announced descriptor bytes |
| `value_length` | `int32` | 28 | Committed or announced payload bytes |
| `publisher_process_id` | `int32` | 32 | Reservation/publication owner PID |
| `reserved` | `int32` | 36 | Bytes advanced while `Publishing`; zero after commit |
| `key_hash` | `uint64` | 40 | FNV-1a hash of the key |
| `descriptor_offset` | `int64` | 48 | Absolute per-slot descriptor address |
| `payload_offset` | `int64` | 56 | Absolute per-slot payload address |
| `committed_sequence` | `int64` | 64 | Zero before commit; assigned before publication |

The lifecycle identity is the pair `(generation, reuse_epoch)`, initially
`(1, 0)`. Reclaim increments generation. Reclaiming generation `2147483647`
sets generation to `1` and increments the epoch. The pair
`(2147483647, 9223372036854775807)` cannot advance and makes reuse unsafe; the
operation reports corruption rather than repeating an old identity.

Descriptor address for slot `i` is
`descriptor_storage_offset + i * descriptor_stride`; payload address follows
the equivalent payload formula. Zero-length descriptors are valid, but their
physical stride is still 8 bytes.

## Lease record: 40 bytes

| Field | Type | Offset |
|---|---:|---:|
| `state` | `int32` | 0 |
| `lease_record_id` | `int32` | 4 |
| `slot_index` | `int32` | 8 |
| `slot_generation` | `int32` | 12 |
| `slot_reuse_epoch` | `int64` | 16 |
| `owner_process_id` | `int32` | 24 |
| `reserved` | `int32` | 28 |
| `acquire_sequence` | `int64` | 32 |

An active lease is valid only when its record id, slot index, and complete
lifecycle pair still match. Publishing a record's `Active` state occurs after
its other fields are written. `Released` and `Abandoned` records may later be
overwritten for a new lease; stale process-local tokens must still be rejected
by their captured identity and token lifetime.

## Numeric assignments and transitions

| State family | Assignments |
|---|---|
| Store | `Initializing=0`, `Ready=1`, `Disposing=2`, `Corrupt=3`, `Unsupported=4` |
| Index | `Empty=0`, `Occupied=1`, `Tombstone=2` |
| Slot | `Free=0`, `Publishing=1`, `Published=2`, `RemoveRequested=3`, `Reclaiming=4` |
| Lease | `Free=0`, `Active=1`, `Released=2`, `Abandoned=3` |

Slot flow is `Free -> Publishing -> Published`. Abort or authorized recovery
removes the pending index entry and returns `Publishing -> Free` without making
bytes visible. A remove with no leases removes the index entry and flows
`Published -> Reclaiming -> Free`. A remove with leases publishes
`Published -> RemoveRequested`; existing leases remain readable, no new lease
may be acquired, and the final release performs
`RemoveRequested -> Reclaiming -> Free`.

For commit, descriptor and payload bytes and all non-state metadata are written
first, the sequence is assigned, and `Published` is stored last. Readers treat
only `Published` as newly acquirable and must recheck the state and lifecycle
after registering a lease. All shared mutations participate in the common
platform synchronization contract.

## Checked layout calculation

`align8(x) = (x + 7) & ~7`, with checked signed arithmetic. Let `S` be slot
count, `L` lease-record count, `K` maximum key bytes, `D` maximum descriptor
bytes, and `V` maximum value bytes:

```text
header_length             = 160
index_entry_count         = next_power_of_two(max(4, S * 2))
index_entry_size          = align8(32 + K)
index_offset              = header_length
index_length              = index_entry_count * index_entry_size
lease_registry_offset     = align8(index_offset + index_length)
lease_registry_length     = L * 40
slot_metadata_offset      = align8(lease_registry_offset + lease_registry_length)
slot_metadata_length      = S * 72
descriptor_stride         = align8(max(1, D))
descriptor_storage_offset = align8(slot_metadata_offset + slot_metadata_length)
descriptor_storage_length = S * descriptor_stride
payload_stride            = align8(max(1, V))
payload_storage_offset    = align8(descriptor_storage_offset + descriptor_storage_length)
payload_storage_length    = S * payload_stride
required_bytes            = align8(payload_storage_offset + payload_storage_length)
```

`S`, `L`, `K`, and `V` must be positive; `D` may be zero. The index count must
be representable as a positive signed 32-bit power of two no greater than
2^30. Intermediate 32-bit operations used for counts and strides and all
64-bit offsets and lengths are checked. Overflow is an invalid layout, never
wraparound. `total_bytes` may exceed `required_bytes` but must be positive and
must not be smaller.

Opening validates all stored dimensions, calculated offsets and lengths, and
monotonic in-bounds sections. A major-version mismatch, impossible arithmetic,
unknown unsafe shape, or section extending past `total_bytes` is incompatible;
no payload pointer may be formed from an unvalidated header.
