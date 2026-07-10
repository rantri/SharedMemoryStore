using SharedMemoryStore.InteropAgent;

return await AgentHost.RunAsync(Console.In, Console.Out).ConfigureAwait(false);
