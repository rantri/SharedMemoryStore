# Data Model: Documentation and Samples Excellence

## Documentation Set

**Represents**: The complete public and maintainer-facing documentation surface
for the package.

**Fields**:
- `entry_points`: README, `docs/index.md`, package README content, and release
  notes entry points.
- `user_guides`: getting started, concepts, usage, examples, errors,
  diagnostics, lifecycle, integration, performance, portability, packaging, and
  release preparation.
- `maintainer_guides`: architecture, maintainers, validation, release, and
  documentation update responsibilities.
- `sample_guides`: sample ladder page plus each sample README.
- `contract_references`: Spec Kit contracts and package metadata that support
  public behavior claims.
- `validation_rules`: link checks, placeholder checks, sample build/run checks,
  public API/status reference checks, package metadata alignment, release note
  alignment, and unsupported-claim checks.

**Relationships**:
- Contains many `Reader Journey` entries.
- Contains many `Feature Guide` entries.
- Contains many `Runnable Sample` entries.
- References many `Package Contract Reference` entries.
- Is reviewed by a `Documentation Validation Review`.

**Validation Rules**:
- Must have a single obvious navigation path from README and `docs/index.md`.
- Must cover every public workflow and outcome category named in the feature
  specification.
- Must have no unresolved placeholders, broken internal links, stale public API
  names, stale status names, contradictory behavior statements, or unsupported
  behavior claims.

## Reader Journey

**Represents**: A goal-based path through the documentation.

**Fields**:
- `goal`: first use, feature learning, production evaluation,
  troubleshooting, sample exploration, maintainer onboarding, or release
  maintenance.
- `audience`: evaluator, package consumer, production reviewer, contributor,
  maintainer, or future implementer.
- `starting_point`: README section or `docs/index.md` section.
- `ordered_pages`: the pages a reader follows from simple to advanced.
- `success_measure`: time or completeness target from the feature success
  criteria.

**Relationships**:
- Uses one or more `Concept Guide` and `Feature Guide` entries.
- May include one or more `Runnable Sample` entries.
- May reference one or more `Package Contract Reference` entries.

**Validation Rules**:
- Must be discoverable within two navigation steps from `docs/index.md`.
- First-use journey must allow a clean consumer to run the documented workflow
  in under 10 minutes.
- Maintainer journey must expose architecture, invariants, validation, and
  release responsibilities within two navigation steps.

## Concept Guide

**Represents**: The shared vocabulary and mental model used by all other docs.

**Fields**:
- `concept_name`: package-specific term such as store, key, descriptor, slot,
  lease, reservation, wait policy, diagnostics snapshot, or package contract.
- `plain_language_definition`: short user-facing explanation.
- `why_it_matters`: when the reader needs the concept.
- `related_workflows`: feature guides or samples that depend on the concept.
- `contract_links`: source contracts that define stable behavior, when
  applicable.

**Relationships**:
- Supports many `Feature Guide` entries.
- Supports many `Runnable Sample` README explanations.

**Validation Rules**:
- Must introduce package-specific concepts before advanced workflows rely on
  them.
- Must not expose implementation details as public guarantees.

## Feature Guide

**Represents**: Task-oriented documentation for a public package capability.

**Fields**:
- `feature_area`: create/open, options, capacity, publish, acquire, lease,
  remove, reuse, reservation ingest, segmented publish, diagnostics, recovery,
  waits, disposal, troubleshooting, packaging, or integration.
- `use_when`: guidance for when to use the capability.
- `avoid_when`: non-goals or inappropriate use cases.
- `workflow`: user-facing steps and expected outcomes.
- `status_coverage`: expected success and non-success outcomes.
- `ownership_rules`: resources or responsibilities the user owns.
- `examples`: snippets or sample links.
- `contract_links`: references supporting behavior claims.

**Relationships**:
- Depends on concepts from `Concept Guide`.
- May be demonstrated by one or more `Runnable Sample` entries.
- Must trace behavior claims to `Package Contract Reference` entries.

**Validation Rules**:
- Must include purpose, usage, outcomes, ownership, troubleshooting links, and
  sample or example coverage where applicable.
- Must cover all public workflows listed in the feature specification.

## Runnable Sample

**Represents**: A sample project that users can run from a clean checkout.

**Fields**:
- `sample_name`: BasicUsage, FrameValue, ZeroCopyIngest,
  HostedServiceIntegration, or future sample name.
