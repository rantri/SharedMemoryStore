# Documentation Coverage Matrix

This matrix records how the documentation set covers reader journeys, public
workflows, outcomes, samples, and maintainer responsibilities.

## Reader Journeys

| Journey | Entry point | Ordered route | Success check | Status |
|---------|-------------|---------------|---------------|--------|
| First use | `README.md` | `README.md` -> `docs/getting-started.md` -> `samples/BasicUsage/README.md` -> `docs/usage.md` | Reader can identify purpose, non-goals, install path, minimal workflow, expected output, and next step in under 10 minutes | Reviewed |
| Feature learning | `docs/index.md` | `docs/concepts.md` -> `docs/byte-encoding.md` -> `docs/usage.md` -> `docs/examples.md` -> related sample README | Any public feature has concept, workflow, outcome, ownership, example/sample, and contract reference within two steps | Reviewed |
| Troubleshooting | `docs/index.md` | `docs/errors.md` -> `docs/diagnostics.md` -> `docs/lifecycle.md` | Every outcome category has likely cause, safe action, and diagnostic signal | Reviewed |
| Sample exploration | `docs/index.md` | `docs/samples.md` -> sample README -> related guide | Each sample states audience, concepts, command, expected output, cleanup, non-success statuses, and related docs | Reviewed |
| Production evaluation | `docs/index.md` | `docs/lifecycle.md` -> `docs/performance.md` -> `docs/portability.md` -> `docs/packaging.md` -> `docs/releases.md` | Platform, performance, package, lifecycle, and release claims are scoped and evidence-bounded | Reviewed |
| Maintainer onboarding | `docs/index.md` | `docs/architecture.md` -> `docs/maintainers.md` -> contracts -> validation scripts | Maintainer can explain source areas, invariants, contract boundaries, validation, and release impact | Reviewed |

## Public Workflow Coverage

| Workflow | Primary guide | Example or sample | Contract trace | Status |
|----------|---------------|-------------------|----------------|--------|
| Install or reference package | `docs/getting-started.md`, `docs/packaging.md` | `scripts/validate-package-consumption.ps1` | `src/SharedMemoryStore/SharedMemoryStore.csproj` | Covered |
| Create or open store | `docs/usage.md` | `samples/BasicUsage/README.md` | `specs/001-frame-memory-store/contracts/public-api.md` | Covered |
| Validate options | `docs/usage.md`, `docs/integration.md` | `samples/HostedServiceIntegration/README.md` | `specs/005-api-production-readiness/contracts/public-api-contract.md` | Covered |
| Choose capacities | `docs/concepts.md`, `docs/usage.md`, `docs/performance.md` | `samples/BasicUsage/README.md` | `specs/001-frame-memory-store/contracts/shared-memory-layout.md` | Covered |
| Encode keys, descriptors, and payload bytes | `docs/byte-encoding.md`, `docs/usage.md`, `docs/examples.md` | `samples/BasicUsage/README.md` | `specs/001-frame-memory-store/contracts/shared-memory-layout.md` | Covered |
| Publish values | `docs/usage.md` | `samples/BasicUsage/README.md` | `specs/001-frame-memory-store/contracts/public-api.md` | Covered |
| Acquire values | `docs/usage.md`, `docs/lifecycle.md` | `samples/BasicUsage/README.md` | `specs/001-frame-memory-store/contracts/public-api.md` | Covered |
| Read descriptor and payload bytes | `docs/concepts.md`, `docs/examples.md` | `samples/FrameValue/README.md` | `specs/001-frame-memory-store/contracts/shared-memory-layout.md` | Covered |
| Release leases | `docs/lifecycle.md` | `samples/FrameValue/README.md` | `specs/001-frame-memory-store/contracts/public-api.md` | Covered |
| Remove values and reuse storage | `docs/usage.md`, `docs/lifecycle.md` | `samples/BasicUsage/README.md`, `samples/FrameValue/README.md` | `specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md` | Covered |
| Reserve direct ingest | `docs/usage.md`, `docs/examples.md` | `samples/ZeroCopyIngest/README.md` | `specs/003-zero-copy-ingest/contracts/reservation-api.md` | Covered |
| Advance and commit reservation | `docs/usage.md`, `docs/lifecycle.md` | `samples/ZeroCopyIngest/README.md` | `specs/003-zero-copy-ingest/contracts/ingest-layout.md` | Covered |
| Abort reservation | `docs/errors.md`, `docs/lifecycle.md` | `samples/ZeroCopyIngest/README.md` | `specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md` | Covered |
| Publish segmented payloads | `docs/usage.md`, `docs/examples.md` | `samples/ZeroCopyIngest/README.md` | `specs/003-zero-copy-ingest/contracts/reservation-api.md` | Covered |
| Configure or handle waits | `docs/usage.md`, `docs/errors.md` | `samples/HostedServiceIntegration/README.md` | `specs/005-api-production-readiness/contracts/contention-configuration-contract.md` | Covered |
| Inspect diagnostics | `docs/diagnostics.md` | `samples/HostedServiceIntegration/README.md` | `specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md` | Covered |
| Run explicit recovery | `docs/lifecycle.md`, `docs/diagnostics.md` | `samples/HostedServiceIntegration/README.md` | `specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md` | Covered |
| Dispose resources | `docs/lifecycle.md` | All sample READMEs | `specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md` | Covered |
| Prepare package consumption and release validation | `docs/packaging.md`, `docs/releases.md`, `docs/maintainers.md` | `scripts/validate-package-consumption.ps1` | `specs/006-improve-docs-samples/contracts/documentation-validation-contract.md` | Covered |

