using System.Reflection;

namespace SharedMemoryStore.ContractTests;

internal static class PublicApiAssertions
{
    public static MethodInfo SinglePublicMethod(Type type, string name, int parameterCount)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);
    }

    public static void DoesNotExposePublicType(string fullName)
    {
        var exported = typeof(MemoryStore).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, type => type.FullName == fullName);
    }
}
