using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;
using System.Reflection;
using System.Xml.Linq;

namespace GameSaves.Tests;

public sealed class GoogleSdkBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string> ApprovedGooglePackages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Google.Apis.Auth"] = "1.75.0",
            ["Google.Apis.Drive.v3"] = "1.75.0.4210"
        };

    private static readonly string[] ProviderNeutralProjectNames =
    {
        "GameSaves.Core",
        "GameSaves.App",
        "GameSaves",
        "GameSaves.Reviewer"
    };

    private static readonly string[] ForbiddenGoogleTypeNames =
    {
        "DriveService",
        "GoogleCredential",
        "UserCredential",
        "ClientSecrets",
        "AuthorizationCodeFlow",
        "TokenResponse",
        "Google.Apis.Drive.v3.Data.File",
        "Google.Apis.Drive.v3.Data.Permission",
        "Google.Apis.Upload",
        "Google.Apis.Download",
        "FileDataStore"
    };

    [Fact]
    public void InfrastructureProject_HasOnlyApprovedDirectGooglePackages()
    {
        string managerRoot = FindManagerRoot();
        IReadOnlyDictionary<string, string> googleReferences = GetGooglePackageReferences(
            Path.Combine(managerRoot, "GameSaves.Infrastructure", "GameSaves.Infrastructure.csproj"));

        Assert.Equal(ApprovedGooglePackages.Count, googleReferences.Count);
        Assert.All(ApprovedGooglePackages, expected =>
        {
            Assert.True(
                googleReferences.TryGetValue(expected.Key, out string? actualVersion),
                $"Infrastructure is missing the approved direct dependency {expected.Key}.");
            Assert.Equal(expected.Value, actualVersion);
        });
    }

    [Fact]
    public void OtherProductionProjects_HaveNoDirectGooglePackageReferences()
    {
        string managerRoot = FindManagerRoot();

        foreach (string projectName in ProviderNeutralProjectNames)
        {
            string projectPath = Path.Combine(managerRoot, projectName, $"{projectName}.csproj");
            Assert.Empty(GetGooglePackageReferences(projectPath));
        }
    }

    [Fact]
    public void CoreAndAppSource_HaveNoGoogleUsingDirectives()
    {
        string managerRoot = FindManagerRoot();

        AssertSourceFilesDoNotContain(
            Path.Combine(managerRoot, "GameSaves.Core"),
            "using Google.");
        AssertSourceFilesDoNotContain(
            Path.Combine(managerRoot, "GameSaves.App"),
            "using Google.");
    }

    [Fact]
    public void ProviderNeutralModelSource_HasNoGoogleSdkTypeNames()
    {
        string managerRoot = FindManagerRoot();
        string coreRoot = Path.Combine(managerRoot, "GameSaves.Core");

        foreach (string forbiddenName in ForbiddenGoogleTypeNames)
            AssertSourceFilesDoNotContain(coreRoot, forbiddenName);
    }

    [Fact]
    public void PublicCoreApi_ExposesNoGoogleSdkTypes()
    {
        Assembly coreAssembly = typeof(ISyncProvider).Assembly;

        Assert.All(coreAssembly.GetExportedTypes(), AssertPublicApiHasNoGoogleTypes);
    }

    [Fact]
    public void PublicAppViewModelApi_ExposesNoGoogleSdkTypes()
    {
        IEnumerable<Type> publicViewModels = typeof(SyncViewModel).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith(
                "GameSaves.App.ViewModels",
                StringComparison.Ordinal) == true);

        Assert.NotEmpty(publicViewModels);
        Assert.All(publicViewModels, AssertPublicApiHasNoGoogleTypes);
    }

    [Fact]
    public void GoogleDrive_RemainsUnavailable()
    {
        var catalog = new SyncProviderCatalog();
        SyncProviderDescriptor descriptor =
            catalog.GetDescriptor(SyncProviderKind.GoogleDrive);

        Assert.False(descriptor.IsImplemented);
        Assert.NotNull(descriptor.UnavailableMessage);
        Assert.DoesNotContain(
            descriptor,
            catalog.GetAll().Where(candidate => candidate.IsImplemented));
    }

    private static IReadOnlyDictionary<string, string> GetGooglePackageReferences(
        string projectPath)
    {
        XDocument project = XDocument.Load(projectPath);

        return project.Descendants("PackageReference")
            .Select(reference => new
            {
                Name = (string?)reference.Attribute("Include"),
                Version = (string?)reference.Attribute("Version")
            })
            .Where(reference =>
                reference.Name?.StartsWith("Google.", StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(
                reference => reference.Name!,
                reference => reference.Version ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertSourceFilesDoNotContain(
        string projectRoot,
        string forbiddenText)
    {
        IEnumerable<string> sourceFiles = Directory
            .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain(forbiddenText, source, StringComparison.Ordinal);
        }
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static void AssertPublicApiHasNoGoogleTypes(Type type)
    {
        AssertNotGoogleType(type, $"type {type.FullName}");

        if (type.BaseType is not null)
            AssertNotGoogleType(type.BaseType, $"base type of {type.FullName}");

        foreach (Type implementedInterface in type.GetInterfaces())
            AssertNotGoogleType(implementedInterface, $"interface of {type.FullName}");

        const BindingFlags publicMembers =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (ConstructorInfo constructor in type.GetConstructors(publicMembers))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
                AssertNotGoogleType(parameter.ParameterType, $"constructor of {type.FullName}");
        }

        foreach (PropertyInfo property in type.GetProperties(publicMembers))
        {
            AssertNotGoogleType(property.PropertyType, $"property {type.FullName}.{property.Name}");
            foreach (ParameterInfo parameter in property.GetIndexParameters())
                AssertNotGoogleType(parameter.ParameterType, $"indexer {type.FullName}.{property.Name}");
        }

        foreach (FieldInfo field in type.GetFields(publicMembers))
            AssertNotGoogleType(field.FieldType, $"field {type.FullName}.{field.Name}");

        foreach (EventInfo eventInfo in type.GetEvents(publicMembers))
        {
            if (eventInfo.EventHandlerType is not null)
                AssertNotGoogleType(eventInfo.EventHandlerType, $"event {type.FullName}.{eventInfo.Name}");
        }

        foreach (MethodInfo method in type.GetMethods(publicMembers))
        {
            AssertNotGoogleType(method.ReturnType, $"return type of {type.FullName}.{method.Name}");
            foreach (ParameterInfo parameter in method.GetParameters())
                AssertNotGoogleType(parameter.ParameterType, $"parameter of {type.FullName}.{method.Name}");
        }
    }

    private static void AssertNotGoogleType(Type type, string apiLocation)
    {
        foreach (Type referencedType in FlattenType(type))
        {
            Assert.False(
                referencedType.FullName?.StartsWith("Google.", StringComparison.Ordinal) == true,
                $"Google SDK type {referencedType.FullName} leaked through public {apiLocation}.");
        }
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is Type elementType)
        {
            foreach (Type nestedType in FlattenType(elementType))
                yield return nestedType;
        }

        if (type.IsGenericType)
        {
            foreach (Type genericArgument in type.GetGenericArguments())
            {
                foreach (Type nestedType in FlattenType(genericArgument))
                    yield return nestedType;
            }
        }
    }

    private static string FindManagerRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Manager.sln by walking up from the test output directory.");
    }
}
