# Mapped Layout 2.0 Contract

## Identity

| Contract | Value |
|---|---|
| Magic | `SMS2` (`0x32534D53`, little-endian) |
| Layout | major 2, minor 0 |
| Resource protocol | 2 |
| Endianness | little-endian only |
| Architecture | x64 only in layout-2.0 C# support; other architectures reject before mapping mutation |
| Atomic width | aligned 64-bit only |
| Minimum slot generation | 1 |
| Slot-count range | `1..1,048,575` (`2^20-1`) |
| Maximum slot index | `1,048,574` (zero based) |
| Required features | bit 0 (`0x1`): `versioned_empty_spill_summary`; bit 1 (`0x2`): `publication_intent`; bit 2 (`0x4`): `pid_namespace_identity` |

An opener validates identity, required feature bits, complete header bounds, and
requested dimensions before obtaining any descriptor/payload pointer. Unknown
major versions or required features are incompatible. A higher minor version is
compatible only when its required feature set is fully understood and every
section used by this participant has a recognized length/stride.

The current pre-release 2.0 contract requires bits 0, 1, and 2, a mask of `7`,
exactly. A prior v2 binary that supports required-features zero rejects bit 0 as
unknown; a bit-0-only draft rejects bit 1, and a mask-3 draft rejects bit 2. The
current binary rejects every older shape because their spill-summary,
publication-intent, or process-identity semantics cannot be interpreted safely.
These compatibility fences change neither the layout nor resource-protocol
version.

## Canonical section order

