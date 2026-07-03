namespace SharedMemoryStore.UnitTests.TestSupport;

internal static class ConcurrentOperationRunner
{
    public static void RunDisposalRace(int operationCount, Func<int, StoreStatus> operation, Action dispose)
    {
        var exceptions = new List<Exception>();
        using var start = new ManualResetEventSlim();

        var worker = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < operationCount; i++)
            {
                try
                {
                    _ = operation(i);
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        });

        var disposer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                dispose();
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        });

        start.Set();
        Task.WaitAll(worker, disposer);
        Assert.Empty(exceptions);
    }
}
