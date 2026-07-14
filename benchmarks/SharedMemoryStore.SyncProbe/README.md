# SharedMemoryStore synchronization probe

This executable is a process-level correctness and performance probe. It emits
raw JSON; it is not a BenchmarkDotNet microbenchmark.

## Evidence semantics

Report schema v6 is additive over schemas v3-v5. Existing JSON property names
remain present. Readers that ignore unknown properties can continue to consume
the report. `MinimumCompatibleSchemaVersion` and `SchemaCompatibility` state
this contract in every report.

`Environment` records the repository commit and clean/dirty state plus SHA-256
digests for the exact SharedMemoryStore and probe assemblies that produced the
report. Assembly digests are the authoritative trace when a qualification is
run from an intentionally dirty development tree.

`FullPayloadCopies` is retained for schema compatibility. A zero in that legacy
field is not, by itself, a measured copy count. Consumers must also inspect:

- `FullPayloadCopyCountIsInstrumented`
- `FullPayloadCopyEvidenceKind`

The broker scenarios use direct reservation writes and borrowed lease reads, so
their copy evidence is structural and the instrumentation flag is `false`.

Broker allocation evidence has two scopes:

- `MeasuredThreadAllocatedBytes` covers the complete measurement interval on an
  explicitly created producer/coordinator thread. That same thread first runs an
  unmeasured warm-up and resets worker counters. The measured value includes JSON
  and pipe coordination performed by the benchmark harness.
- `ProducerStoreOperationAllocatedBytes` sums current-thread allocation deltas
  immediately around producer store calls in that same interval.

`AllocationMeasurementScope` identifies the applicable scope for every run.

## Tiny-operation topology and latency sampling

The synchronization scenarios use one deterministic catalog for both store
profiles. Each of the 12 supported workers owns two keys and alternates between
them by cycle. Both keys for worker `i` have canonical bucket `i` in the
16-bucket directory used by the 32-slot synchronization store. Configuration
records the key count per worker, maximum worker count, canonical bucket count,
SHA-256 of the ordered key bytes, and the ordered per-key bucket assignments.

Autonomous workers select latency candidates with deterministic xorshift-driven
geometric gaps whose mean is `SamplingInterval` cycles. Separate early and late
Algorithm-R reservoirs retain at most 32,768 samples each across their complete
measurement windows. Overall percentiles use the merged reservoirs;
`EarlySampleCount`, `LateSampleCount`, and `SampleCount` expose the retained
sample topology for machine validation. Reservoir storage is allocated before
the measured region and candidate selection and replacement are allocation-free.
`MaxMicroseconds` is tracked separately across every sampled candidate, so an
outlier remains visible to the stall gate even if Algorithm R later evicts it
from a percentile reservoir.

## Sticky-overflow qualification

The `overflow` mode constructs 17 exact two-choice bucket-pair collisions in a
large lock-free store. It measures a missing key before churn, repeatedly
publishes/removes the colliding set, and measures the same missing key after the
overflow directory is empty. The report includes raw early and late samples plus
spill/occupancy/scan diagnostics in `StickyOverflow`.

```powershell
dotnet run -c Release --project benchmarks/SharedMemoryStore.SyncProbe -- `
  --mode overflow --profile v2 --overflow-slot-count 4096 `
  --overflow-churn-cycles 10000 --overflow-missing-samples 16384 `
  --trials 3 --output artifacts/sticky-overflow.json
```

The diagnostics gate requires a real spill and occupied overflow cell during the
first cycle, a full cleanup scan and logical `Empty` summary immediately after
that cycle, no residual spill or overflow occupancy after churn, and exactly zero
new overflow scans in the late missing-key window. The latency gate is late
missing-key p99 no greater than 2x early p99. Exit code `4` means the raw report
was written but that performance gate failed. Exit code `3` means the diagnostics
gate failed; exit code `2` means a correctness failure occurred.
