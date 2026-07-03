# Acceptance Traceability

| Requirement | Planned validation |
|-------------|--------------------|
| FR-001, FR-002 | Production API contract tests and package-consumption smoke compile `MemoryStore` without aliasing the old type. |
| FR-003, LC-001, LC-002 | API change inventory, release notes, README, and docs release guidance list breaking migration impact. |
| FR-004, FR-005, FR-006, LC-003 | Reservation contract and lifetime tests verify `GetMemory` is absent and completed/stale reservations cannot be used through current public APIs. |
| FR-007, FR-008, FR-009, LC-004 | Store wait policy unit, contract, and integration tests cover default, no-wait, busy, cancellation, and diagnostics outcomes. |
| FR-010, FR-011, FR-012, LC-005 | Options validation unit and contract tests cover `Create`, `Validate`, invalid modes, derived sizes, and capacity errors. |
| FR-013, FR-014 | Key validation tests cover empty-key `InvalidKey` and oversized-key `KeyTooLarge` across public entry points. |
| FR-015, LC-006 | Diagnostics shape and contract tests cover aggregate failure counts and removed convenience names. |
| FR-016, FR-017, FR-018, FR-019, LC-007 | Package dependency and public API shape tests prove the core package has no hosting dependencies and no broad mirror interface. |
| FR-020 | Documentation validation covers naming, lifetime, contention, validation, diagnostics, and integration guidance. |
| SC-001 | Package-consumption validation and docs snippets compile with `MemoryStore`. |
| SC-002 | Reservation lifetime tests cover completed and reused reservation token outcomes. |
| SC-003 | Contention tests verify no-wait and canceled waits return within bounded tolerance. |
| SC-004 | Option validation tests reject invalid modes and inconsistent sizes. |
| SC-005 | Key validation tests distinguish empty and oversized keys. |
| SC-006 | Diagnostics API shape tests reject per-status public convenience members. |
| SC-007 | Package contract tests inspect core dependencies and optional integration docs/sample boundaries. |
| SC-008 | Full `dotnet test`, package consumption, docs validation, and `dotnet pack` are release validation gates. |
| SC-009 | API change inventory and `docs/releases.md` summarize all required migration edits. |
