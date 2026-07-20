# Release Qualification: Lock-Free-Only Multi-Language Store

## Frozen Contract Status

This document freezes the default evidence predicate for feature 010 before
final qualification runs. It also records the user-approved compact delta
decision at the end of the document without representing that decision as a
new immutable full-rollup run.

| Field | Frozen value |
|---|---|
| Contract revision | `1` |
| Current full-rollup status | `NOT RUN AT CURRENT SOURCE REVISION` |
| Current production source revision | `fc605a0` |
| Compact production-readiness decision | `PASS` |
| Immutable baseline revision | `1b784d7` |
| Exact-revision delta evidence | `RECORDED BELOW` |

The release is **QUALIFIED if and only if** every required predicate in this
document is true in immutable runner-generated evidence from one clean source
revision. Missing evidence, a skipped required row, an unsupported required
environment, validation-only output, a nonzero command exit, a timeout, or any
failed predicate means **NOT QUALIFIED**.

Prose, a task checkbox, a successful local smoke test, or an exit code without
its required evidence cannot establish qualification. This tracked contract
must not be edited after a run merely to turn failed or missing evidence into a
pass. A legitimate contract change requires a new committed revision, new
previously unused evidence paths, and a complete rerun.

## Normative Qualification Inputs

Qualification validates the implementation against:

- [`spec.md`](spec.md), including every success criterion;
- [`plan.md`](plan.md);
- [`data-model.md`](data-model.md);
- [`contracts/protocol-conformance.md`](contracts/protocol-conformance.md);
- [`contracts/public-api.md`](contracts/public-api.md);
- [`contracts/interoperability-and-validation.md`](contracts/interoperability-and-validation.md);
- [`contracts/packaging-and-migration.md`](contracts/packaging-and-migration.md);
- [`../../protocol/layout-v2.0.md`](../../protocol/layout-v2.0.md);
- [`../../protocol/resource-naming-v2.md`](../../protocol/resource-naming-v2.md); and
- [`../../protocol/fixtures/v2.0/manifest.json`](../../protocol/fixtures/v2.0/manifest.json).

If these inputs disagree, qualification stops as a specification defect. A
runner may not choose whichever interpretation produces a pass.

## Reserved Evidence Paths

The runner chooses one unique `<run-id>` after the qualifying source revision
is committed. Every path below is a template, not a claim that the file exists.
The complete destination directory must not exist before its one-shot run.

| Tier / platform | Required generated path template | Current status |
|---|---|---|
| PR Windows x64 | `artifacts/010-qualification/<run-id>/pr/windows-x64/summary.json` | `NOT GENERATED` |
| PR Linux x64 | `artifacts/010-qualification/<run-id>/pr/linux-x64/summary.json` | `NOT GENERATED` |
| Nightly Windows x64 | `artifacts/010-qualification/<run-id>/nightly/windows-x64/summary.json` | `NOT GENERATED` |
| Nightly Linux x64 | `artifacts/010-qualification/<run-id>/nightly/linux-x64/summary.json` | `NOT GENERATED` |
| Release Windows x64 | `artifacts/010-qualification/<run-id>/release/windows-x64/summary.json` | `NOT GENERATED` |
| Release Linux x64 | `artifacts/010-qualification/<run-id>/release/linux-x64/summary.json` | `NOT GENERATED` |
| Cross-platform release rollup | `artifacts/010-qualification/<run-id>/release/summary.json` | `NOT GENERATED` |
| Evidence manifest | `artifacts/010-qualification/<run-id>/manifest.json` | `NOT GENERATED` |
| Independent review | `artifacts/010-qualification/<run-id>/release/code-review.json` | `NOT GENERATED` |

Each platform summary owns a sibling `evidence/` directory containing its raw
logs, test results, traces, benchmark samples, package manifests, fixture
results, and child-process reports. The cross-platform rollup records and
revalidates the cryptographic digest of both release platform summaries and
their complete evidence-tree manifests.

Generated evidence is not committed by editing this document. The final
release report remains authoritative even when it records failure.

## Immutable Provenance Predicate

Every PR, nightly, release, and platform summary must record:

- repository commit ID and tree ID;
- clean working-tree state using a stable porcelain representation;
- a digest of that working-tree status;
- a source-manifest digest covering every tracked input used to build or test;
- the protocol manifest and current protocol-document digests;
- build configuration and deterministic build identifiers;
- tested managed assembly, native library, public header, Python distribution,
  wheel, NuGet, symbol-package, and CMake-package digests as applicable;
