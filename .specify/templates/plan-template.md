# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]

**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: C# / .NET 10 for the first implementation; identify any
future C++ or Python portability considerations

**Primary Dependencies**: [NuGet packages or NEEDS CLARIFICATION; justify every runtime dependency]

**Storage**: [if applicable, e.g., PostgreSQL, CoreData, files or N/A]

**Testing**: dotnet test with unit, contract, and integration coverage

**Target Platform**: .NET 10 supported platforms; document platform-specific behavior

**Project Type**: NuGet package / reusable library

**Performance Goals**: [library-specific latency, throughput, allocation, or concurrency target or NEEDS CLARIFICATION]

**Constraints**: [API compatibility, package size, allocation, platform, threading, or memory constraints or NEEDS CLARIFICATION]

**Scale/Scope**: [expected library consumers, data size, process count, memory region count, or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Library/package first: Feature exposes reusable library APIs and does not
  couple behavior to a specific production application.
- NuGet deliverable: Plan identifies package metadata, public namespaces, XML
  documentation, and packaging impact.
- Stable contracts: Public API, shared-memory semantics, serialized formats,
  errors, and semantic version impact are documented.
- .NET 10 baseline with portability: Implementation targets C#/.NET 10 and
  documents future C++/Python portability constraints.
- Test coverage: Unit, contract, integration, and relevant concurrency/resource
  tests are planned before implementation tasks.
- Dependency discipline: New runtime dependencies, global state, background
  work, or process-wide configuration are justified.
- Diagnostics and resource ownership: Consumer-controlled diagnostics and public
  API lifecycle/cleanup rules are specified.

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```text
src/
└── SharedMemoryStore/
    ├── SharedMemoryStore.csproj
    └── [library source organized by responsibility]

tests/
├── SharedMemoryStore.UnitTests/
├── SharedMemoryStore.ContractTests/
└── SharedMemoryStore.IntegrationTests/

docs/
└── [consumer documentation and examples]

bindings/
├── cpp/       # Add only when C++ implementation/bindings are planned
└── python/    # Add only when Python implementation/bindings are planned
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
