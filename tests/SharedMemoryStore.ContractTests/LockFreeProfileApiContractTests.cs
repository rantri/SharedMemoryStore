using System.Reflection;
using System.Runtime.CompilerServices;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.ContractTests;

public sealed class LockFreeProfileApiContractTests
{
    private static readonly Assembly StoreAssembly = typeof(Store).Assembly;

    [Fact]
    public void EveryAdditiveLockFreePublicSymbolHasPackagedXmlDocumentation()
    {
        LockFreePackageContractTests.AssertEveryAdditiveLockFreePublicSymbolHasPackagedXmlDocumentation();
    }

    [Fact]
    public void StoreProfileHasOnlyTheStableProfileAssignments()
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");

        Assert.True(profileType.IsEnum);
        Assert.Equal(new[] { "Legacy", "LockFree" }, Enum.GetNames(profileType));
        Assert.Equal(0, Convert.ToInt32(Enum.Parse(profileType, "Legacy")));
        Assert.Equal(1, Convert.ToInt32(Enum.Parse(profileType, "LockFree")));
    }

    [Fact]
    public void OptionsExposeInitOnlyProfileAndParticipantCapacityWithLegacyDefaults()
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");
        var optionsType = typeof(SharedMemoryStoreOptions);
        var profile = RequireProperty(optionsType, "Profile", profileType);
        var participantCount = RequireProperty(optionsType, "ParticipantRecordCount", typeof(int));

        AssertInitOnly(profile);
        AssertInitOnly(participantCount);

        var options = new SharedMemoryStoreOptions();
        Assert.Equal("Legacy", profile.GetValue(options)?.ToString());
        Assert.Equal(64, participantCount.GetValue(options));
    }

    [Fact]
    public void LegacySizingAndCreateSignaturesRemainUnchanged()
    {
        var optionsType = typeof(SharedMemoryStoreOptions);

        var calculate = RequireMethod(
            optionsType,
            nameof(SharedMemoryStoreOptions.CalculateRequiredBytes),
            BindingFlags.Public | BindingFlags.Static,
            typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
        Assert.Equal(typeof(long), calculate.ReturnType);
        AssertParameterNames(
            calculate,
            "slotCount", "maxValueBytes", "maxDescriptorBytes", "maxKeyBytes", "leaseRecordCount");
        Assert.All(calculate.GetParameters(), parameter => Assert.False(parameter.IsOptional));

        var create = RequireMethod(
            optionsType,
            nameof(SharedMemoryStoreOptions.Create),
            BindingFlags.Public | BindingFlags.Static,
            typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(OpenMode), typeof(bool));
        Assert.Equal(optionsType, create.ReturnType);
        AssertParameterNames(
            create,
            "name", "slotCount", "maxValueBytes", "maxDescriptorBytes", "maxKeyBytes",
            "leaseRecordCount", "openMode", "enableLeaseRecovery");
        AssertOptionalDefault(create.GetParameters()[6], OpenMode.CreateOrOpen);
        AssertOptionalDefault(create.GetParameters()[7], false);
    }

    [Fact]
    public void ExistingCreateAlwaysSelectsLegacyProfile()
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");
        var profile = RequireProperty(typeof(SharedMemoryStoreOptions), "Profile", profileType);

        var options = SharedMemoryStoreOptions.Create(
            "contract-create-legacy",
            slotCount: 2,
            maxValueBytes: 32,
            maxDescriptorBytes: 8,
            maxKeyBytes: 16,
            leaseRecordCount: 2);

        Assert.Equal("Legacy", profile.GetValue(options)?.ToString());
    }

    [Fact]
    public void ProfileAwareSizingIsAdditiveAndParticipantCapacityIsOptional()
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");
        var calculate = RequireMethod(
            typeof(SharedMemoryStoreOptions),
            nameof(SharedMemoryStoreOptions.CalculateRequiredBytes),
            BindingFlags.Public | BindingFlags.Static,
            profileType, typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));

        Assert.Equal(typeof(long), calculate.ReturnType);
        AssertParameterNames(
            calculate,
            "profile", "slotCount", "maxValueBytes", "maxDescriptorBytes", "maxKeyBytes",
            "leaseRecordCount", "participantRecordCount");
        AssertOptionalDefault(calculate.GetParameters()[6], 64);
    }

    [Fact]
    public void CreateLockFreeHasTheSpecifiedAdditiveSignatureAndDefaults()
    {
        var create = RequireMethod(
            typeof(SharedMemoryStoreOptions),
            "CreateLockFree",
            BindingFlags.Public | BindingFlags.Static,
            typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(int), typeof(OpenMode), typeof(bool));

        Assert.Equal(typeof(SharedMemoryStoreOptions), create.ReturnType);
        AssertParameterNames(
            create,
            "name", "slotCount", "maxValueBytes", "maxDescriptorBytes", "maxKeyBytes",
            "leaseRecordCount", "participantRecordCount", "openMode", "enableLeaseRecovery");
        AssertOptionalDefault(create.GetParameters()[6], 64);
        AssertOptionalDefault(create.GetParameters()[7], OpenMode.CreateOrOpen);
        AssertOptionalDefault(create.GetParameters()[8], false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_576)]
    public void LockFreeParticipantCapacityOutsideTheContractRangeIsInvalid(int participantRecordCount)
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");
        var calculate = RequireMethod(
            typeof(SharedMemoryStoreOptions),
            nameof(SharedMemoryStoreOptions.CalculateRequiredBytes),
            BindingFlags.Public | BindingFlags.Static,
            profileType, typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
        var lockFree = Enum.Parse(profileType, "LockFree");
        var requiredBytes = (long)calculate.Invoke(
            null,
            new[] { lockFree, 2, 32, 8, 16, 2, 64 })!;

        var options = new SharedMemoryStoreOptions
        {
            Name = $"contract-invalid-participants-{participantRecordCount}",
            OpenMode = OpenMode.CreateNew,
            SlotCount = 2,
            MaxValueBytes = 32,
            MaxDescriptorBytes = 8,
            MaxKeyBytes = 16,
            LeaseRecordCount = 2,
            TotalBytes = requiredBytes
        };
        RequireProperty(typeof(SharedMemoryStoreOptions), "Profile", profileType).SetValue(options, lockFree);
        RequireProperty(typeof(SharedMemoryStoreOptions), "ParticipantRecordCount", typeof(int))
            .SetValue(options, participantRecordCount);

        Assert.Equal(StoreOpenStatus.InvalidOptions, options.Validate().Status);
    }

    [Fact]
    public void LockFreeSlotCapacityMaximumIsAcceptedAndTheNextValueIsRejectedWithoutChangingLegacySizing()
    {
        const int maximumLockFreeSlotCount = 1_048_575;
        const int firstRejectedLockFreeSlotCount = maximumLockFreeSlotCount + 1;

        long maximumBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
            StoreProfile.LockFree,
            maximumLockFreeSlotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1,
            participantRecordCount: 1);

        Assert.True(maximumBytes > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SharedMemoryStoreOptions.CalculateRequiredBytes(
                StoreProfile.LockFree,
                firstRejectedLockFreeSlotCount,
                maxValueBytes: 1,
                maxDescriptorBytes: 0,
                maxKeyBytes: 1,
                leaseRecordCount: 1,
                participantRecordCount: 1));

        long legacyBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
            StoreProfile.Legacy,
            firstRejectedLockFreeSlotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: 1,
            leaseRecordCount: 1,
            participantRecordCount: 1);
        Assert.True(legacyBytes > 0);
    }

    [Fact]
    public void StoreProtocolInfoIsTheSpecifiedReadonlyRecordValue()
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");
        var protocolType = RequirePublicType("SharedMemoryStore.StoreProtocolInfo");

        Assert.True(protocolType.IsValueType);
        Assert.True(protocolType.IsSealed);
        Assert.NotNull(protocolType.GetCustomAttribute<IsReadOnlyAttribute>());

        var constructor = protocolType.GetConstructor(new[]
        {
            profileType, typeof(int), typeof(int), typeof(int), typeof(ulong), typeof(ulong)
        });
        Assert.NotNull(constructor);
        AssertParameterNames(
            constructor!,
            "Profile", "LayoutMajorVersion", "LayoutMinorVersion", "ResourceProtocolVersion",
            "RequiredFeatures", "OptionalFeatures");

        RequireProperty(protocolType, "Profile", profileType);
        RequireProperty(protocolType, "LayoutMajorVersion", typeof(int));
        RequireProperty(protocolType, "LayoutMinorVersion", typeof(int));
        RequireProperty(protocolType, "ResourceProtocolVersion", typeof(int));
        RequireProperty(protocolType, "RequiredFeatures", typeof(ulong));
        RequireProperty(protocolType, "OptionalFeatures", typeof(ulong));
    }

    [Fact]
    public void MemoryStoreExposesImmutableProfileAndProtocolIdentity()
    {
        var profileType = RequirePublicType("SharedMemoryStore.StoreProfile");
        var protocolType = RequirePublicType("SharedMemoryStore.StoreProtocolInfo");
        var profile = RequireProperty(typeof(Store), "Profile", profileType);
        var protocol = RequireProperty(typeof(Store), "ProtocolInfo", protocolType);

        Assert.Null(profile.SetMethod);
        Assert.Null(protocol.SetMethod);
    }

    [Fact]
    public void StoreOpenStatusAppendsParticipantTableFullWithoutRenumberingLegacyValues()
    {
        var expected = new Dictionary<string, int>
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
        };

        Assert.Equal(expected.Keys, Enum.GetNames<StoreOpenStatus>());
        Assert.All(expected, pair =>
            Assert.Equal(pair.Value, Convert.ToInt32(Enum.Parse<StoreOpenStatus>(pair.Key))));
    }

    [Fact]
    public void StoreStatusNumericAssignmentsRemainUnchanged()
    {
        var expected = new Dictionary<string, int>
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
        };

        Assert.Equal(expected.Keys, Enum.GetNames<StoreStatus>());
        Assert.All(expected, pair =>
            Assert.Equal(pair.Value, Convert.ToInt32(Enum.Parse<StoreStatus>(pair.Key))));
    }

    private static Type RequirePublicType(string fullName)
    {
        var type = StoreAssembly.GetType(fullName, throwOnError: false, ignoreCase: false);
        Assert.True(type is not null, $"Required public type '{fullName}' is missing.");
        Assert.True(type!.IsPublic, $"Required type '{fullName}' must be public.");
        return type;
    }

    private static PropertyInfo RequireProperty(Type declaringType, string name, Type propertyType)
    {
        var property = declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, $"Required public property '{declaringType.FullName}.{name}' is missing.");
        Assert.Equal(propertyType, property!.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.True(property.GetMethod!.IsPublic);
        return property;
    }

    private static MethodInfo RequireMethod(
        Type declaringType,
        string name,
        BindingFlags bindingFlags,
        params Type[] parameterTypes)
    {
        var method = declaringType.GetMethod(name, bindingFlags, binder: null, parameterTypes, modifiers: null);
        Assert.True(
            method is not null,
            $"Required method '{declaringType.FullName}.{name}({string.Join(", ", parameterTypes.Select(type => type.Name))})' is missing.");
        return method!;
    }

    private static void AssertInitOnly(PropertyInfo property)
    {
        Assert.NotNull(property.SetMethod);
        Assert.True(property.SetMethod!.IsPublic);
        Assert.Contains(
            typeof(IsExternalInit),
            property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
    }

    private static void AssertParameterNames(MethodBase method, params string[] expectedNames)
    {
        Assert.Equal(expectedNames, method.GetParameters().Select(parameter => parameter.Name));
    }

    private static void AssertOptionalDefault(ParameterInfo parameter, object expectedDefault)
    {
        Assert.True(parameter.IsOptional, $"Parameter '{parameter.Name}' must be optional.");
        Assert.Equal(expectedDefault, parameter.DefaultValue);
    }
}
