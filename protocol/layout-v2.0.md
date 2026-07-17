# Mapped Layout 2.0

Layout 2.0 is the store's sole current, language-neutral mapped format. All
multi-byte values are little-endian two's-complement integers. The supported
runtime architecture is x86-64; every shared atomic word is an 8-byte-aligned
signed `int64` accessed with acquire/release `Volatile` operations or
sequentially consistent full-word read-modify-write operations. C# uses
`Volatile`/`Interlocked`; native C++ uses the equivalent qualified mapped-atomic
adapter, and Python delegates mapped-state transitions to that native adapter.

The magic integer is `0x32534d53`, whose little-endian bytes spell `SMS2`.
The layout version is `2.0` and the shared-resource protocol is `2`.
Required-feature bit 0 (value `0x1`) is
`versioned_empty_spill_summary` and bit 1 (value `0x2`) is
`publication_intent`; bit 2 (value `0x4`) is `pid_namespace_identity`.
This pre-release v2 contract requires the exact mask `7`. Creators write
optional-feature mask `0`.
Required-features-zero, bit-0-only, and mask-3 draft v2 binaries reject the current
mapping, and the current binary rejects all older shapes, before payload
projection.

## Canonical region order

```text
StoreHeaderV2[512]
ParticipantRecordV2[participant_record_count]      stride 64
PrimaryDirectoryBucketV2[primary_bucket_count]     stride 128
OverflowBinding[slot_count]                        stride 8
LeaseRecordV2[lease_record_count]                  stride 64
ValueSlotMetadataV2[slot_count]                    stride 128
KeyStorage[slot_count]                             Align8(max(1,max_key_bytes))
DescriptorStorage[slot_count]                      Align8(max(1,max_descriptor_bytes))
PayloadStorage[slot_count]                         Align8(max(1,max_value_bytes))
```

Every section begins on an 8-byte boundary. The participant, primary bucket,
lease, and slot sections begin on 64-byte boundaries. Arithmetic is checked.

```text
primary_lane_count = NextPowerOfTwo(max(32, checked(slot_count * 4)))
primary_bucket_count = primary_lane_count / 8
participant_index_bits = ceil(log2(participant_record_count + 1))
participant_generation_bits = 28 - participant_index_bits
```

Participant count is 1..1,048,575 and generation bits are therefore at least
8. Slot count is also 1..1,048,575, which bounds `primary_lane_count` at
`2^22`. Required bytes is the aligned end of payload storage.

## Store control

The aligned atomic header control encodes `Initializing=1`, `Ready=2`,
`Corrupt=3`, and `Unsupported=4`. Initialization release-publishes `Ready`.
After exact-reference/race revalidation proves persistent mapped structural
corruption, any participant may full-word-CAS `Ready` to terminal `Corrupt`.
Every mapped-data operation acquire-loads this word before a new projection or
mutation, and later openers reject a corrupt mapping as incompatible. Invalid
caller-owned inputs and normal capacity, contention, cancellation, disposal,
or concurrent-lifecycle outcomes must not set the latch. No OS synchronization
or process-held lock participates in this mechanism.

## StoreHeaderV2: 512 bytes

The header itself is cache-line aligned. All reserved bytes are zero when the
creator release-publishes `Ready`; current readers ignore reserved ordinary
bytes but validate every declared field below before projecting another
section.

