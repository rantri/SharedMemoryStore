using System.Runtime.InteropServices;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.LinearizabilityTests;

public sealed class AcquireHistoryTests
{
    [Fact]
    [Trait("Category", "Linearizability")]
    public void OverlappingCommitAndAcquireMayLinearizeAsMissingBeforeCommit()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 1, 3, 8, 9),
            Operation(2, ReferenceCommand.Acquire(1, "key"), ReferenceResultCode.NotFound, 2, 4, 5, 6)
        };

        var result = Checker().Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([2, 1], result.Linearization);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void OverlappingCommitAndAcquireMayLinearizeAsAcquireAfterCommit()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 1, 3, 5, 8),
            Operation(2, ReferenceCommand.Acquire(1, "key"), ReferenceResultCode.Success, 2, 4, 6, 7)
        };

        var result = Checker().Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2], result.Linearization);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void AcquireCannotReturnMissingAfterCommitCompletedBeforeInvocation()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.Acquire(1, "key"), ReferenceResultCode.NotFound, 5, 6, 7, 8)
        };

        var result = Checker().Check(history);

        Assert.False(result.IsLinearizable);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void PublicCommitThenAcquireHistoryMatchesReferenceModel()
    {
        RequireLeaseRegistry();
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var store = CreateStore();
        var recorder = new MonotonicHistoryRecorder();
        var publish = recorder.Invoke(1, 1, ReferenceCommand.Publish(1, "key", "value"));
        publish.Enter();
        publish.Complete(MapPublish(store.TryPublish([0x41], [0x51])));

        var acquire = recorder.Invoke(2, 1, ReferenceCommand.Acquire(1, "key"));
        acquire.Enter();
        var acquireStatus = store.TryAcquire([0x41], out var lease);
        acquire.Complete(MapAcquire(acquireStatus));

        var result = new LinearizabilityChecker(
            participantCapacity: 2,
            valueCapacity: 2,
            initialParticipants: [1]).Check(recorder.Snapshot());
        try
        {
            Assert.Equal(StoreStatus.Success, acquireStatus);
            Assert.True(lease.IsValid);
            Assert.Equal(0x51, lease.ValueSpan[0]);
            Assert.True(result.IsLinearizable, result.Failure);
        }
        finally
        {
            if (lease.IsValid)
            {
                Assert.Equal(StoreStatus.Success, lease.Release());
            }
        }
    }

    private static LinearizabilityChecker Checker() =>
        new(participantCapacity: 2, valueCapacity: 2, initialParticipants: [1]);

    private static RecordedOperation Operation(
        int id,
        ReferenceCommand command,
        ReferenceResultCode result,
        long invocation,
        long entry,
        long returned,
        long response) =>
        new(id, id, command, result, invocation, entry, returned, response);

    private static ReferenceResultCode MapPublish(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.DuplicateKey => ReferenceResultCode.DuplicateKey,
        StoreStatus.StoreFull => ReferenceResultCode.StoreFull,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapAcquire(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.NotFound => ReferenceResultCode.NotFound,
        _ => ReferenceResultCode.Unexpected
    };

    private static void RequireLeaseRegistry()
    {
        Assert.True(
            typeof(MemoryStore).Assembly.GetType(
                "SharedMemoryStore.LockFree.LockFreeLeaseRegistry",
                throwOnError: false,
                ignoreCase: false) is not null,
            "Acquire histories require the missing LockFreeLeaseRegistry implementation.");
    }

    private static Store CreateStore()
    {
        var options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-acquire-history-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        var status = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