- OS, architecture, kernel/build, filesystem, compiler, .NET, CMake, Python,
  and container/tracing-tool identities;
- exact tier, scenario catalog version, seeds, counts, timeouts, and start/end
  monotonic timestamps; and
- controller and complete child-tree exit status.

The following values must be equal at run start and completion:

- commit and tree IDs;
- clean working-tree state and status digest;
- source-manifest digest;
- protocol-manifest digest; and
- tested-artifact manifest.

PR, nightly, Windows release, Linux release, and cross-platform rollup evidence
must identify the same clean source revision and protocol manifest. A binary or
package rebuilt from a different source tree is different evidence and cannot
be combined into the qualifying set.

The evidence manifest must enumerate the exact non-reparse files under the run
directory using normalized unique relative paths, byte lengths, and
cryptographic hashes. Every summary-to-log and summary-to-package reference
must resolve inside that directory and reproduce its recorded digest. Missing,
extra, duplicate, linked, out-of-root, truncated, or digest-mismatched evidence
fails qualification.

Before the aggregate suite runs, the controller exercises the validated native
and Python distributions, copies their exact package/runtime inputs into the
run evidence tree, and binds every copy to its source path, length, and digest.
Completion revalidates both sides; source or evidence-copy drift fails the run.

## One-Protocol and No-Legacy Gate

Static source, built-artifact, package, public-API, and runtime inspection must
prove all of the following:

1. SMS2 layout `2.0`, resource protocol `2`, required-feature mask `7`, and
   optional-feature mask `0` are the only current creatable/readable protocol.
2. C# exposes no `StoreProfile`, options/store/diagnostics `Profile`,
   `CreateLockFree`, or profile-aware sizing overload.
3. Native ABI 2 and the C++ API expose no layout/profile selector, ABI 1
   fallback, legacy store engine, or operation-wide compatibility lock.
4. Python exposes no profile/layout selector, ABI 1 loader path, or pure-Python
   mapped-state implementation.
5. Product binaries contain no executable SMS1 parser, creator, mutation path,
   legacy engine, or fallback synchronization path.
6. Current samples, package readmes, compatibility metadata, generated API
   documentation, and release notes advertise only SMS2 as current.
7. `protocol/compatibility.json` declares only the current distribution
   versions and SMS2 support described by the packaging contract.
8. Historical feature artifacts may mention the retired design but are not
   packaged or indexed as current compatibility guidance.
9. A canonical retired SMS1 header is rejected by every current distribution
   as `IncompatibleLayout` before directory, key, descriptor, payload, slot,
   lease, or participant projection and without creating a parallel mapping.
10. Unknown layout majors, incompatible required features, malformed SMS2
    topology, and unsupported architectures likewise fail closed without
    falling back to retired behavior.

Any executable retired-layout support fails the gate even if no test invokes
it. Merely changing the default to SMS2 while retaining a hidden selector is
not sufficient.

## Canonical Protocol Conformance Gate

C#, native C++, and packaged Python must each pass 100% of the same canonical
vectors for:

- header and record sizes, alignments, fields, section order, and checked
  capacity arithmetic;
- control, binding, participant-token, spill-summary, directory-location, and
  directory-operation codecs;
- state and public-status numeric assignments;
- FNV hashing, exact-key comparison, directory selection, overflow scan, and
  resource-name vectors;
- required-feature and incompatible-draft masks;
- canonical binary fixtures and malformed fixture rejection; and
- acquire/release visibility plus sequentially consistent mapped 64-bit atomic
  litmus tests in Release builds.

The platform report must include per-runtime vector totals, zero mismatches,
zero forbidden litmus outcomes, the exact fixture digest, and the tested binary
digest. Implementation-local expected values cannot replace the canonical
manifest.

## Pull-Request Tier

Both Windows x64 and Linux x64 PR summaries must contain passed required rows
for:

- single-protocol API and static absence checks;
- all managed unit and contract tests;
- native unit, codec, mapped-atomic, and conformance tests;
- Python wrapper, loader, constant, and lifetime tests;
- retired, unknown, malformed, and feature-incompatible mapping rejection;
- all nine ordered runtime cells with at least 100 lifecycle cases per cell;
- at least one deterministic test for every participant, directory, slot,
  lease, reclamation, recovery, and corruption transition family;
