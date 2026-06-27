# Data Model: Shared Memory Value Store

## SharedMemoryStore

Represents one bounded named memory region.

**Fields**:
- `Name`: OS-visible store name supplied during create/open.
- `StoreId`: generated identifier stored in the region header.
- `LayoutVersion`: version of the shared-memory contract.
- `TotalBytes`: total mapped region size.
- `SlotCount`: number of reusable slots.
- `MaxValueBytes`: maximum payload bytes per value.
- `MaxDescriptorBytes`: maximum descriptor bytes per value.
- `MaxKeyBytes`: maximum key bytes.
- `LeaseRecordCount`: maximum simultaneously tracked active leases.
- `State`: `Initializing`, `Ready`, `Disposing`, `Corrupt`, or `Unsupported`.

**Relationships**:
- Owns many `ReusableSlot` records.
- Owns one shared key index.
- Owns many `LeaseRecord` records.
- Produces `DiagnosticsSnapshot` values for callers.

**Validation Rules**:
- `Name` must satisfy platform naming rules.
- capacity values must be positive and internally consistent.
- `TotalBytes` must fit the header, index, lease registry, slot metadata, and
  all configured slot payload/descriptor storage.
- open operations must reject incompatible layout versions or incompatible
  configuration.

## ValueEntry

Represents one published value in one slot.

**Fields**:
- `SlotIndex`: zero-based reusable slot index.
- `Generation`: monotonically increasing slot generation.
- `State`: `Free`, `Publishing`, `Published`, `RemoveRequested`, or
  `Reclaiming`.
- `UsageCount`: number of active leases protecting the value.
- `KeyHash`: stable 64-bit hash of `KeyBytes`.
- `KeyLength`: byte length of the key.
- `DescriptorLength`: byte length of optional descriptor data.
- `ValueLength`: byte length of payload data.
- `CommittedSequence`: sequence written after payload and descriptor bytes are
  copied into the slot.
- `OwnerProcessId`: publishing process id for diagnostics.

**Relationships**:
- Belongs to one `ReusableSlot`.
- Is found through one shared key index entry while published or pending
  removal.
- May be protected by many active `ValueLease` records.
- May include one `ValueDescriptor`.

**Validation Rules**:
- `KeyLength` must be between 1 and `MaxKeyBytes`.
- `DescriptorLength` must be between 0 and `MaxDescriptorBytes`.
- `ValueLength` must be between 0 and `MaxValueBytes`.
- duplicate keys are rejected while the previous value is published or pending
  removal.
- payload and descriptor bytes are immutable once state becomes `Published`.
- `UsageCount` must never become negative.

## StoreKey

Language-neutral identifier for a value.

**Fields**:
- `KeyBytes`: caller-supplied opaque bytes.
- `Length`: number of key bytes.
- `Hash64`: stored hash used for probing.

**Validation Rules**:
- key bytes are compared by exact byte equality after hash match.
- the core allocation-free API accepts `ReadOnlySpan<byte>` keys.
- UTF-8 string helpers may exist, but byte keys define the compatibility
  contract.

## ValueDescriptor

Optional consumer-defined metadata stored beside a value.

**Fields**:
- `DescriptorBytes`: opaque descriptor bytes.
- `Length`: descriptor byte length.

**Validation Rules**:
- descriptor bytes are not interpreted by the core store.
- descriptor bytes are copied into the slot before publish commit.
- descriptor bytes remain immutable while the value is published or leased.

## ValueLease

Reader access token that protects a value from reuse.

**Fields**:
- `StoreId`: store identity observed during acquire.
- `SlotIndex`: acquired slot.
- `Generation`: slot generation observed during acquire.
- `LeaseRecordId`: shared lease registry entry.
- `OwnerProcessId`: acquiring process id for diagnostics and recovery.
- `State`: `Active`, `Released`, `Abandoned`, or `Invalid`.

**Relationships**:
- Protects one `ValueEntry` generation.
- References one shared lease registry record.
- Releasing the final active lease can transition a removed value to reusable
  storage.

**Validation Rules**:
- acquire succeeds only for `Published` entries with matching generation.
- release succeeds at most once for each active lease record.
- a release with mismatched store id, slot index, generation, or lease record
  returns a deterministic invalid-release status.
- spans obtained from a lease are valid only while the lease and store are open.

## ReusableSlot

Preallocated storage region that can hold one value entry.

**Fields**:
- `SlotIndex`: zero-based index.
- `Generation`: incremented whenever storage is reused.
- `PayloadOffset`: offset to payload bytes inside the mapped region.
- `DescriptorOffset`: offset to descriptor bytes inside the mapped region.
- `CapacityBytes`: maximum payload bytes.
- `DescriptorCapacityBytes`: maximum descriptor bytes.
- `State`: mirrors the owning value lifecycle.

**State Transitions**:
- `Free` -> `Publishing`: producer reserves the slot.
- `Publishing` -> `Published`: producer commits key, descriptor, and payload.
- `Publishing` -> `Free`: producer aborts before commit.
- `Published` -> `RemoveRequested`: remove called while usage count is greater
  than zero.
- `Published` -> `Reclaiming` -> `Free`: remove called with no active leases.
- `RemoveRequested` -> `Reclaiming` -> `Free`: final lease releases.
- Any state -> `Corrupt`: integrity validation fails and the store blocks
  unsafe operations.

## LeaseRecord

Shared registry entry used to prevent double release and support recovery.

**Fields**:
- `LeaseRecordId`: stable index in the lease registry.
- `SlotIndex`: protected slot.
- `Generation`: protected slot generation.
- `OwnerProcessId`: acquiring process id.
- `AcquireSequence`: monotonic sequence for diagnostics.
- `State`: `Free`, `Active`, `Released`, or `Abandoned`.

**Validation Rules**:
- active lease records must match the slot generation before release.
- recovery may mark a record `Abandoned` only when platform-specific owner
  liveness checks are supported and confirm the owner is gone.
- abandoned recovery decrements usage count once.

## StoreOwner

Consumer role responsible for lifecycle and cleanup.

**Responsibilities**:
- create the store with capacity and layout settings.
- decide when values are removed.
- dispose mapped resources.
- run explicit recovery for stale leases when required.
- expose diagnostics to production monitoring through consumer-owned logging or
  metrics.

## DiagnosticsSnapshot

Allocation-conscious snapshot of store state.

**Fields**:
- total configured bytes.
- used slot count and free slot count.
- active lease count.
- pending removal count.
- failed operation counters by status.
- capacity pressure counters.
- last observed layout/status issue.

**Validation Rules**:
- snapshot retrieval must not write to console.
- snapshot retrieval must not mutate value lifecycle state.
- any allocating diagnostic formatting belongs to caller-controlled helpers, not
  hot-path store operations.