| Field | Type | Offset |
|---|---:|---:|
| `magic` | uint32 | 0 |
| `layout_major_version` | uint16 | 4 |
| `layout_minor_version` | uint16 | 6 |
| `header_length` | int32 | 8 |
| `resource_protocol_version` | int32 | 12 |
| `required_features` | uint64 | 16 |
| `optional_features` | uint64 | 24 |
| `total_bytes` | int64 | 32 |
| `store_id` | uint64 | 40 |
| `control` | atomic uint64 | 48 |
| `sequence` | atomic uint64 | 56 |
| `slot_count` | int32 | 64 |
| `lease_record_count` | int32 | 68 |
| `participant_record_count` | int32 | 72 |
| `max_key_bytes` | int32 | 76 |
| `max_descriptor_bytes` | int32 | 80 |
| `max_value_bytes` | int32 | 84 |
| `participant_index_bits` | int32 | 88 |
| `participant_generation_bits` | int32 | 92 |
| `participant_offset` | int64 | 96 |
| `participant_length` | int64 | 104 |
| `participant_stride` | int32 | 112 |
| `primary_lane_count` | int32 | 116 |
| `primary_bucket_count` | int32 | 120 |
| `primary_bucket_stride` | int32 | 124 |
| `primary_directory_offset` | int64 | 128 |
| `primary_directory_length` | int64 | 136 |
| `overflow_directory_offset` | int64 | 144 |
| `overflow_directory_length` | int64 | 152 |
| `overflow_stride` | int32 | 160 |
| `lease_stride` | int32 | 164 |
| `lease_registry_offset` | int64 | 168 |
| `lease_registry_length` | int64 | 176 |
| `slot_metadata_stride` | int32 | 184 |
| `key_stride` | int32 | 188 |
| `slot_metadata_offset` | int64 | 192 |
| `slot_metadata_length` | int64 | 200 |
| `key_storage_offset` | int64 | 208 |
| `key_storage_length` | int64 | 216 |
| `descriptor_stride` | int32 | 224 |
| `payload_stride` | int32 | 228 |
| `descriptor_storage_offset` | int64 | 232 |
| `descriptor_storage_length` | int64 | 240 |
| `payload_storage_offset` | int64 | 248 |
| `payload_storage_length` | int64 | 256 |
| `pid_namespace_id` | uint64 | 264 |
| `pid_namespace_mode` | atomic uint64 | 272 |
| reserved | bytes | 280 |

The opener recomputes all derived bit counts, strides, offsets, lengths, and
`required_bytes` with checked arithmetic. Declared `total_bytes` must cover the
computed end and must not exceed the actual mapped capacity used for
projection. `store_id` is nonzero. Store control is one of Initializing=1,
Ready=2, Corrupt=3, or Unsupported=4; PID-namespace mode is Enabled=1 or the
irreversible Mixed=2 state once the header is Ready.

## Shared atomic ordering

| Operation | Required order |
|---|---|
| Observe a control, binding, mutation, location, operation, summary, or counter | acquire load |
| Single-writer publication after immutable metadata/bytes are initialized | release store |
| Claim, help, handoff, state advance, release, generation advance, or corruption latch | sequentially consistent full-word compare/exchange or RMW |
| Failed compare/exchange | acquire or stronger |

Every atomic address must be naturally 8-byte aligned and the native primitive
must report always-lock-free on the qualified x86-64 target. `volatile`, a
process-local mutex, or a named/file lock is not a mapped-memory-ordering
substitute. No-wait, finite, infinite, and canceled operations share one
operation-wide budget; retry loops check that budget and cancellation
periodically instead of resetting a timeout at each sub-operation.

## Fixed records

### ParticipantRecordV2: 64 bytes

| Field | Type | Offset |
|---|---:|---:|
| `control` | atomic uint64 | 0 |
| `identity_kind` | int32 | 8 |
| reserved | int32 | 12 |
| `process_start_value` | int64 | 16 |
| `open_sequence` | int64 | 24 |
| `pid_namespace_id` | uint64 | 32 |
| reserved | bytes | 40 |

Participant control uses state bits 0..2, incarnation bits 3..30, PID bits
31..62, and a zero reserved bit 63. States are Free=0, Registering=1,
Active=2, Closing=3, Recovering=4, Reclaiming=5, Retired=6. The hot token is
`generation << participant_index_bits | record_index + 1`. Generation begins
at one and terminal generations retire instead of wrapping.

