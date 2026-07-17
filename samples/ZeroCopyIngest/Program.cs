using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using SharedMemoryStore;

var mode = args.Length == 0 ? "all" : args[0].ToLowerInvariant();
var options = CreateOptions();
var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine($"open failed: {openStatus}");
    return 1;
}

using (store)
{
    if (store.ProtocolInfo != new StoreProtocolInfo(2, 0, 2, 7, 0))
    {
        Console.WriteLine($"unexpected protocol: {store.ProtocolInfo}");
        return 2;
    }

    switch (mode)
    {
        case "all":
            var directKey = await RunLengthPrefixedStreamIngestAsync(store);
            RunReaderExample(store, directKey, "direct reader");
            RunAbortExample(store);
            RunSegmentedBufferedExample(store);
            await RunPipelinesAdapterExampleAsync(store);
            return 0;

        case "socket":
        case "stream":
            _ = await RunLengthPrefixedStreamIngestAsync(store);
            return 0;

        case "pipeline":
        case "pipelines":
            await RunPipelinesAdapterExampleAsync(store);
            return 0;

        case "reader":
            var readerKey = SeedReaderValue(store);
            RunReaderExample(store, readerKey, "reader");
            return 0;

        case "segmented":
            RunSegmentedBufferedExample(store);
            return 0;

        case "abort":
            RunAbortExample(store);
            return 0;

        default:
            Console.WriteLine("usage: ZeroCopyIngest [all|socket|pipeline|reader|segmented|abort]");
            return 2;
    }
}

static async Task<byte[]> RunLengthPrefixedStreamIngestAsync(MemoryStore store)
{
    var key = new byte[] { 1 };
    var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
    await using var stream = new MemoryStream(CreateLengthPrefixedFrame(payload));

    var header = new byte[4];
    await stream.ReadExactlyAsync(header);
    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
    var descriptor = CreateDescriptor(payloadLength, frameKind: 1);

    var reserveStatus = store.TryReserve(key, payloadLength, descriptor, out var reservation);
    if (reserveStatus != StoreStatus.Success)
    {
        Console.WriteLine($"stream reserve: {reserveStatus}");
        return key;
    }

    while (reservation.RemainingBytes > 0)
    {
        var readLength = Math.Min(4, reservation.RemainingBytes);
        var target = reservation.DangerousGetMemory(readLength).Slice(0, readLength);
        var received = await stream.ReadAsync(target);
        if (received == 0)
        {
            Console.WriteLine($"stream abort: {reservation.Abort()}");
            return key;
        }

        var advance = reservation.Advance(received);
        if (advance != StoreStatus.Success)
        {
            Console.WriteLine($"stream advance: {advance}");
            _ = reservation.Abort();
            return key;
        }
    }

    Console.WriteLine($"stream commit: {reservation.Commit()}");
    return key;
}

static async Task RunPipelinesAdapterExampleAsync(MemoryStore store)
{
    var key = new byte[] { 4 };
    var payload = new byte[] { 30, 31, 32, 33, 34, 35 };
    var frame = CreateLengthPrefixedFrame(payload);
    var pipe = new Pipe();

    await pipe.Writer.WriteAsync(frame.AsMemory(0, 5));
    await pipe.Writer.WriteAsync(frame.AsMemory(5));
    await pipe.Writer.CompleteAsync();

    var read = await pipe.Reader.ReadAsync();
    var reader = new SequenceReader<byte>(read.Buffer);
    if (!reader.TryReadLittleEndian(out int payloadLength) || read.Buffer.Length - 4 < payloadLength)
    {
        pipe.Reader.AdvanceTo(read.Buffer.End);
        await pipe.Reader.CompleteAsync();
        Console.WriteLine("pipeline parse: invalid frame");
        return;
    }

    var payloadSequence = read.Buffer.Slice(reader.Position, payloadLength);
    var descriptor = CreateDescriptor(payloadLength, frameKind: 2);
    var status = store.TryPublishSegments(key, payloadSequence, descriptor, out var copiedBytes);
    pipe.Reader.AdvanceTo(read.Buffer.GetPosition(payloadLength, reader.Position));
    await pipe.Reader.CompleteAsync();

    Console.WriteLine($"pipeline publish: {status}");
    if (status != StoreStatus.Success)
    {
        return;
    }

    Console.WriteLine($"pipeline copied: {copiedBytes}");
    RunReaderExample(store, key, "pipeline reader");
}

