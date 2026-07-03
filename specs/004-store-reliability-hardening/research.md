# Research: Store Reliability Hardening

## Decision: Enforce exact lease owner policy before recovery mutation

**Decision**: Lease recovery must classify each active lease as current owner,
other live owner, stale owner, unsupported owner check, or unsafe record before
mutating the record. When `RecoverCurrentProcessLeases` is `true`, recovery may
recover current-process leases and stale-owner leases, but it must still skip
leases held by another live process. When the option is `false`, recovery may
recover only stale-owner leases.

**Rationale**: The existing broad process-liveness check is insufficient because
current-process recovery is a controlled cleanup path, not permission to reclaim
another live process's lease. Skipping other live owners preserves slot reuse
protection and keeps recovery aligned with the store's data-safety contract.

**Alternatives considered**:
- Recover any active lease when current-process recovery is enabled. Rejected
  because it can invalidate live readers in other processes.
- Disable all live-process recovery. Rejected because tests and controlled
  shutdown need a deterministic current-process cleanup path.
- Rely on diagnostics only without changing recovery mutation rules. Rejected
  because diagnostics cannot repair a correctness defect that mutates shared
  state too early.

## Decision: Make lease recovery result categories consumer-visible

**Decision**: Extend recovery reporting and diagnostics to distinguish scanned,
recovered, still-active, unsupported, and failed or unsafe lease records. The
implementation must preserve existing report members where practical and record
semantic version impact when additive report members are required.

**Rationale**: Operators need to know whether recovery made progress, skipped
live owners, could not evaluate owners on the platform, or refused unsafe shared
state. This is also required for contract tests and release notes.

**Alternatives considered**:
- Keep the current `UnsupportedLeaseCount` as a catch-all. Rejected because it
  hides the difference between live owners and unsafe records.
- Throw exceptions for unsafe records. Rejected because operational failures in
  the library contract should return deterministic statuses and diagnostics.
- Write recovery details to console or logs. Rejected by the constitution's
  consumer-controlled diagnostics requirement.

## Decision: Normalize disposal races at the public boundary

**Decision**: Public store methods and token methods must use a single lifecycle
boundary that treats disposed mutexes, disposed mapped regions, and disposed
store state as documented `StoreDisposed`, invalid, already-completed, or empty
diagnostic outcomes. Token memory and span projection after disposal must be
empty and must not expose mapped memory.

**Rationale**: A disposed store handle is a normal lifecycle boundary. Callers
should not have to catch internal `ObjectDisposedException`, mapped-memory
exceptions, or synchronization exceptions from documented thread-safe APIs.

**Alternatives considered**:
- Rely on the `_disposed` flag checks already in individual methods. Rejected
  because races can occur after the flag check while waiting on synchronization
  or before mapped-memory access.
- Make disposal non-thread-safe. Rejected because the public contract documents
  deterministic, thread-safe operations.
- Swallow all exceptions as `UnknownFailure`. Rejected because disposal has a
  specific documented status and should be distinguishable in diagnostics.

## Decision: Use wrap-safe bounded probing for slot and lease searches

**Decision**: Slot and lease-record probe cursors must use unchecked monotonic
advancement plus unsigned or non-negative modulo arithmetic that cannot produce
negative indexes or arithmetic overflow. Candidate validation must use unsigned
bounds checks before touching shared records.

**Rationale**: The current long-running search cursors can reach signed integer
boundaries. Probe arithmetic must continue to produce candidates inside the
configured table sizes and return deterministic full statuses when no free
record exists.

**Alternatives considered**:
- Keep `Math.Abs(start + step) % count`. Rejected because `Math.Abs(int.MinValue)`
  and signed addition boundaries are rollover-prone.
- Reset cursors under the store lock. Rejected because it still needs correct
  boundary arithmetic and makes tests less direct.
- Use random probing. Rejected because deterministic tests and repeatable
  benchmarks are more valuable for this bounded table design.

## Decision: Introduce stale-proof slot lifecycle identity across generation boundaries

**Decision**: Slot lifecycle validation must compare a lifecycle identity that
cannot accept stale leases or reservations after a generation boundary. The
preferred design is a generation plus reuse-epoch identity captured by index
entries, lease records, reservation tokens, and value leases. When the
generation component reaches its boundary, the slot advances the epoch and
continues from a valid nonzero generation. If the full lifecycle identity cannot
advance safely, the operation returns a deterministic capacity or corruption
status and does not expose storage.

**Rationale**: A single signed generation counter can overflow or eventually
collide with an old token. A stale-proof identity allows controlled boundary
tests without accepting a lease or reservation created for earlier contents.

**Alternatives considered**:
- Use checked increment and fail on overflow. Rejected because long-running
  services need deterministic behavior instead of runtime overflow exceptions.
- Use unchecked wrap of the existing integer. Rejected because a stale token can
  regain validity after wrap.
- Move directly to a public versioned replacement API. Rejected as out of scope
  for this reliability feature.

## Decision: Manage tombstone pressure with diagnostics plus benchmark-proven synchronous maintenance

**Decision**: Add index health diagnostics for live entries, tombstones, empty
entries, usable capacity, pressure thresholds, and observed probe cost. Add a
high-churn benchmark before choosing the maintenance threshold. If diagnostics
alone cannot keep missing-key lookup and insert latency within success
criteria, implement bounded synchronous index compaction or rehashing under the
existing store lock. Do not add a public maintenance API unless benchmark
evidence shows internal management is insufficient.

**Rationale**: Tombstones are a production performance risk, but the smallest
compatible solution is to make pressure visible and keep the index healthy
inside normal operations. Synchronous bounded work avoids hidden background
threads and preserves caller control over when operations happen.

**Alternatives considered**:
- Add `CompactIndex()` as the first solution. Rejected because public surface
  should not expand before benchmark evidence proves it is needed.
- Run a background compaction worker. Rejected because hidden background work is
  prohibited and complicates lifecycle/disposal semantics.
- Clear tombstones opportunistically without metrics. Rejected because the
  feature requires evidence and measurable success criteria.

## Decision: Keep runtime dependencies unchanged

**Decision**: Use only .NET BCL runtime APIs and existing benchmark/test
tooling. Process-liveness checks remain behind platform-aware helpers and must
return unsupported or reported outcomes when unavailable.

**Rationale**: SharedMemoryStore is a low-level reusable package. Additional
runtime dependencies would increase integration risk without being necessary
for owner checks, lifecycle guards, rollover arithmetic, or index diagnostics.

**Alternatives considered**:
- Add a logging abstraction dependency. Rejected because diagnostics are already
  returned through caller-owned snapshots and reports.
- Add a platform process library. Rejected because process liveness is simple
  enough to isolate behind BCL calls and unsupported fallbacks.

## Decision: Use deterministic test seams for impractical rollover counts

**Decision**: Boundary tests may use internal test-only helpers or visible-to-
test accessors to seed probe cursors, slot lifecycle identity components, and
index tombstone states near their boundaries. Production behavior must remain
the same as if the service reached those states through normal operation.

**Rationale**: Success criteria require rollover validation without impractical
wall-clock runtimes. Deterministic seeding allows repeatable unit and
integration coverage for the exact failure modes.

**Alternatives considered**:
- Run enough operations to naturally reach signed integer boundaries. Rejected
  as impractical for CI.
- Skip rollover tests and rely on code inspection. Rejected because the
  constitution requires automated boundary coverage for relevant failure modes.
