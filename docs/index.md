# Documentation Index

This is the public table of contents for SharedMemoryStore. It routes readers
by goal first, then provides the full file inventory for validation and review.

## Simple-To-Advanced Path

1. [README.md](../README.md): package purpose, supported scenarios, non-goals,
   package identity, first-use path, and policy links.
2. [Getting started](getting-started.md): create a local package source, run
   the smallest useful workflow, and compare expected output.
3. [Concepts](concepts.md): learn store, name, key, descriptor, payload, slot,
   lease, reservation, wait policy, status, diagnostics, recovery, capacity
   pressure, lifecycle, portability, and package contract vocabulary.
4. [Byte encoding](byte-encoding.md): choose canonical, allocation-conscious
   key, descriptor, and payload byte layouts.
5. [Usage](usage.md): create/open, options, capacity, publish, acquire, release,
   remove, reuse, reservation ingest, segmented publish, waits, diagnostics,
   recovery, and disposal.
6. [Examples](examples.md): basic values, frame-shaped values, direct ingest,
   segmented payloads, diagnostics, waits, and error handling.
7. [Errors](errors.md) and [Diagnostics](diagnostics.md): troubleshoot expected
   statuses and inspect caller-owned observability signals.
8. [Samples](samples.md): run the sample ladder from basic usage to optional
   hosted integration.
9. [Lifecycle](lifecycle.md), [Performance](performance.md),
   [Portability](portability.md), [Packaging](packaging.md), and
   [Release preparation](releases.md): evaluate production use and release
   readiness.
10. [Architecture](architecture.md) and [Maintainers](maintainers.md): review
   internals, invariants, validation, documentation maintenance, and release
   responsibilities.

## Goal Routes

| Goal | Route |
|------|-------|
| Decide whether the package fits | [README.md](../README.md) -> [Concepts](concepts.md) -> [Usage](usage.md) -> [Portability](portability.md) -> [Support](../SUPPORT.md) |
| Run the first workflow | [Getting started](getting-started.md) -> [samples/BasicUsage/README.md](../samples/BasicUsage/README.md) -> [Byte encoding](byte-encoding.md) -> [Usage](usage.md) |
| Learn every public feature | [Concepts](concepts.md) -> [Byte encoding](byte-encoding.md) -> [Usage](usage.md) -> [Examples](examples.md) -> [Samples](samples.md) |
| Troubleshoot a status | [Errors](errors.md) -> [Diagnostics](diagnostics.md) -> [Lifecycle](lifecycle.md) |
| Evaluate production use | [Lifecycle](lifecycle.md) -> [Performance](performance.md) -> [Portability](portability.md) -> [Packaging](packaging.md) -> [Release preparation](releases.md) |
| Run samples | [Samples](samples.md) -> [samples/BasicUsage/README.md](../samples/BasicUsage/README.md) -> [samples/FrameValue/README.md](../samples/FrameValue/README.md) -> [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md) -> [samples/HostedServiceIntegration/README.md](../samples/HostedServiceIntegration/README.md) |
| Review internals | [Architecture](architecture.md) -> [Maintainers](maintainers.md) -> contract sources |
| Prepare a contribution | [CONTRIBUTING.md](../CONTRIBUTING.md) -> [Maintainers](maintainers.md) -> [Release preparation](releases.md) |
| Review future portability | [Concepts](concepts.md) -> [Portability](portability.md) -> [shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md) |

## Guides

- [getting-started.md](getting-started.md)
- [concepts.md](concepts.md)
- [byte-encoding.md](byte-encoding.md)
- [usage.md](usage.md)
- [examples.md](examples.md)
- [errors.md](errors.md)
- [diagnostics.md](diagnostics.md)
- [lifecycle.md](lifecycle.md)
- [integration.md](integration.md)
- [performance.md](performance.md)
- [portability.md](portability.md)
- [samples.md](samples.md)
- [architecture.md](architecture.md)
- [maintainers.md](maintainers.md)
- [packaging.md](packaging.md)
- [releases.md](releases.md)

## Samples

- [samples/BasicUsage/README.md](../samples/BasicUsage/README.md)
- [samples/FrameValue/README.md](../samples/FrameValue/README.md)
- [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md)
- [samples/HostedServiceIntegration/README.md](../samples/HostedServiceIntegration/README.md)

## Repository Entry Points

- [README.md](../README.md)
- [LICENSE](../LICENSE)
- [CHANGELOG.md](../CHANGELOG.md)
- [CONTRIBUTING.md](../CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md)
- [SECURITY.md](../SECURITY.md)
- [SUPPORT.md](../SUPPORT.md)

## GitHub Templates

- [.github/ISSUE_TEMPLATE/bug_report.yml](../.github/ISSUE_TEMPLATE/bug_report.yml)
- [.github/ISSUE_TEMPLATE/documentation.yml](../.github/ISSUE_TEMPLATE/documentation.yml)
- [.github/ISSUE_TEMPLATE/feature_request.yml](../.github/ISSUE_TEMPLATE/feature_request.yml)
- [.github/ISSUE_TEMPLATE/config.yml](../.github/ISSUE_TEMPLATE/config.yml)
- [.github/pull_request_template.md](../.github/pull_request_template.md)

## Contract Sources

- [specs/001-frame-memory-store/contracts/public-api.md](../specs/001-frame-memory-store/contracts/public-api.md)
- [specs/001-frame-memory-store/contracts/error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [specs/001-frame-memory-store/contracts/shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [specs/003-zero-copy-ingest/contracts/reservation-api.md](../specs/003-zero-copy-ingest/contracts/reservation-api.md)
- [specs/003-zero-copy-ingest/contracts/ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md](../specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md)
- [specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md](../specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md)
- [specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md](../specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md)
- [specs/004-store-reliability-hardening/contracts/index-health-contract.md](../specs/004-store-reliability-hardening/contracts/index-health-contract.md)
- [specs/005-api-production-readiness/contracts/public-api-contract.md](../specs/005-api-production-readiness/contracts/public-api-contract.md)
- [specs/005-api-production-readiness/contracts/contention-configuration-contract.md](../specs/005-api-production-readiness/contracts/contention-configuration-contract.md)
- [specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md](../specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md)
- [specs/005-api-production-readiness/contracts/reservation-memory-contract.md](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md)
- [specs/006-improve-docs-samples/contracts/documentation-information-architecture.md](../specs/006-improve-docs-samples/contracts/documentation-information-architecture.md)
- [specs/006-improve-docs-samples/contracts/sample-contract.md](../specs/006-improve-docs-samples/contracts/sample-contract.md)
- [specs/006-improve-docs-samples/contracts/maintainer-documentation-contract.md](../specs/006-improve-docs-samples/contracts/maintainer-documentation-contract.md)
- [specs/006-improve-docs-samples/contracts/documentation-validation-contract.md](../specs/006-improve-docs-samples/contracts/documentation-validation-contract.md)

Runtime behavior claims in public documentation must trace to these contracts,
current package metadata, tests, or a guide that links to the relevant contract.
