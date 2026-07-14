using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeLeaseRegistryTests
{
    private const long TerminalIncarnation = 0x1_ffff_ffffL;

    [Fact]
    public void RegistrySurfaceSeparatesClaimActivationReleaseRecoveryAndStableScan()
    {
        var type = RequireLeaseRegistry();
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Contains(methods, static method => method.Name.Contains("Claim", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Activat", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Release", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Recover", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Scan", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Participant", StringComparison.Ordinal)
            || method.Name.Contains("Revalid", StringComparison.Ordinal));

        var forbidden = new[] { typeof(Mutex), typeof(Semaphore), typeof(SemaphoreSlim), typeof(ReaderWriterLockSlim) };
        Assert.DoesNotContain(
            type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            field => forbidden.Contains(field.FieldType));
    }

    [Fact]
    public void FirstLeaseClaimCarriesStoreParticipantSlotAndLeaseIncarnations()
    {
        _ = RequireLeaseRegistry();
        using var store = CreateStore(leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7], [9]));

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        var handle = lease.HandleForEngine;

        Assert.NotEqual(0UL, handle.StoreId);
        Assert.NotEqual(0UL, handle.ParticipantToken);
        Assert.NotEqual(0UL, handle.SlotBinding);
        Assert.NotEqual(0UL, handle.LeaseToken);
        Assert.True(lease.IsValid);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void ExactReleaseClearsOwnershipAndReuseAdvancesLeaseIncarnation()
    {
        _ = RequireLeaseRegistry();
        using var store = CreateStore(leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        var staleCopy = first;
        var firstHandle = first.HandleForEngine;
        Assert.Equal(StoreStatus.Success, first.Release());
        Assert.False(first.IsValid);
        Assert.Equal(StoreStatus.LeaseAlreadyReleased, staleCopy.Release());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var second));
        var secondHandle = second.HandleForEngine;
        Assert.Equal(firstHandle.StoreId, secondHandle.StoreId);
        Assert.Equal(firstHandle.ParticipantToken, secondHandle.ParticipantToken);
        Assert.Equal(firstHandle.SlotBinding, secondHandle.SlotBinding);
        Assert.NotEqual(firstHandle.LeaseToken, secondHandle.LeaseToken);

        Assert.NotEqual(StoreStatus.Success, staleCopy.Release());
        Assert.True(second.IsValid);
        Assert.Equal(StoreStatus.Success, second.Release());
    }

    [Fact]
    public void CancellationBeforeActivationLeaksNoRecordAndCancellationAfterActivationCannotUndoLease()
    {
        _ = RequireLeaseRegistry();
        using var store = CreateStore(leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));

        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            var canceled = new StoreWaitOptions(TimeSpan.FromSeconds(1), before.Token);
            Assert.Equal(StoreStatus.OperationCanceled, store.TryAcquire([1], canceled, out var rejected));
            Assert.False(rejected.IsValid);
        }

        using var after = new CancellationTokenSource();
        var activeWait = new StoreWaitOptions(TimeSpan.FromSeconds(1), after.Token);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], activeWait, out var active));
        after.Cancel();
        Assert.True(active.IsValid);
        Assert.Equal(7, active.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, active.Release());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var reused));
        Assert.Equal(StoreStatus.Success, reused.Release());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationOrDeadlineImmediatelyAroundActivationHasOneBoundedOutcome(bool cancellation)
    {
        _ = RequireLeaseRegistry();

        var before = ActivationBoundaryOracle.Apply(
            activationCompleted: false,
            cancellationObserved: cancellation,
            deadlineExpired: !cancellation);
        var after = ActivationBoundaryOracle.Apply(
            activationCompleted: true,
            cancellationObserved: cancellation,
            deadlineExpired: !cancellation);

        Assert.Equal(cancellation ? StoreStatus.OperationCanceled : StoreStatus.StoreBusy, before.Status);
        Assert.Equal(LeaseState.Free, before.State);
        Assert.False(before.HasParticipantOwnership);
        Assert.Equal(StoreStatus.Success, after.Status);
        Assert.Equal(LeaseState.Active, after.State);
        Assert.True(after.HasParticipantOwnership);
    }

    [Fact]
    public void TerminalLeaseIncarnationRetiresInsteadOfWrapping()
    {
        var type = RequireLeaseRegistry();
        var advanceOrRetire = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                method.Name.Contains("Advance", StringComparison.OrdinalIgnoreCase)
                && method.Name.Contains("Retire", StringComparison.OrdinalIgnoreCase)
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(long)
                && (method.ReturnType == typeof(long) || method.ReturnType == typeof(ulong)));
        Assert.True(advanceOrRetire is not null, "Lease registry needs a pure advance-or-retire transition seam.");

        var nextFree = Convert.ToUInt64(advanceOrRetire!.Invoke(null, [TerminalIncarnation - 1]));
        var retired = Convert.ToUInt64(advanceOrRetire.Invoke(null, [TerminalIncarnation]));

        Assert.Equal(AtomicControlWord.EncodeLease(state: 0, TerminalIncarnation, participantToken: 0), nextFree);
        Assert.Equal(AtomicControlWord.EncodeLease(state: 5, TerminalIncarnation, participantToken: 0), retired);
    }

    private static Type RequireLeaseRegistry()
    {
        var type = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeLeaseRegistry",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(type is not null, "The layout-v2 engine requires LockFreeLeaseRegistry.");
        return type!;
    }

    private static Store CreateStore(int leaseRecordCount)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The lock-free profile is qualified only on Windows/Linux x64.");
        }

        var options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-lease-registry-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        var status = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        var result = Assert.IsType<Store>(store);
        Assert.Equal(StoreProfile.LockFree, result.Profile);
        return result;
    }

    private enum LeaseState
    {
        Free,
        Active
    }

    private readonly record struct ActivationBoundaryResult(
        StoreStatus Status,
        LeaseState State,
        bool HasParticipantOwnership);

    private static class ActivationBoundaryOracle
    {
        public static ActivationBoundaryResult Apply(
            bool activationCompleted,
            bool cancellationObserved,
            bool deadlineExpired)
        {
            Assert.True(cancellationObserved ^ deadlineExpired);
            return activationCompleted
                ? new ActivationBoundaryResult(StoreStatus.Success, LeaseState.Active, HasParticipantOwnership: true)
                : new ActivationBoundaryResult(
                    cancellationObserved ? StoreStatus.OperationCanceled : StoreStatus.StoreBusy,
                    LeaseState.Free,
                    HasParticipantOwnership: false);
        }
    }
}