- managed, installed native, and installed Python clean-consumer smoke tests;
- package metadata, compatibility-manifest, documentation, and link checks; and
- compilation in Release configuration with warnings treated according to each
  project's release policy.

The PR tier may use reduced stress counts only where this contract explicitly
permits them. It may not skip a runtime pair, protocol vector, architecture
check, or package-consumer smoke test.

## Nightly Tier

Both Windows x64 and Linux x64 nightly summaries must contain every PR row plus:

- all managed unit, contract, integration, linearizability, package, sample,
  allocation, and platform tests;
- all native unit, process, lifecycle, recovery, diagnostics, C ABI, C++, CMake
  install, and clean-consumer tests;
- all Python source, installed-wheel, rebuilt-sdist-wheel, lifecycle, recovery,
  diagnostics, view-invalidation, and sample tests;
- all nine ordered runtime pairs with at least 1,000 arbitrary binary lifecycle
  cases per cell;
- required three-runtime mixed publication, reservation, lease, removal,
  collision, participant, recovery, and final-close scenarios;
- deterministic pause/help/reuse schedules, crash/recovery schedules,
  corruption and non-poisoning injections, and raw memory-order litmus tests;
- Windows and Linux steady-state lock traces;
- Docker and same-host Linux-container scenarios on the qualified Linux host;
- absolute allocation, wait-bound, latency, throughput, scaling, raw-stall,
  reader-fan-out, and mixed-stress gates; and
- complete raw evidence retained beneath the nightly platform report.

A nightly test may use a smaller documented count than release for long stress
or crash matrices, but it must exercise every required scenario family and
must record the effective target.

## Release Tier

Both release platform summaries and the cross-platform rollup must include all
PR and nightly rows at their unreduced release targets. Required release
evidence includes:

- every success criterion in `spec.md` mapped to a named machine-derived row;
- 100% canonical conformance for all three distributions;
- all nine ordered runtime pairs with at least 1,000 lifecycle cases per cell;
- at least 1,000,000 credited three-runtime lifecycle operations;
- complete deterministic checkpoint-catalog coverage and at least 1,000,000
  total pause/reuse repetitions;
- at least 10,000 reservation-owner and lease-owner terminations distributed
  across runtimes and platforms;
- zero partial values, stale-generation mutations, false successful removals,
  live-owner reclamations, accepted stale-token actions, access violations, or
  safely recoverable capacity leaks;
- twelve mixed-runtime readers over one 1.3 MB generation through logical
  removal and exact final reclamation;
- complete corruption-latch propagation and legal-race non-poisoning matrices;
- all clean-consumer, install, packaging, sample, documentation, migration, and
  compatibility-manifest gates;
- Windows and Linux raw lock-trace trees;
- every absolute performance and boundedness gate; and
- an independent review report with no unresolved High or Medium finding for
  the exact tested revision and artifacts.

No release-required row may be skipped, marked optional, validation-only,
unsupported, inconclusive, or inferred from a different tier.

## Windows x64 Evidence Predicate

The Windows platform report must prove:

- process and OS architecture are x64 and the tested mapped atomic primitive is
  always lock-free at the required alignment;
- all managed, MSVC/native, Python wheel, package, sample, and ordered-pair
  suites use the exact recorded release artifacts;
- named mapping and cold-mutex identity match resource protocol 2;
- physical creation authority is acquired before mapping creation/open and is
  retained through header validation and participant registration;
- the steady-state trace window begins after all participants are Active and
  ends before close;
- successful publish, segmented publish, reserve, advance, commit, abort,
  acquire, projection, release, remove, help, reclaim, recovery, and diagnostics
  cause zero waits or acquisitions of the store's named cold mutex;
- child-process creation, trace coverage, and raw event binding are complete;
  and
- absolute Windows performance thresholds pass from retained raw samples.

Process-local lifecycle entry and mapped atomic instructions are permitted and
must not be misclassified as a store-wide OS operation lock.

## Linux x64 Evidence Predicate

The Linux platform report must prove:

- process and kernel architecture are x86-64 and the tested mapped atomic
  primitive is always lock-free at the required alignment;
- all managed, GCC/Clang-native, Python wheel, package, sample, Docker, and
  ordered-pair suites use the exact recorded release artifacts;
- deterministic `.region`, `.owners`, `.lifecycle`, `.lock`, anchor, and
  release-marker resources match resource protocol 2 and required permissions;
