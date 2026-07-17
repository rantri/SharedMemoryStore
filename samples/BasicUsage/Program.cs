using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    name: $"sms-basic-{Guid.NewGuid():N}",
    slotCount: 2,
    maxValueBytes: 64,
    maxDescriptorBytes: 16,
    maxKeyBytes: 16,
    leaseRecordCount: 4,
    participantRecordCount: 4,
    enableLeaseRecovery: true);

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine($"open failed: {openStatus}");
    return 1;
}

using (store)
{
    var protocol = store.ProtocolInfo;
    if (protocol.LayoutMajorVersion != 2
        || protocol.LayoutMinorVersion != 0
        || protocol.ResourceProtocolVersion != 2
        || protocol.RequiredFeatures != 7
        || protocol.OptionalFeatures != 0)
    {
        Console.WriteLine($"unexpected protocol: {protocol}");
        return 2;
    }
    Console.WriteLine(
        $"protocol: SMS2 {protocol.LayoutMajorVersion}.{protocol.LayoutMinorVersion}, "
        + $"resource {protocol.ResourceProtocolVersion}, required 0x{protocol.RequiredFeatures:X}, "
        + $"optional 0x{protocol.OptionalFeatures:X}, participants {options.ParticipantRecordCount}");

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
        return 3;
    }

    var acquire = store.TryAcquire(key, out var lease);
    Console.WriteLine(acquire);
    if (acquire != StoreStatus.Success)
    {
        return 4;
    }

    Console.WriteLine($"value bytes: {BitConverter.ToString(lease.ValueSpan.ToArray())}");
    var release = lease.Release();
    Console.WriteLine(release);
    if (release != StoreStatus.Success)
    {
        return 5;
    }

    var remove = store.TryRemove(key);
    Console.WriteLine(remove);
    if (remove != StoreStatus.Success)
    {
        return 6;
    }

    Span<byte> replacementPayload = stackalloc byte[] { 10 };
    var reusePublish = store.TryPublish(key, replacementPayload);
    Console.WriteLine(reusePublish);
    if (reusePublish != StoreStatus.Success)
    {
        return 7;
    }

    Console.WriteLine($"free slots: {store.GetDiagnostics().FreeSlotCount}");
}

return 0;
