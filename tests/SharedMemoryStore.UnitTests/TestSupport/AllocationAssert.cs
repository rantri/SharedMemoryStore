namespace SharedMemoryStore.UnitTests.TestSupport;

internal static class AllocationAssert
{
    public static void NoAllocAfterWarmup(Func<StoreStatus> operation)
    {
        operation();
        operation();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = operation();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(StoreStatus.Success, status);
        Assert.Equal(0, allocated);
    }
}
