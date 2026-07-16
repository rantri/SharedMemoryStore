using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.LockFreeAgent;

/// <summary>
/// Production-no-op full-protocol visibility workers. The workers deliberately
/// use no checkpoint callback, diagnostics counter, file signal, or console
/// write between their common clock rendezvous and the terminal store value.
/// </summary>
internal static class RawVisibilityCommands
{
    private const int InvalidArgumentsExitCode = 64;
    private const int OperationFailureExitCode = 66;
    private const int ContentMismatchExitCode = 67;
    private const int TimeoutExitCode = 68;
    private const int PayloadHeaderLength = 96;
    private const int DescriptorLength = 48;
    private const ulong PayloadMagic = 0x3257_4152_534d_5301UL;
    private const ulong DescriptorMagic = 0x3252_4353_4544_5301UL;
    private static readonly TimeSpan WorkloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstRemovalDelay = TimeSpan.FromMilliseconds(500);

    internal static int RunPublisher(string[] arguments)
    {
        if (!Arguments.TryParse(arguments, out Arguments parsed))
        {
            return InvalidArgumentsExitCode;
        }

        StoreOpenStatus open = MemoryStore.TryCreateOrOpen(parsed.Options, out MemoryStore? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            return OperationFailureExitCode;
        }

        using (store)
        {
            WaitForCommonStart(parsed.StartUtcTicks);
            long deadline = Deadline();
            var spin = new SpinWait();
            long minimumGeneration = long.MaxValue;
            long maximumGeneration = 0;
            byte[][] keys = CreateDataKeys(parsed.Seed, parsed.KeyCount);
            var descriptor = new byte[DescriptorLength];
            for (var sequence = 1; sequence <= parsed.Iterations; sequence++)
            {
                int keyIndex = (sequence - 1) % parsed.KeyCount;
                byte[] key = keys[keyIndex];
                Pattern.FillDescriptor(descriptor, (ulong)sequence, key, keyIndex);
                while (true)
                {
                    StoreStatus reserve = store.TryReserve(
                        key,
                        parsed.PayloadLength,
                        descriptor,
                        out ValueReservation reservation);
                    if (reserve == StoreStatus.Success)
                    {
                        long generation = IndexBinding.Decode(
                            reservation.HandleForEngine.SlotBinding).Generation;
                        minimumGeneration = Math.Min(minimumGeneration, generation);
                        maximumGeneration = Math.Max(maximumGeneration, generation);
                        Span<byte> payload = reservation.GetSpan(parsed.PayloadLength);
                        if (payload.Length != parsed.PayloadLength)
                        {
                            _ = reservation.Abort();
                            return ContentMismatchExitCode;
                        }

                        Pattern.FillPayload(payload, (ulong)sequence, generation, key, keyIndex);
                        if (reservation.Advance(parsed.PayloadLength) != StoreStatus.Success
                            || reservation.Commit() != StoreStatus.Success)
                        {
                            return OperationFailureExitCode;
                        }

                        break;
                    }

                    if (!IsRetryablePublish(reserve))
                    {
                        return OperationFailureExitCode;
                    }

                    if (Expired(deadline))
                    {
                        return TimeoutExitCode;
                    }

                    spin.SpinOnce();
                }
            }

            byte[] terminalKey = Pattern.CreateKey(parsed.Seed, Pattern.TerminalKeyIndex);
            Pattern.FillDescriptor(
                descriptor,
                checked((ulong)parsed.Iterations + 1),
                terminalKey,
                Pattern.TerminalKeyIndex);
            while (true)
            {
                StoreStatus reserve = store.TryReserve(
                    terminalKey,
                    parsed.PayloadLength,
                    descriptor,
                    out ValueReservation reservation);
                if (reserve == StoreStatus.Success)
                {
                    long generation = IndexBinding.Decode(
                        reservation.HandleForEngine.SlotBinding).Generation;
                    Span<byte> payload = reservation.GetSpan(parsed.PayloadLength);
                    if (payload.Length != parsed.PayloadLength)
                    {
                        _ = reservation.Abort();
                        return ContentMismatchExitCode;
                    }

                    Pattern.FillPayload(
                        payload,
                        checked((ulong)parsed.Iterations + 1),
                        generation,
                        terminalKey,
                        Pattern.TerminalKeyIndex);
                    if (reservation.Advance(parsed.PayloadLength) != StoreStatus.Success
                        || reservation.Commit() != StoreStatus.Success)
                    {
                        return OperationFailureExitCode;
                    }

                    break;
                }

                if (!IsRetryablePublish(reserve))
                {
                    return OperationFailureExitCode;
                }

                if (Expired(deadline))
                {
                    return TimeoutExitCode;
                }

                spin.SpinOnce();
            }

            WriteResult(new Result(
                "publisher",
                parsed.Iterations,
                parsed.Iterations,
                0,
                minimumGeneration,
                maximumGeneration));
            return 0;
        }
    }

