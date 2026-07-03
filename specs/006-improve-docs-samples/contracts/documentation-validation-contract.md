# Contract: Documentation Validation

## Purpose

Define the validation checks required for documentation and sample changes to
be considered release-ready.

## Required Automated Checks

The validation workflow MUST include:

- Required documentation inventory check.
- Required sample README inventory check.
- Relative Markdown link validation for public docs and sample READMEs.
- Placeholder scan for TODO, TBD, clarification markers, and template markers.
- Package metadata alignment check for README, package project, license,
  changelog, release notes, and packaging guide.
- Required cross-link checks from README and `docs/index.md`.
- Sample project build validation.
- Package consumption validation from a clean consumer project.

## Required Manual Review Checks

The validation workflow MUST include a review for:

- Public workflow coverage.
- Public status and outcome coverage.
- Stale public API, option, type, method, and status names.
- Unsupported behavior claims.
- Performance evidence and benchmark context.
- Platform and portability wording.
- Maintainer internals wording that could accidentally create a public
  compatibility promise.
- Release-note and changelog impact.

## Required Commands

The quickstart validation guide MUST include:

```powershell
scripts/validate-docs.ps1
dotnet build SharedMemoryStore.slnx -c Release
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
scripts/validate-package-consumption.ps1
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Sample commands MAY be adjusted during implementation if a sample has a
documented platform limitation or if the validation script wraps them.

## Pass Criteria

Validation passes when:

- All automated checks complete successfully.
- Every sample builds and its documented output shape is still accurate.
- Public docs contain no unresolved placeholders or broken relative links.
- Public API and status names match the current package surface.
- README, package metadata, changelog, release notes, packaging guide, and
  support/security references are aligned.
- Performance and platform claims are evidence-bounded.
- Maintainer internals do not create unsupported public promises.

## Failure Handling

Any failed validation item MUST be resolved before release, or explicitly
recorded in release notes as an unsupported or known limitation when that is the
correct user-facing outcome.
