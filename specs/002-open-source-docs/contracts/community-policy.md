# Community Policy Contract

## License Policy

- The repository license file must match the package license expression.
- Current selected license baseline: MIT, based on package metadata.
- `README.md`, `LICENSE`, package metadata, and release documentation must not
  conflict.

## Code of Conduct

- `CODE_OF_CONDUCT.md` must provide project-specific conduct expectations and a
  maintainer-owned enforcement path.
- The document must avoid placeholders and must not name unavailable contacts.
- `CONTRIBUTING.md` and issue templates must link to the code of conduct.

## Security Policy

- `SECURITY.md` must direct vulnerability reporters to a private reporting path.
- Selected default path: GitHub private vulnerability reporting/security
  advisories for this repository.
- The policy must instruct reporters not to include exploit details in public
  issues.
- The policy must describe supported versions or state that support is
  prerelease/best effort until the first stable release.
- If the repository cannot enable GitHub private vulnerability reporting before
  publication, maintainers must replace the default path with an
  owner-approved private contact path before release.

## Support Policy

- `SUPPORT.md` must distinguish general questions, bug reports, security
  reports, documentation issues, feature requests, and unsupported scenarios.
- The policy must not promise response-time SLAs unless maintainers explicitly
  approve them.
- The policy must clearly state that current support is best effort for a
  prerelease library unless release policy changes.

## Contribution Policy

`CONTRIBUTING.md` must cover:

- local setup prerequisites.
- build, test, package, sample, benchmark, and documentation validation commands.
- how to choose issue templates.
- pull request expectations.
- documentation update requirements for public behavior changes.
- compatibility and semantic-version review expectations.
- security disclosure expectations.
- code of conduct link.

## Issue and Pull Request Templates

Issue templates must collect enough information for maintainers to reproduce or
triage reports:

- bug reports: package version, OS, .NET SDK/runtime, store options, operation,
  observed status, expected status, reproduction steps, logs without secrets.
- documentation issues: affected file/link, observed problem, expected change.
- feature requests: use case, public API impact, compatibility impact, runtime
  dependency impact, alternatives considered.

The pull request template must ask for:

- summary and motivation.
- behavior/API/package metadata impact.
- tests and documentation validation run.
- compatibility/semantic-version impact.
- linked issue or rationale.
- security/support/release note impact when applicable.

## Release and Changelog Policy

- `CHANGELOG.md` must use reverse chronological entries.
- `docs/releases.md` must define release preparation checks for package
  description, release notes, compatibility, known limitations, support,
  security, license, and documentation links.
- Documentation-only changes must be identified as such unless they change a
  public compatibility promise.
