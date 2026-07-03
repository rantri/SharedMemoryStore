using System.Buffers;

namespace SharedMemoryStore.Ingest;

internal static class SegmentedPublisher
{
    public static StoreStatus Publish(
        MemoryStore store,
        ReadOnlySpan<byte> key,
        in ReadOnlySequence<byte> payload,
        ReadOnlySpan<byte> descriptor,
        StoreWaitOptions waitOptions,
        out long copiedBytes)
    {
        copiedBytes = 0;
        if (payload.Length > int.MaxValue)
        {
            return StoreStatus.ValueTooLarge;
        }

        var status = store.TryReserve(key, (int)payload.Length, descriptor, waitOptions, out var reservation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        try
        {
            foreach (var segment in payload)
            {
                var source = segment.Span;
                while (!source.IsEmpty)
                {
                    var target = reservation.GetSpan(source.Length);
                    if (target.IsEmpty)
                    {
                        _ = reservation.Abort(waitOptions);
                        return StoreStatus.ReservationWriteOutOfRange;
                    }

                    var copyLength = Math.Min(source.Length, target.Length);
                    source[..copyLength].CopyTo(target);
                    status = reservation.Advance(copyLength, waitOptions);
                    if (status != StoreStatus.Success)
                    {
                        _ = reservation.Abort(waitOptions);
                        return status;
                    }

                    copiedBytes += copyLength;
                    source = source[copyLength..];
                }
            }

            status = reservation.Commit(waitOptions);
            if (status != StoreStatus.Success)
            {
                _ = reservation.Abort(waitOptions);
            }

            return status;
        }
        catch
        {
            _ = reservation.Abort(waitOptions);
            return StoreStatus.UnknownFailure;
        }
    }
}
