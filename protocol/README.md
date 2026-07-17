# SharedMemoryStore Protocol

This directory is the language-neutral compatibility boundary shared by the
managed, native, and Python distributions. Public APIs may be idiomatic, but
all three implementations preserve the same mapped bytes, atomic transitions,
status values, platform resource derivation, and lifetime rules.

## Current identities

| Contract | Identity | Canonical definition |
|---|---:|---|
| Mapped layout | `2.0` (`SMS2`) | [layout-v2.0.md](layout-v2.0.md) |
| Resource protocol | `2` | [resource-naming-v2.md](resource-naming-v2.md) |
| Required / optional features | `7` / `0` | [fixtures/v2.0/manifest.json](fixtures/v2.0/manifest.json) |
| Native C ABI | `2.0` | [public API contract](../specs/010-lock-free-only-multilang/contracts/public-api.md) |

Package versions are independent: NuGet is `3.0.0`; the CMake and Python
distributions are `1.0.0`. Their current compatibility declaration is
[compatibility.json](compatibility.json).

## One protocol

Every ordinary store creation uses layout 2.0, resource protocol 2, required
feature mask 7, and a participant-record capacity. There is no profile selector,
fallback engine, compatibility reader, converter, or alternate creatable
layout. A noncurrent, unknown, malformed, or unsupported mapping is rejected
before any directory, slot, descriptor, or payload projection.

Layout 2.0 is a bounded shared-memory key-value protocol, not a queue or stream.
Hot operations use naturally aligned lock-free 64-bit atomics and helpable,
generation-fenced record state machines. This guarantees system-wide progress;
it does not make every individual call wait-free. Cold lifecycle resources are
used only for physical creation, attachment, participant registration, and
cleanup.

Compatibility requires more than the two layout version fields. An opener also
validates magic, header and record sizes, configured capacities, calculated
section offsets and lengths, total-region bounds, required and optional feature
masks, store control, and every observed state before projecting payload bytes.

## Canonical evidence

The v2 manifest pins record sizes and offsets, numeric states and statuses,
open modes, layout arithmetic, FNV-1a hashes, atomic memory orders, and resource
name vectors. Its deterministic mapped-region fixtures are offline conformance
inputs only; they are never opened as live mappings.

Any change to mapped bytes, field meaning, state transitions, hashing, probing,
or visibility rules requires a new layout identity and synchronized updates to
the narrative, manifest, language conformance tests, and nine ordered runtime
pairs.

## Migration from a noncurrent store

There is no in-place migration. Stop writers and readers, dispose every handle,
remove or replace the old physical store, create a fresh SMS2 store using the
current participant-aware sizing API, and republish values from an
application-owned authoritative source. A current client deliberately cannot
read the old store to perform this migration.