    internal static int RunReader(string[] arguments)
    {
        if (!Arguments.TryParse(arguments, out Arguments parsed))
        {
            return InvalidArgumentsExitCode;
        }

        StoreOpenStatus open = MemoryStore.TryCreateOrOpen(parsed.Options, out MemoryStore? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            return OperationFailureExitCode;
        }

        using (store)
        {
            WaitForCommonStart(parsed.StartUtcTicks);
            long deadline = Deadline();
            long observations = 0;
            ulong checksum = 14_695_981_039_346_656_037UL;
            long minimumGeneration = long.MaxValue;
            long maximumGeneration = 0;
            var spin = new SpinWait();
            var scanStart = 0;
            byte[][] keys = CreateDataKeys(parsed.Seed, parsed.KeyCount);
            byte[] terminalKey = Pattern.CreateKey(parsed.Seed, Pattern.TerminalKeyIndex);
            while (true)
            {
                for (var offset = 0; offset < parsed.KeyCount; offset++)
                {
                    int keyIndex = (scanStart + offset) % parsed.KeyCount;
                    byte[] key = keys[keyIndex];
                    StoreStatus acquire = store.TryAcquire(key, out ValueLease lease);
                    if (acquire == StoreStatus.Success)
                    {
                        if (!Pattern.ValidateLease(
                                lease,
                                key,
                                keyIndex,
                                parsed.KeyCount,
                                parsed.PayloadLength,
                                parsed.Iterations,
                                out ulong sequence))
                        {
                            _ = lease.Release();
                            return ContentMismatchExitCode;
                        }

                        checksum = unchecked((checksum ^ sequence) * 1_099_511_628_211UL);
                        long generation = IndexBinding.Decode(lease.HandleForEngine.SlotBinding).Generation;
                        minimumGeneration = Math.Min(minimumGeneration, generation);
                        maximumGeneration = Math.Max(maximumGeneration, generation);
                        observations++;
                        if (lease.Release() != StoreStatus.Success)
                        {
                            return OperationFailureExitCode;
                        }
                    }
                    else if (!IsRetryableAcquire(acquire))
                    {
                        return OperationFailureExitCode;
                    }
                }

                StoreStatus terminalAcquire = store.TryAcquire(terminalKey, out ValueLease terminalLease);
                if (terminalAcquire == StoreStatus.Success)
                {
                    bool valid = Pattern.ValidateLease(
                        terminalLease,
                        terminalKey,
                        Pattern.TerminalKeyIndex,
                        parsed.KeyCount,
                        parsed.PayloadLength,
                        parsed.Iterations,
                        out ulong sequence);
                    StoreStatus release = terminalLease.Release();
                    if (!valid
                        || sequence != checked((ulong)parsed.Iterations + 1)
                        || release != StoreStatus.Success)
                    {
                        return ContentMismatchExitCode;
                    }

                    if (observations == 0)
                    {
                        return ContentMismatchExitCode;
                    }

                    WriteResult(new Result(
                        "reader",
                        1,
                        observations,
                        checksum,
                        minimumGeneration,
                        maximumGeneration));
                    return 0;
                }

                if (!IsRetryableAcquire(terminalAcquire))
                {
                    return OperationFailureExitCode;
                }

                if (Expired(deadline))
                {
                    return TimeoutExitCode;
                }

                scanStart = (scanStart + 1) % parsed.KeyCount;
                spin.SpinOnce();
            }
        }
    }

