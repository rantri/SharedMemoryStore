# Documentation Index

This index is the public table of contents for SharedMemoryStore. It links the
repository entry points, package consumer guides, behavior contracts, community
policy files, samples, and release-maintenance material.

## Start Here

- [Root readme](../README.md): package purpose, prerelease status, first-use path,
  documentation map, and project policies.
- [Getting started](getting-started.md): local package source setup, first
  runnable workflow, and expected status outcomes.
- [Usage guide](usage.md): create/open, publish, reserve, segmented publish,
  acquire, release, remove, reuse, diagnostics, recovery, and dispose.
- [Examples](examples.md): basic workflow, error handling, and frame-shaped
  values represented as opaque bytes.
- [Integration](integration.md): optional lifecycle, health, and hosting
  boundaries outside the core package.

## By Audience

- Evaluators: [Root readme](../README.md), [Getting started](getting-started.md),
  [Packaging](packaging.md), [Support](../SUPPORT.md), [License](../LICENSE).
- Package consumers: [Usage guide](usage.md), [Errors](errors.md),
  [Diagnostics](diagnostics.md), [Lifecycle](lifecycle.md),
  [Integration](integration.md),
  [Basic sample](../samples/BasicUsage/README.md),
  [Zero-copy ingest sample](../samples/ZeroCopyIngest/README.md).
- Production reviewers: [Public API contract](../specs/001-frame-memory-store/contracts/public-api.md),
  [Error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md),
  [Shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md),
  [Performance scope](performance.md), [Portability](portability.md).
- Future implementers: [Shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md),
  [Lifecycle](lifecycle.md), [Portability](portability.md),
  [Frame value sample](../samples/FrameValue/README.md).
- Contributors: [Contributing](../CONTRIBUTING.md),
  [Code of conduct](../CODE_OF_CONDUCT.md),
  [Bug report template](../.github/ISSUE_TEMPLATE/bug_report.yml),
  [Documentation issue template](../.github/ISSUE_TEMPLATE/documentation.yml),
  [Feature request template](../.github/ISSUE_TEMPLATE/feature_request.yml),
  [Issue template configuration](../.github/ISSUE_TEMPLATE/config.yml),
  [Pull request template](../.github/pull_request_template.md).
- Maintainers: [Release preparation](releases.md), [Changelog](../CHANGELOG.md),
  [Packaging](packaging.md), [Support](../SUPPORT.md),
  [Security](../SECURITY.md).

## Guides

- [Getting started](getting-started.md)
- [Usage](usage.md)
- [Errors](errors.md)
- [Diagnostics](diagnostics.md)
- [Lifecycle](lifecycle.md)
- [Integration](integration.md)
- [Packaging](packaging.md)
- [Portability](portability.md)
- [Performance](performance.md)
- [Examples](examples.md)
- [Release preparation](releases.md)

## Repository Entry Points

- [README.md](../README.md)
- [License file](../LICENSE)
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

## Samples

- [samples/BasicUsage/README.md](../samples/BasicUsage/README.md)
- [samples/FrameValue/README.md](../samples/FrameValue/README.md)
- [samples/HostedServiceIntegration/README.md](../samples/HostedServiceIntegration/README.md)
- [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md)

## Contract Sources

- [specs/001-frame-memory-store/contracts/public-api.md](../specs/001-frame-memory-store/contracts/public-api.md)
- [specs/001-frame-memory-store/contracts/error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [specs/001-frame-memory-store/contracts/shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [specs/003-zero-copy-ingest/contracts/reservation-api.md](../specs/003-zero-copy-ingest/contracts/reservation-api.md)
- [specs/003-zero-copy-ingest/contracts/ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md](../specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md)

Runtime behavior claims in public documentation must trace back to these
contracts or to the current package project metadata in
[`src/SharedMemoryStore/SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj).
