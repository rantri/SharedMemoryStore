# Contract: Maintainer Documentation

## Purpose

Define what maintainer-facing documentation must explain so future changes
preserve package contracts, performance expectations, validation discipline,
and documentation quality.

## Required Topics

Maintainer documentation MUST cover:

- Package purpose and responsibility boundaries.
- Public contract versus changeable implementation detail.
- Major source areas and their responsibilities.
- Shared-memory storage model at a conceptual level.
- Slot, key, descriptor, payload, lease, and reservation lifecycles.
- Synchronization, wait, contention, and cancellation expectations.
- Recovery ownership and abnormal termination handling.
- Diagnostics taxonomy and caller-owned observability boundaries.
- Performance evidence rules, benchmark methodology, and claim boundaries.
- Portability constraints and future implementation considerations.
- Package metadata and release documentation responsibilities.
- Documentation update rules for public behavior, API names, statuses,
  samples, performance claims, platform support, diagnostics, and release
  status.

## Contract Boundary Rules

- Stable public behavior MUST be identified as a package contract and linked to
  the relevant contract source.
- Implementation details MAY be explained for maintainability but MUST be
  labeled as current implementation detail when they are not compatibility
  promises.
- Maintainer docs MUST NOT imply new support guarantees, hidden background
  work, broad service abstractions, persistence semantics, or future language
  bindings.

## Review Rules

A maintainer reviewing a change MUST be able to use the docs to answer:

- Which public contracts could this change affect?
- Which docs and samples must be updated?
- Which validation commands must pass?
- Does this change affect package metadata or release notes?
- Does this change alter a public compatibility promise?
- Is performance wording backed by evidence?

## Required Links

Maintainer documentation MUST link to:

- Public API and error/status contracts.
- Shared-memory layout or lifecycle contracts.
- Production API readiness contracts.
- Benchmark and performance guide.
- Validation scripts.
- Package project metadata.
- Release preparation guide.

## Non-Goals

- Maintainer documentation does not replace source code comments for local
  implementation details.
- Maintainer documentation does not turn every private type or file layout into
  a stable public contract.
