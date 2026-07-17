using System.Reflection;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.ContractTests;

public sealed class RetiredLayoutAbsenceContractTests
{
    private static readonly Assembly StoreAssembly = typeof(MemoryStore).Assembly;

    [Fact]
    public void PublicApiCannotSelectAProfileOrCallACompatibilityCreator()
    {
        Assert.Null(StoreAssembly.GetType("SharedMemoryStore.StoreProfile", throwOnError: false));
        Assert.Null(typeof(SharedMemoryStoreOptions).GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(MemoryStore).GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            typeof(SharedMemoryStoreOptions).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "CreateLockFree");
        Assert.DoesNotContain(
            typeof(SharedMemoryStoreOptions).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.GetParameters().Any(parameter =>
                parameter.ParameterType.FullName == "SharedMemoryStore.StoreProfile"));
    }

    [Fact]
    public void EngineBoundaryAndFactoryContainNoLegacyDispatch()
    {
        Type engine = RequireInternalType("SharedMemoryStore.Engines.IStoreEngine");
        Type factory = RequireInternalType("SharedMemoryStore.Engines.StoreEngineFactory");

        Assert.Null(engine.GetProperty("Profile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.DoesNotContain(
            factory.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name.Contains("Legacy", StringComparison.Ordinal));
        Assert.Null(StoreAssembly.GetType(
            "SharedMemoryStore.Engines.LegacyV12.LegacyV12StoreEngine",
            throwOnError: false));
    }

    [Fact]
    public void FacadeHasNoEmbeddedSms1TopologyOrOperationLock()
    {
        Type facade = typeof(MemoryStore);
        Type engine = RequireInternalType("SharedMemoryStore.Engines.IStoreEngine");
        FieldInfo[] fields = facade.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Single(fields, field => field.FieldType == engine);
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(SemaphoreSlim));
        Assert.DoesNotContain(fields, static field => typeof(ISharedStoreSynchronization).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(MemoryMappedStoreRegion));
        Assert.DoesNotContain(fields, static field => field.FieldType.Namespace == "SharedMemoryStore.Layout");
        Assert.DoesNotContain(fields, static field => field.FieldType.Namespace == "SharedMemoryStore.Slots");
        Assert.DoesNotContain(fields, static field => field.FieldType.Namespace == "SharedMemoryStore.Leasing");

        ConstructorInfo constructor = Assert.Single(
            facade.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        ParameterInfo parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(engine, parameter.ParameterType);
    }

    [Fact]
    public void CreatableSms1RecordsAndFacadeWorkflowsAreAbsent()
    {
        string[] retiredTypes =
        [
            "SharedMemoryStore.Layout.StoreLayout",
            "SharedMemoryStore.Layout.StoreHeader",
            "SharedMemoryStore.Layout.SharedKeyIndex",
            "SharedMemoryStore.Slots.ReusableSlotTable",
            "SharedMemoryStore.Leasing.LeaseRegistry",
            "SharedMemoryStore.Ingest.ReservationMemoryManager"
        ];
        Assert.All(retiredTypes, fullName => Assert.Null(StoreAssembly.GetType(fullName, throwOnError: false)));

        string[] retiredFacadeMethods =
        [
            "InitializeOrValidate",
            "DisposeUninitialized",
            "CompactIndexCore",
            "ReclaimRemovedSlot"
        ];
        MethodInfo[] facadeMethods = typeof(MemoryStore).GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(
            facadeMethods,
            method => retiredFacadeMethods.Contains(method.Name, StringComparer.Ordinal));
    }

    private static Type RequireInternalType(string fullName)
    {
        Type? type = StoreAssembly.GetType(fullName, throwOnError: false, ignoreCase: false);
        Assert.True(type is not null, $"Required implementation type '{fullName}' is missing.");
        Assert.False(type!.IsPublic, $"Implementation type '{fullName}' must not be public.");
        return type;
    }
}
