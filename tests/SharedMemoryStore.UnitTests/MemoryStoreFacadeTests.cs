using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharedMemoryStore.UnitTests
{

public sealed class MemoryStoreFacadeTests
{
    private static readonly Assembly StoreAssembly = typeof(MemoryStore).Assembly;

    [Fact]
    public void FacadeRoutesCoreStatusOperationsThroughInjectedEngine()
    {
        var engineType = RequireInternalType("SharedMemoryStore.Engines.IStoreEngine");
        Assert.True(engineType.IsInterface, "IStoreEngine must be an interface so the facade has one profile-neutral dependency.");

        FakeEngineCallLog.Reset(StoreStatus.UnsupportedPlatform);
        using var store = CreateFacadeWithFakeEngine(engineType);

        Assert.Equal(StoreStatus.UnsupportedPlatform, store.TryPublish([1], [2]));
        Assert.Equal(StoreStatus.UnsupportedPlatform, store.TryReserve([2], 1, default, out _));
        Assert.Equal(StoreStatus.UnsupportedPlatform, store.TryAcquire([3], out _));
        Assert.Equal(StoreStatus.UnsupportedPlatform, store.TryRemove([4]));

        Assert.Equal(1, FakeEngineCallLog.Count("TryPublish"));
        Assert.Equal(1, FakeEngineCallLog.Count("TryReserve"));
        Assert.Equal(1, FakeEngineCallLog.Count("TryAcquire"));
        Assert.Equal(1, FakeEngineCallLog.Count("TryRemove"));
    }

    [Fact]
    public void PublicTokensCarryOnlyFacadeAndOpaqueEngineHandle()
    {
        var reservationHandle = RequireOpaqueHandle(
            "SharedMemoryStore.Engines.ReservationHandle",
            typeof(ulong), typeof(ulong), typeof(ulong), typeof(int));
        var leaseHandle = RequireOpaqueHandle(
            "SharedMemoryStore.Engines.LeaseHandle",
            typeof(ulong), typeof(ulong), typeof(ulong), typeof(ulong));

        AssertTokenFields<ValueReservation>(reservationHandle);
        AssertTokenFields<ValueLease>(leaseHandle);
    }

    [Fact]
    public void ReservationHandleFencesStoreParticipantAndSlotIncarnations()
    {
        var handleType = RequireOpaqueHandle(
            "SharedMemoryStore.Engines.ReservationHandle",
            typeof(ulong), typeof(ulong), typeof(ulong), typeof(int));

        var baseline = CreateHandle(handleType, 11UL, 21UL, 31UL, 41);
        Assert.NotEqual(baseline, CreateHandle(handleType, 12UL, 21UL, 31UL, 41));
        Assert.NotEqual(baseline, CreateHandle(handleType, 11UL, 22UL, 31UL, 41));
        Assert.NotEqual(baseline, CreateHandle(handleType, 11UL, 21UL, 32UL, 41));
        Assert.NotEqual(baseline, CreateHandle(handleType, 11UL, 21UL, 31UL, 42));
    }

    [Fact]
    public void LeaseHandleFencesStoreParticipantSlotAndLeaseIncarnations()
    {
        var handleType = RequireOpaqueHandle(
            "SharedMemoryStore.Engines.LeaseHandle",
            typeof(ulong), typeof(ulong), typeof(ulong), typeof(ulong));

        var baseline = CreateHandle(handleType, 11UL, 21UL, 31UL, 41UL);
        Assert.NotEqual(baseline, CreateHandle(handleType, 12UL, 21UL, 31UL, 41UL));
        Assert.NotEqual(baseline, CreateHandle(handleType, 11UL, 22UL, 31UL, 41UL));
        Assert.NotEqual(baseline, CreateHandle(handleType, 11UL, 21UL, 32UL, 41UL));
        Assert.NotEqual(baseline, CreateHandle(handleType, 11UL, 21UL, 31UL, 42UL));
    }

    private static Type RequireInternalType(string fullName)
    {
        var type = StoreAssembly.GetType(fullName, throwOnError: false, ignoreCase: false);
        Assert.True(type is not null, $"Required engine-neutral type '{fullName}' is missing.");
        Assert.False(type!.IsPublic, $"'{fullName}' is an implementation seam and must not expand the public API.");
        return type;
    }

    private static Type RequireOpaqueHandle(string fullName, params Type[] constructorParameterTypes)
    {
        var type = RequireInternalType(fullName);
        Assert.True(type.IsValueType, $"'{fullName}' must be an allocation-free value handle.");
        Assert.NotNull(type.GetCustomAttribute<IsReadOnlyAttribute>());
        Assert.NotNull(type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            constructorParameterTypes,
            modifiers: null));
        Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
        return type;
    }

    private static object CreateHandle(Type handleType, params object[] arguments)
    {
        var instance = Activator.CreateInstance(
            handleType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null);
        Assert.NotNull(instance);
        return instance!;
    }

    private static void AssertTokenFields<TToken>(Type expectedHandleType)
    {
        var fields = typeof(TToken).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Contains(fields, field => field.FieldType == typeof(MemoryStore));
        Assert.Single(fields, field => field.FieldType == expectedHandleType);
        Assert.DoesNotContain(fields, field => field.FieldType.FullName == "SharedMemoryStore.Layout.SlotLifecycleId");
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(int));
    }

    private static MemoryStore CreateFacadeWithFakeEngine(Type engineType)
    {
        var constructor = typeof(MemoryStore)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == engineType;
            });
        Assert.True(constructor is not null, "MemoryStore must expose an internal one-engine constructor for routing tests.");

        var fake = FakeEngineEmitter.Create(engineType);
        return (MemoryStore)constructor!.Invoke([fake]);
    }

    private static class FakeEngineEmitter
    {
        public static object Create(Type interfaceType)
        {
            var assemblyName = new AssemblyName($"SharedMemoryStore.UnitTests.Fakes.{Guid.NewGuid():N}");
            var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var ignoresAccessChecks = typeof(IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)]);
            Assert.NotNull(ignoresAccessChecks);
            assembly.SetCustomAttribute(new CustomAttributeBuilder(ignoresAccessChecks!, ["SharedMemoryStore"]));

            var module = assembly.DefineDynamicModule(assemblyName.Name!);
            var type = module.DefineType(
                $"FakeStoreEngine_{Guid.NewGuid():N}",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            type.AddInterfaceImplementation(interfaceType);
            type.DefineDefaultConstructor(MethodAttributes.Public);

            var methods = interfaceType
                .GetInterfaces()
                .Append(interfaceType)
                .SelectMany(type => type.GetMethods())
                .DistinctBy(method =>
                    $"{method.Name}|{method.ReturnType.AssemblyQualifiedName}|{string.Join("|", method.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName))}")
                .ToArray();
            Assert.DoesNotContain(methods, method => method.IsGenericMethodDefinition);
            foreach (var method in methods)
            {
                EmitMethod(type, method);
            }

            var generated = type.CreateType();
            return Activator.CreateInstance(generated!)!;
        }

        private static void EmitMethod(TypeBuilder type, MethodInfo interfaceMethod)
        {
            var parameters = interfaceMethod.GetParameters();
            var implementation = type.DefineMethod(
                interfaceMethod.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
                MethodAttributes.HideBySig | MethodAttributes.NewSlot |
                (interfaceMethod.IsSpecialName ? MethodAttributes.SpecialName : 0),
                interfaceMethod.ReturnType,
                parameters.Select(parameter => parameter.ParameterType).ToArray());

            for (var index = 0; index < parameters.Length; index++)
            {
                implementation.DefineParameter(index + 1, parameters[index].Attributes, parameters[index].Name);
            }

            var il = implementation.GetILGenerator();
            il.Emit(OpCodes.Ldstr, interfaceMethod.Name);
            il.Emit(OpCodes.Call, typeof(FakeEngineCallLog).GetMethod(nameof(FakeEngineCallLog.Record))!);

            for (var index = 0; index < parameters.Length; index++)
            {
                var parameterType = parameters[index].ParameterType;
                if (!parameterType.IsByRef || parameters[index].IsIn)
                {
                    continue;
                }

                il.Emit(OpCodes.Ldarg, index + 1);
                il.Emit(OpCodes.Initobj, parameterType.GetElementType()!);
            }

            EmitReturn(il, interfaceMethod.ReturnType);
            type.DefineMethodOverride(implementation, interfaceMethod);
        }

        private static void EmitReturn(ILGenerator il, Type returnType)
        {
            if (returnType == typeof(void))
            {
                il.Emit(OpCodes.Ret);
                return;
            }

            if (returnType == typeof(StoreStatus))
            {
                il.Emit(OpCodes.Call, typeof(FakeEngineCallLog).GetMethod(nameof(FakeEngineCallLog.GetStatus))!);
                il.Emit(OpCodes.Ret);
                return;
            }

            if (!returnType.IsValueType)
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                return;
            }

            var value = il.DeclareLocal(returnType);
            il.Emit(OpCodes.Ldloca, value);
            il.Emit(OpCodes.Initobj, returnType);
            il.Emit(OpCodes.Ldloc, value);
            il.Emit(OpCodes.Ret);
        }
    }
}

public static class FakeEngineCallLog
{
    private static readonly object Sync = new();
    private static readonly List<string> Calls = [];
    private static StoreStatus _status;

    public static void Reset(StoreStatus status)
    {
        lock (Sync)
        {
            Calls.Clear();
            _status = status;
        }
    }

    public static void Record(string methodName)
    {
        lock (Sync)
        {
            Calls.Add(methodName);
        }
    }

    public static int Count(string methodName)
    {
        lock (Sync)
        {
            return Calls.Count(call => call == methodName);
        }
    }

    public static StoreStatus GetStatus() => _status;
}

}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
