# Data Model: Lock-Free-Only Multi-Language Store

## Authority and Scope

This feature adopts the existing SMS2 mapped protocol as the only current
product data model. It does not create a new layout or reinterpret any SMS2
field. The normative byte layout, record sizes, field offsets, alignments,
bit allocations, state numbers, and required-feature mask remain defined by:

- [`protocol/layout-v2.0.md`](../../protocol/layout-v2.0.md),
- [`protocol/fixtures/v2.0/manifest.json`](../../protocol/fixtures/v2.0/manifest.json), and
- [`protocol/resource-naming-v2.md`](../../protocol/resource-naming-v2.md).

This document describes the semantic entities and relationships every C#,
C++, and Python implementation must expose. If prose here conflicts with a
canonical protocol artifact, the canonical artifact wins and the conflict is a
release-blocking specification defect.

SMS2 contains only fixed-width, little-endian mapped data. It contains no
managed references, native pointers, Python objects, process-local mutexes,
background-worker state, or application-specific payload schema.

## Aggregate Model

```text
Canonical Store (one mapping incarnation)
|-- Store Header / terminal store control
|-- Participant Registry
|    `-- one exact Participant Incarnation per open handle
|-- Primary Directory
|    |-- fixed buckets
|    |-- fixed lanes
|    `-- versioned spill summaries and helpable mutations
|-- Overflow Directory
|    `-- bounded binding cells
|-- Value Slot Table
|    `-- one reusable Published Value Generation per owned slot
|-- Lease Registry
|    `-- zero or more Lease Incarnations protecting exact slot generations
`-- Fixed key, descriptor, and payload storage

Process-local Store Handle
|-- mapped-view ownership
|-- exact participant token
|-- local operation/disposal gate
|-- local diagnostics and retry counters
`-- zero or more local Reservation / Lease token wrappers
```

The public key-value relation remains:

```text
opaque non-empty key -> at most one ordered value generation
                                      |
                                      +-> zero or more active read leases
```

Keys, descriptors, and payloads are opaque byte sequences. No distribution may
interpret their application-level format.

## Canonical Store

A canonical store is one named SMS2 mapping and its resource-protocol-2 cold
lifecycle resources.

On Linux, stable mapping, lock, owner-sidecar, and lifecycle identities remain
in the shared resource root. Volatile owner anchors and release markers belong
to one exact per-store owner-artifact directory, which is the only directory
enumerated by that store's cold lifecycle.

### Identity

- Public store name: caller-facing identity from which all platform resources
  are derived.
- Protocol identity: SMS2 magic, layout version, resource protocol, and
  required/optional feature masks.
- Store ID: nonzero mapping-incarnation identity. Recreating the same public
  name produces a new store ID.
- Physical creation disposition: process-local proof of whether the cold-open
  call created the mapping. It is not persisted and is the only authority to
  initialize an all-zero mapping.

### Capacity

The header fixes total bytes, value-slot count, lease-record count,
participant-record count, maximum key/descriptor/value lengths, directory
shape, and every section boundary. Every distribution performs identical
checked sizing and validates the complete declared topology against the actual
mapped capacity before projecting variable sections.

### Store states

```text
Zero --physical creator only--> Initializing --> Ready
                                  |               |
                                  +--> Unsupported|
                                  +-------------->Corrupt
                                                  ^
                             revalidated Ready ---+