```text
0
| StoreHeaderV2                (64-byte aligned length)
| ParticipantRecordV2[]        (64-byte stride)
| PrimaryDirectoryBucket[]     (128-byte stride)
| OverflowBinding[SlotCount]   (8-byte cells)
| LeaseRecordV2[]              (64-byte stride)
| ValueSlotMetadataV2[]        (128-byte stride)
| KeyStorage[SlotCount]        (aligned fixed stride)
| DescriptorStorage[SlotCount] (aligned fixed stride)
| PayloadStorage[SlotCount]    (aligned fixed stride)
` end <= TotalBytes
```

All section starts are at least 8-byte aligned. Header, participant records,
primary buckets, lease records, and slot metadata begin on 64-byte boundaries.
Padding bytes are zero at initialization and ignored by readers.

## Sizing

Let:

```text
PrimaryLaneCount = NextPowerOfTwo(max(32, checked(SlotCount * 4)))
LanesPerBucket   = 8
BucketCount      = PrimaryLaneCount / LanesPerBucket
OverflowCount    = SlotCount
ParticipantCount = configured ParticipantRecordCount (default 64, max 2^20-1)
ParticipantIndexBits = ceil(log2(ParticipantCount + 1))
ParticipantGenerationBits = 28 - ParticipantIndexBits (minimum 8)
ParticipantIndexMask = (1 << ParticipantIndexBits) - 1
ParticipantGenerationMask = (1 << ParticipantGenerationBits) - 1
ParticipantToken = (participantGeneration << ParticipantIndexBits) | (recordIndex + 1)
KeyStride        = Align8(max(1, MaxKeyBytes))
DescriptorStride = Align8(max(1, MaxDescriptorBytes))
PayloadStride    = Align8(max(1, MaxValueBytes))
```

`SlotCount` is in `1..2^20-1`, so `PrimaryLaneCount` is at most `2^22`.
Creation/sizing rejects an out-of-range lock-free slot count before mapping
creation or open. The legacy profile retains its own validation contract.

Bucket mixing maps every key to two buckets and one canonical home bucket. The
layout calculator uses checked 64-bit arithmetic for every product/addition and
aligns each section. `CalculateRequiredBytes(StoreProfile.LockFree, ...)` and
`CreateLockFree(...)` use this one canonical calculation. An existing mapping's
header values, offsets, lengths, and strides must exactly match the requested
dimensions.

## Atomic record rules

1. Atomic words are naturally 8-byte aligned both by record stride and field
   offset.
2. A shared atomic word is never read or written through a 32-bit field.
3. A participant uses `Interlocked` for read-modify-write and `Volatile` acquire/
   release access according to the memory-ordering contract.
4. Non-atomic metadata is mutable only while an atomic state grants exclusive
   initialization/reclamation ownership; it is immutable while published or
   protected by a lease.
5. No on-memory field contains a pointer, `nint`, `size_t`, managed object ID,
   OS handle, enum with unspecified width, or native structure.
6. Every remaining reserved/padding field is zero on creation and ignored on
   open unless a future required feature assigns it. Slot offset 52 is assigned
   by required-feature bit 1; header offsets 264/272 and participant offset 32 are
   assigned by bit 2 and are not padding.

## Binding codec

Every directory and mutation word uses this unsigned bit pattern through a
signed 64-bit atomic storage location:

```text
bits  0..30: slot index plus one (1..2^31-1)
bits 31..63: slot generation (1..2^33-1)
all zero:     empty
```

Decoding zero, slot zero/out-of-range, generation zero, or a binding whose slot
control generation differs is invalid/stale. Exact CAS always compares the full
64 bits. A slot reaching the terminal generation is retired and never wraps.

## Spill-summary codec

The first atomic word of each canonical primary bucket is a versioned negative
cache encoded independently from `IndexBinding` while preserving the same exact
slot generation:

```text
bits  0..19: candidate slot index plus one (1..2^20-1)
bits 20..52: candidate slot generation (1..2^33-1)
bit      53: Present (1 = overflow scan required, 0 = confirmed empty)
bits 54..63: reserved zero
all zero:    initial EmptyNone
```

`Present(X)` and `Empty(X)` carry the same exact candidate identity. A completed
empty scan clears with one full-word `Present(X) -> Empty(X)` CAS and never
restores zero. Candidate identities never repeat because slot generations never
wrap, so a delayed setter's old expected Empty token and a delayed clearer's old
expected Present token cannot match a later summary version. Index zero,
generation zero, a slot index greater than or equal to the current header's
configured `SlotCount`, or a reserved bit is corruption; the token is retained
and the mapping fails closed.

## Store header requirements

The header contains, in fixed-width fields:

- identity/version/header length/resource protocol;
- required and optional feature masks;
- total bytes and random nonzero Store ID;
- atomic store control;
- all public dimensions;
- participant-record count/offset/length/stride;
- primary bucket/lane/overflow dimensions;
- every section offset, length, and stride;
- PID-namespace identity/mode at exact byte offsets 264/272;
- bounded diagnostic counter offsets/counts;
- header checksum only if a future required feature defines its atomic update
  behavior (not required by 2.0).

`StoreHeaderV2` is encoded/validated by explicit constants and offsets. Managed
`Marshal.SizeOf` is verified by tests but is not the language-neutral authority.
On Linux `PidNamespaceId` is the positive numeric token parsed from
`/proc/self/ns/pid`; on Windows it is zero. Creation writes it and atomic mode
`Enabled=1` (or `Mixed=2` when unproven) before `Ready`. A different or
unproven Linux opener release-publishes irreversible Mixed before its first
Registering CAS and then retains ordinary KV access.

Store control values are `Initializing=1`, `Ready=2`, `Corrupt=3`, and
`Unsupported=4`. Initialization release-publishes `Ready`. A path that has
stabilized and revalidated persistent mapped structural corruption full-word-
CASes `Ready` to `Corrupt`; the state is terminal. Each public mapped-data
operation acquire-loads the control before projection or mutation. A corrupt
mapping rejects later opens as incompatible. Caller-owned malformed inputs,
ordinary capacity/contended/canceled outcomes, and legal raced observations do
not change this word. These are mapped atomics, not a lock or OS primitive.

## Primary directory bucket

One 128-byte bucket contains:

```text
offset  0: SpillSummary (atomic versioned Present/Empty u64)
offset  8: Mutation (atomic binding or zero)
offset 16: Lane[8]  (eight atomic bindings)
offset 80: reserved zero padding through byte 127
```

The mutation binding refers to a slot whose generation-tagged
`DirectoryOperation` identifies insert or unlink and phase. It is publishable
only after all descriptor fields are initialized. A helper may act only when the
binding, operation, and slot control generations agree. A helper that finds a
stale generation may CAS-clear only that exact mutation word.

An exact current insert helper CAS-publishes `Present(candidate)` before any
overflow-cell CAS and revalidates both the operation and canonical mutation
afterward. Before releasing a completed mutation that may have touched the
summary, a helper scans overflow for a stable current binding whose hash maps to
that canonical bucket. A current witness retains Present. Only an empty full
scan followed by exact operation/mutation revalidation permits the captured
Present token to become its matching Empty token. Cancellation, deadline,
instability, or a CAS loss retains conservative Present and leaves the mutation
helpable.

## Overflow directory

The overflow section contains `SlotCount` atomic binding cells. It has no
sentinel other than zero, no tombstones, and no header count required for
correctness. A directory binding may exist in exactly one primary or overflow
cell. Diagnostics derive occupancy by bounded scan.

Primary lanes, overflow cells, and versioned Present spill summaries are exact
atomic directory reference words. Classification keeps the captured raw word
separate from its decoded slot binding: a lane/cell raw word equals the binding,
while a summary raw word is the complete encoded `Present(binding)` value. A
would-be corrupt binding classification is reportable only after the same raw
source word is acquire-read unchanged around a repeated stable, fully
shape-validated snapshot of the decoded slot binding. Source movement requires
a budgeted lookup or maintenance retry instead. This source/slot/source rule is
joint validation, not a mapped multi-word atomic or a new shared owner.

## Participant record

One 64-byte record is claimed only under the cold lifecycle lock and contains:

```text
offset  0: Control (atomic u64; includes PID)
offset  8: IdentityKind (i32)
offset 12: reserved zero (i32)
offset 16: ProcessStartValue (i64)
offset 24: OpenSequence (i64)
offset 32: PidNamespaceId (u64)
offset 40: reserved zero through byte 63
```

Participant control encoding is:

```text
bits  0..2:  state (Free=0, Registering=1, Active=2, Closing=3,
                    Recovering=4, Reclaiming=5, Retired=6; 7 invalid)
