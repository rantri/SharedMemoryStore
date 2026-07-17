using System.Diagnostics;
using SharedMemoryStore.Lifecycle;

namespace SharedMemoryStore.UnitTests;

[Collection("DynamicEngineFacade")]
public sealed class StoreLifecycleGateBudgetTests
{
    [Fact]
    public void BoundedEntryPreservesExpiredCanceledAndTrueNoWaitSemantics()
    {
        var gate = new StoreLifecycleGate();
        long oldStart = Stopwatch.GetTimestamp() - Stopwatch.Frequency;

        Assert.Equal(
            StoreStatus.StoreBusy,
            gate.TryEnter(
                new StoreWaitOptions(TimeSpan.FromMilliseconds(10)),
                oldStart,
                out _));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            StoreStatus.OperationCanceled,
            gate.TryEnter(
                new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token),
                Stopwatch.GetTimestamp(),
                out _));

        Assert.Equal(
            StoreStatus.Success,
            gate.TryEnter(StoreWaitOptions.NoWait, Stopwatch.GetTimestamp(), out StoreLifecycleGate.Operation operation));
        operation.Dispose();
    }

    [Fact]
    public async Task HighContentionEntryAndDisposalConvergeWithoutUnboundedEntrants()
    {
        var gate = new StoreLifecycleGate();
        using var start = new ManualResetEventSlim(initialState: false);
        int workerCount = Math.Max(8, Environment.ProcessorCount * 2);
        long successes = 0;
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                while (true)
                {
                    StoreStatus status = gate.TryEnter(
                        StoreWaitOptions.NoWait,
                        Stopwatch.GetTimestamp(),
                        out StoreLifecycleGate.Operation operation);
                    if (status == StoreStatus.StoreDisposed)
                    {
                        return;
                    }

                    if (status == StoreStatus.StoreBusy)
                    {
                        continue;
                    }

                    Assert.Equal(StoreStatus.Success, status);
                    Interlocked.Increment(ref successes);
                    Thread.SpinWait(16);
                    operation.Dispose();
                }
            }))
            .ToArray();

        start.Set();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref successes) >= 1_000, TimeSpan.FromSeconds(5)));
        Task disposer = Task.Run(() =>
        {
            Assert.True(gate.TryBeginDispose());
            gate.CompleteDispose();
        });

        await Task.WhenAll(workers.Append(disposer)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(successes > 0);
        Assert.True(gate.IsDisposed);
    }

    [Fact]
    public void FacadePassesRemainingFiniteTimeAndDoesNotEnterEngineWhenCanceled()
    {
        FakeEngineCallLog.Reset(StoreStatus.Success);
        using MemoryStore store = MemoryStoreFacadeTests.CreateFacadeWithFakeEngine();
        TimeSpan requested = TimeSpan.FromSeconds(1);

        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish([1], [2], [], new StoreWaitOptions(requested)));
        Assert.Equal(1, FakeEngineCallLog.Count("TryPublish"));
        Assert.InRange(FakeEngineCallLog.LastWait.Timeout, TimeSpan.FromTicks(1), requested - TimeSpan.FromTicks(1));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryPublish(
                [1],
                [2],
                [],
                new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token)));
        Assert.Equal(1, FakeEngineCallLog.Count("TryPublish"));
        Assert.Equal(1, FakeEngineCallLog.Count("RecordFacadeStatus"));
    }
}
