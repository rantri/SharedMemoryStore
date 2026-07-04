# Contract: Development and Validation

## Scope

This contract defines the repository workflows required to prove Linux,
Windows, and same-host Docker support before release.

## Clean Checkout Validation

From clean Linux and Windows checkouts, maintainers must be able to run:

```powershell
dotnet restore
dotnet build SharedMemoryStore.slnx -c Release
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Repository scripts must also run with portable shell invocation:

```powershell
pwsh ./scripts/validate-docs.ps1
pwsh ./scripts/validate-package-consumption.ps1
```

On Windows, `powershell` may remain supported, but `pwsh` must work on Linux.

## Runtime Validation Matrix

The release validation matrix must include:

| Environment | Required Coverage |
|-------------|-------------------|
| Windows | Full runtime, tests, samples, package consumption, docs, pack |
| Linux | Full runtime, tests, samples, package consumption, docs, pack |
| Docker same-host profile | Cross-container visibility, lease protection, diagnostics, recovery |

## Script Requirements

Validation scripts must:

- Use `Join-Path`, `Resolve-Path`, or equivalent structured path handling.
- Avoid hard-coded Windows path separators.
- Avoid assuming `powershell` exists on Linux.
- Clean only generated artifacts under approved artifact directories.
- Fail with actionable output that identifies the platform and workflow.
- Preserve existing package artifacts unless the script owns their directory.

## Test Requirements

Automated tests must cover:

- Public API contracts and status values.
- Store creation/opening on Linux and Windows.
- Multi-process visibility.
- Contended synchronization.
- Owner recovery and unsupported owner handling.
- Reservation recovery.
- Disposal races.
- Long-running reuse and churn.
- Docker cross-container workflows.
- Clean package consumption.

## Documentation Validation

Documentation validation must fail when docs or metadata still claim Linux,
Docker, or container usage is unsupported, future-only, or unvalidated for the
shipping package version.
