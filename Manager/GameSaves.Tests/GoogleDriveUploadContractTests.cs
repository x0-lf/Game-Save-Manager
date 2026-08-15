using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadContractTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("ba7b6dc7-2f86-4d2c-af81-801fa20e467d");

    [Fact]
    public void ValidRequest_PreservesCanonicalInputAndLength()
    {
        var request = GoogleDriveBinaryUploadRequest.Parse(
            ProfileId,
            "run/folder/save.bin",
            42);

        Assert.Equal(ProfileId, request.RemoteProfileId);
        Assert.Equal("run/folder/save.bin", request.CanonicalRemotePath);
        Assert.Equal(
            ["run", "folder", "save.bin"],
            request.RemotePath.Segments);
        Assert.Equal(42, request.ExpectedLength);
    }

    [Fact]
    public void ZeroLengthRequest_IsValid()
    {
        var request = GoogleDriveBinaryUploadRequest.Parse(
            ProfileId,
            "run/empty.bin",
            0);

        Assert.Equal(0, request.ExpectedLength);
    }

    [Fact]
    public void InvalidRequestInputs_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveBinaryUploadRequest.Parse(
                Guid.Empty,
                "run/save.bin",
                1));
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveBinaryUploadRequest(ProfileId, null!, 1));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryUploadRequest(
                ProfileId,
                GoogleDriveRelativePath.Root,
                1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "run/save.bin",
                -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/run/save.bin")]
    [InlineData("run/save.bin/")]
    [InlineData("run//save.bin")]
    [InlineData("run/../save.bin")]
    [InlineData("run/./save.bin")]
    public void InvalidRemoteTargets_AreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveBinaryUploadRequest.Parse(ProfileId, value, 1));
    }

    [Fact]
    public void StatusValues_AreStable()
    {
        Assert.Equal(0, (int)GoogleDriveBinaryUploadStatus.Completed);
        Assert.Equal(1, (int)GoogleDriveBinaryUploadStatus.Failed);
        Assert.Equal(2, (int)GoogleDriveBinaryUploadStatus.Indeterminate);
    }

    [Fact]
    public void CompletedResult_CarriesCompletedBytesOnly()
    {
        var result = new GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus.Completed,
            42);

        Assert.Equal(GoogleDriveBinaryUploadStatus.Completed, result.Status);
        Assert.Equal(42, result.CompletedBytes);
        Assert.Null(result.SafeErrorCode);
    }

    [Fact]
    public void FailedResult_CarriesFixedErrorCodeAndNoBytes()
    {
        var result = new GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus.Failed,
            0,
            GoogleDriveBinaryUploadErrorCodes.Failed);

        Assert.Equal(GoogleDriveBinaryUploadStatus.Failed, result.Status);
        Assert.Equal(0, result.CompletedBytes);
        Assert.Equal(
            "GoogleDriveBinaryUploadFailed",
            result.SafeErrorCode);
    }

    [Fact]
    public void IndeterminateResult_CarriesFixedErrorCodeAndNoBytes()
    {
        var result = new GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus.Indeterminate,
            0,
            GoogleDriveBinaryUploadErrorCodes.CompletionIndeterminate);

        Assert.Equal(
            GoogleDriveBinaryUploadStatus.Indeterminate,
            result.Status);
        Assert.Equal(0, result.CompletedBytes);
        Assert.Equal(
            "GoogleDriveUploadCompletionIndeterminate",
            result.SafeErrorCode);
    }

    [Fact]
    public void InconsistentResults_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Completed,
                1,
                GoogleDriveBinaryUploadErrorCodes.Failed));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Failed,
                1,
                GoogleDriveBinaryUploadErrorCodes.Failed));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Failed,
                0));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Failed,
                0,
                "unsafe/private/path"));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Indeterminate,
                0,
                GoogleDriveBinaryUploadErrorCodes.Failed));
    }

    [Fact]
    public void NegativeOrUnknownResults_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Completed,
                -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveBinaryUploadResult(
                (GoogleDriveBinaryUploadStatus)int.MaxValue,
                0));
    }

    [Fact]
    public void SafeFormatting_ContainsOnlyFixedStateAndCounts()
    {
        const string privatePath = "Private Folder/Personal Save.bin";
        var request = GoogleDriveBinaryUploadRequest.Parse(
            ProfileId,
            privatePath,
            42);
        var completed = new GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus.Completed,
            42);
        var failed = new GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus.Failed,
            0,
            GoogleDriveBinaryUploadErrorCodes.Failed);
        var indeterminate = new GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus.Indeterminate,
            0,
            GoogleDriveBinaryUploadErrorCodes.CompletionIndeterminate);

        Assert.Equal(
            "Google Drive binary upload request (segments=2; expectedBytes=42)",
            request.ToSafeDiagnosticString());
        Assert.Equal(request.ToSafeDiagnosticString(), request.ToString());
        Assert.Equal(
            "Google Drive binary upload: status=Completed; completedBytes=42",
            completed.ToSafeDiagnosticString());
        Assert.Equal(completed.ToSafeDiagnosticString(), completed.ToString());
        Assert.Equal(
            "Google Drive binary upload: status=Failed; completedBytes=0",
            failed.ToSafeDiagnosticString());
        Assert.Equal(failed.ToSafeDiagnosticString(), failed.ToString());
        Assert.Equal(
            "Google Drive binary upload: status=Indeterminate; " +
            "completedBytes=0",
            indeterminate.ToSafeDiagnosticString());

        string formatted = string.Join(
            Environment.NewLine,
            request,
            completed,
            failed,
            indeterminate);
        Assert.DoesNotContain(privatePath, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Folder", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Save.bin", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileId.ToString(), formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GoogleDriveBinaryUploadErrorCodes.Failed,
            formatted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Contracts_AreInfrastructureInternalImmutableAndSdkFree()
    {
        Type requestType = typeof(GoogleDriveBinaryUploadRequest);
        Type resultType = typeof(GoogleDriveBinaryUploadResult);
        Type[] contractTypes =
        [
            requestType,
            resultType,
            typeof(GoogleDriveBinaryUploadStatus),
            typeof(GoogleDriveBinaryUploadErrorCodes),
            typeof(GoogleDriveUploadCompletionIndeterminateException)
        ];

        Assert.True(requestType.IsSealed);
        Assert.True(resultType.IsSealed);
        Assert.All(contractTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
            AssertNoGoogleSdkType(type);
        });
        Assert.All(
            requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Concat(resultType.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance)),
            property => Assert.False(property.CanWrite));

        Type[] coreTypes = typeof(ISyncProvider).Assembly.GetTypes();
        Type[] appTypes = typeof(SyncViewModel).Assembly.GetTypes();
        Assert.All(contractTypes, contractType =>
        {
            Assert.DoesNotContain(coreTypes, type => type.Name == contractType.Name);
            Assert.DoesNotContain(appTypes, type => type.Name == contractType.Name);
        });
    }

    [Fact]
    public void ContractSource_UsesNeitherHostPathsNorGoogleSdkTypes()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveBinaryUpload.cs"));

        Assert.DoesNotContain("Path.Combine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectorySeparatorChar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AltDirectorySeparatorChar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using Google.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Google.Apis", source, StringComparison.Ordinal);
    }

    private static void AssertNoGoogleSdkType(Type type)
    {
        const BindingFlags members =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        IEnumerable<Type> exposedTypes = type.GetConstructors(members)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(type.GetProperties(members).Select(property => property.PropertyType))
            .Concat(type.GetMethods(members).Select(method => method.ReturnType));

        Assert.DoesNotContain(
            exposedTypes,
            exposed => exposed.Namespace?.StartsWith(
                "Google.",
                StringComparison.Ordinal) == true);
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