    internal static int RunRemover(string[] arguments)
    {
        if (!Arguments.TryParse(arguments, out Arguments parsed))
        {
            return InvalidArgumentsExitCode;
        }

        StoreOpenStatus open = MemoryStore.TryCreateOrOpen(parsed.Options, out MemoryStore? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            return OperationFailureExitCode;
        }

        using (store)
        {
            WaitForCommonStart(parsed.StartUtcTicks);
            WaitUntilUtc(checked(parsed.StartUtcTicks + FirstRemovalDelay.Ticks));
            long deadline = Deadline();
            var removed = new bool[parsed.Iterations + 1];
            var spin = new SpinWait();
            var scanStart = 0;
            var removedCount = 0;
            ulong checksum = 14_695_981_039_346_656_037UL;
            long minimumGeneration = long.MaxValue;
            long maximumGeneration = 0;
            byte[][] keys = CreateDataKeys(parsed.Seed, parsed.KeyCount);
            while (removedCount < parsed.Iterations)
            {
                for (var offset = 0; offset < parsed.KeyCount && removedCount < parsed.Iterations; offset++)
                {
                    int keyIndex = (scanStart + offset) % parsed.KeyCount;
                    byte[] key = keys[keyIndex];
                    StoreStatus acquire = store.TryAcquire(key, out ValueLease lease);
                    if (acquire == StoreStatus.Success)
                    {
                        if (!Pattern.ValidateLease(
                                lease,
                                key,
                                keyIndex,
                                parsed.KeyCount,
                                parsed.PayloadLength,
                                parsed.Iterations,
                                out ulong sequence)
                            || sequence == 0
                            || sequence > (ulong)parsed.Iterations
                            || removed[checked((int)sequence)])
                        {
                            _ = lease.Release();
                            return ContentMismatchExitCode;
                        }

                        if (lease.Release() != StoreStatus.Success)
                        {
                            return OperationFailureExitCode;
                        }

                        StoreStatus remove = store.TryRemove(key);
                        if (remove is StoreStatus.Success or StoreStatus.RemovePending)
                        {
                            long generation = IndexBinding.Decode(lease.HandleForEngine.SlotBinding).Generation;
                            minimumGeneration = Math.Min(minimumGeneration, generation);
                            maximumGeneration = Math.Max(maximumGeneration, generation);
                            removed[checked((int)sequence)] = true;
                            removedCount++;
                            checksum = unchecked((checksum ^ sequence) * 1_099_511_628_211UL);
                        }
                        else if (remove != StoreStatus.StoreBusy)
                        {
                            return OperationFailureExitCode;
                        }
                    }
                    else if (!IsRetryableAcquire(acquire))
                    {
                        return OperationFailureExitCode;
                    }
                }

                if (Expired(deadline))
                {
                    return TimeoutExitCode;
                }

                scanStart = (scanStart + 1) % parsed.KeyCount;
                spin.SpinOnce();
            }

            WriteResult(new Result(
                "remover",
                parsed.Iterations,
                removedCount,
                checksum,
                minimumGeneration,
                maximumGeneration));
            return 0;
        }
    }

    private static bool IsRetryablePublish(StoreStatus status) =>
        status is StoreStatus.DuplicateKey or StoreStatus.StoreFull or StoreStatus.StoreBusy;

    private static bool IsRetryableAcquire(StoreStatus status) =>
        status is StoreStatus.NotFound or StoreStatus.StoreBusy or StoreStatus.LeaseTableFull;

    private static byte[][] CreateDataKeys(int seed, int keyCount)
    {
        var keys = new byte[keyCount][];
        for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            keys[keyIndex] = Pattern.CreateKey(seed, keyIndex);
        }

