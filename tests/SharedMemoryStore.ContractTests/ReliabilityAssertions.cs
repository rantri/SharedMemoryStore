namespace SharedMemoryStore.ContractTests;

internal static class ReliabilityAssertions
{
    public static void AssertDisposedOutcome(StoreStatus status)
    {
        Assert.Equal(StoreStatus.StoreDisposed, status);
    }

    public static void AssertNoInternalLifecycleFailure(Action operation)
    {
        var exception = Record.Exception(operation);
        Assert.Null(exception);
    }
}
