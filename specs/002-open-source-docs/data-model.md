# Data Model: Open Source Documentation

## Documentation Set

Represents the complete public-facing documentation delivered by this feature.

**Fields**
- `id`: stable feature identifier, `open-source-docs`.
- `version_scope`: package/documentation version scope, initially `0.1.0`.
- `status`: `Draft`, `ReviewReady`, `Published`, or `NeedsUpdate`.
- `entry_points`: ordered list of repository entry point files.
- `documents`: collection of `DocumentationFile` records.
- `audiences`: collection of `DocumentationAudience` records.
- `validation_profile`: commands and review checks required before merge.

**Relationships**
- Owns many `DocumentationFile` records.
- Covers many `DocumentationAudience` records.
- References package metadata from `PackageDocumentation`.
- References policies from `CommunityPolicyDocument`.

**Validation Rules**
- Must include every common open source file required by FR-007.
- Must include project-specific content; unresolved placeholders are invalid.
- Must keep repository docs and package-facing docs consistent.
- Must clearly label implemented, prerelease, unsupported, and future work.

## Documentation File

Represents one Markdown, YAML, or policy file in the public documentation set.

**Fields**
- `path`: project-relative path such as `README.md` or `docs/usage.md`.
- `kind`: `Overview`, `UsageGuide`, `ContractReference`, `CommunityPolicy`,
  `ReleaseDocument`, `Template`, `SampleGuide`, or `ValidationScript`.
- `owner`: maintainer role responsible for keeping content current.
- `audience_ids`: audiences served by the file.
- `source_of_truth`: implementation or contract source the file must align to.
- `required_links`: internal links that must resolve.
- `status`: `Planned`, `Draft`, `ReviewReady`, `Published`, or `Deprecated`.

**Relationships**
- Belongs to one `Documentation Set`.
- May reference one or more `Contract Document` records.
- May be included by `PackageDocumentation` when packaged.

**Validation Rules**
- Root files must be reachable from `README.md`.
- `docs/` files must be reachable from `docs/index.md` and from at least one
  relevant repository entry point.
- File content must avoid stale commands, unresolved placeholders, and claims
  not supported by implementation, package metadata, or contracts.
- Public command examples must have expected outcomes.

## Documentation Audience

Represents one reader persona and its required path through the documentation.

**Fields**
- `id`: stable audience key.
- `name`: human-readable audience name.
- `primary_questions`: questions the docs must answer.
- `entry_path`: first document and follow-up links.
- `success_time_limit_minutes`: target time from the success criteria.

**Initial Records**
- `evaluator`: understands purpose, status, license, support, and first-use path.
- `consumer`: installs package and completes basic workflow.
- `production_reviewer`: evaluates lifecycle, diagnostics, compatibility,
  performance claim scope, and ownership.
- `future_implementer`: finds language-neutral layout, key, lease, lifecycle,
  and error rules.
- `contributor`: reports issues and submits pull requests.
- `maintainer`: prepares release documentation and policy updates.

**Validation Rules**
- Each audience must have a path from `README.md` or `docs/index.md`.
- Each path must answer the audience's core questions within its target time.

## Contract Document

Represents a detailed behavior or compatibility reference linked from public
docs.

**Fields**
- `path`: project-relative path.
- `contract_area`: `PublicApi`, `Errors`, `Lifecycle`, `Diagnostics`,
  `Layout`, `Portability`, `Package`, or `Release`.
- `behavior_source`: source file, existing spec contract, or package metadata.
- `semantic_version_impact`: how changes affect versioning promises.

**Relationships**
- Referenced by usage guides and production review docs.
- Must align with `specs/001-frame-memory-store/contracts/` for runtime
  behavior unless a later feature supersedes those contracts.

**Validation Rules**
- Must not introduce behavior beyond the implemented runtime package or approved
  future-work notes.
- Breaking public contract changes require migration/release notes.

## Package Documentation

Represents the documentation embedded in or aligned with the NuGet package.

**Fields**
- `package_id`: `SharedMemoryStore`.
- `version`: package version from the project file.
- `target_framework`: `net10.0`.
- `license_expression`: `MIT`.
- `readme_file`: package README file path.
- `release_notes`: package release notes text or linked release notes.
- `repository_url`: package source repository metadata when available.
- `support_path`: public support documentation path.

**Relationships**
- Draws summary and first-use material from `README.md`.
- Links to `CHANGELOG.md`, `SUPPORT.md`, `SECURITY.md`, and `docs/packaging.md`.

**Validation Rules**
- Package metadata must not conflict with repository docs.
- Package README must describe install/use/support paths without requiring
  source inspection.
- License metadata must match `LICENSE`.

## Community Policy Document

Represents project policy files and templates used by public contributors and
users.

**Fields**
- `path`: policy or template file path.
- `policy_area`: `License`, `Conduct`, `Security`, `Support`, `Contribution`,
  `IssueIntake`, `PullRequest`, or `Release`.
- `commitment_level`: `Policy`, `BestEffort`, `Prerelease`, or `OwnerApproval`.
- `private_contact_required`: true for vulnerability reporting policy.

**Relationships**
- Linked from `README.md`, `SUPPORT.md`, `CONTRIBUTING.md`, and issue templates.

**Validation Rules**
- Must not promise unsupported SLAs, warranty, paid support, or platform support.
- Security policy must direct vulnerability details away from public issues.
- Contribution guidance must include local validation, documentation updates,
  compatibility checks, tests, and review expectations.

## Validation Scenario

Represents one end-to-end documentation validation path.

**Fields**
- `name`: scenario name.
- `audience_id`: audience being validated.
- `commands`: commands or manual review steps.
- `expected_outcome`: measurable pass condition.
- `evidence`: files, command output, or checklist item used during review.

**Initial Scenarios**
- Repository evaluation from `README.md`.
- Clean consumer first-use using package docs.
- Contract trace from docs to public behavior.
- Contributor orientation through policy files and templates.
- Maintainer release readiness review.

**Validation Rules**
- Each success criterion must map to at least one validation scenario.
- Automated checks should cover inventory, links, placeholders, package
  metadata alignment, and runnable sample/package commands where practical.
