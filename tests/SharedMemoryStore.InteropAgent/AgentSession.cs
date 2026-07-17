using System.Buffers;
using System.Text.Json;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.InteropAgent;

internal sealed class AgentSession : IDisposable
{
    private readonly Dictionary<string, MemoryStore> _stores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedMemoryStoreOptions> _storeOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueReservation> _reservations = new(StringComparer.Ordinal);
    private AgentCheckpointOperation? _checkpointOperation;
    private AgentColdLock? _coldLock;

    public AgentResponse Handle(AgentRequest request)
    {
        try
        {
            return request.Command switch
            {
                "ping" => Success(request.Id, 0, "Success", new
                {
                    runtime = "dotnet",
                    protocolVersion = 2,
                    checkpointCatalogVersion = 1,
                    layoutMajorVersion = 2,
                    layoutMinorVersion = 0,
                    resourceProtocolVersion = 2,
                    requiredFeatures = 7UL,
                    optionalFeatures = 0UL
                }),
                "open" => Open(request),
                "close" => Close(request),
                "publish" => Publish(request),
                "publishSegments" or "publishSegmented" => PublishSegments(request),
                "acquire" => Acquire(request),
                "read" => Read(request),
                "checksum" => Checksum(request),
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
                "checkpointCatalog" => CheckpointCatalog(request),
                "pauseAtCheckpoint" => BeginCheckpoint(request, crash: false),
                "resumeCheckpoint" => CompleteCheckpoint(request, cancel: false),
                "cancelCheckpoint" => CompleteCheckpoint(request, cancel: true),
                "crashAtCheckpoint" => BeginCheckpoint(request, crash: true),
                "injectRawFault" => InjectRawFault(request),
                "holdColdLock" => HoldColdLock(request),
                "releaseColdLock" => ReleaseColdLock(request),
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
        _checkpointOperation?.Dispose();
        _checkpointOperation = null;
        _coldLock?.Dispose();
        _coldLock = null;

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
        _storeOptions.Clear();
    }

    private AgentResponse Open(AgentRequest request)
    {
        var arguments = Arguments(request);
        var storeId = RequiredString(arguments, "storeId");
        if (_stores.Remove(storeId, out var previous))
        {
            previous.Dispose();
        }
        _storeOptions.Remove(storeId);

        SharedMemoryStoreOptions options;
        try
        {
            options = ReadOptions(arguments);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Success(
                request.Id,
                (int)StoreOpenStatus.InvalidOptions,
                StoreOpenStatus.InvalidOptions.ToString(),
                result: null);
        }
        var status = MemoryStore.TryCreateOrOpen(options, Wait(arguments), out var store);
        if (status == StoreOpenStatus.Success && store is not null)
        {
            _stores.Add(storeId, store);
            _storeOptions.Add(storeId, options);
        }

        return Success(
            request.Id,
            (int)status,
            status.ToString(),
            store is null
                ? null
                : new
                {
                    storeId,
                    participantRecordCount = options.ParticipantRecordCount,
                    protocolInfo = store.ProtocolInfo
                });
    }

    private AgentResponse Close(AgentRequest request)
    {
        var storeId = RequiredString(Arguments(request), "storeId");
        if (_stores.Remove(storeId, out var store))
        {
            store.Dispose();
        }
        _storeOptions.Remove(storeId);

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

    private AgentResponse Read(AgentRequest request)
    {
        var leaseId = RequiredString(Arguments(request), "leaseId");
        if (!_leases.TryGetValue(leaseId, out var lease) || !lease.IsValid)
        {
            return Status(request.Id, StoreStatus.InvalidLease);
        }

        return Success(request.Id, 0, StoreStatus.Success.ToString(), new
        {
            leaseId,
            value = AgentProtocol.EncodeBytes(lease.ValueSpan),
            descriptor = AgentProtocol.EncodeBytes(lease.DescriptorSpan)
        });
    }

    private AgentResponse Checksum(AgentRequest request)
    {
        string leaseId = RequiredString(Arguments(request), "leaseId");
        if (!_leases.TryGetValue(leaseId, out ValueLease lease) || !lease.IsValid)
        {
            return Status(request.Id, StoreStatus.InvalidLease);
        }

        return Success(request.Id, 0, StoreStatus.Success.ToString(), new
        {
            leaseId,
            valueLength = lease.ValueSpan.Length,
            descriptorLength = lease.DescriptorSpan.Length,
            valueChecksum = Fnv1a64(lease.ValueSpan),
            descriptorChecksum = Fnv1a64(lease.DescriptorSpan)
        });
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
        string storeId = RequiredString(arguments, "storeId");
        var status = Store(arguments).TryGetDiagnostics(Wait(arguments), out var snapshot);
        if (status != StoreStatus.Success)
        {
            return Status(request.Id, status);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["storeId"] = storeId
        };
        foreach (JsonProperty property in AgentProtocol.ToJsonElement(snapshot).EnumerateObject())
        {
            result.Add(property.Name, property.Value.Clone());
        }

        result.Add(
            "failureCounts",
            Enum.GetValues<StoreStatus>()
                .Select(snapshot.GetFailureCount)
                .ToArray());
        return Success(request.Id, 0, status.ToString(), result);
    }

    private static AgentResponse CheckpointCatalog(AgentRequest request)
    {
        object[] checkpoints = LockFreeCheckpointCatalog.Entries
            .Select(entry => (object)new
            {
                id = (int)entry.Id,
                name = entry.Id.ToString(),
                family = entry.Family.ToString(),
                position = entry.Position.ToString(),
                pause = entry.Pause.ToString(),
                crash = entry.Crash.ToString(),
                race = entry.Race.ToString(),
                isPublicOrderingPoint = entry.IsPublicOrderingPoint,
                description = entry.Description
            })
            .ToArray();
        return Success(request.Id, 0, StoreStatus.Success.ToString(), new
        {
            checkpointCatalogVersion = 1,
            checkpoints
        });
    }

    private AgentResponse BeginCheckpoint(AgentRequest request, bool crash)
    {
        if (_checkpointOperation is not null)
        {
            return AgentHost.Failure(
                request.Id,
                -3,
                "CheckpointAlreadyArmed",
                "checkpoint_already_armed",
                "One managed checkpoint operation is already paused.");
        }

        JsonElement arguments = Arguments(request);
        int checkpointValue = RequiredInt32(arguments, "checkpointId");
        int occurrence = checked((int)(OptionalInt64(arguments, "occurrence") ?? 1));
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);
        var checkpoint = (LockFreeCheckpointId)checkpointValue;
        _ = LockFreeCheckpointCatalog.Get(checkpoint);
        var spec = new AgentCheckpointSpec(
            checkpoint,
            occurrence,
            RequiredString(arguments, "operation"),
            ReadOptions(arguments),
            OptionalBytes(arguments, "key"),
            OptionalBytes(arguments, "value"),
            OptionalBytes(arguments, "descriptor"));
        var operation = new AgentCheckpointOperation(spec);
        _checkpointOperation = operation;
        if (!operation.WaitUntilPaused(TimeSpan.FromSeconds(10)))
        {
            AgentCheckpointCompletion completion = operation.Complete(cancel: true);
            operation.Dispose();
            _checkpointOperation = null;
            return AgentHost.Failure(
                request.Id,
                -4,
                "CheckpointNotReached",
                "checkpoint_not_reached",
                $"Checkpoint {checkpoint} was not reached; open={completion.OpenStatus}, operation={completion.Status}.");
        }

        LockFreeCheckpointEntry reached = operation.Reached
            ?? throw new InvalidOperationException("The checkpoint gate signaled without an entry.");
        if (crash)
        {
            Environment.Exit(97);
            throw new InvalidOperationException("Process termination returned unexpectedly.");
        }

        return Success(request.Id, 0, StoreStatus.Success.ToString(), CheckpointResult(reached, spec.Operation));
    }

    private AgentResponse CompleteCheckpoint(AgentRequest request, bool cancel)
    {
        AgentCheckpointOperation? operation = _checkpointOperation;
        if (operation is null)
        {
            return AgentHost.Failure(
                request.Id,
                -5,
                "CheckpointNotArmed",
                "checkpoint_not_armed",
                "No managed checkpoint operation is currently paused.");
        }

        _checkpointOperation = null;
        LockFreeCheckpointEntry reached = operation.Reached
            ?? throw new InvalidOperationException("The paused checkpoint has no entry.");
        AgentCheckpointCompletion completion;
        try
        {
            completion = operation.Complete(cancel);
        }
        finally
        {
            operation.Dispose();
        }

        return Success(request.Id, (int)completion.Status, completion.Status.ToString(), new
        {
            checkpoint = CheckpointResult(reached, operation: null),
            canceled = cancel,
            openStatus = new { code = (int)completion.OpenStatus, name = completion.OpenStatus.ToString() }
        });
    }

    private AgentResponse InjectRawFault(AgentRequest request)
    {
        JsonElement arguments = Arguments(request);
        string storeId = RequiredString(arguments, "storeId");
        if (!_storeOptions.TryGetValue(storeId, out SharedMemoryStoreOptions? options))
        {
            throw new KeyNotFoundException($"Store handle '{storeId}' does not exist.");
        }

        string target = RequiredString(arguments, "target");
        AgentRawFaultResult result = target switch
        {
            "directoryMutation" => AgentRawFaults.InjectDirectoryMutation(options),
            "participantProcessId" => AgentRawFaults.ReplaceParticipantProcessId(
                options,
                RequiredInt32(arguments, "targetProcessId"),
                RequiredInt32(arguments, "replacementProcessId")),
            "participantNamespace" => AgentRawFaults.ReplaceParticipantNamespace(
                options,
                RequiredInt32(arguments, "targetProcessId"),
                RequiredUInt64(arguments, "replacementPidNamespaceId")),
            "headerNamespace" => AgentRawFaults.ReplaceHeaderNamespace(
                options,
                RequiredUInt64(arguments, "replacementPidNamespaceId")),
            "layoutMajorVersion" => AgentRawFaults.ReplaceLayoutMajorVersion(
                options,
                RequiredUInt16(arguments, "replacementLayoutMajorVersion")),
            "requiredFeatures" => AgentRawFaults.ReplaceRequiredFeatures(
                options,
                RequiredUInt64(arguments, "replacementRequiredFeatures")),
            _ => throw new JsonException($"Unknown raw fault target '{target}'.")
        };
        return Success(request.Id, 0, StoreStatus.Success.ToString(), result);
    }

    private AgentResponse HoldColdLock(AgentRequest request)
    {
        if (_coldLock is not null)
        {
            return AgentHost.Failure(
                request.Id,
                -6,
                "ColdLockAlreadyHeld",
                "cold_lock_already_held",
                "This agent already holds a cold synchronization resource.");
        }

        string name = RequiredString(Arguments(request), "name");
        try
        {
            _coldLock = AgentColdLock.Acquire(name);
            return Success(request.Id, 0, StoreStatus.Success.ToString(), new { name });
        }
        catch (Exception exception)
        {
            return AgentHost.Failure(
                request.Id,
                -7,
                "ColdLockFailed",
                "cold_lock_failed",
                exception.Message);
        }
    }

    private AgentResponse ReleaseColdLock(AgentRequest request)
    {
        AgentColdLock? coldLock = _coldLock;
        if (coldLock is null)
        {
            return AgentHost.Failure(
                request.Id,
                -8,
                "ColdLockNotHeld",
                "cold_lock_not_held",
                "This agent does not hold a cold synchronization resource.");
        }

        _coldLock = null;
        coldLock.Dispose();
        return Success(request.Id, 0, StoreStatus.Success.ToString(), new { released = true });
    }

    private static object CheckpointResult(LockFreeCheckpointEntry entry, string? operation) => new
    {
        checkpointId = (int)entry.Id,
        checkpointName = entry.Id.ToString(),
        family = entry.Family.ToString(),
        position = entry.Position.ToString(),
        operation,
        processId = Environment.ProcessId
    };

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

    private static ulong RequiredUInt64(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var property) && property.TryGetUInt64(out ulong value)
            ? value
            : throw new JsonException($"'{name}' is required and must be an unsigned 64-bit integer.");

    private static ushort RequiredUInt16(JsonElement arguments, string name)
    {
        int value = RequiredInt32(arguments, name);
        if ((uint)value > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must fit an unsigned 16-bit integer.");
        }

        return (ushort)value;
    }

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

    private static SharedMemoryStoreOptions ReadOptions(JsonElement arguments)
    {
        int slotCount = RequiredInt32(arguments, "slotCount");
        int maxValueBytes = RequiredInt32(arguments, "maxValueBytes");
        int maxDescriptorBytes = RequiredInt32(arguments, "maxDescriptorBytes");
        int maxKeyBytes = RequiredInt32(arguments, "maxKeyBytes");
        int leaseRecordCount = RequiredInt32(arguments, "leaseRecordCount");
        int participantRecordCount = RequiredInt32(arguments, "participantRecordCount");
        long totalBytes = OptionalInt64(arguments, "totalBytes")
            ?? SharedMemoryStoreOptions.CalculateRequiredBytes(
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount,
                participantRecordCount);
        return new SharedMemoryStoreOptions
        {
            Name = RequiredString(arguments, "name"),
            OpenMode = ParseOpenMode(arguments),
            TotalBytes = totalBytes,
            SlotCount = slotCount,
            MaxValueBytes = maxValueBytes,
            MaxDescriptorBytes = maxDescriptorBytes,
            MaxKeyBytes = maxKeyBytes,
            LeaseRecordCount = leaseRecordCount,
            ParticipantRecordCount = participantRecordCount,
            EnableLeaseRecovery = OptionalBoolean(arguments, "enableLeaseRecovery") ?? false
        };
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

    private static string Fnv1a64(ReadOnlySpan<byte> value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong checksum = offsetBasis;
        foreach (byte item in value)
        {
            checksum = unchecked((checksum ^ item) * prime);
        }

        return checksum.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
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