- `audience`: beginner, intermediate, advanced, production evaluator, or
  maintainer.
- `concepts_demonstrated`: concepts and features shown by the sample.
- `prerequisites`: SDK, platform, package source, or repository requirements.
- `run_command`: exact command from repository root.
- `expected_output_shape`: stable output expectations without brittle machine
  details.
- `cleanup_guidance`: what resources are created and how cleanup happens.
- `expected_non_success_statuses`: documented statuses users may see and why.
- `related_docs`: links to guides and contracts.

**Relationships**:
- Demonstrates one or more `Feature Guide` entries.
- Appears in a `Reader Journey`.
- Is checked by `Documentation Validation Review`.

**Validation Rules**:
- README must include all required fields.
- Project must build against the current public package surface.
- Run command must complete as documented in the supported validation
  environment.

## Maintainer Internals Guide

**Represents**: Documentation for maintainers and contributors who need design
context and review rules.

**Fields**:
- `architecture_overview`: major package responsibilities and component
  boundaries.
- `design_invariants`: lifecycle, storage, synchronization, recovery,
  diagnostics, portability, and package constraints.
- `public_contract_boundary`: what consumers can rely on.
- `changeable_internals`: implementation details that are not compatibility
  guarantees.
- `performance_evidence_rules`: what evidence is needed for public claims.
- `validation_commands`: commands and review checks required before release.
- `documentation_maintenance_rules`: what docs to update when behavior,
  metadata, samples, or release status changes.

**Relationships**:
- References `Package Contract Reference` entries.
- Defines expectations for `Documentation Validation Review`.

**Validation Rules**:
- Must not create new runtime behavior promises.
- Must separate current implementation explanation from stable public
  contracts.
- Must identify review and validation expectations for future changes.

## Documentation Validation Review

**Represents**: The repeatable process used before implementation completion and
release.

**Fields**:
- `link_results`: all relative documentation links resolve.
- `placeholder_results`: no TODO, TBD, placeholder, or clarification markers
  remain in reader-facing docs.
- `sample_results`: sample projects build and run as documented.
- `package_results`: package metadata, README, changelog, release notes, and
  package consumption validation align.
- `coverage_results`: all workflows, statuses, samples, and contracts have
  documentation coverage.
- `claim_results`: performance, platform, portability, and integration claims
  are supported and scoped.

**Relationships**:
- Validates the `Documentation Set`.
- Validates each `Runnable Sample`.
- Uses `Package Contract Reference` entries to confirm behavior claims.

**Validation Rules**:
- Must complete with zero critical failures before release.
- Documentation-only changes still require link, placeholder, metadata, sample,
  and release-impact review.

## Package Contract Reference

**Represents**: A stable source used to support public behavior claims.

**Fields**:
- `reference_path`: Spec Kit contract, package metadata file, public XML
  documentation, or release notes path.
- `behavior_area`: API shape, error taxonomy, shared-memory layout,
  reservation behavior, diagnostics, contention, configuration, lifecycle, or
  portability.
- `stability_level`: public contract, release note, implementation explanation,
  or future consideration.
- `consumer_impact`: how the referenced behavior affects users.

**Relationships**:
- Supports `Feature Guide`, `Concept Guide`, and `Maintainer Internals Guide`
  claims.
- Is checked during `Documentation Validation Review`.

**Validation Rules**:
- Public behavior statements must trace to a contract or current package
  metadata.
- Future portability statements must not imply delivered C++ or Python
  bindings.

## State Transitions

### Documentation Page

```text
Draft -> Reviewed -> Link-Checked -> Coverage-Checked -> Release-Ready
```

- `Draft`: Content exists but may still have gaps.
- `Reviewed`: Wording and scope have been checked against the target audience.
- `Link-Checked`: Relative links and required navigation entries resolve.
- `Coverage-Checked`: Public workflows, statuses, and contract references are
  covered.
- `Release-Ready`: Page passes validation and release-impact review.

### Runnable Sample

```text
Source Updated -> README Updated -> Builds -> Runs As Documented -> Linked
```

- `Source Updated`: Sample code matches current public API.
- `README Updated`: README includes the required sample contract fields.
- `Builds`: Sample builds in the supported validation environment.
- `Runs As Documented`: Run command produces the documented output shape.
- `Linked`: README, docs index, and relevant guides link to the sample.
