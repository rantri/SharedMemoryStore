using System.Reflection;

namespace SharedMemoryStore.ContractTests;

public sealed class ProductionApiContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ProductionApiContract)]
    public void PrimaryStoreIdentityIsMemoryStoreWithoutOldConcreteType()
    {
        Assert.Equal("SharedMemoryStore.MemoryStore", typeof(MemoryStore).FullName);
        PublicApiAssertions.DoesNotExposePublicType("SharedMemoryStore.SharedMemoryStore");
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ProductionApiContract)]
    public void BroadStoreMirrorInterfaceIsNotPublic()
    {
        var exported = typeof(MemoryStore).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, type => type.IsInterface && type.Name == "ISharedMemoryStore");
        Assert.DoesNotContain(typeof(MemoryStore).GetInterfaces(), type => type.Name == "ISharedMemoryStore");
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ProductionApiContract)]
    public void WaitPolicyOverloadsArePublic()
    {
        Assert.Contains(typeof(MemoryStore).GetMethods(BindingFlags.Public | BindingFlags.Static), method =>
            method.Name == nameof(MemoryStore.TryCreateOrOpen)
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StoreWaitOptions)));

        Assert.Contains(typeof(MemoryStore).GetMethods(BindingFlags.Public | BindingFlags.Instance), method =>
            method.Name == nameof(MemoryStore.TryPublish)
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StoreWaitOptions)));
    }
}
