using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharedMemoryStore.ContractTests;

public sealed class SingleProtocolApiContractTests
{
    private static readonly Assembly StoreAssembly = typeof(MemoryStore).Assembly;

    [Fact]
    public void PublicSurfaceHasNoProfileSelectorOrCompatibilityHelper()
    {
        Assert.Null(StoreAssembly.GetType("SharedMemoryStore.StoreProfile", throwOnError: false));
        Assert.Null(typeof(SharedMemoryStoreOptions).GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(MemoryStore).GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            typeof(SharedMemoryStoreOptions).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "CreateLockFree");
        Assert.DoesNotContain(
            typeof(SharedMemoryStoreOptions).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method =>
                method.Name == nameof(SharedMemoryStoreOptions.CalculateRequiredBytes)
                && method.GetParameters().FirstOrDefault()?.ParameterType.FullName == "SharedMemoryStore.StoreProfile");
    }

    [Fact]
    public void OrdinaryCreateAndSizingAreTheOnlyParticipantAwareHelpers()
    {
        PropertyInfo participantCount = RequireProperty(
            typeof(SharedMemoryStoreOptions),
            "ParticipantRecordCount",
            typeof(int));
        AssertInitOnly(participantCount);
        Assert.Equal(64, participantCount.GetValue(new SharedMemoryStoreOptions()));

        MethodInfo calculate = Assert.Single(
            typeof(SharedMemoryStoreOptions).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == nameof(SharedMemoryStoreOptions.CalculateRequiredBytes));
        Assert.Equal(typeof(long), calculate.ReturnType);
        Assert.Equal(
            new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) },
            calculate.GetParameters().Select(static parameter => parameter.ParameterType));
        AssertParameterNames(
            calculate,
            "slotCount",
            "maxValueBytes",
            "maxDescriptorBytes",
            "maxKeyBytes",
            "leaseRecordCount",
            "participantRecordCount");
        Assert.All(calculate.GetParameters()[..5], static parameter => Assert.False(parameter.IsOptional));
        AssertOptionalDefault(calculate.GetParameters()[5], 64);

        MethodInfo create = Assert.Single(
            typeof(SharedMemoryStoreOptions).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == nameof(SharedMemoryStoreOptions.Create));
        Assert.Equal(typeof(SharedMemoryStoreOptions), create.ReturnType);
        Assert.Equal(
            new[]
            {
                typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                typeof(int), typeof(OpenMode), typeof(bool)
            },
            create.GetParameters().Select(static parameter => parameter.ParameterType));
        AssertParameterNames(
            create,
            "name",
            "slotCount",
            "maxValueBytes",
            "maxDescriptorBytes",
            "maxKeyBytes",
            "leaseRecordCount",
            "participantRecordCount",
            "openMode",
            "enableLeaseRecovery");
        Assert.All(create.GetParameters()[..6], static parameter => Assert.False(parameter.IsOptional));
        AssertOptionalDefault(create.GetParameters()[6], 64);
        AssertOptionalDefault(create.GetParameters()[7], OpenMode.CreateOrOpen);
        AssertOptionalDefault(create.GetParameters()[8], false);
    }

    [Fact]
    public void ProtocolInfoIsTheImmutableFiveFieldSms2Identity()
    {
        Type protocolType = RequirePublicType("SharedMemoryStore.StoreProtocolInfo");
        Assert.True(protocolType.IsValueType);
        Assert.True(protocolType.IsSealed);
        Assert.NotNull(protocolType.GetCustomAttribute<IsReadOnlyAttribute>());

        ConstructorInfo? constructor = protocolType.GetConstructor(
        [
            typeof(int), typeof(int), typeof(int), typeof(ulong), typeof(ulong)
        ]);
        Assert.NotNull(constructor);
        AssertParameterNames(
            constructor!,
            "LayoutMajorVersion",
            "LayoutMinorVersion",
            "ResourceProtocolVersion",
            "RequiredFeatures",
            "OptionalFeatures");

        string[] expectedProperties =
        [
            "LayoutMajorVersion",
            "LayoutMinorVersion",
            "OptionalFeatures",
            "RequiredFeatures",
            "ResourceProtocolVersion"
        ];
        Assert.Equal(
            expectedProperties,
            protocolType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .Order()
                .ToArray());
        Assert.All(
            protocolType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            AssertInitOnly);

        object identity = constructor!.Invoke([2, 0, 2, 7UL, 0UL]);
        Assert.Equal(2, RequireProperty(protocolType, "LayoutMajorVersion", typeof(int)).GetValue(identity));
        Assert.Equal(0, RequireProperty(protocolType, "LayoutMinorVersion", typeof(int)).GetValue(identity));
        Assert.Equal(2, RequireProperty(protocolType, "ResourceProtocolVersion", typeof(int)).GetValue(identity));
        Assert.Equal(7UL, RequireProperty(protocolType, "RequiredFeatures", typeof(ulong)).GetValue(identity));
        Assert.Equal(0UL, RequireProperty(protocolType, "OptionalFeatures", typeof(ulong)).GetValue(identity));

        PropertyInfo storeProtocol = RequireProperty(typeof(MemoryStore), "ProtocolInfo", protocolType);
        Assert.Null(storeProtocol.SetMethod);
    }

    [Fact]
    public void OpenModesAndPublicStatusesKeepTheirCanonicalNumbers()
    {
        AssertEnumValues(
            new Dictionary<string, int>
            {
                ["CreateNew"] = 0,
                ["OpenExisting"] = 1,
                ["CreateOrOpen"] = 2
            },
            typeof(OpenMode));
        AssertEnumValues(
            new Dictionary<string, int>
            {
                ["Success"] = 0,
                ["AlreadyExists"] = 1,
                ["NotFound"] = 2,
                ["InvalidOptions"] = 3,
                ["IncompatibleLayout"] = 4,
                ["UnsupportedPlatform"] = 5,
                ["InsufficientCapacity"] = 6,
                ["AccessDenied"] = 7,
                ["MappingFailed"] = 8,
                ["StoreBusy"] = 9,
                ["OperationCanceled"] = 10,
                ["ParticipantTableFull"] = 11
            },
            typeof(StoreOpenStatus));
        AssertEnumValues(
            new Dictionary<string, int>
            {
                ["Success"] = 0,
                ["DuplicateKey"] = 1,
                ["NotFound"] = 2,
                ["KeyTooLarge"] = 3,
                ["ValueTooLarge"] = 4,
                ["DescriptorTooLarge"] = 5,
                ["StoreFull"] = 6,
                ["LeaseTableFull"] = 7,
                ["InvalidLease"] = 8,
                ["LeaseAlreadyReleased"] = 9,
                ["RemovePending"] = 10,
                ["UnsupportedPlatform"] = 11,
                ["StoreDisposed"] = 12,
                ["CorruptStore"] = 13,
                ["AccessDenied"] = 14,
                ["UnknownFailure"] = 15,
                ["InvalidReservation"] = 16,
                ["ReservationIncomplete"] = 17,
                ["ReservationAlreadyCompleted"] = 18,
                ["ReservationWriteOutOfRange"] = 19,
                ["InvalidKey"] = 20,
                ["StoreBusy"] = 21,
                ["OperationCanceled"] = 22
            },
            typeof(StoreStatus));
    }

    private static Type RequirePublicType(string fullName)
    {
        Type? type = StoreAssembly.GetType(fullName, throwOnError: false, ignoreCase: false);
        Assert.True(type is not null, $"Required public type '{fullName}' is missing.");
        Assert.True(type!.IsPublic, $"Required type '{fullName}' must be public.");
        return type;
    }

    private static PropertyInfo RequireProperty(Type declaringType, string name, Type propertyType)
    {
        PropertyInfo? property = declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, $"Required public property '{declaringType.FullName}.{name}' is missing.");
        Assert.Equal(propertyType, property!.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.True(property.GetMethod!.IsPublic);
        return property;
    }

    private static void AssertInitOnly(PropertyInfo property)
    {
        Assert.NotNull(property.SetMethod);
        Assert.True(property.SetMethod!.IsPublic);
        Assert.Contains(typeof(IsExternalInit), property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
    }

    private static void AssertParameterNames(MethodBase method, params string[] expectedNames)
    {
        Assert.Equal(expectedNames, method.GetParameters().Select(static parameter => parameter.Name));
    }

    private static void AssertOptionalDefault(ParameterInfo parameter, object expectedDefault)
    {
        Assert.True(parameter.IsOptional, $"Parameter '{parameter.Name}' must be optional.");
        Assert.Equal(expectedDefault, parameter.DefaultValue);
    }

    private static void AssertEnumValues(IReadOnlyDictionary<string, int> expected, Type enumType)
    {
        Assert.Equal(expected.Keys, Enum.GetNames(enumType));
        Assert.All(expected, pair => Assert.Equal(pair.Value, Convert.ToInt32(Enum.Parse(enumType, pair.Key))));
    }
}