- cold ordering is `.lifecycle -> .lock -> mapping/owner -> header/participant`
  with reverse gate release;
- owner anchors, exact owner lines, bounded close, durable release markers,
  marker reconciliation, PID reuse, and PID-namespace handling preserve every
  live or ambiguous owner;
- symbolic links, special files, malformed records, permission failures,
  locked anchors, inaccessible metadata, and unsupported filesystem/kernel
  behavior fail conservatively;
- child-following syscall traces retain all `fcntl`/OFD-lock and `flock` events;
- the steady-state trace contains zero `.lock` or `.lifecycle` acquisition and
  zero owner-anchor lock action caused by a data operation;
- expected cold and final-cleanup lock events bind to exact derived paths; and
- absolute Linux performance thresholds pass from retained raw samples.

A missing `strace` child, ambiguous pathname, truncated event tree, or summary
without its raw trace is not qualified.

## Package and Clean-Consumer Predicate

The exact release artifacts must satisfy:

| Distribution | Required identity | Required package evidence |
|---|---|---|
| NuGet | `SharedMemoryStore` `3.0.0` | Release `.nupkg` and `.snupkg`, XML docs, metadata, restore-only clean `net10.0` consumer, complete public lifecycle |
| Native | CMake package `1.0.0`, C ABI `2.0`, Linux SOVERSION `2` | Clean configure/build/test/install, exported targets, installed-header/binary agreement, external C and C++ consumers |
| Python | `shared-memory-store` `1.0.0`, requires C ABI `2.0` | Wheel and sdist, wheel rebuilt from sdist, isolated no-dependencies install, adjacent packaged native library, unrelated-working-directory import and lifecycle |

The Python wheel must contain the correct same-platform library and must not
search the working directory, `PATH`, or a system location. The sdist must
contain every declared build input and no compiled native library. Clean
consumers must not resolve repository source paths, project references, stale
headers, or stale build outputs.

All artifact names, versions, ABI/protocol identities, public documentation,
release notes, compatibility metadata, and migration guidance must agree.

## Interoperability Predicate

The release report must contain all ordered cells:

| Creator / producer | `dotnet` consumer | `cpp` consumer | `python` consumer |
|---|---:|---:|---:|
| `dotnet` | required | required | required |
| `cpp` | required | required | required |
| `python` | required | required | required |

Each cell uses independent processes and installed release artifacts. Every
cell covers create/open, contiguous and segmented publish, reservation partial
invisibility/commit/abort, acquire/release, logical removal with foreign active
leases, final reclamation, republish, exact collisions and spill churn,
bounded/canceled outcomes, diagnostics, close/reopen, and stale mapping/token
rejection. The reverse direction is never inferred.

Triad scenarios rotate all three runtimes through producer, reservation owner,
lease owner, remover, helper, recovery caller, and final closer roles. Pairwise
success cannot substitute for these mixed-runtime rows.

Each result binds runtime roles, seed, exact byte/checksum counts, status
counts, store/protocol identity, tested-artifact digests, and child exit codes.
Any byte mismatch, undocumented outcome, missing role, or source-tree-loaded
artifact fails the cell.

## Absolute Performance and Boundedness Predicate

Release performance uses Release builds, an idle qualified host, recorded CPU
affinity/topology and memory information, separate warm-up, retained raw
samples, and monotonic controller deadlines. It contains no Legacy process,
Legacy result row, live retired implementation, or relative comparison with
layout 1.2.

| Gate | Required result |
|---|---|
| Warmed allocation | C# and native hot contiguous publish/acquire/release/remove paths report `0 B/op` or zero allocator calls; segmented/direct ingest creates no payload-sized temporary buffer; Python zero-copy views create no payload-sized copy |
| Finite waits | Every credited call finishes within its configured bound plus 250 ms and leaves no leaked token |
| Linux tiny-operation p99 | Eight-process publish/remove and acquire/release p99 is at most 10 microseconds |
| Windows tiny-operation p99 | Eight-process publish/remove and acquire/release p99 is at most 25 microseconds |
| Tiny-operation throughput | Every eight-process scenario sustains at least 100,000 credited operations/second aggregate |
| Scaling | Eight-process p99 is at most 3 times its one-process p99 |
| Raw stall | No successful raw operation stalls longer than 10 milliseconds on Linux x64 or 250 milliseconds on Windows x64; the Windows maximum is a scheduler-tolerant hang detector and does not replace the strict p99, scaling, duration-bound, or suspension-progress gates |
| Reader fan-out | Twelve mixed-runtime readers complete the 1.3 MB pending-removal/final-reclaim scenario without timeout or hot lock |
| Mixed stress | At least 1,000,000 credited operations complete with zero forbidden safety outcome or safely recoverable capacity leak |

