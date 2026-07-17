using System.Reflection;
using SharedMemoryStore.Engines;

namespace SharedMemoryStore.UnitTests;

[Collection("DynamicEngineFacade")]
public sealed class StoreEngineFactoryOwnershipTests
{
    [Fact]
    public void FacadeConstructionFailureDisposesTransferredEngineExactlyOnce()
    {
        FakeEngineCallLog.Reset(StoreStatus.Success, throwOnProtocolInfo: true);
        object engine = MemoryStoreFacadeTests.CreateFakeEngine();

        TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(
            () => InvokeWrapOwnedEngine(engine));

        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Equal(1, FakeEngineCallLog.Count(nameof(IDisposable.Dispose)));
    }

    [Fact]
    public void SuccessfulFacadeConstructionTransfersEngineOwnershipUntilFacadeDisposal()
    {
        FakeEngineCallLog.Reset(StoreStatus.Success);
        object engine = MemoryStoreFacadeTests.CreateFakeEngine();

        using MemoryStore store = InvokeWrapOwnedEngine(engine);

        Assert.Equal(0, FakeEngineCallLog.Count(nameof(IDisposable.Dispose)));
        store.Dispose();
        Assert.Equal(1, FakeEngineCallLog.Count(nameof(IDisposable.Dispose)));
    }

    private static MemoryStore InvokeWrapOwnedEngine(object engine)
    {
        MethodInfo method = typeof(StoreEngineFactory).GetMethod(
                "WrapOwnedEngine",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("StoreEngineFactory.WrapOwnedEngine is absent.");
        return (MemoryStore)method.Invoke(null, [engine])!;
    }
}
