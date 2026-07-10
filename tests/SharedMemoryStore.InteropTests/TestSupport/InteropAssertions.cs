using SharedMemoryStore.InteropAgent;

namespace SharedMemoryStore.InteropTests.TestSupport;

internal static class InteropAssertions
{
    public static readonly string[] Runtimes = ["dotnet", "cpp", "python"];

    public static object OpenArguments(
        string storeId,
        string name,
        int openMode,
        bool enableLeaseRecovery = true,
        int slotCount = 6,
        int maxValueBytes = 128,
        int maxDescriptorBytes = 32,
        int maxKeyBytes = 32,
        int leaseRecordCount = 16) => new
        {
            storeId,
            name,
            openMode,
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            enableLeaseRecovery
        };

    public static void Success(AgentResponse response)
    {
        Assert.True(response.Ok, response.Error?.Message);
        Assert.Equal(0, response.Status.Code);
        Assert.Equal("Success", response.Status.Name);
    }

    public static void Status(AgentResponse response, int code, string name)
    {
        Assert.True(response.Ok, response.Error?.Message);
        Assert.Equal(code, response.Status.Code);
        Assert.Equal(name, response.Status.Name);
    }

    public static byte[] Decode(AgentResponse response, string property) =>
        AgentProtocol.DecodeBytes(response.Result!.Value.GetProperty(property).GetString()!);
}
