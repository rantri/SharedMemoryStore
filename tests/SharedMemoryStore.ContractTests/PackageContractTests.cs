using System.Reflection;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.ContractTests;

public sealed class PackageContractTests
{
    [Fact]
    public void AssemblyIdentityAndXmlDocumentationAreProduced()
    {
        var assembly = typeof(Store).Assembly;
        Assert.Equal("SharedMemoryStore", assembly.GetName().Name);

        var xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), $"Expected XML documentation at {xmlPath}");
    }

    [Fact]
    public void PackageProjectCarriesMetadata()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "SharedMemoryStore", "SharedMemoryStore.csproj"));

        Assert.Contains("<PackageId>SharedMemoryStore</PackageId>", project);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", project);
        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", project);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", project);
        Assert.Contains("linux", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windows", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docker", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same-host Docker support", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeProjectHasNoPackageReferences()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "SharedMemoryStore", "SharedMemoryStore.csproj"));

        Assert.DoesNotContain("<PackageReference", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
