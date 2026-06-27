# Quickstart Validation Guide

This guide describes how to validate the documentation feature after
implementation. It is not an implementation script.

## Prerequisites

- .NET SDK 10 compatible with the repository.
- PowerShell.
- Clean checkout of the repository.
- Active feature plan: `specs/002-open-source-docs/plan.md`.

## Documentation Inventory

Run the documentation validation helper planned for this feature:

```powershell
scripts/validate-docs.ps1
```

Expected outcome:

- every required root, `.github`, `docs/`, and sample README file exists.
- required files are project-specific and contain no unresolved placeholders.
- relative Markdown links resolve.
- `README.md` and `docs/index.md` link to the major documentation groups.
- package metadata and package-facing documentation agree.

## Manual Placeholder Check

Use a repository search to catch unresolved template text:

```powershell
rg -n "TODO|TBD|NEEDS CLARIFICATION|\\[[A-Z][A-Z _-]+\\]" README.md docs .github CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md SUPPORT.md CHANGELOG.md LICENSE samples
```

Expected outcome:

- no unresolved placeholder matches in public documentation.
- any intentional example token is explicitly documented as an example and is
  not a missing project value.

## Package Documentation Alignment

Build and pack the runtime package:

```powershell
dotnet restore
dotnet build -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Expected outcome:

- package builds for `net10.0`.
- package includes the configured README.
- package license metadata matches `LICENSE`.
- package release notes do not conflict with `CHANGELOG.md`.
- no new runtime dependency is added by the documentation feature.

## Clean Consumer Scenario

Validate the documented consumer path:

```powershell
scripts/validate-package-consumption.ps1
```

Expected outcome:

- a clean consumer project installs the local package.
- the consumer uses public APIs to create/open, publish, acquire, release,
  remove, reuse, and dispose.
- the scenario is consistent with `docs/getting-started.md` and
  `docs/usage.md`.

## Samples

Run the sample projects referenced by documentation:

```powershell
dotnet run --project samples/BasicUsage -c Release
dotnet run --project samples/FrameValue -c Release
```

Expected outcome:

- sample commands complete successfully.
- sample README files describe the same purpose, prerequisites, commands, and
  expected statuses.
- frame-shaped value docs state that frame layout is consumer-owned and the core
  store remains opaque-value based.

## Test Suite

Run the package tests to ensure docs did not drift from current behavior:

```powershell
dotnet test -c Release
```

Expected outcome:

- public API, contract, unit, and integration tests pass.
- documentation claims about statuses, lifecycle, package consumption, and
  portability remain consistent with tested behavior.

## Reader Workflow Review

Review the documentation against the success criteria:

- First-time evaluator: from `README.md`, identify purpose, maturity,
  installation path, first-use path, license, and support path in under
  10 minutes.
- Package consumer: follow `docs/getting-started.md` and complete the basic
  workflow in under 10 minutes.
- Production reviewer: locate lifecycle ownership, diagnostics, compatibility,
  versioning, performance scope, and portability statements within two
  navigation steps from the README.
- Contributor: identify issue reporting, support, local validation, pull
  request, review, conduct, documentation update, and security reporting
  expectations in under 15 minutes.
- Maintainer: use `docs/releases.md` and `CHANGELOG.md` to verify package
  description, license, release notes, compatibility, known limitations,
  support, and security reporting before publication.

Expected outcome:

- every user story from `spec.md` has a successful reader path.
- public docs contain no contradictory package, runtime, policy, or release
  claims.
