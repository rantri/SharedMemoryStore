using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Lifecycle;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.Options;

namespace SharedMemoryStore;

/// <summary>
/// Disposable process-local handle for one bounded named shared-memory value store.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private readonly StoreLifecycleGate _lifecycle = new();
    private readonly IStoreEngine _engine;

    internal MemoryStore(IStoreEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        ProtocolInfo = engine.ProtocolInfo;
    }

    /// <summary>
    /// Gets the immutable persisted layout and resource-protocol identity, independently of the
    /// package version.
    /// </summary>
    public StoreProtocolInfo ProtocolInfo { get; }

    /// <summary>
    /// Creates or opens a named store using the supplied options and the default bounded wait policy.
    /// </summary>
    public static StoreOpenStatus TryCreateOrOpen(
        in SharedMemoryStoreOptions options,
        out MemoryStore? store)
    {
        return TryCreateOrOpen(options, StoreWaitOptions.Default, out store);
    }

    /// <summary>
    /// Creates or opens a named store using the supplied options and caller-selected cold-path
    /// lifecycle and participant wait policy.
    /// </summary>
    public static StoreOpenStatus TryCreateOrOpen(
        in SharedMemoryStoreOptions options,
        StoreWaitOptions waitOptions,
        out MemoryStore? store)
    {
        store = null;
        if (!waitOptions.IsValid)
        {
            return StoreOpenStatus.InvalidOptions;
        }

        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            return StoreOpenStatus.OperationCanceled;
        }

        StoreOpenStatus validation = SharedMemoryStoreOptionsValidator.Validate(options, out _);
        if (validation != StoreOpenStatus.Success)
        {
            return validation;
        }

        if (!LayoutV2Constants.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture))
        {
            return StoreOpenStatus.UnsupportedPlatform;
        }

        long waitStartTimestamp = Stopwatch.GetTimestamp();
        StoreOpenStatus mappingStatus = SharedStorePlatform.TryBeginOpen(
            options,
            waitOptions,
            waitStartTimestamp,
            out SharedStoreOpenScope? openScope);
        if (mappingStatus != StoreOpenStatus.Success || openScope is null)
        {
            return mappingStatus;
        }

        IStoreEngine? engine = null;
        StoreOpenStatus status;
        try
        {
            using (openScope)
            {
                status = StoreEngineFactory.TryCreateUnderColdGate(
                    options,
                    waitOptions,
                    waitStartTimestamp,
                    openScope.Region,
                    openScope.Synchronization,
                    openScope.Disposition,
                    out engine);
                if (status == StoreOpenStatus.Success && engine is not null)
                {
                    openScope.TransferResourceOwnership();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            engine?.Dispose();
            return StoreOpenStatus.AccessDenied;
        }
        catch (Exception)
        {
            engine?.Dispose();
            return StoreOpenStatus.MappingFailed;
        }

        if (status != StoreOpenStatus.Success || engine is null)
        {
            return status;
        }

        try
        {
            store = StoreEngineFactory.WrapOwnedEngine(engine);
            return StoreOpenStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return StoreOpenStatus.AccessDenied;
        }
        catch (Exception)
        {
            return StoreOpenStatus.MappingFailed;
        }
    }

    /// <summary>Publishes immutable payload and optional descriptor bytes under an opaque key.</summary>
    public StoreStatus TryPublish(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor = default)
    {
        return TryPublish(key, value, descriptor, StoreWaitOptions.Default);
    }

    /// <summary>Publishes bytes using the supplied bounded wait policy.</summary>
    public StoreStatus TryPublish(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.TryPublish(key, value, descriptor, remainingWait);
        }
    }

    /// <summary>Reserves store-owned payload storage using the default wait policy.</summary>
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        out ValueReservation reservation)
    {
        return TryReserve(key, payloadLength, descriptor, StoreWaitOptions.Default, out reservation);
    }

    /// <summary>Reserves store-owned payload storage using the supplied bounded wait policy.</summary>
    public StoreStatus TryReserve(
        ReadOnlySpan<byte> key,
        int payloadLength,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out ValueReservation reservation)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            reservation = default;
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            StoreStatus result = _engine.TryReserve(
                key,
                payloadLength,
                descriptor,
                remainingWait,
                out ReservationHandle handle);
            reservation = result == StoreStatus.Success ? new ValueReservation(this, handle) : default;
            return result;
        }
    }

    /// <summary>Publishes a segmented payload using the default wait policy.</summary>
    public StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        in ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        out long copiedBytes)
    {
        return TryPublishSegments(key, payload, descriptor, StoreWaitOptions.Default, out copiedBytes);
    }

    /// <summary>Publishes a segmented payload using the supplied bounded wait policy.</summary>
    public StoreStatus TryPublishSegments(
        ReadOnlySpan<byte> key,
        in ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            copiedBytes = 0;
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.TryPublishSegments(key, payload, descriptor, remainingWait, out copiedBytes);
        }
    }

    /// <summary>Acquires a shared read lease using the default wait policy.</summary>
    public StoreStatus TryAcquire(ReadOnlySpan<byte> key, out ValueLease lease)
    {
        return TryAcquire(key, StoreWaitOptions.Default, out lease);
    }

    /// <summary>Acquires a shared read lease using the supplied bounded wait policy.</summary>
    public StoreStatus TryAcquire(
        ReadOnlySpan<byte> key,
        StoreWaitOptions waitOptions,
        out ValueLease lease)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            lease = default;
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            StoreStatus result = _engine.TryAcquire(key, remainingWait, out LeaseHandle handle);
            lease = result == StoreStatus.Success ? new ValueLease(this, handle) : default;
            return result;
        }
    }

    /// <summary>
    /// Makes the key logically absent and attempts physical reclamation using the default wait policy.
    /// </summary>
    /// <remarks>
    /// <see cref="StoreStatus.Success"/> means physical reclamation completed.
    /// <see cref="StoreStatus.RemovePending"/> means the key is already logically absent while
    /// a live lease or bounded helper cleanup still delays physical slot reuse.
    /// </remarks>
    /// <param name="key">The exact opaque key bytes to remove.</param>
    /// <returns>The deterministic logical-removal and physical-reclamation outcome.</returns>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key)
    {
        return TryRemove(key, StoreWaitOptions.Default);
    }

    /// <summary>
    /// Makes the key logically absent and attempts physical reclamation using the supplied bounded wait policy.
    /// </summary>
    /// <remarks>
    /// The successful ordering point is logical absence. <see cref="StoreStatus.Success"/>
    /// additionally proves physical reclamation completed; <see cref="StoreStatus.RemovePending"/>
    /// preserves logical absence while a live lease or bounded helper cleanup delays physical reuse.
    /// </remarks>
    /// <param name="key">The exact opaque key bytes to remove.</param>
    /// <param name="waitOptions">The timeout and cancellation budget for this call.</param>
    /// <returns>The deterministic logical-removal and physical-reclamation outcome.</returns>
    public StoreStatus TryRemove(ReadOnlySpan<byte> key, StoreWaitOptions waitOptions)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.TryRemove(key, remainingWait);
        }
    }

    /// <summary>Explicitly recovers stale leases using the default wait policy.</summary>
    public StoreStatus TryRecoverLeases(
        in LeaseRecoveryOptions options,
        out LeaseRecoveryReport report)
    {
        return TryRecoverLeases(options, StoreWaitOptions.Default, out report);
    }

    /// <summary>Explicitly recovers stale leases using the supplied bounded wait policy.</summary>
    public StoreStatus TryRecoverLeases(
        in LeaseRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out LeaseRecoveryReport report)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            report = default;
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.TryRecoverLeases(options, remainingWait, out report);
        }
    }

    /// <summary>Explicitly recovers stale reservations using the default wait policy.</summary>
    public StoreStatus TryRecoverReservations(
        in ReservationRecoveryOptions options,
        out ReservationRecoveryReport report)
    {
        return TryRecoverReservations(options, StoreWaitOptions.Default, out report);
    }

    /// <summary>Explicitly recovers stale reservations using the supplied bounded wait policy.</summary>
    public StoreStatus TryRecoverReservations(
        in ReservationRecoveryOptions options,
        StoreWaitOptions waitOptions,
        out ReservationRecoveryReport report)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            report = default;
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.TryRecoverReservations(options, remainingWait, out report);
        }
    }

    /// <summary>Returns a caller-formatted diagnostics snapshot.</summary>
    public DiagnosticsSnapshot GetDiagnostics()
    {
        _ = TryGetDiagnostics(StoreWaitOptions.Default, out DiagnosticsSnapshot snapshot);
        return snapshot;
    }

    /// <summary>Attempts to create a diagnostics snapshot using the default wait policy.</summary>
    public StoreStatus TryGetDiagnostics(out DiagnosticsSnapshot snapshot)
    {
        return TryGetDiagnostics(StoreWaitOptions.Default, out snapshot);
    }

    /// <summary>Attempts to create a bounded diagnostics snapshot.</summary>
    public StoreStatus TryGetDiagnostics(
        StoreWaitOptions waitOptions,
        out DiagnosticsSnapshot snapshot)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            snapshot = status == StoreStatus.StoreDisposed ? CreateDisposedSnapshot() : default;
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.TryGetDiagnostics(remainingWait, out snapshot);
        }
    }

    /// <summary>
    /// Releases this local handle and invalidates future operations and borrowed views without
    /// disposing other handles attached to the mapping.
    /// </summary>
    public void Dispose()
    {
        if (!_lifecycle.IsDisposingOrDisposed
            && _engine is ILockFreeCheckpointEmitter checkpointEmitter)
        {
            checkpointEmitter.ReachCheckpoint(LockFreeCheckpointId.DisposalBeforeLocalGateClose);
        }

        if (!_lifecycle.TryBeginDispose())
        {
            return;
        }

        try
        {
            _engine.Dispose();
        }
        finally
        {
            _lifecycle.CompleteDispose();
        }
    }

    internal bool IsLeaseActive(in LeaseHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return false;
        }

        using (operation)
        {
            return _engine.IsLeaseActive(handle);
        }
    }

    internal int GetValueLength(in LeaseHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return 0;
        }

        using (operation)
        {
            return _engine.GetValueLength(handle);
        }
    }

    internal int GetDescriptorLength(in LeaseHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return 0;
        }

        using (operation)
        {
            return _engine.GetDescriptorLength(handle);
        }
    }

    internal ReadOnlySpan<byte> GetValueSpan(LeaseHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        using (operation)
        {
            return _engine.GetValueSpan(handle);
        }
    }

    internal ReadOnlySpan<byte> GetDescriptorSpan(LeaseHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        using (operation)
        {
            return _engine.GetDescriptorSpan(handle);
        }
    }

    internal StoreStatus ReleaseLease(in LeaseHandle handle, StoreWaitOptions waitOptions)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.ReleaseLease(handle, remainingWait);
        }
    }

    internal bool IsReservationPending(in ReservationHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return false;
        }

        using (operation)
        {
            return _engine.IsReservationPending(handle);
        }
    }

    internal int GetReservationBytesWritten(in ReservationHandle handle)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return 0;
        }

        using (operation)
        {
            return _engine.GetReservationBytesWritten(handle);
        }
    }

    internal Span<byte> GetReservationSpan(ReservationHandle handle, int sizeHint)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return Span<byte>.Empty;
        }

        using (operation)
        {
            return _engine.GetReservationSpan(handle, sizeHint);
        }
    }

    internal Memory<byte> GetReservationMemory(ReservationHandle handle, int sizeHint)
    {
        if (!_lifecycle.TryEnter(out var operation))
        {
            return Memory<byte>.Empty;
        }

        using (operation)
        {
            return _engine.DangerousGetReservationMemory(handle, sizeHint);
        }
    }

    internal StoreStatus AdvanceReservation(
        in ReservationHandle handle,
        int byteCount,
        StoreWaitOptions waitOptions)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.AdvanceReservation(handle, byteCount, remainingWait);
        }
    }

    internal StoreStatus CommitReservation(in ReservationHandle handle, StoreWaitOptions waitOptions)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.CommitReservation(handle, remainingWait);
        }
    }

    internal StoreStatus AbortReservation(
        in ReservationHandle handle,
        StoreWaitOptions waitOptions)
    {
        if (!TryEnterEngineOperation(waitOptions, out var operation, out var remainingWait, out var status))
        {
            return _engine.RecordFacadeStatus(status);
        }

        using (operation)
        {
            return _engine.AbortReservation(handle, remainingWait);
        }
    }

    internal DiagnosticsSnapshot CreateDisposedSnapshot() =>
        _engine.CreateDisposedDiagnosticsSnapshot();

    private bool TryEnterEngineOperation(
        StoreWaitOptions waitOptions,
        out StoreLifecycleGate.Operation operation,
        out StoreWaitOptions remainingWait,
        out StoreStatus status)
    {
        long started = Stopwatch.GetTimestamp();
        status = _lifecycle.TryEnter(waitOptions, started, out operation);
        if (status != StoreStatus.Success)
        {
            remainingWait = default;
            return false;
        }

        if (TryGetRemainingWaitOptions(waitOptions, started, out remainingWait, out status))
        {
            return true;
        }

        operation.Dispose();
        operation = default;
        return false;
    }

    private static bool TryGetRemainingWaitOptions(
        StoreWaitOptions waitOptions,
        long started,
        out StoreWaitOptions remainingWait,
        out StoreStatus status)
    {
        remainingWait = default;
        if (!waitOptions.IsValid)
        {
            status = StoreStatus.UnknownFailure;
            return false;
        }

        if (waitOptions.CancellationToken.IsCancellationRequested)
        {
            status = StoreStatus.OperationCanceled;
            return false;
        }

        if (waitOptions.IsInfinite || waitOptions.Timeout == TimeSpan.Zero)
        {
            remainingWait = waitOptions;
            status = StoreStatus.Success;
            return true;
        }

        TimeSpan remaining = waitOptions.Timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            status = StoreStatus.StoreBusy;
            return false;
        }

        remainingWait = new StoreWaitOptions(remaining, waitOptions.CancellationToken);
        status = StoreStatus.Success;
        return true;
    }
}
