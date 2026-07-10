# Ingest Layout Contract

## Compatibility Goals

The ingest feature extends the existing shared-memory layout contract without
changing the physical region order:

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

The layout remains little-endian, 8-byte aligned, and language-neutral. C# APIs
may wrap this layout, but C++, Python, and future bindings must follow the same
state and visibility rules.

## Versioning

- Layout major version remains `1` unless implementation requires incompatible
  section sizes, field ordering changes, or unsafe behavior for old clients.
- Layout minor version is `2`. Minor `1` introduced pending reservation
  progress in existing slot metadata; minor `2` subsequently added the reuse
  epoch to index, slot, and lease records so lifecycle identities cannot repeat
  when the 32-bit generation rolls over.
- Opening a mapping with an unsupported higher major version fails with
  `IncompatibleLayout`.
- Contract tests must verify header version constants and state numeric values.

## Slot Metadata Meaning During Reservation

Existing slot metadata fields keep their physical positions. During
`SlotPublishing`, fields have these meanings:

- `State`: `SlotPublishing`, meaning pending ingest reservation or internal
  simple publish.
- `Generation`: generation captured by the reservation token.
- `UsageCount`: `0`; readers cannot lease pending reservations.
- `KeyLength`: reserved key length.
- `DescriptorLength`: fixed descriptor length.
- `ValueLength`: announced payload length.
- `PublisherProcessId`: process that created the reservation.
- `Reserved`: payload bytes advanced by the producer.
- `KeyHash`: reserved key hash.
- `DescriptorOffset`: descriptor storage offset for the slot.
- `PayloadOffset`: payload storage offset for the slot.
- `CommittedSequence`: `0` until commit.

When the slot becomes `Published`, `Reserved` is reset to `0`,
`CommittedSequence` is set, and payload plus descriptor bytes become immutable.

## Index Rules

- Reservation inserts a shared key index entry before payload fill begins.
- The index entry points to the pending slot and generation.
- Duplicate detection treats `SlotPublishing`, `SlotPublished`, and
  `SlotRemoveRequested` as key ownership states.
- Acquire finds the index entry but succeeds only when the slot state is
  `SlotPublished` and generation matches.
- Abort and stale recovery remove or tombstone the index entry before the slot
  transitions to free.

## Publication Rule

Commit order:

1. Validate store state, slot index, generation, pending state, and reservation
   progress.
2. Verify `Reserved == ValueLength`.
3. Publish descriptor and payload metadata already written in the slot.
4. Increment the store sequence.
5. Store committed sequence.
6. Transition state from `SlotPublishing` to `SlotPublished` with release
   ordering.

Readers acquire only after step 6 and must see the complete descriptor and
payload metadata for the committed generation.

## Abort Rule

Abort order:

1. Validate store state, slot index, generation, and pending state.
2. Remove or tombstone the pending key index entry.
3. Clear key hash, key length, descriptor length, value length, progress,
   publisher process id, and committed sequence.
4. Transition the slot to `SlotFree`.

No reader can acquire the value during or after abort.

## Stale Reservation Recovery

Recovery scans slots in `SlotPublishing` state and evaluates owner liveness
according to `ReservationRecoveryOptions` and platform support.

Recovery may reclaim a pending reservation only when:
- slot generation still matches the pending index entry.
- owner process is confirmed unavailable, or current-process recovery was
  explicitly allowed for controlled tests/shutdown.
- the pending index entry is removed before slot reuse.

Unsupported owner-liveness checks must not reclaim blindly. They report
unsupported counts or return `UnsupportedPlatform`.

## State Values

Existing numeric state assignments remain stable:

- slot `Free` = 0.
- slot `Publishing` = 1.
- slot `Published` = 2.
- slot `RemoveRequested` = 3.
- slot `Reclaiming` = 4.

`SlotPublishing` is now contractually defined as the pending reservation state
for public ingest APIs and as the internal pre-commit state for simple publish.
No new slot state is required for this feature.

## Portability Rules

- The reservation state machine is defined by shared-memory state, not C#
  object identity.
- `BytesWritten` progress is stored as an integer in shared slot metadata so
  future language clients can enforce exact commit length.
- Writable memory views in other languages must not outlive pending state.
- Commit, abort, and recovery must validate slot generation before changing
  state.
- Future scatter/gather committed values require a separate layout review and
  are out of scope for this feature.