```

- `Zero` is uninitialized storage, never open authority.
- `Initializing` is visible only during the cold transaction.
- `Ready` is the only state that admits ordinary mapped operations.
- `Unsupported` rejects a topology or platform the implementation cannot
  execute safely.
- `Corrupt` is terminal for that mapping incarnation.

Only physical creation may initialize SMS2. An opener observing a zero,
retired-layout, unknown-version, unsupported-feature, truncated, or malformed
mapping fails according to the public open contract without initializing,
converting, or projecting payload bytes.

## Store Header

The Store Header owns:

- protocol and mapping-incarnation identity;
- required and optional feature declarations;
- immutable capacities and checked section topology;
- the aligned atomic store control;
- creator and participant namespace-recovery mode;
- approximate mapped diagnostic counters explicitly declared by SMS2.

After `Ready`, structural identity and capacity fields are immutable. The only
terminal structural mutation is the exact store-control transition to
`Corrupt`. Diagnostic counters and the documented monotonic namespace mode may
change independently and are not transactional snapshots.

The header does not identify a language implementation. A mapping created by
any conforming runtime is indistinguishable at the protocol level from one
created by another.

## Participant Registry and Participant Incarnation

Each successfully opened store handle owns exactly one participant record
before it may claim a value slot or lease record. A Participant Incarnation is
the combination of record index, nonwrapping incarnation, process identity,
process-start evidence, PID-namespace evidence where applicable, and its exact
control state.

### Participant states

```text
Free(g)
  -> Registering(g, owner)
  -> Active(g, owner)
       |-> Closing(g, owner)       # orderly local close after call drain
       `-> Recovering(g, owner)    # exact stale-owner recovery
  -> Reclaiming(g, owner=none)
  -> Free(g+1) | Retired(g)
```

Rules:

- `Registering -> Active` release-publishes complete owner identity before the
  handle is returned.
- `Active` is the only state that may originate new slot or lease claims.
- `Closing` and `Recovering` are claim-closed, helpable ownership handoffs.
- `Reclaiming` carries no live owner and may be completed by any conforming
  helper after exact-reference absence is revalidated.
- A record returns to `Free` only after a bounded full scan proves that no
  persistent slot, lease, or directory operation contains its exact token.
- The record retires rather than wrapping to a previously valid token.
- Participant-table exhaustion fails only the new open attempt; it does not
  mutate or impede existing handles.

The participant token embedded in other controls is the canonical SMS2 token
described by the protocol manifest. Language wrappers may store it in an opaque
local type but must not alter its representation.

## Directory Model

The directory maps an exact key to an exact value-slot generation. A hash is a
candidate-selection aid, never key identity; equality always includes the
stored key bytes.

### Directory Binding

A Directory Binding is one atomic word containing a slot reference and exact
slot generation according to the canonical codec. A binding is usable only
when its decoded slot is in range, the slot control has the same generation and
a compatible state, required metadata is discoverable, and the exact key bytes
match. Stale bindings may be cleaned only with exact-generation proof.

### Primary Directory Bucket and Lane

A Primary Directory Bucket owns:

- a fixed set of atomic binding lanes;
- one helpable canonical mutation reference; and
- one versioned Spill Summary.

Candidate buckets and lane order are derived from the canonical key hash.
Lanes never contain pointers or language-specific handles. The bucket mutation
is a cooperative descriptor reference, not a process-owned lock: observers
validate and help the referenced slot operation.

### Directory operation states

Insert and unlink operations use the canonical intent, phase, target-kind,
target-index, and slot-generation codec. Their forward phase model is:

```text
None
  -> Insert|Unlink / Prepared
  -> TargetSelected
  -> BindingChanged
  -> Complete

Insert / Prepared -> Rejected  # duplicate/cancellation arbitration loss
```

Helpers compare/exchange complete operation words and may act only while the
operation, bucket mutation, selected location, immutable slot binding, and slot
control still identify one exact generation. A paused helper cannot match a
later generation after slot reuse.

### Overflow Directory

The overflow directory is a fixed array of atomic binding cells with exactly
the capacity specified by SMS2. It has no tombstones, linked nodes, resize,
compaction, or process-local owner. A deterministic hash-derived scan provides
placement and lookup.

The capacity invariant is:

```text
live directory bindings <= owned non-Free slots <= configured slot count
```

Therefore arbitrary exact hash collisions cannot reduce configured value
capacity. Exhausting a complete overflow scan after the caller already owns a
new slot is structural inconsistency, not an ordinary index-full outcome.

### Spill Summary

