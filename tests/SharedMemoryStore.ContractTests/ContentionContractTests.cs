using System.Reflection;

namespace SharedMemoryStore.ContractTests;

public sealed class ContentionContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ContentionContract)]
    public void PublicOperationFamiliesExposeWaitPolicyOverloads()
    {
        var names = new[]
        {
            nameof(MemoryStore.TryPublish),
            nameof(MemoryStore.TryReserve),
            nameof(MemoryStore.TryPublishSegments),
            nameof(MemoryStore.TryAcquire),
            nameof(MemoryStore.TryRemove),
            nameof(MemoryStore.TryRecoverLeases),
            nameof(MemoryStore.TryRecoverReservations),
            nameof(MemoryStore.TryGetDiagnostics)
        };

        var methods = typeof(MemoryStore).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (var name in names)
        {
            Assert.Contains(methods, method =>
                method.Name == name
                && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StoreWaitOptions)));
        }

        Assert.Contains(typeof(ValueLease).GetMethods(), method =>
            method.Name == nameof(ValueLease.Release)
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StoreWaitOptions)));
        Assert.Contains(typeof(ValueReservation).GetMethods(), method =>
            method.Name == nameof(ValueReservation.Commit)
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StoreWaitOptions)));
    }
}