Every threshold is evaluated directly from raw samples by the runner. A copied
summary, host-relative threshold, historical baseline, or Legacy comparison is
not accepted.

## Success-Criterion Evidence Map

The final cross-platform summary must bind each feature success criterion to a
named required machine row. The row names below are frozen; the generated
status remains `NOT GENERATED` until a runner emits it.

| Criterion | Required release row | Current status |
|---|---|---|
| SC-001 | `ordered-pair-3x3-lifecycle` | `NOT GENERATED` |
| SC-002 | `canonical-conformance-all-runtimes` | `NOT GENERATED` |
| SC-003 | `mixed-runtime-million-operation-stress` | `NOT GENERATED` |
| SC-004 | `cross-runtime-ten-thousand-crash-recovery` | `NOT GENERATED` |
| SC-005 | `complete-transition-pause-reuse-million` | `NOT GENERATED` |
| SC-006 | `finite-wait-envelope` | `NOT GENERATED` |
| SC-007 | `dual-platform-zero-hot-os-locks` | `NOT GENERATED` |
| SC-008 | `twelve-reader-pending-removal` | `NOT GENERATED` |
| SC-009 | `all-distribution-clean-consumers` | `NOT GENERATED` |
| SC-010 | `full-dual-platform-release-suite` | `NOT GENERATED` |
| SC-011 | `one-current-protocol-static-inspection` | `NOT GENERATED` |
| SC-012 | `retired-store-migration-and-fail-closed` | `NOT GENERATED` |
| SC-013 | `dual-platform-absolute-performance` | `NOT GENERATED` |

If `spec.md` gains another success criterion before the frozen run, this
contract must be revised and recommitted before evidence generation. A runner
must reject an unmapped criterion rather than silently omit it.

## Independent Review Input

`scripts/finalize-lock-free-qualification.ps1` accepts one reviewer-produced
JSON document and copies it into the reserved `release/code-review.json` path
only after validating this closed schema:

```json
{
  "schemaVersion": 1,
  "contractRevision": 1,
  "revision": {
    "commit": "<40-hex commit>",
    "sourceManifestSha256": "<64-hex digest>"
  },
  "reviewer": {
    "identity": "<reviewer identity>",
    "independentFromImplementation": true
  },
  "overallStatus": "passed",
  "findings": [
    {
      "id": "REV-001",
      "severity": "low",
      "status": "resolved",
      "summary": "Example only"
    }
  ]
}
```

The reviewer must inspect the exact committed revision and its generated
release artifacts. `overallStatus` may be `passed` only when no High or Medium
finding remains unresolved. The finalizer rejects revision drift, a missing
independence assertion, a non-passing review, reused reserved outputs, any
failed platform row, and any evidence-tree hash or file-set mismatch.
The schema is closed at every object level: additional or missing properties
and incorrectly typed values are rejected. Finding severity is exactly one of
`high`, `medium`, or `low`; finding status is exactly `open` or `resolved`;
finding IDs are non-empty and unique; and finding summaries are non-empty.

## Generated Summary Requirements

Every generated summary must include at least:

```text
schemaVersion
contractRevision
tier
platform
validationOnly
overallStatus
provenance
testedArtifacts
protocolManifest
results[]
skips[]
evidenceManifest
startedAtMonotonic
completedAtMonotonic
controllerExitCode
```

Accepted values are deliberately strict:

- `contractRevision` is `1`;
- `tier` and `platform` equal the path's frozen tier/platform;
- `validationOnly` is `false`;
- `overallStatus` is the runner's result and must be `passed` for qualification;
- every required result has `required: true` and `status: passed`;
- `skips` is empty for release evidence;
- all counts meet their exact configured targets;
- all forbidden-outcome counts are zero; and
- the controller exits zero only after completion-integrity and digest
  revalidation pass.

The concrete JSON schema and runner may add fields but may not weaken or omit a
predicate frozen here.

## One-Shot Execution Rules

Before a tier begins, the controller must verify:

1. the repository is at the selected clean revision;
2. all required platform prerequisites are available;
3. the unique tier/platform destination does not exist;
4. no release artifact will be reused from another revision;
5. the protocol and source manifests are recorded; and
6. watchdogs are armed before store or child-process setup.

