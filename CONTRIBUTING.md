# Contributing

Contributions should keep SharedMemoryStore usable as a general-purpose
`net10.0` package with stable public contracts, clear diagnostics, and no
unplanned runtime dependencies.

By participating, follow the [Code of conduct](CODE_OF_CONDUCT.md). For support
questions, use [SUPPORT.md](SUPPORT.md). For private vulnerability reports, use
[SECURITY.md](SECURITY.md) instead of public issues.

## Local Setup

Prerequisites:

- .NET SDK compatible with `net10.0`.
- PowerShell 7 (`pwsh`) for repository scripts.
- Linux or Windows for ordinary runtime and development validation.
- Docker Engine or Docker Desktop when validating same-host container sharing.

Restore and build:

```powershell
dotnet restore
dotnet build -c Release
```

## Validation Commands

Run the relevant checks before opening a pull request:

```powershell
pwsh ./scripts/validate-docs.ps1
dotnet build SharedMemoryStore.slnx -c Release
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
dotnet run --project samples/DockerSharedMemory/DockerSharedMemory.csproj -c Release -- all
pwsh ./scripts/validate-package-consumption.ps1
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
pwsh ./scripts/validate-cross-platform.ps1 -SkipDocker
pwsh ./scripts/validate-docker-shared-memory.ps1
```

Benchmarks are useful for performance-sensitive changes:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release
```

## Issues

Use the GitHub templates:

- bug reports: [.github/ISSUE_TEMPLATE/bug_report.yml](.github/ISSUE_TEMPLATE/bug_report.yml).
- documentation issues: [.github/ISSUE_TEMPLATE/documentation.yml](.github/ISSUE_TEMPLATE/documentation.yml).
- feature requests: [.github/ISSUE_TEMPLATE/feature_request.yml](.github/ISSUE_TEMPLATE/feature_request.yml).

Do not include secrets, proprietary payloads, or vulnerability exploit details
in public issues.

## Pull Requests

Use [.github/pull_request_template.md](.github/pull_request_template.md). A
maintainable pull request should include:

- summary and motivation.
- behavior, API, package metadata, compatibility, and dependency impact.
- validation commands run and results.
- documentation updates for public behavior changes.
- release note or changelog impact.
- linked issue or rationale.

## Documentation Requirements

Public behavior changes must update the relevant docs and contracts in the same
change:

- [Usage](docs/usage.md)
- [Errors](docs/errors.md)
- [Lifecycle](docs/lifecycle.md)
- [Diagnostics](docs/diagnostics.md)
- [Portability](docs/portability.md)
- [Samples](docs/samples.md)
- [Packaging](docs/packaging.md)
- [Public API contract](specs/001-frame-memory-store/contracts/public-api.md)
- [Error taxonomy contract](specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [Shared-memory layout contract](specs/001-frame-memory-store/contracts/shared-memory-layout.md)

Documentation-only changes still require `scripts/validate-docs.ps1`.
They also require a release-impact review against
[Maintainers](docs/maintainers.md), [Release preparation](docs/releases.md),
[Packaging](docs/packaging.md), [CHANGELOG.md](CHANGELOG.md),
[SUPPORT.md](SUPPORT.md), and [SECURITY.md](SECURITY.md) when wording touches
public behavior, support scope, security process, package metadata, platform
support, performance claims, diagnostics, samples, or release status.

## Compatibility Review

Before changing public APIs, layout fields, state values, lifecycle rules,
status values, package metadata, support commitments, or security process,
record the semantic-version impact and update [CHANGELOG.md](CHANGELOG.md) or
[Release preparation](docs/releases.md) guidance as needed.

## Runtime Dependency Discipline

Runtime dependency additions require an explicit feature plan, license review,
transitive dependency review, and package impact review. Documentation tooling
should remain repository-local and outside the runtime package unless a feature
plan approves otherwise.
