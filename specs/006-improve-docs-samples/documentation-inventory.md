# Documentation Inventory

This inventory records the documentation surfaces that must stay aligned for
the documentation and samples excellence feature.

## Root Entry Points

| Path | Audience | Current role | Reusable content |
|------|----------|--------------|------------------|
| `README.md` | Evaluators, first-time package consumers, maintainers | Package purpose, supported scenarios, non-goals, first-use path, documentation map, package status, policies, validation path | Package identity, non-goals, minimal workflow, policy links, contract links |
| `CHANGELOG.md` | Maintainers, package consumers | Versioned change history and compatibility impact | Package version, compatibility impact, documentation-only scope |
| `CONTRIBUTING.md` | Contributors, maintainers | Contribution setup, validation, doc update expectations | Validation commands, documentation review rules |
| `SUPPORT.md` | Consumers, maintainers | Support scope and reporting path | Supported and unsupported scenario wording |
| `SECURITY.md` | Consumers, maintainers | Private security reporting path | Same-host trust-boundary references |

## Public Guides

| Path | Audience | Current role | Reusable content |
|------|----------|--------------|------------------|
| `docs/index.md` | All readers | Goal-based navigation and simple-to-advanced path | Reader routes, guide inventory, contract inventory |
| `docs/getting-started.md` | First-time consumers | Local package source and minimal workflow | First-use command path and expected statuses |
| `docs/concepts.md` | Consumers, maintainers | Concept-first vocabulary | Store, name, key, descriptor, payload, slot, lease, reservation, status, diagnostics, portability |
| `docs/byte-encoding.md` | Package consumers, sample readers | Canonical byte encoding guidance | Integer, GUID, string, composite key, descriptor, and payload helper patterns |
| `docs/usage.md` | Package consumers | Task-oriented feature guide | Create/open, options, publish, acquire, remove, reservation, segmented publish, waits, diagnostics, recovery |
| `docs/examples.md` | Package consumers | Focused snippets and use cases | Basic values, frame-shaped values, direct ingest, segmented payloads, error handling |
| `docs/errors.md` | Troubleshooting readers | Status taxonomy and safe actions | Open statuses, operation statuses, likely causes, diagnostics |
| `docs/diagnostics.md` | Operators, production reviewers | Snapshot fields and observability boundaries | Capacity, recovery, failure count, tombstone, probe, support evidence fields |
| `docs/lifecycle.md` | Consumers, maintainers | Ownership and cleanup rules | Store, lease, reservation, removal, reuse, recovery, disposal, abnormal termination |
| `docs/integration.md` | Application owners | Optional lifecycle and health wrapper guidance | Narrow wrapper boundaries, no core hosting dependency |
| `docs/performance.md` | Production reviewers, maintainers | Evidence-bounded performance material | Benchmarks, measured scope, unmeasured scope, capacity assumptions |
| `docs/portability.md` | Future implementers, reviewers | .NET baseline and layout portability constraints | Windows-first validation, no delivered C++/Python bindings, same-host trust boundary |
| `docs/samples.md` | Learners, reviewers | Ordered sample ladder | Basic, frame, zero-copy ingest, hosted integration progression |
| `docs/architecture.md` | Maintainers | Public internals overview | Source areas, storage model, lifecycle, synchronization, diagnostics, recovery, invariants |
| `docs/maintainers.md` | Maintainers | Validation and documentation maintenance rules | Contract boundary review, validation commands, release impact checklist |
| `docs/packaging.md` | Maintainers, package consumers | Package metadata and clean consumer validation | Package fields, local source workflow, release-note alignment |
| `docs/releases.md` | Maintainers | Release readiness checklist | Metadata review, compatibility review, validation commands, known limitations |

## Runnable Samples

| Path | Audience | Current role | Reusable content |
|------|----------|--------------|------------------|
| `samples/BasicUsage/README.md` | New consumers | Minimal create/publish/acquire/release/remove workflow with allocation-conscious key and descriptor helpers | Getting-started output shape, byte encoding helper pattern, and cleanup |
| `samples/FrameValue/README.md` | Intermediate consumers | Consumer-owned descriptor and frame payload layout | Frame neutrality, multiple readers, `RemovePending` |
| `samples/ZeroCopyIngest/README.md` | Advanced producers | Reservation ingest, segmented publish, abort cleanup, reader workflow | Direct ingest and segmented publish output shape |
| `samples/HostedServiceIntegration/README.md` | Application owners | Optional lifecycle and health wrapper | Start/stop, diagnostics, explicit recovery, no core hosting dependency |

## Validation and Source-Of-Truth Files

| Path | Role |
|------|------|
| `scripts/validate-docs.ps1` | Required docs, links, placeholders, sample README sections, public reference drift, package metadata alignment |
| `scripts/validate-package-consumption.ps1` | Clean consumer package install and first-use workflow validation |
| `src/SharedMemoryStore/SharedMemoryStore.csproj` | Package identity, readme packing, release notes, target framework, runtime dependency boundary |
| `src/SharedMemoryStore/MemoryStore.cs` | Public store method names and XML documentation examples |
| `samples/BasicUsage/StoreByteEncoding.cs` | Sample-only span-writing helpers for integer keys, descriptors, and UTF-8 text |
| `src/SharedMemoryStore/SharedMemoryStoreOptions.cs` | Option names, `OpenMode`, validation API, lease recovery report shape |
| `src/SharedMemoryStore/StoreWaitOptions.cs` | Wait policy names and timeout/cancellation semantics |
| `src/SharedMemoryStore/StoreStatus.cs` | Open and operation status names |
| `src/SharedMemoryStore/ValueLease.cs` | Lease token shape and release/dispose behavior |
| `src/SharedMemoryStore/Ingest/ValueReservation.cs` | Reservation token shape and direct write lifecycle |

## Contract References

| Path | Behavior area |
|------|---------------|
| `specs/001-frame-memory-store/contracts/public-api.md` | Public create/open, publish, acquire, release, remove, diagnostics API |
| `specs/001-frame-memory-store/contracts/error-taxonomy.md` | Status names and outcome categories |
| `specs/001-frame-memory-store/contracts/shared-memory-layout.md` | Shared layout, opaque bytes, lease and slot lifecycle model |
| `specs/003-zero-copy-ingest/contracts/reservation-api.md` | Reservation API and completion behavior |
| `specs/003-zero-copy-ingest/contracts/ingest-layout.md` | Pending reservation layout and visibility rules |
| `specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md` | Reservation diagnostics and error outcomes |
| `specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md` | Owner-scoped lease recovery behavior |
| `specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md` | Disposal race and lifecycle rollover behavior |
| `specs/004-store-reliability-hardening/contracts/index-health-contract.md` | Key-index tombstone and compaction diagnostics |
| `specs/005-api-production-readiness/contracts/public-api-contract.md` | Production public API naming and compatibility contract |
| `specs/005-api-production-readiness/contracts/contention-configuration-contract.md` | Wait policy and contention behavior |
| `specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md` | Diagnostics integration and observability boundaries |
| `specs/005-api-production-readiness/contracts/reservation-memory-contract.md` | Immediate reservation span access and memory lifetime |

## Gaps Closed By This Feature

- Add concept-first and sample-ladder guides so advanced material has a clear
  progression.
- Add public maintainer internals docs without turning implementation details
  into compatibility promises.
- Expand validation to include the new guide inventory, all sample README
  contract sections, broader relative-link coverage, public type/status/method
  names, package release-note alignment, and stale public references.
- Align package metadata, package README wording, changelog, packaging guide,
  release guide, and sample outputs with the current `1.0.0` public surface.