`Closing` is published by a handle only after its local operation gate drains,
and before cleanup begins. `Closing` and `Recovering` are claim-closed handoff
states: exact referenced slot/lease controls may be recovered without PID/start
liveness classification, while participant reuse still requires a fresh exact-
token absence scan followed by full-word `Closing/Recovering -> Reclaiming` CAS.

The header stores the creator's `pid_namespace_id` at byte offset 264 and an
atomic recovery mode at offset 272 (`Enabled=1`, `Mixed=2`). Linux uses the
positive numeric token parsed from `/proc/self/ns/pid`; Windows stores zero. A
different or unproven Linux opener release-publishes the irreversible Mixed mode
before its first Registering CAS, then continues ordinary KV access. Registering
recovery is unsupported in Mixed. A Registering record writes its own observed
value before Active publication; stable Active snapshots include that value and
may be classified only after an exact current-namespace comparison. Closing and
Recovering remain explicit, namespace-independent handoffs.

### PrimaryDirectoryBucketV2: 128 bytes

| Field | Type | Offset |
|---|---:|---:|
| `spill_summary` | atomic uint64 | 0 |
| `mutation` | atomic uint64 | 8 |
| `lane[8]` | atomic uint64[8] | 16 |
| reserved | bytes | 80 |

`spill_summary` is a versioned negative-cache word. Bits 0..19 contain candidate
slot-index-plus-one, bits 20..52 contain its exact 33-bit slot generation, bit
53 is Present, and bits 54..63 are zero. Raw zero is only initial EmptyNone.
Overflow publication full-word-CASes the exact prior version to
`Present(candidate)` before its cell CAS. After a stable empty full scan under
the exact canonical mutation, cleanup CASes only `Present(X) -> Empty(X)` and
never restores zero. Lookup scans overflow only for Present. Malformed tokens
fail closed. When a full scan instead finds another exact current spill Y, the
same revalidated mutation may full-word-CAS `Present(X) -> Present(Y)` so the
summary continues to name a directly valid witness. This may repeat a Present
word only while Y's nonwrapping binding remains current; it never reintroduces
an Empty identity and cannot validate a prior stable-empty clearer. `mutation`
contains the exact binding of a fully described helpable
insert or unlink operation; it is not a process-owned lock.

### LeaseRecordV2: 64 bytes

| Field | Type | Offset |
|---|---:|---:|
| `control` | atomic uint64 | 0 |
| `slot_binding` | uint64 | 8 |
| `acquire_sequence` | int64 | 16 |
| reserved | bytes | 24 |

Lease control uses state bits 0..2, a 33-bit record generation in bits 3..35,
and the complete participant token in bits 36..63. States are Free=0,
Claiming=1, Active=2, Releasing=3, Recovering=4, Retired=5.

### ValueSlotMetadataV2: 128 bytes

| Field | Type | Offset |
|---|---:|---:|
| `control` | atomic uint64 | 0 |
| `directory_binding` | uint64 | 8 |
| `directory_location` | atomic uint64 | 16 |
| `directory_operation` | atomic uint64 | 24 |
| `key_hash` | uint64 | 32 |
| `key_length` | int32 | 40 |
| `descriptor_length` | int32 | 44 |
| `value_length` | int32 | 48 |
| `publication_intent` | int32 | 52 |
| `bytes_advanced` | atomic int64 | 56 |
| `commit_sequence` | int64 | 64 |
| `key_offset` | int64 | 72 |
| `descriptor_offset` | int64 | 80 |
| `payload_offset` | int64 | 88 |
| reserved | bytes | 96 |

Slot control uses state bits 0..2, a 33-bit generation in bits 3..35, and a
28-bit participant token in bits 36..63 only while Initializing or Reserved.
States are Free=0, Initializing=1, Reserved=2, Published=3,
RemoveRequested=4, Aborting=5, Reclaiming=6, Retired=7.

