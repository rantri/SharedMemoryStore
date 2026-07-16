using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreePublishIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentSameKeyPublishersProduceExactlyOneWinnerWithoutLeakingCandidateSlots()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        const int publisherCount = 8;
        using var store = CreateStore(slotCount: publisherCount + 1);
        using var start = new Barrier(publisherCount + 1);
        var statuses = new StoreStatus[publisherCount];
        var publishers = Enumerable.Range(0, publisherCount)
            .Select(index => Task.Run(() =>
            {
                start.SignalAndWait();
                statuses[index] = store.TryPublish([0x41], [(byte)index]);
            }))
            .ToArray();

        start.SignalAndWait();
        await Task.WhenAll(publishers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(statuses, static status => status == StoreStatus.Success);
        Assert.Equal(publisherCount - 1, statuses.Count(static status => status == StoreStatus.DuplicateKey));

        // Duplicate candidates must have been returned; all remaining configured
        // slots are still available for unrelated keys.
        for (var index = 0; index < publisherCount; index++)
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish(Key(index + 1), [(byte)index]));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentUnrelatedKeyPublishersCompleteIndependently()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        const int publisherCount = 12;
        using var store = CreateStore(slotCount: publisherCount);
        using var start = new Barrier(publisherCount + 1);
        var statuses = new StoreStatus[publisherCount];
        var publishers = Enumerable.Range(0, publisherCount)
            .Select(index => Task.Run(() =>
            {
                start.SignalAndWait();
                statuses[index] = store.TryPublish(Key(index), [(byte)index]);
            }))
            .ToArray();

        start.SignalAndWait();
        await Task.WhenAll(publishers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(statuses, static status => Assert.Equal(StoreStatus.Success, status));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PausedInsertionTransitionDoesNotBlockSameKeyClassificationOrUnrelatedProgress()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var controller = new CheckpointController(LockFreeCheckpointId.DirectoryBeforeDescriptorPublication);
        using var store = CreateInstrumentedStore(slotCount: 4, controller);

        var owner = Task.Run(() => store.TryPublish([0x41], [0x11]));
        Assert.True(
            controller.WaitUntilPaused(TimeSpan.FromSeconds(5)),
            "The insertion owner did not reach the configured directory checkpoint.");

        // A same-key contender exercises completion/classification of the
        // insert lifecycle while an unrelated publisher proves local progress.
        var sameKeyHelper = Task.Run(() => store.TryPublish([0x41], [0x22]));
        var unrelated = Task.Run(() => store.TryPublish([0x52], [0x33]));
        StoreStatus[] concurrent = await Task.WhenAll(sameKeyHelper, unrelated)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, concurrent[1]);

        controller.Continue();
        StoreStatus ownerStatus = await owner.WaitAsync(TimeSpan.FromSeconds(5));
        StoreStatus[] sameKeyStatuses = [ownerStatus, concurrent[0]];
        Assert.Single(sameKeyStatuses, static status => status == StoreStatus.Success);
        Assert.Single(sameKeyStatuses, static status => status == StoreStatus.DuplicateKey);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationAfterSlotClaimRelinquishesOwnerAndRestoresFullCapacity()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        using var controller = new CheckpointController(LockFreeCheckpointId.DirectoryBeforeDescriptorPublication);
        using var store = CreateInstrumentedStore(slotCount: 4, controller);
        var wait = new StoreWaitOptions(TimeSpan.FromSeconds(5), cancellation.Token);

        var canceledPublish = Task.Run(() => store.TryPublish([0x61], [0x11], default, wait));
        Assert.True(controller.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        controller.Continue();

        Assert.Equal(StoreStatus.OperationCanceled, await canceledPublish.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertFullCapacityCanBePublished(store, firstKey: 0x61, slotCount: 4);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeadlineAfterSlotClaimReturnsWithinBoundAndRestoresFullCapacity()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var timeout = TimeSpan.FromMilliseconds(50);
        using var controller = new CheckpointController(LockFreeCheckpointId.DirectoryBeforeDescriptorPublication);
        using var store = CreateInstrumentedStore(slotCount: 4, controller);

        var expiredPublish = Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            StoreStatus status = store.TryPublish(
                [0x71],
                [0x11],
                default,
                new StoreWaitOptions(timeout));
            stopwatch.Stop();
            return (Status: status, Elapsed: stopwatch.Elapsed);
        });
        Assert.True(controller.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        Thread.Sleep(timeout + TimeSpan.FromMilliseconds(50));
        controller.Continue();

        (StoreStatus status, TimeSpan elapsed) = await expiredPublish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.StoreBusy, status);
        Assert.True(
            elapsed <= timeout + TimeSpan.FromMilliseconds(250),
            $"Bounded publication took {elapsed} for a {timeout} limit.");
        AssertFullCapacityCanBePublished(store, firstKey: 0x71, slotCount: 4);
    }

    private static MemoryStore CreateStore(int slotCount)
    {
        var options = Options($"sms-v2-publish-integration-{Guid.NewGuid():N}", slotCount);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateInstrumentedStore(int slotCount, CheckpointController controller)
    {
        var options = Options($"sms-v2-instrumented-publish-{Guid.NewGuid():N}", slotCount);
        Type? factoryType = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeInstrumentedStoreFactory",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(
            factoryType is not null,
            "The lock-free engine must integrate the friend-only checkpoint strategy through LockFreeInstrumentedStoreFactory.");

        MethodInfo? factory = factoryType!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "TryCreateOrOpen" && method.ReturnType == typeof(StoreOpenStatus));
        Assert.NotNull(factory);

        object strategy = LockFreeCheckpointFactory.CreateInstrumented(controller.Observe);
        object?[] arguments = BindInstrumentedFactoryArguments(factory!, options, controller.Observe, strategy);
        var status = (StoreOpenStatus)factory!.Invoke(null, arguments)!;
        MemoryStore? store = arguments.SingleOrDefault(static argument => argument is MemoryStore) as MemoryStore;

        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static object?[] BindInstrumentedFactoryArguments(
        MethodInfo factory,
        SharedMemoryStoreOptions options,
        Action<LockFreeCheckpointEntry> observer,
        object strategy)
    {
        ParameterInfo[] parameters = factory.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            Type parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            if (parameter.IsOut && parameterType == typeof(MemoryStore))
            {
                arguments[index] = null;
            }
            else if (parameterType == typeof(SharedMemoryStoreOptions))
            {
                arguments[index] = options;
            }
            else if (parameterType == typeof(StoreWaitOptions))
            {
                arguments[index] = StoreWaitOptions.Default;
            }
            else if (parameterType.IsInstanceOfType(observer))
            {
                arguments[index] = observer;
            }
            else if (parameterType.IsInstanceOfType(strategy))
            {
                arguments[index] = strategy;
            }
            else
            {
                throw new Xunit.Sdk.XunitException(
                    $"Unsupported instrumented factory parameter {parameter.Name}: {parameter.ParameterType}.");
            }
        }

        Assert.Contains(parameters, static parameter =>
            parameter.IsOut && parameter.ParameterType.GetElementType() == typeof(MemoryStore));
        return arguments;
    }

    private static SharedMemoryStoreOptions Options(string name, int slotCount) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: Math.Max(8, slotCount),
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);

    private static void AssertFullCapacityCanBePublished(MemoryStore store, byte firstKey, int slotCount)
    {
        for (var index = 0; index < slotCount; index++)
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish([(byte)(firstKey + index)], [(byte)index]));
        }

        Assert.Equal(StoreStatus.StoreFull, store.TryPublish([0xff], [0xff]));
    }

    private static byte[] Key(int index) =>
        [(byte)(index >> 24), (byte)(index >> 16), (byte)(index >> 8), (byte)index];

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class CheckpointController : IDisposable
    {
        private readonly LockFreeCheckpointId _target;
        private readonly ManualResetEventSlim _paused = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private int _targetReached;

        public CheckpointController(LockFreeCheckpointId target)
        {
            _ = LockFreeCheckpointCatalog.Get(target);
            _target = target;
        }

        public void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id != _target || Interlocked.CompareExchange(ref _targetReached, 1, 0) != 0)
            {
                return;
            }

            _paused.Set();
            _resume.Wait();
        }

        public bool WaitUntilPaused(TimeSpan timeout) => _paused.Wait(timeout);

        public void Continue() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _paused.Dispose();
            _resume.Dispose();
        }
    }
}
