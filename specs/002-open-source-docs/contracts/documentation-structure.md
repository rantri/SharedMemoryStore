# Documentation Structure Contract

## Required Repository Files

The open source documentation set must include these root-level files:

- `README.md`
- `LICENSE`
- `CHANGELOG.md`
- `CONTRIBUTING.md`
- `CODE_OF_CONDUCT.md`
- `SECURITY.md`
- `SUPPORT.md`

The documentation set must include these GitHub workflow files:

- `.github/ISSUE_TEMPLATE/bug_report.yml`
- `.github/ISSUE_TEMPLATE/documentation.yml`
- `.github/ISSUE_TEMPLATE/feature_request.yml`
- `.github/ISSUE_TEMPLATE/config.yml`
- `.github/pull_request_template.md`

The documentation set must include these guide files:

- `docs/index.md`
- `docs/getting-started.md`
- `docs/usage.md`
- `docs/errors.md`
- `docs/diagnostics.md`
- `docs/lifecycle.md`
- `docs/packaging.md`
- `docs/portability.md`
- `docs/performance.md`
- `docs/examples.md`
- `docs/releases.md`

The documentation set must include sample-specific guidance:

- `samples/BasicUsage/README.md`
- `samples/FrameValue/README.md`

## Repository Entry Point Rules

- `README.md` must be the first-visit overview and must link to all major doc
  groups: getting started, usage, contracts, examples, lifecycle, package,
  support, security, contributing, license, changelog, and release notes.
- `README.md` must state package purpose, current prerelease maturity, target
  framework, package id, license, first supported validation platform, and
  future C++/Python status.
- `docs/index.md` must act as the complete documentation table of contents.
- Root policy files must link back to `README.md` or `docs/index.md` where
  useful, but must remain readable on their own.

## Content Accuracy Rules

- Runtime behavior must align with the public API and existing contracts from
  `specs/001-frame-memory-store/contracts/`.
- Documentation must not claim C++ or Python bindings exist.
- Documentation must not claim broad platform support beyond validated support.
- Performance language must describe documented benchmark scenarios and must not
  imply guarantees on unmeasured hardware.
- Frame-shaped values must be documented as consumer-owned layouts, not core
  store concepts.
- Expected failures must be described as status outcomes, not exceptions, except
  where the runtime contract explicitly reserves exceptions.

## Link and Placeholder Rules

- Every relative Markdown link must resolve.
- Every required file must be reachable within two navigation steps from
  `README.md`.
- Public docs must not contain unresolved placeholder tokens such as `TODO`,
  `TBD`, `NEEDS CLARIFICATION`, `[PROJECT]`, `[EMAIL]`, or template-only text.
- Examples must include expected output or expected status outcomes.

## Maintenance Rules

- Documentation changes that alter public behavior claims must update the
  relevant contract/reference docs in the same change.
- Public API, package metadata, support policy, security policy, license,
  platform support, or performance claim changes must update `CHANGELOG.md` or
  `docs/releases.md` guidance as appropriate.
- Documentation-only pull requests still require placeholder/link validation and
  reviewer confirmation that claims remain accurate.
