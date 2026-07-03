using System.Runtime.InteropServices;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.ContractTests;

public sealed class SharedMemoryLayoutContractTests
{
    [Fact]
    public void SharedRecordsCarryFullSlotLifecycleIdentity()
    {
        Assert.Equal(2, LayoutConstants.LayoutMinorVersion);
        Assert.True(Marshal.SizeOf<SharedIndexEntryHeader>() >= 32);
        Assert.True(Marshal.SizeOf<SharedLeaseRecord>() >= 40);

        var next = new SlotLifecycleId(int.MaxValue, 7).Advance();
        Assert.Equal(1, next.Generation);
        Assert.Equal(8, next.ReuseEpoch);
    }
}
