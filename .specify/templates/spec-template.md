# Feature Specification: [FEATURE NAME]

**Feature Branch**: `[###-feature-name]`

**Created**: [DATE]

**Status**: Draft

**Input**: User description: "$ARGUMENTS"

## User Scenarios & Testing *(mandatory)*

<!--
  IMPORTANT: User stories should be PRIORITIZED as user journeys ordered by importance.
  Each user story/journey must be INDEPENDENTLY TESTABLE - meaning if you implement just ONE of them,
  you should still have a viable MVP (Minimum Viable Product) that delivers value.

  Assign priorities (P1, P2, P3, etc.) to each story, where P1 is the most critical.
  Think of each story as a standalone slice of functionality that can be:
  - Developed independently
  - Tested independently
  - Packaged independently
  - Demonstrated to users independently
-->

### User Story 1 - [Brief Title] (Priority: P1)

[Describe this user journey in plain language]

**Why this priority**: [Explain the value and why it has this priority level]

**Independent Test**: [Describe how this can be tested independently - e.g., "Can be fully tested by [specific action] and delivers [specific value]"]

**Acceptance Scenarios**:

1. **Given** [initial state], **When** [action], **Then** [expected outcome]
2. **Given** [initial state], **When** [action], **Then** [expected outcome]

---

### User Story 2 - [Brief Title] (Priority: P2)

[Describe this user journey in plain language]

**Why this priority**: [Explain the value and why it has this priority level]

**Independent Test**: [Describe how this can be tested independently]

**Acceptance Scenarios**:

1. **Given** [initial state], **When** [action], **Then** [expected outcome]

---

### User Story 3 - [Brief Title] (Priority: P3)

[Describe this user journey in plain language]

**Why this priority**: [Explain the value and why it has this priority level]

**Independent Test**: [Describe how this can be tested independently]

**Acceptance Scenarios**:

1. **Given** [initial state], **When** [action], **Then** [expected outcome]

---

[Add more user stories as needed, each with an assigned priority]

### Edge Cases

<!--
  ACTION REQUIRED: The content in this section represents placeholders.
  Fill them out with the right edge cases.
-->

- What happens at documented size, count, lifetime, and concurrency boundaries?
- How does the library handle invalid inputs, resource exhaustion, cleanup
  failures, and platform-specific unsupported behavior?
- How are version skew, backward compatibility, and migration scenarios handled?

## Requirements *(mandatory)*

<!--
  ACTION REQUIRED: The content in this section represents placeholders.
  Fill them out with the right functional requirements.
-->

### Functional Requirements

- **FR-001**: Library MUST [specific capability, e.g., "create or open a named shared memory region"]
- **FR-002**: Library MUST [specific validation behavior, e.g., "reject invalid region names with a documented exception"]
- **FR-003**: Consumers MUST be able to [key interaction, e.g., "dispose resources deterministically"]
- **FR-004**: Library MUST [data or compatibility requirement, e.g., "preserve documented binary layout"]
- **FR-005**: Library MUST [diagnostic behavior, e.g., "surface failures through documented errors and diagnostics hooks"]

### Library Contract & Compatibility *(mandatory)*

- **LC-001**: Public API surface MUST be described, including namespaces,
  types, methods, errors, lifecycle rules, and examples.
- **LC-002**: NuGet packaging impact MUST be described, including package
  metadata, public documentation, and release notes.
- **LC-003**: Semantic version impact MUST be identified as none, patch, minor,
  or major with rationale.
- **LC-004**: Future C++ and Python portability considerations MUST be listed
  when the feature defines core concepts, data structures, errors, or interop
  behavior.
- **LC-005**: Diagnostics and resource ownership expectations MUST be stated for
  public APIs that allocate, pin, map, share, or dispose resources.

*Example of marking unclear requirements:*

- **FR-006**: Library MUST support [NEEDS CLARIFICATION: platform support not specified - Windows only, Linux only, or cross-platform?]
- **FR-007**: Library MUST retain compatibility for [NEEDS CLARIFICATION: supported package versions not specified]

### Key Entities *(include if feature involves data)*

- **[Entity 1]**: [What it represents, key attributes without implementation]
- **[Entity 2]**: [What it represents, relationships to other entities]

## Success Criteria *(mandatory)*

<!--
  ACTION REQUIRED: Define measurable success criteria.
  These must be technology-agnostic and measurable.
-->

### Measurable Outcomes

- **SC-001**: [Measurable metric, e.g., "Consumers can complete the primary library operation in under 50ms for the documented input size"]
- **SC-002**: [Measurable metric, e.g., "Library handles 100 concurrent operations without data corruption or resource leaks"]
- **SC-003**: [Consumer success metric, e.g., "A clean .NET 10 project can use the feature through documented APIs only"]
- **SC-004**: [Operational metric, e.g., "Diagnostics identify all documented failure modes without console output"]
- **SC-005**: [Library quality metric, e.g., "Package can be consumed from a clean .NET 10 project using only documented APIs"]

## Assumptions

<!--
  ACTION REQUIRED: The content in this section represents placeholders.
  Fill them out with the right assumptions based on reasonable defaults
  chosen when the feature description did not specify certain details.
-->

- [Assumption about target consumers, e.g., "Consumers are .NET 10 applications"]
- [Assumption about scope boundaries, e.g., "C++ and Python bindings are out of scope for this feature unless explicitly specified"]
- [Assumption about data/environment, e.g., "Target platforms provide the required shared-memory primitives"]
- [Dependency on existing system/service, e.g., "Requires the existing package signing or publishing pipeline"]