static void RunSegmentedBufferedExample(MemoryStore store)
{
    var key = new byte[] { 3 };
    var first = new byte[] { 20, 21 };
    var second = new byte[] { 22, 23, 24 };
    var sequence = SequenceFactory.Create(first, second);
    var descriptor = CreateDescriptor((int)sequence.Length, frameKind: 3);

    var publish = store.TryPublishSegments(key, sequence, descriptor, out var copied);
    Console.WriteLine($"segmented publish: {publish}");
    if (publish != StoreStatus.Success)
    {
        return;
    }

    Console.WriteLine($"segmented copied: {copied}");
    RunReaderExample(store, key, "segmented reader");
}

static void RunAbortExample(MemoryStore store)
{
    var key = new byte[] { 2 };
    var reserve = store.TryReserve(key, 4, default, out var reservation);
    Console.WriteLine($"abort reserve: {reserve}");
    if (reserve != StoreStatus.Success)
    {
        return;
    }

    reservation.GetSpan()[0] = 42;
    Console.WriteLine($"abort: {reservation.Abort()}");
    Console.WriteLine($"abort acquire: {store.TryAcquire(key, out _)}");
}

static byte[] SeedReaderValue(MemoryStore store)
{
    var key = new byte[] { 9 };
    var descriptor = CreateDescriptor(3, frameKind: 9);
    Console.WriteLine($"seed reader value: {store.TryPublish(key, [90, 91, 92], descriptor)}");
    return key;
}

static void RunReaderExample(MemoryStore store, byte[] key, string label)
{
    var status = store.TryAcquire(key, out var lease);
    Console.WriteLine($"{label} acquire: {status}");
    if (status != StoreStatus.Success)
    {
        return;
    }

    Console.WriteLine($"{label} descriptor: {BitConverter.ToString(lease.DescriptorSpan.ToArray())}");
    Console.WriteLine($"{label} value: {BitConverter.ToString(lease.ValueSpan.ToArray())}");
    Console.WriteLine($"{label} release: {lease.Release()}");
    Console.WriteLine($"{label} remove: {store.TryRemove(key)}");
}

static SharedMemoryStoreOptions CreateOptions()
{
    return SharedMemoryStoreOptions.Create(
        name: $"sms-ingest-{Guid.NewGuid():N}",
        slotCount: 4,
        maxValueBytes: 64,
        maxDescriptorBytes: 16,
        maxKeyBytes: 16,
        leaseRecordCount: 8,
        participantRecordCount: 4,
        openMode: OpenMode.CreateNew,
        enableLeaseRecovery: true);
}

static byte[] CreateLengthPrefixedFrame(byte[] payload)
{
    var frame = new byte[4 + payload.Length];
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
    payload.CopyTo(frame.AsSpan(4));
    return frame;
}

static byte[] CreateDescriptor(int payloadLength, byte frameKind)
{
    var descriptor = new byte[5];
    BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(0, 4), payloadLength);
    descriptor[4] = frameKind;
    return descriptor;
}

internal static class SequenceFactory
{
    public static ReadOnlySequence<byte> Create(params byte[][] segments)
    {
        BufferSegment? first = null;
        BufferSegment? last = null;
        foreach (var segment in segments)
        {
            last = last is null ? first = new BufferSegment(segment) : last.Append(segment);
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }
}

internal sealed class BufferSegment : ReadOnlySequenceSegment<byte>
{
    public BufferSegment(byte[] memory)
    {
        Memory = memory;
    }

    public BufferSegment Append(byte[] memory)
    {
        var segment = new BufferSegment(memory)
        {
            RunningIndex = RunningIndex + Memory.Length
        };
        Next = segment;
        return segment;
    }
}