        return keys;
    }

    private static long Deadline() =>
        Stopwatch.GetTimestamp() + (long)(WorkloadTimeout.TotalSeconds * Stopwatch.Frequency);

    private static bool Expired(long deadline) => Stopwatch.GetTimestamp() >= deadline;

    private static void WaitForCommonStart(long utcTicks) => WaitUntilUtc(utcTicks);

    private static void WaitUntilUtc(long utcTicks)
    {
        var spin = new SpinWait();
        while (DateTime.UtcNow.Ticks < utcTicks)
        {
            spin.SpinOnce();
        }
    }

    private static void WriteResult(in Result result) =>
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(result));

    private readonly record struct Result(
        string Role,
        int Completed,
        long Observations,
        ulong Checksum,
        long MinimumGeneration,
        long MaximumGeneration);

    private readonly record struct Arguments(
        SharedMemoryStoreOptions Options,
        int Iterations,
        int KeyCount,
        int PayloadLength,
        int Seed,
        long StartUtcTicks)
    {
        internal static bool TryParse(string[] arguments, out Arguments parsed)
        {
            parsed = default;
            if (arguments.Length != 13
                || string.IsNullOrWhiteSpace(arguments[1])
                || !TryPositive(arguments[2], out int slotCount)
                || !TryPositive(arguments[3], out int maxValueBytes)
                || !TryNonNegative(arguments[4], out int maxDescriptorBytes)
                || !TryPositive(arguments[5], out int maxKeyBytes)
                || !TryPositive(arguments[6], out int leaseRecordCount)
                || !TryPositive(arguments[7], out int participantRecordCount)
                || !TryPositive(arguments[8], out int iterations)
                || !TryPositive(arguments[9], out int keyCount)
                || !TryPositive(arguments[10], out int payloadLength)
                || !int.TryParse(arguments[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)
                || !long.TryParse(arguments[12], NumberStyles.None, CultureInfo.InvariantCulture, out long startUtcTicks)
                || slotCount > 1_048_575
                || keyCount > 1_024
                || iterations > 1_000_000
                || payloadLength < PayloadHeaderLength
                || payloadLength > maxValueBytes
                || maxDescriptorBytes < DescriptorLength
                || maxKeyBytes < Pattern.KeyLength
                || startUtcTicks <= 0)
            {
                return false;
            }

            parsed = new Arguments(
                SharedMemoryStoreOptions.CreateLockFree(
                    arguments[1],
                    slotCount,
                    maxValueBytes,
                    maxDescriptorBytes,
                    maxKeyBytes,
                    leaseRecordCount,
                    participantRecordCount,
                    OpenMode.OpenExisting,
                    enableLeaseRecovery: true),
                iterations,
                keyCount,
                payloadLength,
                seed,
                startUtcTicks);
            return true;
        }

        private static bool TryPositive(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

        private static bool TryNonNegative(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static class Pattern
    {
        internal const int KeyLength = 16;
        internal const int TerminalKeyIndex = -1;

        internal static byte[] CreateKey(int seed, int keyIndex)
        {
            var key = new byte[KeyLength];
            BinaryPrimitives.WriteInt32LittleEndian(key, seed);
            BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(4), keyIndex);
            ulong identity = Mix(unchecked((uint)seed) | ((ulong)unchecked((uint)keyIndex) << 32));
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), identity);
            return key;
        }

        internal static void FillDescriptor(
            Span<byte> descriptor,
            ulong sequence,
            ReadOnlySpan<byte> key,
            int keyIndex)
        {
            ulong signature = KeySignature(key);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor, DescriptorMagic);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[8..], sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[16..], ~sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[24..], signature);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[32..], ~signature);
            BinaryPrimitives.WriteInt32LittleEndian(descriptor[40..], keyIndex);
            BinaryPrimitives.WriteInt32LittleEndian(descriptor[44..], ~keyIndex);
        }

        internal static void FillPayload(
            Span<byte> payload,
            ulong sequence,
            long generation,
            ReadOnlySpan<byte> key,
            int keyIndex)
        {
            ulong signature = KeySignature(key);
            BinaryPrimitives.WriteUInt64LittleEndian(payload, PayloadMagic);
            BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(payload[16..], ~sequence);
            BinaryPrimitives.WriteInt64LittleEndian(payload[24..], generation);
            BinaryPrimitives.WriteInt64LittleEndian(payload[32..], ~generation);
            BinaryPrimitives.WriteUInt64LittleEndian(payload[40..], signature);
            BinaryPrimitives.WriteUInt64LittleEndian(payload[48..], ~signature);
            BinaryPrimitives.WriteInt64LittleEndian(payload[56..], payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(payload[64..], ~((long)payload.Length));
            BinaryPrimitives.WriteInt32LittleEndian(payload[72..], keyIndex);
            BinaryPrimitives.WriteInt32LittleEndian(payload[76..], ~keyIndex);
            key.CopyTo(payload[80..(80 + KeyLength)]);
            for (var offset = PayloadHeaderLength; offset < payload.Length; offset++)
            {
                payload[offset] = PayloadByte(sequence, generation, signature, offset);
            }
        }

        internal static bool ValidateLease(
            in ValueLease lease,
            ReadOnlySpan<byte> expectedKey,
            int expectedKeyIndex,
            int keyCount,
            int expectedPayloadLength,
            int dataIterations,
            out ulong sequence)
        {
            sequence = 0;
            ReadOnlySpan<byte> payload = lease.ValueSpan;
            if (lease.ValueLength != expectedPayloadLength
                || payload.Length != expectedPayloadLength
                || lease.DescriptorLength != DescriptorLength
                || payload.Length < PayloadHeaderLength
                || BinaryPrimitives.ReadUInt64LittleEndian(payload) != PayloadMagic)
            {
                return false;
            }

            sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]);
            long generation = BinaryPrimitives.ReadInt64LittleEndian(payload[24..]);
            ulong signature = KeySignature(expectedKey);
            if (sequence == 0
                || sequence > checked((ulong)dataIterations + 1)
                || BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]) != ~sequence
                || generation <= 0
                || BinaryPrimitives.ReadInt64LittleEndian(payload[32..]) != ~generation
                || BinaryPrimitives.ReadUInt64LittleEndian(payload[40..]) != signature
                || BinaryPrimitives.ReadUInt64LittleEndian(payload[48..]) != ~signature
                || BinaryPrimitives.ReadInt64LittleEndian(payload[56..]) != expectedPayloadLength
                || BinaryPrimitives.ReadInt64LittleEndian(payload[64..]) != ~((long)expectedPayloadLength)
                || BinaryPrimitives.ReadInt32LittleEndian(payload[72..]) != expectedKeyIndex
                || BinaryPrimitives.ReadInt32LittleEndian(payload[76..]) != ~expectedKeyIndex
                || !payload[80..(80 + KeyLength)].SequenceEqual(expectedKey)
                || IndexBinding.Decode(lease.HandleForEngine.SlotBinding).Generation != generation)
            {
                return false;
            }

            ReadOnlySpan<byte> descriptor = lease.DescriptorSpan;
            if (BinaryPrimitives.ReadUInt64LittleEndian(descriptor) != DescriptorMagic
                || BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]) != sequence
                || BinaryPrimitives.ReadUInt64LittleEndian(descriptor[16..]) != ~sequence
                || BinaryPrimitives.ReadUInt64LittleEndian(descriptor[24..]) != signature
                || BinaryPrimitives.ReadUInt64LittleEndian(descriptor[32..]) != ~signature
                || BinaryPrimitives.ReadInt32LittleEndian(descriptor[40..]) != expectedKeyIndex
                || BinaryPrimitives.ReadInt32LittleEndian(descriptor[44..]) != ~expectedKeyIndex)
            {
                return false;
            }

            for (var offset = PayloadHeaderLength; offset < payload.Length; offset++)
            {
                if (payload[offset] != PayloadByte(sequence, generation, signature, offset))
                {
                    return false;
                }
            }

            return expectedKeyIndex == TerminalKeyIndex
                ? sequence == checked((ulong)dataIterations + 1)
                : sequence <= (ulong)dataIterations
                    && expectedKeyIndex == checked((int)((sequence - 1) % (ulong)keyCount));
        }

        private static byte PayloadByte(ulong sequence, long generation, ulong signature, int offset)
        {
            ulong value = sequence
                ^ unchecked((ulong)generation * 0x9e37_79b9_7f4a_7c15UL)
                ^ signature
                ^ unchecked((ulong)offset * 0xd6e8_feb8_6659_fd93UL);
            return (byte)(Mix(value) >> ((offset & 7) * 8));
        }

        private static ulong KeySignature(ReadOnlySpan<byte> key)
        {
            ulong hash = 14_695_981_039_346_656_037UL;
            foreach (byte value in key)
            {
                hash = unchecked((hash ^ value) * 1_099_511_628_211UL);
            }

            return hash;
        }

        private static ulong Mix(ulong value)
        {
            value ^= value >> 30;
            value *= 0xbf58_476d_1ce4_e5b9UL;
            value ^= value >> 27;
            value *= 0x94d0_49bb_1331_11ebUL;
            return value ^ (value >> 31);
        }
    }
}
