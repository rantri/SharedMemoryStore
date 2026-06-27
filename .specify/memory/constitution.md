<!--
Sync Impact Report
Version change: template -> 1.0.0
Modified principles:
- Template principle 1 placeholder -> I. Library and Package First
- Template principle 2 placeholder -> II. Stable Contracts and Semantic Versioning
- Template principle 3 placeholder -> III. Test-Driven Production Quality
- Template principle 4 placeholder -> IV. .NET 10 Baseline, Portable Core
- Template principle 5 placeholder -> V. Minimal, Observable, Dependency-Conscious Design
Added sections:
- Package and Runtime Constraints
- Development Workflow and Quality Gates
Removed sections: None
Templates requiring updates:
- .specify/templates/plan-template.md: updated
- .specify/templates/spec-template.md: updated
- .specify/templates/tasks-template.md: updated
- .specify/templates/commands/*.md: not present
- AGENTS.md: reviewed, no change required
Follow-up TODOs: None
-->
# SharedMemoryStore Constitution

## Core Principles

### I. Library and Package First
SharedMemoryStore MUST be designed and delivered as a reusable general-purpose
library before any application-specific integration is added. The first
implementation MUST produce a versioned NuGet package with clear namespaces,
package metadata, XML documentation for public APIs, and runnable usage examples.
Features MUST expose cohesive library APIs rather than workflows coupled to one
production application's storage, deployment, UI, or hosting assumptions.

Rationale: the project is intended to become shared production infrastructure,
so the primary artifact must remain independently usable, testable, and
packageable.

### II. Stable Contracts and Semantic Versioning
Public APIs, package metadata, serialized representations, shared-memory
semantics, error behavior, and any interop surface are project contracts. Every
feature plan MUST identify contract additions, compatibility expectations, and
the semantic version impact. Breaking changes require a major version bump,
migration notes, and compatibility tests; silent behavioral changes to existing
contracts are prohibited.

Rationale: downstream production systems and future C++, Python, and .NET
consumers need predictable upgrades and explicit migration paths.

### III. Test-Driven Production Quality
Behavior-changing work MUST include automated tests before implementation is
considered complete. Plans and tasks MUST include unit tests for core behavior,
contract tests for public APIs and package compatibility, and integration tests
for cross-component behavior. Concurrency, memory ownership, resource cleanup,
boundary conditions, and failure modes MUST be tested whenever they are relevant
to the change. Release candidates MUST pass the full test suite before
packaging.

Rationale: shared library defects propagate into every consuming system, so
quality gates must be built into normal development rather than deferred to
consumer projects.

### IV. .NET 10 Baseline, Portable Core
The initial implementation MUST target C# on .NET 10. Core concepts, data
models, lifecycle rules, and documented behavior MUST avoid unnecessary
C#-specific assumptions so equivalent C++ and Python implementations or bindings
can be added later. Platform-specific or runtime-specific behavior MUST be
isolated behind documented adapters, and any non-portable decision MUST be
called out in the plan with its rationale.

Rationale: .NET is the first delivery vehicle, but the library must not trap the
domain model or public contracts inside one runtime.

### V. Minimal, Observable, Dependency-Conscious Design
The library MUST keep its runtime dependency surface small and deliberate.
Global mutable state, hidden background work, implicit process-wide
configuration, and direct console output are prohibited in library code unless a
feature plan justifies the exception. Diagnostics MUST be exposed through
consumer-controlled mechanisms such as structured errors, events, logging
abstractions, or metrics hooks. Performance and resource ownership assumptions
MUST be documented for public APIs that allocate, pin, map, share, or dispose
memory.

Rationale: production consumers need predictable behavior, low integration
friction, and control over diagnostics, lifecycle, and resource costs.

## Package and Runtime Constraints

- The primary deliverable is a NuGet package built from the C#/.NET 10
  implementation.
- Source layout, build scripts, and release artifacts MUST keep package creation
  reproducible from a clean checkout.
- Public APIs MUST include XML documentation and examples sufficient for a
  consumer to use the feature without reading implementation internals.
- Future C++ and Python implementations or bindings MUST conform to documented
  contracts rather than redefining behavior per language.
- External dependencies MUST be justified in the implementation plan, including
  version, license, transitive dependency risk, and why the dependency belongs in
  a general-purpose library.
- Platform-specific behavior MUST have an explicit compatibility statement and a
  fallback or unsupported-platform behavior.

## Development Workflow and Quality Gates

- Feature specifications MUST describe user-facing library value, public API
  expectations, edge cases, and measurable success criteria.
- Implementation plans MUST pass the Constitution Check before design work and
  again after design artifacts are produced.
- Task lists MUST include tests, documentation, packaging, and compatibility work
  whenever a feature changes behavior, public API, or release artifacts.
- Code review MUST verify public API shape, semantic version impact, dependency
  additions, diagnostics behavior, and test coverage.
- A release is not valid until `dotnet test` and `dotnet pack` succeed for the
  release configuration, or the plan records an approved equivalent command.

## Governance

This constitution supersedes conflicting project guidance. Amendments require a
documented rationale, a semantic version bump for the constitution, and a review
of dependent Spec Kit templates and runtime guidance.

Versioning policy:
- MAJOR: incompatible changes to governance or removal/redefinition of core
  principles.
- MINOR: new principles, new mandatory sections, or materially expanded
  compliance requirements.
- PATCH: wording clarifications, typo fixes, or non-semantic refinements.

Compliance review is required during planning, task generation, code review, and
release preparation. Any approved violation MUST be listed in the implementation
plan's Complexity Tracking table with the simpler alternative that was rejected.

**Version**: 1.0.0 | **Ratified**: 2026-06-26 | **Last Amended**: 2026-06-26