On timeout, the controller gives the tracked child tree only its documented
bounded termination budget, then fails the isolated controller rather than
waiting indefinitely for in-flight store cleanup. Late timer dispatch cannot
accept completion after the monotonic deadline.

Failed and partial evidence is preserved at its original path. It is never
repaired, copied into a new passing directory, or overwritten. After a code or
contract change, the entire required sequence uses a new run ID.

## Final Qualification Predicate

Feature 010 is QUALIFIED only when one cross-platform release summary proves:

1. PR, nightly, Windows release, and Linux release summaries exist at their
   reserved unique paths and pass this same contract revision;
2. every summary and artifact has identical clean immutable provenance;
3. all three distributions pass canonical SMS2 conformance;
4. one-protocol/no-legacy static, package, and runtime gates pass;
5. all nine ordered pairs and required three-runtime scenarios pass;
6. deterministic, crash/recovery, corruption/non-poisoning, memory-order, and
   lock-trace gates have zero forbidden outcomes;
7. every absolute performance and boundedness threshold passes without a
   Legacy comparison;
8. Windows x64 and Linux x64 platform predicates both pass;
9. every package, clean-consumer, sample, documentation, compatibility, and
   migration gate passes;
10. every success criterion maps to a passed machine row;
11. the independent review has no unresolved High or Medium finding; and
12. the complete evidence tree and final rollup pass digest and completion-
    integrity revalidation.

Until those runner-generated artifacts exist and satisfy every predicate, the
formal full-rollup status of the current revision remains **NOT RUN / NOT
QUALIFIED**. The scoped production-readiness decision below is separate and
does not claim that a missing full rollup exists.

## User-Approved Compact Delta Qualification (2026-07-20)

The owner explicitly approved a shorter final verification after the exhaustive
immutable matrix exposed one Linux cold-lifecycle defect. This section records
the resulting production-readiness decision. It is a scoped exception for the
feature handoff and does not weaken the default full-rollup predicate above for
future releases.

### Baseline and delta boundary

- Immutable baseline `1b784d7` passed the Windows x64 PR, nightly, and release
  tiers and the Linux x64 PR and nightly tiers, including the high-volume,
  crash/recovery, suspension, raw-atomic, packaging, and independent-review
  gates. Its Linux release tier found the persistent shared-root enumeration
  defect addressed by T137, so that baseline alone was not accepted.
- Production source revision `fc605a0` changes only Linux owner-anchor,
  release-marker, and owner-sidecar housekeeping. It preserves the SMS2 mapped
  bytes, public resource identities, ABI, and every hot lock-free state machine.
- The exact delta isolates owner artifacts below each store's `.owners.artifacts`
  directory and proves cold-open cost is independent of unrelated files in the
  shared root. C# and C++ implement the rule directly; Python inherits the C++
  native behavior.

### Exact-source verification

All rows below passed from the `fc605a0` production source tree in Release
configuration.

| Gate | Result |
|---|---|
| Windows managed unit, contract, integration, and linearizability | `445 + 113 + 275 + 83 = 916 passed` |
| Linux managed unit, contract, integration, and linearizability | `445 + 113 + 275 + 83 = 916 passed` |
| Windows native build, CTest, install, and clean consumer | `24/24 passed` |
| Linux native build, CTest, install, and clean consumer | `24/24 passed` |
| Windows and Linux Python source, rebuilt wheel/sdist, installed package, and sample | `83/83 passed per clean installed package` |
| Windows and Linux nine-pair interoperability | `153/153 passed per platform` |
| Windows and Linux stress interoperability | `10/10 passed per platform; 1,000 values per ordered pair and 10,000 lifecycle cycles` |
| Clean Linux Docker native/Python/interop build and test | `24/24 native, 153/153 interop, and 10/10 stress passed` |
| T137 focused regression | `8/8 integration cases passed, including 12,000 unrelated root files and 64 concurrent cold opens within the 500 ms per-open budget` |
| NuGet clean consumer, documentation validation, retired-path inspection, and whitespace validation | `passed` |

### Decision

The immutable baseline plus exact-source delta suite covers every implementation
and distribution affected by T137 while avoiding redundant repetition of the
unchanged high-volume schedules. Production readiness for source revision
`fc605a0` is **PASS**. A later evidence-only commit may record this decision
without changing the qualified production source tree.
