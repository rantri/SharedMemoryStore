using System.Reflection;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.ContractTests;

public sealed class FrameNeutralContractTests
{
    [Fact]
    public void PublicApiDoesNotExposeFrameSpecificMembers()
    {
        var publicMembers = typeof(Store).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        Assert.DoesNotContain(publicMembers, member => member.Name.Contains("Frame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FrameShapedBytesUseSamePublicApiAsOtherValues()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options(maxValueBytes: 32, maxDescriptorBytes: 8));
        var descriptor = new byte[] { 1, 0, 16, 0 };
        var framePayload = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var otherPayload = new byte[] { 99, 100 };

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], framePayload, descriptor));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], otherPayload));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var frameLease));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var otherLease));

        Assert.Equal(framePayload, frameLease.ValueSpan.ToArray());
        Assert.Equal(otherPayload, otherLease.ValueSpan.ToArray());
        frameLease.Dispose();
        otherLease.Dispose();
    }
}
