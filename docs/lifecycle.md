# Lifecycle

SharedMemoryStore exposes one SMS2 lifecycle in C#, C++, and Python. The
normative ownership, token, recovery, and close rules are defined by the
[public API contract](../specs/010-lock-free-only-multilang/contracts/public-api.md)
and
[protocol conformance contract](../specs/010-lock-free-only-multilang/contracts/protocol-conformance.md).

## Open and Participant Ownership

Create/open is a cold lifecycle operation. It derives the canonical resources,
creates or opens the mapping, validates every layout dimension before payload
projection, and claims one participant record. A handle does not escape until
its exact participant incarnation is Active.

Only operating-system creation evidence authorizes initialization. An existing
zeroed, malformed, or retired mapping is never treated as a new store.
Participant capacity exhaustion returns `ParticipantTableFull` without
stealing a live record.

After attachment, data operations use mapped atomics and bounded helping. They
do not enter the platform lifecycle lock or a store-wide operation lock.

## Published Values

A successful publication makes one immutable descriptor and payload visible
under an opaque binary key. Readers validate the directory binding, slot
generation, participant incarnation, lengths, and publication state before
returning a borrowed view.

Removing an unleased value logically unlinks and reclaims it. Removing a value
with active readers returns `RemovePending`; its slot cannot be reused until
the final exact lease releases and reclamation completes.

## Lease Ownership

A lease protects one slot generation and is owned by one exact participant and
lease-record incarnation. Its descriptor and value views remain valid only
while both the lease and store handle are open.

Call `Release()` when the status matters. Dispose/context-manager cleanup is
the best-effort language adapter. Reusing a released, stale, or foreign token
returns a deterministic non-success status and must not mutate a newer record.

## Reservation Ownership

A reservation owns an initializing slot generation while the producer fills
store-owned memory. The key participates in duplicate detection, but readers
cannot acquire the value before commit.

The producer writes only through the current writable view, records exact
progress, and commits only after the announced length is complete. `Abort()`
unlinks the tentative key before reclamation. Commit, abort, recovery, or store
close invalidates the writable view and all derived views.

Segmented publication copies caller segments into one ordinary contiguous SMS2
payload before release-publishing it.

## Wait and Cancellation

One operation-wide policy bounds retry, stable revalidation, helping, scans,
and backoff. A finite deadline or cancellation is observed throughout the
operation. `NoWait` permits only the immediate protocol attempt; `Infinite`
means the caller accepts unbounded retries.

`StoreBusy` is an individual operation's bounded-contention outcome. It does
not mean a hot-path global lock is owned. `OperationCanceled` means the caller's
cancellation won before a terminal result.

## Explicit Recovery

Recovery is caller-triggered and conservative. It classifies the owner using
the complete participant identity and platform evidence, then revalidates the
same raw control word before attempting an exact compare/exchange.

- live, changing, unsupported, or inconsistent ownership is retained;
- a stable stale owner may be reclaimed according to configured policy; and
- a reused PID alone never authorizes recovery.

Lease recovery releases eligible stale lease records and may finish pending
removal. Reservation recovery unlinks eligible unpublished keys and reclaims
their slots without exposing bytes. Reports separate recovered, active,
unsupported, and failed observations.

Current-process recovery is an administrative/test operation. The application
must quiesce relevant borrowed views and token operations itself before opting
into it. Recovery is not a substitute for ordinary release, abort, and close.

## Abnormal Termination and Helping

A terminated participant may leave slot, lease, directory, or participant
transitions in progress. Later participants can help only transitions whose
published descriptor and exact identity satisfy the protocol. Persistent
impossible shared state may be latched as `CorruptStore` only after the required
stable revalidation; caller errors, capacity, cancellation, and legal races do
not corrupt the store.

Where owner evidence is unavailable or ambiguous, recovery reports unsupported
or retains the record. It does not guess that the owner is dead.

## Close

Closing a handle rejects new local operations, drains operations already
entered through that handle, publishes Closing for its exact participant,
cleans or hands off its owned records, and retires the participant only after
exact-reference scans permit it. Borrowed views and tokens must not be used
after their wrapper closes.

Closing one handle does not close another participant. After the final live
handle closes, platform lifecycle cleanup may retire mapping and owner
artifacts according to resource protocol 2. Applications should still use
stable deployment names and should not manipulate protocol-owned files or
named resources directly.

At the native C boundary, `sms_close_store` is the thread-safe, idempotent
logical close. After every thread has stopped using the opaque pointer,
`sms_destroy_store` releases the handle allocation. The C++ and Python
wrappers perform this second, caller-synchronized step automatically after
their local operation drain.

## Generations and Incarnations

Slots, leases, participants, directory operations, and their public tokens use
generation/incarnation identity. A transition compares the complete encoded
identity before exposing memory, helping, releasing, or reclaiming. Terminal
generations retire instead of wrapping to a previously valid identity.

## Application Responsibilities

- Close every store handle.
- Release or close every successful lease.
- Commit, abort, close, or explicitly recover every reservation.
- Do not retain borrowed C# spans, C++ spans, Python memoryviews, or derived
  views past token completion or handle close.
- Treat recovery as an owner-policy decision and preserve conservative
  behavior when liveness evidence is unavailable.
- Drain and close every runtime before replacing a deployment mapping.

## Related Samples

- [Basic usage](../samples/BasicUsage/README.md)
- [Frame value](../samples/FrameValue/README.md)
- [Zero-copy ingest](../samples/ZeroCopyIngest/README.md)
- [Hosted integration](../samples/HostedServiceIntegration/README.md)
- [Docker shared memory](../samples/DockerSharedMemory/README.md)
