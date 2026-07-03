using SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = $"sms-basic-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = 64,
    MaxDescriptorBytes = 16,
    MaxKeyBytes = 16,
    LeaseRecordCount = 4,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 64, 16, 16, 4)
};

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine($"open failed: {openStatus}");
    return 1;
}

using (store)
{
    Span<byte> key = stackalloc byte[1 + StoreByteEncoding.Int32ByteCount];
    key[0] = 1; // application-owned key namespace
    StoreByteEncoding.WriteInt32LittleEndian(42, key[1..]);

    Span<byte> descriptor = stackalloc byte[StoreByteEncoding.BasicDescriptorByteCount];
    StoreByteEncoding.WriteBasicDescriptor(schemaVersion: 1, flags: 0, descriptor);

    Span<byte> payload = stackalloc byte[] { 4, 5, 6, 7 };

    var publish = store.TryPublish(key, payload, descriptor);
    Console.WriteLine(publish);
    if (publish != StoreStatus.Success)
    {
        return 2;
    }

    var acquire = store.TryAcquire(key, out var lease);
    Console.WriteLine(acquire);
    if (acquire != StoreStatus.Success)
    {
        return 3;
    }

    Console.WriteLine($"value bytes: {BitConverter.ToString(lease.ValueSpan.ToArray())}");
    var release = lease.Release();
    Console.WriteLine(release);
    if (release != StoreStatus.Success)
    {
        return 4;
    }

    var remove = store.TryRemove(key);
    Console.WriteLine(remove);
    if (remove != StoreStatus.Success)
    {
        return 5;
    }

    Span<byte> replacementPayload = stackalloc byte[] { 10 };
    var reusePublish = store.TryPublish(key, replacementPayload);
    Console.WriteLine(reusePublish);
    if (reusePublish != StoreStatus.Success)
    {
        return 6;
    }

    Console.WriteLine($"free slots: {store.GetDiagnostics().FreeSlotCount}");
}

return 0;
