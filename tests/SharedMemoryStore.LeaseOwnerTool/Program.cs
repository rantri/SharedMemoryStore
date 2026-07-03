using System.Globalization;
using SharedMemoryStore;
using Store = SharedMemoryStore.SharedMemoryStore;

if (args.Length < 8)
{
    return 64;
}

var mode = args[0];
var slotCount = ParseInt(args[2]);
var maxValueBytes = ParseInt(args[3]);
var maxDescriptorBytes = ParseInt(args[4]);
var maxKeyBytes = ParseInt(args[5]);
var leaseRecordCount = ParseInt(args[6]);
var options = new SharedMemoryStoreOptions
{
    Name = args[1],
    OpenMode = OpenMode.OpenExisting,
    SlotCount = slotCount,
    MaxValueBytes = maxValueBytes,
    MaxDescriptorBytes = maxDescriptorBytes,
    MaxKeyBytes = maxKeyBytes,
    LeaseRecordCount = leaseRecordCount,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
        slotCount,
        maxValueBytes,
        maxDescriptorBytes,
        maxKeyBytes,
        leaseRecordCount)
};

var open = Store.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine("OPEN_FAILED " + open);
    return 65;
}

using (store)
{
    return mode switch
    {
        "live" => RunLiveOwner(store, ParseInt(args[7])),
        "stale" when args.Length >= 9 => RunStaleOwner(store, ParseInt(args[7]), ParseInt(args[8])),
        _ => 66
    };
}

static int RunLiveOwner(Store store, int keyValue)
{
    var key = Key(keyValue);
    var status = store.TryPublish(key, [(byte)keyValue]);
    if (status != StoreStatus.Success)
    {
        Console.WriteLine("PUBLISH_FAILED " + status);
        return 67;
    }

    status = store.TryAcquire(key, out var lease);
    if (status != StoreStatus.Success)
    {
        Console.WriteLine("ACQUIRE_FAILED " + status);
        return 68;
    }

    Console.WriteLine("READY " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    while (Console.ReadLine() is { } command)
    {
        switch (command)
        {
            case "CHECK":
                Console.WriteLine(lease.IsValid
                    && lease.ValueLength == 1
                    && !lease.ValueSpan.IsEmpty
                    && lease.ValueSpan[0] == (byte)keyValue
                        ? "VALID"
                        : "INVALID");
                break;
            case "RELEASE":
                Console.WriteLine(lease.Release());
                break;
            case "EXIT":
                return 0;
            default:
                Console.WriteLine("UNKNOWN");
                break;
        }
    }

    return 0;
}

static int RunStaleOwner(Store store, int firstKeyValue, int leaseCount)
{
    var leases = new ValueLease[leaseCount];
    for (var i = 0; i < leaseCount; i++)
    {
        var keyValue = firstKeyValue + i;
        var key = Key(keyValue);
        var status = store.TryPublish(key, [(byte)i]);
        if (status != StoreStatus.Success)
        {
            Console.WriteLine("PUBLISH_FAILED " + i.ToString(CultureInfo.InvariantCulture) + " " + status);
            return 69;
        }

        status = store.TryAcquire(key, out leases[i]);
        if (status != StoreStatus.Success)
        {
            Console.WriteLine("ACQUIRE_FAILED " + i.ToString(CultureInfo.InvariantCulture) + " " + status);
            return 70;
        }
    }

    Console.WriteLine("READY "
        + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
        + " "
        + leaseCount.ToString(CultureInfo.InvariantCulture));
    return 0;
}

static byte[] Key(int value) => BitConverter.GetBytes(value);

static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