`publication_intent` assignments are None=0, ExplicitReservation=1, and
AtomicPublication=2; every other signed 32-bit value is invalid on a current
discoverable lifecycle. The exclusive claimant writes intent before publishing
the current-generation Insert/Prepared directory operation and does not change
it during that generation. The release publication of that exact nonzero
operation is the metadata-ready marker; canonical mutation and directory-cell
binding publication follow it. Pre-metadata Initializing is exactly a current
Initializing state with operation zero and no current-generation mutation/cell
reference. It, Free, and Retired ignore stale/None intent bytes. A current
mutation/cell without the required operation marker is corruption. A marked
or referenced current lifecycle requires a known nonzero intent and otherwise
fails closed as corrupt. Direct unreferenced cleanup of a recovered pre-metadata
claim does not interpret stale ordinary intent bytes.

ExplicitReservation orders publicly at Initializing-to-Reserved and its
Reserved state is a duplicate-key witness. AtomicPublication covers
`TryPublish` and `TryPublishSegments`; its Reserved state remains an internal
tentative stage and the public operation orders only at Reserved-to-Published.
Lookup must not return DuplicateKey solely from Initializing or from
Reserved(AtomicPublication). A bounded contender may return StoreBusy while it
cannot classify a terminal key owner. StoreFull remains physical: every non-Free
slot, including tentative Initializing/Reserved, is unavailable for reuse. A
raced insertion whose initial same-key lookup returned absent may return
StoreFull at candidate claim before final directory arbitration; duplicate
status does not take precedence over genuine physical exhaustion in that race.
Scan exhaustion is provisional. A public StoreFull result requires two forward
collects of every slot control to be structurally valid, non-Free, and exactly
equal; the confirmed ordering point lies between the collects. Slot controls
never roll back within one generation, so equality cannot hide ABA. The scratch
snapshot and nonblocking guard are private process memory and add no mapped
field, shared counter, named primitive, or OS synchronization.
Every collected control must obey the complete state/generation/owner rules
below. An equal malformed word is corruption, never StoreFull evidence; a free
or moving word or local guard conflict is contention under the caller's wait
policy.

Lease-record scan exhaustion is provisional for the same reason. A public
LeaseTableFull result requires two forward collects of every lease control to
be structurally valid, non-Free, and exactly equal; its confirmed ordering point
lies between those collects. Claiming/Active require a configured, structurally
valid participant token; Free/Releasing/Recovering/Retired require participant
zero, and Retired requires the terminal incarnation. Invalid state,
incarnation, owner shape, or participant token is corruption. Lease incarnation
advance or retirement prevents control ABA. Each open handle may keep an eager
private `int64[lease_record_count]` snapshot behind a nonblocking local guard;
neither is mapped state or OS/cross-process synchronization.

## Binding and descriptor words

Directory bindings use slot-index-plus-one in bits 0..30 and slot generation
in bits 31..63. Zero is empty. Generation zero and index-plus-one zero are
invalid.

Spill-summary identities use the layout's tighter slot cap rather than the
general binding shape: index-plus-one bits 0..19, generation bits 20..52,
Present bit 53, and ten reserved-zero bits. Present and Empty versions preserve
the same candidate identity, and nonwrapping generation makes every later
candidate version distinct. Every nonzero identity must name a slot below the
header's configured `slot_count`; a mapping-out-of-range identity is corrupt
even when its reserved bits and generation are otherwise codec-valid.

Directory location is zero for None; otherwise kind Primary=1 or Overflow=2
occupies bits 0..1, the absolute section cell index occupies bits 2..23, and
the exact nonzero 33-bit slot generation occupies bits 24..56. Bits 57..63 are
zero.

Directory operation uses intent None=0, Insert=1, Unlink=2 in bits 0..1;
phase None=0, Prepared=1, TargetSelected=2, BindingChanged=3, Rejected=4,
Complete=5 in bits 2..4; target kind in bits 5..6; and target index in bits
7..28. The exact nonzero 33-bit slot generation occupies bits 29..61 and bits
62..63 are zero. Helpers compare and replace the complete word.

Prepared and Rejected carry no target. TargetSelected and BindingChanged carry
a section-valid target. Insert Complete retains its target; Unlink Complete may
carry no target only when the exact binding was already absent and no directory
cell required clearing.

