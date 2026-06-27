# Shared Memory Layout Contract

## Compatibility Goals

The mapped layout is the interoperability contract for future C++ and Python
clients. C# APIs may wrap this layout, but they must not change shared-memory
semantics without updating this contract and the semantic version impact.

## Region Order

```text
+-------------------------+
| Store Header            |
+-------------------------+
| Shared Key Index        |
+-------------------------+
| Lease Registry          |
+-------------------------+
| Slot Metadata Table     |
+-------------------------+
| Descriptor Storage      |
+-------------------------+
| Payload Storage         |
+-------------------------+
```

All offsets are relative to the start of the mapped region. Header fields store
the offset, length, and count for every section.

## Encoding Rules

- byte order: little-endian.
- alignment: 8-byte minimum for numeric fields; state, generation, and counter
  fields that participate in atomic operations must be naturally aligned.
- keys: opaque bytes with exact byte equality.
- descriptors: opaque bytes.
- payloads: opaque bytes.
- strings in public docs or helpers are UTF-8 encoded before entering the core
  byte-key contract.

## Store Header

Required fields:
- magic: `SMS1`
- layout major version.
- layout minor version.
- header length.
- total mapped bytes.
- slot count.
- lease record count.
- maximum key bytes.
- maximum descriptor bytes.
- maximum value bytes.
- offsets and lengths for index, lease registry, slot metadata, descriptor
  storage, and payload storage.
- store id.
- store state.

Open validation:
- magic must match.
- major version must be supported.
- configured maxima must match the existing region when opening.
- section offsets and lengths must fit inside total mapped bytes.

## Shared Key Index

The index is an open-addressed table stored inside the mapped region.

Required entry fields:
- state: `Empty`, `Occupied`, or `Tombstone`.
- key hash.
- key length.
- slot index.
- slot generation.
- inline key bytes up to `MaxKeyBytes`.

Rules:
- duplicate detection requires hash match and exact key byte equality.
- index entries are removed or tombstoned when value removal completes.
- probing must not allocate managed memory.
- lookup of missing keys returns `NotFound`.

## Lease Registry

The lease registry prevents double release and supports owner-controlled stale
lease recovery.

Required record fields:
- state: `Free`, `Active`, `Released`, or `Abandoned`.
- lease record id.
- slot index.
- slot generation.
- owner process id.
- acquire sequence.

Rules:
- acquire reserves one free lease record before incrementing usage count.
- release validates lease record, slot index, and generation before decrementing
  usage count.
- recovery can mark a lease abandoned only through explicit owner API calls.
- recovery behavior must return deterministic unsupported statuses on platforms
  without reliable owner-liveness checks.

## Slot Metadata

Required fields:
- slot state: `Free`, `Publishing`, `Published`, `RemoveRequested`, or
  `Reclaiming`.
- generation.
- usage count.
- key hash.
- key length.
- descriptor length.
- value length.
- descriptor offset.
- payload offset.
- committed sequence.
- publisher process id.

Rules:
- a producer writes descriptor and payload bytes before transitioning the slot
  to `Published`.
- readers acquire only `Published` slots.
- remove marks a leased value `RemoveRequested`.
- the final release of a `RemoveRequested` value transitions it to reclaiming
  and then free.
- generation increments before a slot is made available for a new value.

## State Values

Numeric state assignments must be documented in implementation constants and
covered by contract tests. New states require layout version review.

Initial states:
- store: `Initializing`, `Ready`, `Disposing`, `Corrupt`, `Unsupported`
- index: `Empty`, `Occupied`, `Tombstone`
- slot: `Free`, `Publishing`, `Published`, `RemoveRequested`, `Reclaiming`
- lease: `Free`, `Active`, `Released`, `Abandoned`

## Corruption Handling

If layout validation, counters, offsets, or generation checks detect impossible
state, the store transitions to a safe error mode and returns deterministic
corruption statuses for unsafe operations. The library must not attempt unsafe
payload access after detecting corruption.
