using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class ReservationValidationTests
{
    [Fact]
    public void TryReserveRejectsKeyPayloadDescriptorDisposedDuplicateAndFullStore()
    {
        var options = StoreTestNames.Options(slotCount: 1, maxKeyBytes: 2, maxValueBytes: 3, maxDescriptorBytes: 1);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.InvalidKey, store.TryReserve(ReadOnlySpan<byte>.Empty, 1, default, out _));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryReserve([1, 2, 3], 1, default, out _));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryReserve([1], -1, default, out _));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryReserve([1], 4, default, out _));
        Assert.Equal(StoreStatus.DescriptorTooLarge, store.TryReserve([1], 1, [1, 2], out _));
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, [9], out var reservation));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryReserve([1], 1, default, out _));
        Assert.Equal(StoreStatus.StoreFull, store.TryReserve([2], 1, default, out _));
        Assert.Equal(StoreStatus.Success, reservation.Abort());

        store.Dispose();
        Assert.Equal(StoreStatus.StoreDisposed, store.TryReserve([1], 1, default, out _));
    }

    [Fact]
    public void ReservationDiagnosticsTrackAbortRecoveryAndFailures()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out var reservation));
        Assert.Equal(StoreStatus.ReservationWriteOutOfRange, reservation.Advance(3));
        Assert.Equal(StoreStatus.ReservationIncomplete, reservation.Commit());
        Assert.Equal(StoreStatus.Success, reservation.Abort());
        Assert.Equal(StoreStatus.InvalidReservation, reservation.Advance(1));

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out _));
        Assert.Equal(StoreStatus.Success, store.TryRecoverReservations(new ReservationRecoveryOptions(false), out var activeReport));
        Assert.Equal(1, activeReport.ActiveReservationCount);
        Assert.Equal(StoreStatus.Success, store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));

        var diagnostics = store.GetDiagnostics();
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.ReservationWriteOutOfRange));
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.ReservationIncomplete));
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.InvalidReservation));
        Assert.Equal(1, diagnostics.AbortedReservationCount);
        Assert.Equal(1, diagnostics.RecoveredReservationCount);
        Assert.Equal(1, diagnostics.ActiveReservationRecoveryCount);
        Assert.Equal(0, diagnostics.UnsupportedReservationRecoveryCount);
        Assert.Equal(0, diagnostics.FailedReservationRecoveryCount);
        Assert.Equal(1, report.RecoveredReservationCount);
    }

    [Fact]
    public void ReservationRecoveryDiagnosticsTrackFailedRecoveryResults()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out _));
        ref var slot = ref store.GetSlotForTesting(0);
        slot.Generation++;

        Assert.Equal(StoreStatus.Success, store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));

        var diagnostics = store.GetDiagnostics();
        Assert.Equal(1, report.FailedRecoveryCount);
        Assert.Equal(1, diagnostics.FailedReservationRecoveryCount);
        Assert.Equal(0, diagnostics.RecoveredReservationCount);
    }
}