## Outcome Coverage

| Outcome category | Primary guide | Diagnostic/support link | Status |
|------------------|---------------|-------------------------|--------|
| Success outcomes | `docs/getting-started.md`, `docs/usage.md`, sample READMEs | `docs/samples.md` | Covered |
| Validation failures | `docs/errors.md`, `docs/usage.md` | `docs/diagnostics.md` | Covered |
| Capacity failures | `docs/errors.md`, `docs/performance.md` | `docs/diagnostics.md` | Covered |
| Duplicate and missing keys | `docs/errors.md`, `docs/usage.md` | `samples/BasicUsage/README.md` | Covered |
| Lease failures | `docs/errors.md`, `docs/lifecycle.md` | `docs/diagnostics.md` | Covered |
| Reservation failures | `docs/errors.md`, `docs/lifecycle.md` | `samples/ZeroCopyIngest/README.md` | Covered |
| Contention or timeout outcomes | `docs/errors.md`, `docs/usage.md` | `docs/integration.md` | Covered |
| Disposed store outcomes | `docs/errors.md`, `docs/lifecycle.md` | `samples/HostedServiceIntegration/README.md` | Covered |
| Unsupported platform outcomes | `docs/errors.md`, `docs/portability.md` | `SUPPORT.md` | Covered |
| Cleanup and recovery outcomes | `docs/lifecycle.md`, `docs/diagnostics.md` | `samples/HostedServiceIntegration/README.md` | Covered |
| Corruption signals | `docs/errors.md`, `docs/diagnostics.md` | `SUPPORT.md` | Covered |
| Version mismatch signals | `docs/errors.md`, `docs/portability.md`, `docs/releases.md` | `CHANGELOG.md` | Covered |

## User Story Review Rows

| Story | Review result | Remaining entry-point gaps |
|-------|---------------|----------------------------|
| US1 Start Successfully as a New User | README, getting started, docs index, and BasicUsage now provide purpose, non-goals, local package source, minimal workflow, output, cleanup, and next links. | None known after validation. |
| US2 Learn Every Public Feature in Context | Concepts, byte encoding, usage, examples, errors, diagnostics, lifecycle, integration, packaging, and contract links cover every FR-004 workflow and FR-006 outcome category. | None known after validation. |
| US3 Progress Through Runnable Samples | `docs/samples.md` and all four sample READMEs define audience, concepts, prerequisites, run command, output shape, non-success statuses, cleanup, and links. | None known after validation. |
| US4 Understand Internals as a Maintainer | Architecture, maintainers, performance, portability, releases, README, and index expose internals, invariants, performance evidence, validation, and release responsibilities within two steps. | None known after validation. |
| US5 Keep Documentation Trustworthy Over Time | Maintainers, releases, packaging, contributing, changelog, validation scripts, package metadata, and quickstart define maintenance and release-review rules. | None known after validation. |

## Manual Reader Workflow Review

| Quickstart step | Result |
|-----------------|--------|
| `scripts/validate-docs.ps1` | Passed. Required inventory, placeholders, relative links, package metadata, cross-links, sample README contracts, and public API/status reference checks passed. |
| `dotnet build SharedMemoryStore.slnx -c Release` | Passed with 0 warnings and 0 errors. |
| Sample ladder commands | Passed. BasicUsage, FrameValue, ZeroCopyIngest, and HostedServiceIntegration output matched README shapes. |
| `scripts/validate-package-consumption.ps1` | Passed. Local package creation, clean consumer install, first-use workflow, direct ingest, segmented publish, recovery, and disposed status checks succeeded. |
| `dotnet test SharedMemoryStore.slnx -c Release` | Passed. 44 unit, 37 contract, and 30 integration tests passed. |
| `dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package` | Passed. `SharedMemoryStore.1.0.0.nupkg` was created in `artifacts/package`. |
| Manual unsupported-claim review | Performance, portability, integration, architecture, and maintainer wording are scoped to Windows-first .NET 10 validation, same-host trust, no persistence, no distributed cache, no hidden background work, and future C++/Python as portability context only. |
