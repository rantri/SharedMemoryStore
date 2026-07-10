using System.Buffers;
using System.Text.Json;

namespace SharedMemoryStore.InteropAgent;

internal sealed class AgentSession : IDisposable
{
    private readonly Dictionary<string, MemoryStore> _stores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueReservation> _reservations = new(StringComparer.Ordinal);

    public AgentResponse Handle(AgentRequest request)
    {
        try
        {
            return request.Command switch
            {
                "ping" => Success(request.Id, 0, "Success", new { runtime = "dotnet", protocolVersion = 1 }),
                "open" => Open(request),
                "close" => Close(request),
                "publish" => Publish(request),
                "publishSegments" or "publishSegmented" => PublishSegments(request),
                "acquire" => Acquire(request),
                "release" => Release(request),
                "remove" => Remove(request),
                "reserve" => Reserve(request),
                "reservationWrite" => ReservationWrite(request),
                "advance" => Advance(request),
                "commit" => Commit(request),
                "abort" => Abort(request),
                "recoverLeases" => RecoverLeases(request),
                "recoverReservations" => RecoverReservations(request),
                "diagnostics" => Diagnostics(request),
                "crash" => Crash(),
                _ => AgentHost.Failure(
                    request.Id,
                    statusCode: -2,
                    statusName: "UnsupportedCommand",
                    errorCode: "unsupported_command",
                    message: $"The command '{request.Command}' is not implemented by this agent.")
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or ArgumentException)
        {
            return AgentHost.Failure(
                request.Id,
                statusCode: -1,
                statusName: "ProtocolError",
                errorCode: "invalid_arguments",
                message: exception.Message);
        }
    }

    public void Dispose()
    {
        foreach (var lease in _leases.Values)
        {
            lease.Dispose();
        }

        foreach (var reservation in _reservations.Values)
        {
            reservation.Dispose();
        }

        foreach (var store in _stores.Values)
        {
            store.Dispose();
        }

        _leases.Clear();
        _reservations.Clear();
        _stores.Clear();
    }

    private AgentResponse Open(AgentRequest request)
    {
        var arguments = Arguments(request);
        var storeId = RequiredString(arguments, "storeId");
        if (_stores.Remove(storeId, out var previous))
        {
            previous.Dispose();
        }

        var slotCount = RequiredInt32(arguments, "slotCount");
        var maxValueBytes = RequiredInt32(arguments, "maxValueBytes");
        var maxDescriptorBytes = RequiredInt32(arguments, "maxDescriptorBytes");
        var maxKeyBytes = RequiredInt32(arguments, "maxKeyBytes");
        var leaseRecordCount = RequiredInt32(arguments, "leaseRecordCount");
        var totalBytes = OptionalInt64(arguments, "totalBytes") ?? SharedMemoryStoreOptions.CalculateRequiredBytes(
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount);
        var options = new SharedMemoryStoreOptions
        {
            Name = RequiredString(arguments, "name"),
            OpenMode = ParseOpenMode(arguments),
            TotalBytes = totalBytes,
            SlotCount = slotCount,
            MaxValueBytes = maxValueBytes,
            MaxDescriptorBytes = maxDescriptorBytes,
            MaxKeyBytes = maxKeyBytes,
            LeaseRecordCount = leaseRecordCount,
            EnableLeaseRecovery = OptionalBoolean(arguments, "enableLeaseRecovery") ?? false
        };
        var status = MemoryStore.TryCreateOrOpen(options, Wait(arguments), out var store);
        if (status == StoreOpenStatus.Success && store is not null)
        {
            _stores.Add(storeId, store);
        }

        return Success(request.Id, (int)status, status.ToString(), new { storeId });
    }

    private AgentResponse Close(AgentRequest request)
    {
        var storeId = RequiredString(Arguments(request), "storeId");
        if (_stores.Remove(storeId, out var store))
        {
            store.Dispose();
        }

        return Success(request.Id, 0, StoreStatus.Success.ToString(), new { storeId });
    }

    private AgentResponse Publish(AgentRequest request)
    {
        var arguments = Arguments(request);
        var status = Store(arguments).TryPublish(
            Bytes(arguments, "key"),
            Bytes(arguments, "value"),
            OptionalBytes(arguments, "descriptor"),
            Wait(arguments));
        return Status(request.Id, status);
    }

    private AgentResponse PublishSegments(AgentRequest request)
    {
        var arguments = Arguments(request);
        var segments = ReadSegments(arguments);
        var status = Store(arguments).TryPublishSegments(
            Bytes(arguments, "key"),
            segments,
            OptionalBytes(arguments, "descriptor"),
            Wait(arguments),
            out var copiedBytes);
        return Success(request.Id, (int)status, status.ToString(), new { copiedBytes });
    }

    private AgentResponse Acquire(AgentRequest request)
    {
        var arguments = Arguments(request);
        var leaseId = RequiredString(arguments, "leaseId");
        var status = Store(arguments).TryAcquire(Bytes(arguments, "key"), Wait(arguments), out var lease);
        if (status != StoreStatus.Success)
        {
            return Status(request.Id, status);
        }

        _leases[leaseId] = lease;
        return Success(request.Id, (int)status, status.ToString(), new
        {
            leaseId,
            value = AgentProtocol.EncodeBytes(lease.ValueSpan),
            descriptor = AgentProtocol.EncodeBytes(lease.DescriptorSpan)
        });
    }

    private AgentResponse Release(AgentRequest request)
    {
        var arguments = Arguments(request);
        var leaseId = RequiredString(arguments, "leaseId");
        var status = _leases.TryGetValue(leaseId, out var lease)
            ? lease.Release(Wait(arguments))
            : StoreStatus.InvalidLease;
        return Status(request.Id, status);
    }

    private AgentResponse Remove(AgentRequest request)
    {
        var arguments = Arguments(request);
        return Status(request.Id, Store(arguments).TryRemove(Bytes(arguments, "key"), Wait(arguments)));
    }

    private AgentResponse Reserve(AgentRequest request)
    {
        var arguments = Arguments(request);
        var reservationId = RequiredString(arguments, "reservationId");
        var status = Store(arguments).TryReserve(
            Bytes(arguments, "key"),
            RequiredInt32(arguments, "payloadLength"),
            OptionalBytes(arguments, "descriptor"),
            Wait(arguments),
            out var reservation);
        if (status == StoreStatus.Success)
        {
            _reservations[reservationId] = reservation;
        }

        return Success(request.Id, (int)status, status.ToString(), new { reservationId });
    }

    private AgentResponse ReservationWrite(AgentRequest request)
    {
        var arguments = Arguments(request);
        var reservationId = RequiredString(arguments, "reservationId");
        if (!_reservations.TryGetValue(reservationId, out var reservation))
        {
            return Status(request.Id, StoreStatus.InvalidReservation);
        }

        var data = Bytes(arguments, "data");
        var target = reservation.GetSpan(data.Length);
        if (target.Length < data.Length)
        {
            return Status(request.Id, StoreStatus.ReservationWriteOutOfRange);
        }

        data.CopyTo(target);
        return Success(request.Id, 0, StoreStatus.Success.ToString(), new { written = data.Length });
    }

    private AgentResponse Advance(AgentRequest request)
    {
        var arguments = Arguments(request);
        var reservation = Reservation(arguments);
        return Status(request.Id, reservation.Advance(RequiredInt32(arguments, "byteCount"), Wait(arguments)));
    }

    private AgentResponse Commit(AgentRequest request)
    {
        var arguments = Arguments(request);
        return Status(request.Id, Reservation(arguments).Commit(Wait(arguments)));
    }

    private AgentResponse Abort(AgentRequest request)
    {
        var arguments = Arguments(request);
        return Status(request.Id, Reservation(arguments).Abort(Wait(arguments)));
    }

    private AgentResponse RecoverLeases(AgentRequest request)
    {
        var arguments = Arguments(request);
        var status = Store(arguments).TryRecoverLeases(
            new LeaseRecoveryOptions(OptionalBoolean(arguments, "recoverCurrentProcess") ?? false),
            Wait(arguments),
            out var report);
        return Success(request.Id, (int)status, status.ToString(), report);
    }

    private AgentResponse RecoverReservations(AgentRequest request)
    {
        var arguments = Arguments(request);
        var status = Store(arguments).TryRecoverReservations(
            new ReservationRecoveryOptions(OptionalBoolean(arguments, "recoverCurrentProcess") ?? false),
            Wait(arguments),
            out var report);
        return Success(request.Id, (int)status, status.ToString(), report);
    }

    private AgentResponse Diagnostics(AgentRequest request)
    {
        var arguments = Arguments(request);
        var status = Store(arguments).TryGetDiagnostics(Wait(arguments), out var snapshot);
        return Success(request.Id, (int)status, status.ToString(), snapshot);
    }

    private static AgentResponse Crash()
    {
        Environment.Exit(97);
        throw new InvalidOperationException("Process termination returned unexpectedly.");
    }

    private MemoryStore Store(JsonElement arguments)
    {
        var id = RequiredString(arguments, "storeId");
        return _stores.TryGetValue(id, out var value)
            ? value
            : throw new KeyNotFoundException($"Store handle '{id}' does not exist.");
    }

    private ValueReservation Reservation(JsonElement arguments)
    {
        var id = RequiredString(arguments, "reservationId");
        return _reservations.TryGetValue(id, out var value)
            ? value
            : throw new KeyNotFoundException($"Reservation handle '{id}' does not exist.");
    }

    private static JsonElement Arguments(AgentRequest request) =>
        request.Arguments is { ValueKind: JsonValueKind.Object } arguments
            ? arguments
            : throw new JsonException("Command arguments are required.");

    private static string RequiredString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw new JsonException($"'{name}' must not be null.")
            : throw new JsonException($"'{name}' is required and must be a string.");

    private static int RequiredInt32(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : throw new JsonException($"'{name}' is required and must be a 32-bit integer.");

    private static long? OptionalInt64(JsonElement arguments, string name) =>
        !arguments.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null
            : property.TryGetInt64(out var value)
                ? value
                : throw new JsonException($"'{name}' must be a 64-bit integer.");

    private static bool? OptionalBoolean(JsonElement arguments, string name) =>
        !arguments.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null
            : property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : throw new JsonException($"'{name}' must be a boolean.");

    private static byte[] Bytes(JsonElement arguments, string name) =>
        AgentProtocol.DecodeBytes(RequiredString(arguments, name));

    private static byte[] OptionalBytes(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? AgentProtocol.DecodeBytes(property.GetString()!)
            : [];

    private static ReadOnlySequence<byte> ReadSegments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("segments", out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("'segments' is required and must be an array.");
        }

        BufferSegment? first = null;
        BufferSegment? last = null;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Every segment must be a base64 string.");
            }

            var bytes = AgentProtocol.DecodeBytes(item.GetString()!);
            last = last is null ? first = new BufferSegment(bytes) : last.Append(bytes);
        }

        return first is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private static OpenMode ParseOpenMode(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("openMode", out var property))
        {
            return OpenMode.CreateOrOpen;
        }

        if (property.TryGetInt32(out var numeric))
        {
            return (OpenMode)numeric;
        }

        return Enum.Parse<OpenMode>(property.GetString() ?? string.Empty, ignoreCase: true);
    }

    private static StoreWaitOptions Wait(JsonElement arguments)
    {
        var milliseconds = OptionalInt64(arguments, "timeoutMs") ?? 1000;
        return milliseconds switch
        {
            -1 => StoreWaitOptions.Infinite,
            0 => StoreWaitOptions.NoWait,
            < -1 => new StoreWaitOptions(TimeSpan.FromMilliseconds(-2)),
            _ => new StoreWaitOptions(TimeSpan.FromMilliseconds(milliseconds))
        };
    }

    private static AgentResponse Status(string id, StoreStatus status) =>
        Success(id, (int)status, status.ToString(), result: null);

    private static AgentResponse Success(string id, int code, string name, object? result) =>
        new()
        {
            Id = id,
            Ok = true,
            Status = new AgentStatus { Code = code, Name = name },
            Result = result is null ? null : AgentProtocol.ToJsonElement(result)
        };

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
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
}
