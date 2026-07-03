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

Console.WriteLine($"publish: {lifecycle.PublishHealthValue([1], [2, 3, 4])}");
Console.WriteLine($"health: {lifecycle.CheckHealth()}");
Console.WriteLine($"recover leases: {lifecycle.RecoverLeases()}");
Console.WriteLine($"recover reservations: {lifecycle.RecoverReservations()}");
Console.WriteLine($"stop: {lifecycle.Stop()}");
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