bits  3..30: participant incarnation (high unused bits zero per layout codec)
bits 31..62: positive PID while Registering/Active/Closing/Recovering
bit      63: reserved zero
```

Under the cold lock, `Free -> Registering` atomically publishes PID and
incarnation before other identity fields are written. The exclusive claimant
writes the exact admitted PID namespace before an opener release-publishes
`Active` and before any data control may reference it. A stable Active snapshot
jointly fences this field with control and compares it with the current
namespace before PID/start lookup. Registering presence-only classification
uses the header identity only while mode is Enabled; Mixed makes it Unsupported
because its per-record ordinary fields may still be mixed old/new values. A
recovery reader snapshots control before acquire-loading mode, and the opener
publishes Mixed before its claim. The 28-bit hot token packs
record index plus one in the layout's `ParticipantIndexBits` and the same
incarnation in the remaining bits. The record is not reused until a reference
scan proves no exact token remains.

The encoded index-plus-one must be in `1..ParticipantCount`; zero is an invalid
token. Participant generation starts at one and may not exceed
`ParticipantGenerationMask` even though the participant control reserves a
28-bit physical field. Bits above the configured generation mask in both control
and token are zero. Closing/recovery of the terminal configured generation
publishes `Retired`; it never increments to zero or uses the larger physical
field range.

Identity-kind assignments are `Unknown=0`, `WindowsProcessCreationFileTime=1`,
and `LinuxProcStartTicks=2`. Unknown identity permits normal access but explicit
stale-owner recovery remains conservative/unsupported. Values 3 and above are
unknown required semantics for recovery and must not be guessed.

## Slot metadata

The 128-byte slot metadata record has these exact offsets:

```text
offset   0: Control (atomic u64; state + generation + participant token)
offset   8: DirectoryBinding (u64; immutable exact binding for this lifecycle)
offset  16: DirectoryLocation (atomic u64)
offset  24: DirectoryOperation (atomic u64)
offset  32: KeyHash (u64)
offset  40: KeyLength (i32)
offset  44: DescriptorLength (i32)
offset  48: ValueLength (i32)
offset  52: PublicationIntent (i32)
offset  56: BytesAdvanced (atomic i64)
offset  64: CommitSequence (i64)
offset  72: KeyOffset (i64)
offset  80: DescriptorOffset (i64)
offset  88: PayloadOffset (i64)
offset  96: reserved zero through byte 127
```

Slot control encoding is:

```text
bits  0..2:  Free=0, Initializing=1, Reserved=2, Published=3,
             RemoveRequested=4, Aborting=5, Reclaiming=6, Retired=7