The Spill Summary is a versioned negative-cache witness for one canonical
bucket. It is not a count and is not allowed to create a false negative.

- `Present(X)` requires an overflow scan and carries an exact candidate
  generation witness.
- A revalidated stable-empty overflow scan may exact-CAS `Present(X)` to its
  versioned `Empty(X)` form.
- A current same-bucket overflow witness may repoint `Present(X)` to
  `Present(Y)` under the canonical mutation.
- Uncertainty retains `Present`; it never guesses Empty.

All encodings and reserved-bit rules come from the canonical protocol and
manifest.

## Published Value Generation and Value Slot

A Published Value Generation is one slot generation plus its exact key,
immutable descriptor, immutable payload, directory binding/location, commit
identity, and current lifecycle state.

The slot owns three classes of data:

1. atomic control and helpable directory metadata;
2. ordinary lifecycle metadata made discoverable by the documented release
   marker; and
3. fixed-stride key, descriptor, and payload bytes.

### Slot states

```text
Free(g)
  -> Initializing(g, participant)
  -> Reserved(g)
       |-> Published(g)
       |     -> RemoveRequested(g)
       |            -> Reclaiming(g)
       `-> Aborting(g) -> Reclaiming(g)
  -> Free(g+1) | Retired(g)
```

A failed pre-metadata `Initializing` claim also moves forward through abort and
reclaim; it does not restore the same `Free(g)` control. This forward-only rule
is required by full-capacity proofs and stale-helper exclusion.

### Publication Intent

Publication Intent is immutable for a discoverable slot lifecycle:

- `ExplicitReservation`: the public reservation orders at
  `Initializing -> Reserved`; the exact reserved key is already a duplicate
  witness while its payload remains invisible to acquires.
- `AtomicPublication`: `TryPublish` and segmented publication use Reserved as
  an internal tentative stage and order only at `Reserved -> Published`.

The canonical metadata-ready directory-operation marker is published only
after the claimant has written intent, hash, lengths, offsets, key, and fixed
descriptor metadata. A current directory reference without its required marker
or with an unknown discoverable intent is structural corruption. Stale ordinary
bytes in Free, Retired, or pre-metadata Initializing state are ignored.

### Visibility and removal

- Descriptor and payload bytes become visible only through the exact
  release-publication transition to `Published`.
- Acquire activates an exact lease, then revalidates the directory and slot
  generation before returning borrowed views.
- `Published -> RemoveRequested` is logical removal: new acquires fail, but
  existing exact leases retain valid immutable views.
- Physical reclamation requires exact directory unlink, stable absence of
  active exact leases, helper-word cleanup, and release publication of the next
  Free generation or terminal Retired state.

## Reservation and Reservation Token

A Reservation is a process-local exclusive writable capability over one
announced SMS2 slot generation. Its opaque token fences:

- store incarnation;
- participant incarnation;
- slot index and generation; and
- announced payload length.

Bytes-advanced is monotonic and cannot exceed the announced payload length.
Commit succeeds only after exact advancement. Abort, commit, stale-owner
recovery, store close, and slot-generation change invalidate local writable
projection. Copied language wrappers do not create additional shared owners.

Python writable views may be backed by the packaged native implementation, but
their local lifetime must still be invalidated by the Python wrapper when the
reservation or store context ends.

## Lease Record, Lease Incarnation, and Lease Token

A Lease Incarnation protects one exact published slot generation and carries
the claiming participant token.

### Lease states

```text
Free(i)
  -> Claiming(i, participant, slot-generation)
  -> Active(i, participant, slot-generation)
       |-> Releasing(i)
       `-> Recovering(i)
  -> Free(i+1) | Retired(i)
```

- `Claiming` prevents another claimant from reusing the record while owner and
  slot identity are published.
- `Active` is the only state that authorizes borrowed descriptor/payload views.
- `Releasing` and `Recovering` are exact, idempotently helpable transitions.
- A record retires before its incarnation can make an old token valid again.

