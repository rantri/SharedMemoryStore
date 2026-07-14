// Integration classes frequently create independent 8-12 process workloads.
// Running those classes concurrently measures host oversubscription and process
// startup scheduling rather than the bounded cross-process protocol exercised
// inside each test. Keep class execution serial while preserving every test's
// real multi-process concurrency.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