bits  3..35: nonzero 33-bit slot generation
bits 36..63: complete 28-bit participant token in Initializing/Reserved;
             zero in Free/Published/RemoveRequested/Aborting/Reclaiming/Retired
```

The participant token in `Initializing`/`Reserved` must be structurally valid
for the header's configured `ParticipantRecordCount`: index-plus-one is in
range, incarnation is nonzero and within its configured token bits, and no
unused bit is set. Every slot generation is nonzero and bounded. `Retired` is
valid only at the terminal slot generation. Any other state/generation/owner
shape is corruption; in particular, an owned unowned-state control is not a
current binding, and only a structurally valid strictly newer control may make
an older directory binding stale.

Publication-intent encoding is:

```text
0: None
1: ExplicitReservation  (public TryReserve / ValueReservation workflow)
2: AtomicPublication    (public TryPublish / TryPublishSegments workflow)
3..2^31-1 and negative values: invalid
```

`PublicationIntent` is ordinary owner-written metadata. The successful
`Free -> Initializing` claimant writes it with the slot's ordinary
`DirectoryBinding`, key/length/offset metadata, and descriptor before
release-publishing the current-generation `Insert/Prepared`
`DirectoryOperation`. That exact nonzero operation word is the metadata-ready
marker; its later phase/operation-intent changes and eventual clear remain exact-
generation CAS operations. Canonical-bucket mutation and directory-cell binding
publication MUST follow the marker. Intent is immutable for the rest of that
slot generation.

`Pre-metadata Initializing` means exact current `Initializing` with a zero
`DirectoryOperation` and no current-generation canonical mutation or directory
cell referencing the slot. `None` or stale/unknown intent bytes are ignored in
that state and while `Free`/`Retired`. If stale-owner recovery changes that
unmarked claim directly to unreferenced `Aborting`/`Reclaiming`, cleanup also
does not interpret the stale ordinary intent bytes. A current-generation
mutation/cell without the preceding valid operation marker is structural
corruption, not an alternative publication path. While the marker or any valid
later current-generation reference exists, only intent values 1 and 2 are
valid; an unknown intent is `CorruptStore`. Reclaim does not zero the field. The
next exclusive claimant overwrites it before publishing the next lifecycle's
marker.

The intent selects public ordering without changing slot states. For
`ExplicitReservation`, `Initializing -> Reserved` is the public `TryReserve`
ordering point and `Reserved` is a duplicate-key witness. For
`AtomicPublication`, `Reserved` is an internal tentative stage;
`Reserved -> Published` is the public convenience-publication ordering point,
and only `Published`/`RemoveRequested` is its terminal duplicate-key witness.
An intent-aware lookup may help/revalidate a tentative lifecycle and may return
`StoreBusy` on bounded exhaustion, but it must not report `DuplicateKey` solely
from `Initializing` or `Reserved(AtomicPublication)`.

This intent classification does not give duplicate detection priority over
physical allocation in a race. After an initial same-key lookup returns absent,
the candidate claim precedes final directory arbitration and may return
`StoreFull` when every slot is non-Free, including when tentative lifecycles
occupy the remaining capacity.

The public result is certified without adding a layout field. One local
`Int64[SlotCount]` buffer per open handle records a first forward collect of slot
controls; a second forward collect must be exactly equal, structurally valid,
and entirely non-`Free`. The full instant is the candidate point between the
collects, confirmed by the completed second pass. Controls never roll back
within one generation, so equality cannot conceal ABA. The buffer and its
nonblocking local guard are process memory, not mapped protocol state, and no
shared counter, lock word, named primitive, or OS synchronization participates.
An equal malformed control fails `CorruptStore`; it never contributes occupancy
evidence. A free/moving control or local guard conflict is contention governed
by the caller's operation-wide wait policy, not `StoreFull`.

Directory-location encoding is:

```text
all zero:    None
bits  0..1:  kind (Primary=1, Overflow=2; 0 only for None; 3 invalid)
bits  2..23: zero-based absolute cell index within the selected section
bits 24..56: exact nonzero 33-bit slot generation
bits 57..63: reserved zero
```

The index must be within `PrimaryLaneCount` or `OverflowCount` for its kind. A
nonzero location is valid only when its generation equals the directory binding
and current slot lifecycle generation.

Directory-operation encoding is:

```text
bits  0..1:  intent (None=0, Insert=1, Unlink=2; 3 invalid)
bits  2..4:  phase (None=0, Prepared=1, TargetSelected=2,
                    BindingChanged=3, Rejected=4, Complete=5; 6..7 invalid)
