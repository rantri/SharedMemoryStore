# Concurrency Requirements Checklist: Lock-Free Shared-Memory KV Store

**Purpose**: Assess whether the concurrency, capacity, recovery, and release requirements are complete and unambiguous for final peer review
**Created**: 2026-07-13
**Feature**: [spec.md](../spec.md)

**Note**: This checklist evaluates the written requirements, not the implementation or test results.

## Requirement Completeness

- [x] CHK001 Are the key-value-store scope and the explicit exclusion of stream, queue, broker, delivery, and load-balancing responsibilities stated for every public workflow? [Completeness, Spec §Scope/FR-001]
- [x] CHK002 Are requirements defined for the producer, 6–12 load-balanced workers, and unrelated observer/administrative processes without assigning worker identity to the store? [Completeness, Spec §US1/FR-046]
- [x] CHK003 Are lock-free progress requirements present for publish, reserve, commit, acquire, project, release, remove, diagnostics, recovery, and disposal rather than only the primary publish/read path? [Coverage, Spec §FR-025..FR-041]
- [x] CHK004 Are all physical-capacity states—including tentative initialization, explicit/atomic reservation, publication, removal, abort, reclaim, and retirement—covered by the `StoreFull` requirements? [Completeness, Spec §FR-031]
- [x] CHK005 Are requirements defined for failed pre-metadata claims, stale owners, live paused owners, and same/future-generation helpers without introducing a global recovery owner? [Recovery Coverage, Spec §FR-032..FR-036]

## Requirement Clarity

- [x] CHK006 Is “lock-free” distinguished from wait-free completion and from the absence of named/OS synchronization in measurable terms? [Clarity, Spec §FR-025..FR-030]
- [x] CHK007 Is every public ordering point named precisely enough to decide cancellation, duplicate-key, and same-key race outcomes? [Clarity, Spec §Concurrency Outcome Contract/FR-028]
- [x] CHK008 Is `StoreFull` distinguished from provisional scan exhaustion, rotating free capacity, local proof-buffer contention, and caller-budget exhaustion? [Clarity, Spec §FR-029..FR-031]
- [x] CHK009 Is the confirmed between-collect `StoreFull` candidate distinguished from the later confirmation/checkpoint timestamp? [Clarity, Spec §FR-031]
- [x] CHK010 Are `NoWait`, finite, infinite, timeout, and cancellation outcomes specified at both pre-ordering and post-ordering boundaries? [Clarity, Spec §FR-029..FR-030]
- [x] CHK011 Is the per-open proof-buffer cost quantified per slot and at the maximum supported slot count, including its process-local/non-mapped ownership? [Clarity, Spec §FR-037/LC-015]

## Requirement Consistency

- [x] CHK012 Are the intent-specific duplicate witnesses consistent between the concurrency table, functional requirements, data model, and public API contract? [Consistency, Spec §Concurrency Outcome Contract/FR-005..FR-009]
- [x] CHK013 Is the rule that genuine physical exhaustion may precede final same-key duplicate arbitration consistent across all publish and reserve scenarios? [Consistency, Spec §FR-031]
- [x] CHK014 Are the forward-only generation lifecycle and no-same-generation-rollback requirement consistent with recovery, stale-token, and retirement requirements? [Consistency, Spec §FR-033..FR-035/FR-047]
- [x] CHK015 Are the zero-per-operation-allocation requirement and the eager per-open scratch-memory allowance expressed without contradiction? [Consistency, Spec §FR-037/SC-008]
- [x] CHK016 Are platform-specific x64 atomic assumptions consistent with the portable-core and unsupported-platform requirements? [Consistency, Spec §FR-043..FR-045/Assumptions]

## Acceptance Criteria Quality

- [x] CHK017 Can the no-global-operation-owner requirement be objectively assessed for every public v2 operation and across multiple processes? [Measurability, Spec §SC-006]
- [x] CHK018 Are correctness, progress, allocation, latency, throughput, fairness, payload, participant-count, duration, trial-count, and environment fields quantified for each required workload? [Acceptance Criteria, Spec §Benchmark Workload Matrix/SC-001..SC-018]
- [x] CHK019 Are pass, fail, and environment-qualified not-qualified outcomes defined separately for Windows x64 and Linux x64? [Acceptance Criteria, Spec §SC-010/SC-013]
- [x] CHK020 Is the non-convergence stop rule tied to a repeatable invariant and a fixed number of evidence-driven protocol corrections? [Acceptance Criteria, Spec §Non-Convergence Gate]

## Scenario and Edge-Case Coverage

- [x] CHK021 Are rotating-hole, between-collect movement, same-handle proof contention, and independent-handle progress scenarios addressed by the capacity requirements? [Coverage, Spec §FR-029..FR-031]
- [x] CHK022 Are malformed first/second snapshot words and unknown publication intent assigned explicit fail-closed outcomes? [Exception Flow, Spec §FR-029/FR-047..FR-052]
- [x] CHK023 Are terminal generation retirement, participant-token reuse, stale directory reference, and delayed helper scenarios bounded before identity fields can wrap? [Edge Cases, Spec §FR-035/FR-047..FR-052]
- [x] CHK024 Are suspended/crashed-process effects bounded to owned keys, slots, leases, and participant records while unrelated processes continue? [Recovery Coverage, Spec §FR-032/SC-005]
- [x] CHK025 Are cleanup outcomes specified when cancellation or expiry occurs after caller ownership is relinquished but before physical reclaim completes? [Exception Flow, Spec §FR-029/FR-033]

## Dependencies and Assumptions

- [x] CHK026 Is the external message broker dependency limited to distributing keys, with no broker state, worker assignment, or delivery semantics entering the mapped protocol? [Boundary, Spec §Scope/FR-046]
- [x] CHK027 Are the trust boundary, mapped-atomic alignment assumptions, owner-liveness limitations, and lack of cross-host durability documented as explicit constraints? [Assumption, Spec §Assumptions/FR-043..FR-045]
- [x] CHK028 Are package compatibility, required-feature bits, rollout, rollback, and old-runtime fail-closed behavior specified for the revised layout without implying C++/Python v2 support? [Dependency, Spec §FR-042..FR-045/LC-001..LC-016]
- [x] CHK029 Are Linux same-PID managed/native exclusion, stable lock-inode lifetime, synchronization-before-region teardown, OFD unsupported behavior, and the old same-PID compatibility boundary explicit and testable? [Concurrency/Compatibility, Spec §FR-057]

## Notes

- Check items off only after inspecting the cited requirements and their linked contracts.
- Record any ambiguity or conflict inline and route implementation gaps through the convergence workflow.
