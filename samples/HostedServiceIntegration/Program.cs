using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    name: $"sms-hosted-{Guid.NewGuid():N}",
    slotCount: 4,
    maxValueBytes: 64,
    maxDescriptorBytes: 16,
    maxKeyBytes: 16,
    leaseRecordCount: 4,
    enableLeaseRecovery: true);

var lifecycle = new StoreLifecycleAdapter(options);
var start = lifecycle.Start();
Console.WriteLine($"start: {start}");
if (start != StoreOpenStatus.Success)
{
    return 1;
}

var publish = lifecycle.PublishHealthValue([1], [2, 3, 4]);
Console.WriteLine($"publish: {publish}");
if (publish != StoreStatus.Success)
{
    return 2;
}

var health = lifecycle.CheckHealth();
Console.WriteLine($"health: {health}");
if (!health.IsHealthy)
{
    return 3;
}

var leaseRecovery = lifecycle.RecoverLeases();
Console.WriteLine($"recover leases: {leaseRecovery}");
if (leaseRecovery != StoreStatus.Success)
{
    return 4;
}

var reservationRecovery = lifecycle.RecoverReservations();
Console.WriteLine($"recover reservations: {reservationRecovery}");
if (reservationRecovery != StoreStatus.Success)
{
    return 5;
}

var stop = lifecycle.Stop();
Console.WriteLine($"stop: {stop}");
if (stop != StoreStatus.Success)
{
    return 6;
}

return 0;

internal sealed class StoreLifecycleAdapter : IDisposable
{
    private readonly SharedMemoryStoreOptions _options;
    private MemoryStore? _store;

    public StoreLifecycleAdapter(SharedMemoryStoreOptions options)
    {
        _options = options;
    }

    public StoreOpenStatus Start()
    {
        var validation = _options.Validate();
        if (!validation.IsValid)
        {
            return validation.Status;
        }

        return MemoryStore.TryCreateOrOpen(_options, out _store);
    }

    public StoreStatus PublishHealthValue(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        return _store?.TryPublish(key, value) ?? StoreStatus.StoreDisposed;
    }

    public StoreHealth CheckHealth()
    {
        if (_store is null)
        {
            return new StoreHealth(false, StoreStatus.StoreDisposed, 0, 0);
        }

        var status = _store.TryGetDiagnostics(out var snapshot);
        return new StoreHealth(
            status == StoreStatus.Success,
            status == StoreStatus.Success ? snapshot.LastFailureStatus : status,
            snapshot.FreeSlotCount,
            snapshot.GetFailureCount(StoreStatus.StoreBusy));
    }

    public StoreStatus RecoverLeases()
    {
        return _store?.TryRecoverLeases(new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false), out _) ?? StoreStatus.StoreDisposed;
    }

    public StoreStatus RecoverReservations()
    {
        return _store?.TryRecoverReservations(new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false), out _) ?? StoreStatus.StoreDisposed;
    }

    public StoreStatus Stop()
    {
        _store?.Dispose();
        _store = null;
        return StoreStatus.Success;
    }

    public void Dispose()
    {
        _store?.Dispose();
    }
}

internal readonly record struct StoreHealth(bool IsHealthy, StoreStatus LastStatus, int FreeSlotCount, long BusyCount);
