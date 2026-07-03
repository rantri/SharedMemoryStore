# Contract: Documentation Information Architecture

## Purpose

Define the required public documentation structure for first-class package
adoption. This contract governs navigation, reader journeys, concept placement,
feature coverage, and cross-linking.

## Required Entry Points

- `README.md` MUST identify package purpose, supported scenarios, non-goals,
  installation path, minimal workflow, documentation map, package status,
  license, support path, and release validation path.
- `docs/index.md` MUST be the public table of contents and route readers by
  goal: first use, feature learning, production evaluation, troubleshooting,
  samples, and maintainer internals.
- Package-facing README content MUST align with repository README content and
  package metadata.

## Required Simple-to-Advanced Path

The documentation index MUST expose this path:

1. Overview and package purpose.
2. Getting started.
3. Concepts.
4. Basic usage.
5. Feature guides.
6. Troubleshooting and diagnostics.
7. Samples.
8. Production evaluation: lifecycle, performance, portability, packaging, and
   release notes.
9. Maintainer internals: architecture, invariants, validation, and release
   responsibilities.

## Required Audience Routes

`docs/index.md` MUST include routes for:

- Evaluators deciding whether the package fits their scenario.
- New package consumers running the first workflow.
- Users learning all public features.
- Users troubleshooting statuses and diagnostics.
- Production reviewers checking lifecycle, performance, portability, and
  package maturity.
- Contributors and maintainers reviewing architecture and release impact.
- Future implementers reading language-neutral contracts without implying
  delivered bindings.

## Required Feature Coverage

User documentation MUST cover:

- Install or reference the package.
- Create or open a store.
- Validate options and choose capacities.
- Publish values.
- Acquire values.
- Read descriptors and payloads.
- Release leases.
- Remove values and reuse storage.
- Reserve, advance, commit, abort, and recover direct ingest.
- Publish segmented payloads.
- Configure or understand waits and contention outcomes.
- Inspect diagnostics.
- Run explicit recovery.
- Dispose resources.
- Prepare package consumption and release validation.

## Required Outcome Coverage

Troubleshooting and feature docs MUST cover:

- Success outcomes.
- Validation failures.
- Capacity failures.
- Duplicate and missing keys.
- Lease failures.
- Reservation failures.
- Contention or timeout outcomes.
- Disposed store outcomes.
- Unsupported platform outcomes.
- Cleanup and recovery outcomes.
- Corruption signals.
- Version mismatch signals.

## Link Contract

- Every public guide MUST be reachable from `docs/index.md`.
- Every sample README MUST be reachable from `docs/index.md` and from at least
  one related feature guide.
- Runtime behavior claims MUST link to a contract, current package metadata, or
  a guide that links to the relevant contract.
- Links MUST be relative repository links unless an external resource is
  necessary.

## Non-Goals

- This contract does not require a generated documentation website.
- This contract does not change runtime behavior or public API contracts.
- This contract does not require C++ or Python bindings.