bits  5..6:  target kind (None=0, Primary=1, Overflow=2; 3 invalid)
bits  7..28: zero-based target cell index
bits 29..61: exact nonzero 33-bit slot generation
bits 62..63: reserved zero
```

The only legal zero word is `intent=None, phase=None, target=None`. `Prepared`
and `Rejected` require target kind/index zero but retain the exact nonzero slot
generation; Rejected is the terminal
same-key/canceled insert outcome with no binding installed. `TargetSelected`
and `BindingChanged` require a section-valid target. Insert `Complete` also
retains that target. Unlink `Complete` may use target None/index zero when a
generation-matching binding was already absent and the helper therefore had no
cell to clear. Insert and Unlink otherwise use the same phases;
`BindingChanged` means binding installed or exact binding cleared according to
intent. Helpers CAS the full operation word, never one subfield. Every nonzero
phase must carry the same generation as the mutation binding and current
lifecycle before the helper performs a write.

Every write derived from an operation is either an exact CAS on a
generation-tagged operation/location word or an exact CAS on a generation-tagged
directory binding. If a target-cell CAS succeeds but the old operation can no
longer advance, the helper rolls back only that exact old binding. A stale
helper may therefore leave recognizable old-generation residue for another
caller to clear, but it cannot overwrite or unlink a later lifecycle.

Directory-location publication adds no wire state. Its validation tuple is the
canonical mutation word, exact operation word, current location word, slot
control, immutable directory binding, and selected or competing target cells.
A terminal invalid classification requires two identical acquire collections of
that tuple, exact no-op compare/exchanges confirming every atomic member, and a
fresh immutable-binding read; any lost comparison is progress and requires
retry rather than corruption.

When cancellation hands an Insert to `Unlink/Prepared`, an empty target or a
structurally valid different in-range binding is legal target-loss progress;
malformed or out-of-range target state is corruption. The first valid
`Unlink/Prepared` location publisher wins, and a loser exact-clears only its
distinct recovered old binding. `Unlink/TargetSelected` may observe a valid
same-generation alternate location and exact-cleans both old-binding witnesses
and that alternate location while preserving empty cells and valid replacement
bindings. If the source is lost after a location CAS, the publisher withdraws
only its exact old target and location; it never removes a committed Insert
successor or another valid replacement.

Strictly older location residue is exact-cleanable. A future location is benign
reuse only when another member of the old tuple has moved; a future location
inside the confirmed exact old-generation tuple is corruption and is preserved
for diagnosis. These rules change validation and helping only; all offsets and
operation/location encodings remain unchanged.

`protocol/layout-v2.0.md` and fixtures reproduce these authoritative offsets and
encodings; tests reject drift before implementation proceeds.

## Lease record

The 64-byte lease record contains:

```text
offset  0: Control (atomic u64; state + record incarnation + participant token)
offset  8: SlotBinding (u64)
offset 16: AcquireSequence (i64)
offset 24: reserved zero through byte 63
```

Lease control encoding is:

```text
bits  0..2:  Free=0, Claiming=1, Active=2, Releasing=3,
             Recovering=4, Retired=5