Every nonzero operation/location generation must equal both the bucket mutation
binding generation and the current slot-control generation before a helper
acts. Phase changes, location publication/clear, and directory-cell mutation use
exact full-word CAS. A helper paused across reclaim/reuse cannot match a newer
lifecycle; any old binding it installed remains generation-tagged and may be
exactly rolled back or helped clear without touching the newer generation.

For directory-location publication, the validated tuple is the canonical
mutation word, exact operation word, current location word, slot control,
immutable directory binding, and selected or competing target cells. A helper
MUST NOT report terminal corruption until two acquire collections return the
same tuple, exact no-op compare/exchanges confirm every atomic member, and a
fresh immutable-binding read still agrees. A lost comparison proves concurrent
progress and requires retry.

Cancellation may hand an Insert to `Unlink/Prepared`. At that handoff, an empty
target or structurally valid different in-range binding is legal target-loss
progress, while a malformed or out-of-range target is corruption. The first
valid `Unlink/Prepared` location publication wins; a losing publisher may
exact-clear only its distinct recovered old binding. `Unlink/TargetSelected`
MUST tolerate a structurally valid same-generation alternate location and
exact-clean its selected and alternate old-binding witnesses plus the alternate
location, preserving empty cells and valid replacements. Source loss after a
location CAS withdraws only the publisher's exact old target and location; a
committed Insert successor or valid replacement is preserved.

An older location is stale exact-cleanable residue. A future location proves
benign reuse only when another old-tuple member has moved; a future location
inside the confirmed exact old-generation tuple is corruption and remains
untouched for diagnosis. This protocol changes validation and helping only; the
directory-operation and directory-location offsets and encodings are unchanged.

An atomic directory-reference read is a cached witness. The exact raw source
word and its decoded slot binding are separate: a primary/overflow word equals
the binding, while a spill-summary source is the complete encoded
`Present(binding)` word. If later slot classification would be corrupt, readers
or maintenance helpers must acquire-read that exact raw source word, obtain a
fresh stable snapshot of the separately decoded slot binding, and acquire-read
the same raw word again. A source that no longer equals the exact raw reference
on either side is a stale observation that requires a fresh lookup or
maintenance retry; only an unchanged reference word enclosing a repeated
invalid slot snapshot is corruption. Slot classification validates the complete
control shape: Initializing/Reserved require a configured, structurally valid
participant token; Free/Published/RemoveRequested/Aborting/Reclaiming/Retired
require participant zero; every generation is nonzero and bounded; and Retired
is terminal. A newer generation is stale only when that newer control is itself
structurally valid.

## Visibility and safety

- Publication becomes visible at the slot-control CAS from Reserved to
  Published, after immutable key/descriptor/payload metadata and bytes exist.
- Acquire activates an exact lease record and then revalidates the directory
  and Published slot generation.
- Removal becomes logical at Published to RemoveRequested. It rejects new
  leases while existing exact leases continue protecting immutable bytes.
- Reclaim requires a stable no-active-exact-lease scan, exact directory unlink,
  exact generation-tagged helper-word cleanup, and release publication of the
  next Free generation. Ordinary slot metadata is ignored while Free/Retired;
  reclaim helpers do not zero it, and the next Initializing owner overwrites it
  before directory publication.
- All terminal generations retire. No index, slot, lease, or participant
  incarnation wraps to a previously valid value.
- A completed/canceled insert or unlink does not release its exact canonical
  mutation until spill-summary cleanup either revalidates the summary's exact
  current overflow witness, finds another current same-bucket witness by bounded
  scan, exact-clears a stable empty Present token to its versioned Empty form,
  or retains the helpable mutation on budget/uncertainty.

The executable authority for these offsets, encodings, vectors, and offline
snapshots is [`fixtures/v2.0/manifest.json`](fixtures/v2.0/manifest.json).
C#, native C++, and Python conformance tests consume that same authority.
