using System.Buffers;
using System.Reflection;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.ContractTests;

public sealed class ReservationApiContractTests
{
    [Fact]
    public void TryReserveAndValueReservationMembersMatchContract()
    {
        var reserve = typeof(Store).GetMethod(nameof(Store.TryReserve), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(reserve);
        Assert.Equal(typeof(StoreStatus), reserve.ReturnType);

        var parameters = reserve.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
        Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[2].ParameterType);
        Assert.Equal(typeof(ValueReservation).MakeByRefType(), parameters[3].ParameterType);

        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ValueReservation)));
        Assert.NotNull(typeof(ValueReservation).GetProperty(nameof(ValueReservation.IsValid)));
        Assert.NotNull(typeof(ValueReservation).GetProperty(nameof(ValueReservation.PayloadLength)));
        Assert.NotNull(typeof(ValueReservation).GetProperty(nameof(ValueReservation.BytesWritten)));
        Assert.NotNull(typeof(ValueReservation).GetProperty(nameof(ValueReservation.RemainingBytes)));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.GetSpan), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.GetMemory), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.Advance), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.Commit), BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.Abort), BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void TryPublishSegmentsAndRecoveryMembersMatchContract()
    {
        var publishSegments = typeof(Store).GetMethod(nameof(Store.TryPublishSegments), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(publishSegments);
        var segmentParameters = publishSegments.GetParameters();
        Assert.Equal(typeof(ReadOnlySpan<byte>), segmentParameters[0].ParameterType);
        Assert.Equal(typeof(ReadOnlySequence<byte>).MakeByRefType(), segmentParameters[1].ParameterType);
        Assert.True(segmentParameters[1].IsIn);
        Assert.Equal(typeof(ReadOnlySpan<byte>), segmentParameters[2].ParameterType);
        Assert.Equal(typeof(long).MakeByRefType(), segmentParameters[3].ParameterType);

        var recover = typeof(Store).GetMethod(nameof(Store.TryRecoverReservations), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(recover);
        Assert.Equal(typeof(ReservationRecoveryOptions), recover.GetParameters()[0].ParameterType.GetElementType());
        Assert.Equal(typeof(ReservationRecoveryReport).MakeByRefType(), recover.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void SegmentedPublishStoresLogicalSequence()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options());
        var first = new byte[] { 1, 2 };
        var second = new byte[] { 3, 4, 5 };
        var sequence = SequenceFactory.Create(first, second);

        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([9], sequence, [7], out var copied));
        Assert.Equal(5, copied);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([9], out var lease));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 7 }, lease.DescriptorSpan.ToArray());
        lease.Dispose();
    }

    private static class SequenceFactory
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