The opaque Lease Token fences store, participant, slot generation, lease-record
index, and lease incarnation. Release or recovery invalidates that exact token;
it cannot release a later lease using the same record.

## Recovery Decision

Recovery is an explicit caller action, not a hidden worker. A Recovery Decision
combines:

- a stable snapshot of one participant/slot/lease control tuple;
- exact process-start and PID-namespace evidence where applicable;
- caller policy for current-process recovery;
- classification as current, other-live, stale, unsupported/ambiguous, changed,
  or structurally inconsistent; and
- an exact full-word compare/exchange of only the classified incarnation.

Only safely stale or explicitly quiesced current-process ownership is
recoverable. Unsupported, ambiguous, changing, or live ownership is preserved.
Recovery helpers revalidate every generation-tagged reference before mutation
and never infer abandonment from PID absence across an unproven namespace.

## Store Handle and Local Lifetimes

A Store Handle is process-local and owns one mapped view, one participant
incarnation, cold-lifecycle resources, local operation entry, and local
diagnostic counters. It is not stored in the mapping.

Closing a handle:

1. closes and drains local operation entry;
2. release-publishes its participant as Closing;
3. helps or preserves exact outstanding shared transitions according to SMS2;
4. invalidates its local reservation/lease projections;
5. unmaps before releasing Linux owner-liveness evidence; and
6. completes bounded resource-protocol cleanup.

Other handles remain usable. C++ move-only wrappers and Python context managers
are ownership adapters around the same shared model; they do not add protocol
states.

## Corruption Model

Corruption is a property of persistently invalid mapped structure, not of a
failed caller request.

Potential corruption includes incompatible header topology, malformed reserved
bits, impossible control state/generation/owner combinations, out-of-range
bindings, contradictory stable operation/location tuples, a discoverable slot
without required publication metadata, and impossible lease references.

Before publishing terminal corruption, an implementation must perform the
protocol-required repeated acquire observations and exact no-op
compare/exchanges. Any changed word proves concurrent progress and requires
retry or a bounded contention outcome. Only a stable revalidated defect may
exact-CAS the store control from Ready to Corrupt.

The following never poison the store: invalid caller input, duplicate key,
missing key, capacity pressure, participant/lease-table exhaustion, finite
retry exhaustion, cancellation, legal pause/help races, stale observations, or
ambiguous owner liveness.

Once Corrupt is visible, every distribution fails later mapped-data operations
and reopens closed without projecting mutable payload state.

## Diagnostics Model

Diagnostics combine two explicitly different sources:

- shared observations: protocol identity, configured capacity, slot/lease/
  participant occupancy, directory occupancy and spill state, and terminal
  store control; and
- handle-local counters: retries, helping, bounded-contention exhaustion,
  invalid/stale tokens, recovery classifications, and public failure outcomes.

A diagnostic snapshot is observational and may span several instants. No
correctness decision depends on it. All distributions use equivalent names and
units for shared facts and identify local counters as local.

## Cross-Entity Invariants

1. One successful handle owns exactly one Active-or-closing exact participant
   incarnation.
2. Every owned slot or lease embeds a participant token that was Active before
   the claim ordered.
3. Every directory binding references one exact nonwrapping slot generation.
4. Every published exact key has at most one ordered current value generation.
5. Every borrowed view is backed by one Active exact lease.
6. Logical removal rejects new leases before physical reuse.
7. Physical slot reuse occurs only after directory unlink and stable absence of
   active exact leases.
8. Overflow capacity is sufficient for every owned slot under arbitrary hash
   collisions.
9. A participant, slot, or lease retires before its public or helper token can
   wrap to an earlier valid identity.
10. Cold synchronization may serialize create/open/cleanup, but no successful
    steady-state data operation requires a process-owned store-wide lock.
11. C#, C++, and Python differ only in local ownership adapters; they observe
    the same mapped entities, ordering points, statuses, and corruption latch.
12. Linux cold lifecycle enumeration is confined to one store's owner-artifact
    directory and is independent of unrelated resource-root growth.