bits  3..35: nonzero 33-bit lease-record incarnation
bits 36..63: complete 28-bit participant token in Claiming/Active;
             zero in Free/Releasing/Recovering/Retired
```

Record incarnation starts at one, advances before reuse, and retires the record
instead of wrapping.

`Claiming`/`Active` require a structurally valid participant token for the
configured participant table. `Free`/`Releasing`/`Recovering`/`Retired` require
participant zero; `Retired` is valid only at terminal record incarnation, and
state values 6-7 are invalid. Any invalid state/incarnation/owner/token shape is
`CorruptStore`.

An exhausted lease scan is only a capacity candidate because reusable records
can rotate behind a sequential scanner. Each open handle may eagerly own one
private `Int64[LeaseRecordCount]` snapshot protected by a nonblocking local
guard. The proof reads every control in record order, records the candidate
instant between passes, and repeats the collect in the same order. Only two
structurally valid, entirely non-`Free`, exactly equal collects confirm that
candidate and permit `LeaseTableFull`. Every reuse advances incarnation or
retires, so equality cannot hide ABA. A malformed control is `CorruptStore`; a
free/moving control or local guard conflict follows the operation-wide wait
policy as contention. The buffer and guard are private process state and add no
mapped field, shared counter, named primitive, or OS synchronization.

## Byte-storage rules

- Keys are exact opaque nonempty bytes. Key equality always checks length and
  every byte after hash/binding filtering.
- Descriptor and payload lengths may be zero.
- Descriptor and payload bytes are immutable after publication.
- Unused bytes in a fixed stride have no semantic value and are never projected
  beyond the stored length.
- Ordinary slot metadata is semantically dead while its control is Free or
  Retired. Reclaim helpers do not zero it; the next successful Initializing
  claimant overwrites every required lifecycle field before directory
  publication, preventing a delayed reclaim helper from erasing a reused slot.
- Aborted/reclaimed bytes need not be securely erased; trusted mapped writers
  and data remanence policy remain outside this feature. Metadata lengths and
  generation fencing make those bytes inaccessible through safe APIs.

## Initialization and corruption

The cold attempt that physically creates the region zeroes/initializes every
atomic word, slot generation, participant and lease-record incarnation, fixed
offset, and Store ID before publishing `Ready`. Physical creation disposition,
not open mode or observed zero bytes, is the sole initialization authority. An
opener of an existing zero header never writes it: `CreateNew` reports
`AlreadyExists`, `CreateOrOpen` reports `StoreBusy`, and `OpenExisting` reports
`IncompatibleLayout`. Platform abandonment and stale-resource cleanup may make
a later attempt the physical creator, but opening an extant unpublished region
does not. See `protocol/resource-naming-v2.md` for the ordered cold transaction.

Impossible bounds, overlapping sections, unsupported features, misalignment,
invalid/reserved operation or location bits, generation mismatches, or
current-generation bindings outside their legal sections are
reported as incompatible/corrupt before unsafe projection. Normal stale
transitional state is recovered/helped under the concurrency contract rather
than globally marking the store corrupt.
