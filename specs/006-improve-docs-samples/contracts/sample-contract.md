# Contract: Runnable Samples

## Purpose

Define the minimum quality bar for samples as executable learning material.
Samples are part of the public documentation surface and must remain aligned
with current package behavior.

## Required Sample Ladder

The samples list MUST be ordered from simple to advanced:

1. Basic usage.
2. Frame-shaped value usage.
3. Zero-copy/direct ingest and segmented publish.
4. Optional lifecycle or hosting integration.

The ladder MAY add future samples, but every new sample must have a clear place
in the progression.

## Required README Sections

Each sample README MUST include:

- Sample purpose and audience.
- Concepts demonstrated.
- Prerequisites.
- Run command from repository root.
- Expected output shape.
- Expected non-success statuses, if applicable.
- Cleanup behavior.
- Related documentation links.
- Scope boundaries and non-goals.

## Required Source Behavior

Each sample project MUST:

- Build against the current public package surface.
- Use current public type names, method names, option names, and status names.
- Avoid private implementation APIs.
- Avoid depending on generated build output committed under `bin/` or `obj/`.
- Keep console output stable enough for users to recognize success without
  depending on machine-specific values.

## Validation Requirements

Before release:

- Every sample project MUST build in the supported validation environment.
- Every documented run command MUST complete as documented or have an explicit
  platform limitation.
- Every sample README MUST pass link and placeholder validation.
- Sample source and docs MUST be checked for stale public API or status names.

## Failure Handling

If a sample cannot run on the current validation platform, its README MUST:

- State the limitation.
- Point to the relevant portability or troubleshooting documentation.
- Avoid presenting the sample as generally supported.

## Non-Goals

- Samples do not replace complete feature documentation.
- Samples do not establish new runtime behavior beyond the documented public
  package contract.
