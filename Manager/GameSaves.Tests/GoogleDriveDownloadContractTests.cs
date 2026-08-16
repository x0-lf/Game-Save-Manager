using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadContractTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("3d5f2b41-9a7c-4a2e-8f61-2c0b5e7d9a44");

    [Fact]
    public void ValidRequest_PreservesCanonicalInputAndExactName()
    {
        var request = GoogleDriveBinaryDownloadRequest.Parse(
            ProfileId,
            "run/folder/save.bin");

        Assert.Equal(ProfileId, request.RemoteProfileId);
        Assert.Equal("run/folder/save.bin", request.CanonicalRemotePath);
        Assert.Equal(["run", "folder", "save.bin"], request.RemotePath.Segments);
        Assert.Equal("save.bin", request.ExactFileName);
    }

    [Fact]
    public void InvalidRequestInputs_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveBinaryDownloadRequest.Parse(Guid.Empty, "run/save.bin"));
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveBinaryDownloadRequest(ProfileId, null!));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryDownloadRequest(
                ProfileId,
                GoogleDriveRelativePath.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/run/save.bin")]
    [InlineData("run/save.bin/")]
    [InlineData("run//save.bin")]
    [InlineData("run/../save.bin")]
    [InlineData("run/./save.bin")]
    public void InvalidRemoteSources_AreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveBinaryDownloadRequest.Parse(ProfileId, value));
    }

    [Fact]
    public void StatusValues_AreStable()
    {
        Assert.Equal(0, (int)GoogleDriveBinaryDownloadStatus.Completed);
        Assert.Equal(1, (int)GoogleDriveBinaryDownloadStatus.Failed);
        Assert.Equal(2, Enum.GetValues<GoogleDriveBinaryDownloadStatus>().Length);
    }

    [Fact]
    public void CompletedResult_CarriesCompletedBytesOnly()
    {
        var result = new GoogleDriveBinaryDownloadResult(
            GoogleDriveBinaryDownloadStatus.Completed,
            42);

        Assert.Equal(GoogleDriveBinaryDownloadStatus.Completed, result.Status);
        Assert.Equal(42, result.CompletedBytes);
        Assert.Null(result.SafeErrorCode);
    }

    [Fact]
    public void ZeroByteCompletion_IsValid()
    {
        var result = new GoogleDriveBinaryDownloadResult(
            GoogleDriveBinaryDownloadStatus.Completed,
            0);

        Assert.Equal(0, result.CompletedBytes);
        Assert.Null(result.SafeErrorCode);
    }

    [Fact]
    public void FailedResult_CarriesAFixedCodeAndNoBytes()
    {
        var result = new GoogleDriveBinaryDownloadResult(
            GoogleDriveBinaryDownloadStatus.Failed,
            0,
            GoogleDriveBinaryDownloadErrorCodes.Failed);

        Assert.Equal(0, result.CompletedBytes);
        Assert.Equal("GoogleDriveBinaryDownloadFailed", result.SafeErrorCode);
    }

    [Fact]
    public void InconsistentResults_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Completed,
                1,
                GoogleDriveBinaryDownloadErrorCodes.Failed));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Failed,
                1,
                GoogleDriveBinaryDownloadErrorCodes.Failed));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Failed,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Completed,
                -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveBinaryDownloadResult(
                (GoogleDriveBinaryDownloadStatus)int.MaxValue,
                0));
    }

    [Fact]
    public void ErrorCodes_AreDistinctAndStable()
    {
        string[] codes =
        [
            GoogleDriveBinaryDownloadErrorCodes.Failed,
            GoogleDriveBinaryDownloadErrorCodes.InvalidSourcePath,
            GoogleDriveBinaryDownloadErrorCodes.DestinationExists
        ];

        Assert.Equal(
            [
                "GoogleDriveBinaryDownloadFailed",
                "GoogleDriveDownloadInvalidSourcePath",
                "GoogleDriveDownloadDestinationExists"
            ],
            codes);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SafeFormatting_ContainsOnlyFixedStateAndCounts()
    {
        const string privatePath = "Private Folder/Personal Save.bin";
        var request = GoogleDriveBinaryDownloadRequest.Parse(
            ProfileId,
            privatePath);
        var completed = new GoogleDriveBinaryDownloadResult(
            GoogleDriveBinaryDownloadStatus.Completed,
            42);
        var failed = new GoogleDriveBinaryDownloadResult(
            GoogleDriveBinaryDownloadStatus.Failed,
            0,
            GoogleDriveBinaryDownloadErrorCodes.Failed);

        Assert.Equal(
            "Google Drive binary download request (segments=2)",
            request.ToSafeDiagnosticString());
        Assert.Equal(request.ToSafeDiagnosticString(), request.ToString());
        Assert.Equal(
            "Google Drive binary download: status=Completed; completedBytes=42",
            completed.ToString());
        Assert.Equal(
            "Google Drive binary download: status=Failed; completedBytes=0",
            failed.ToString());

        string formatted = string.Join(
            Environment.NewLine,
            request,
            completed,
            failed);
        Assert.DoesNotContain(privatePath, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Folder", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Save.bin", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileId.ToString(), formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GoogleDriveBinaryDownloadErrorCodes.Failed,
            formatted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Contracts_AreInfrastructureInternalImmutableAndSdkFree()
    {
        Type requestType = typeof(GoogleDriveBinaryDownloadRequest);
        Type resultType = typeof(GoogleDriveBinaryDownloadResult);
        Type[] contractTypes =
        [
            requestType,
            resultType,
            typeof(GoogleDriveBinaryDownloadStatus),
            typeof(GoogleDriveBinaryDownloadErrorCodes)
        ];

        Assert.True(requestType.IsSealed);
        Assert.True(resultType.IsSealed);
        Assert.All(contractTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
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
            "GoogleDriveBinaryDownload.cs"));

        Assert.DoesNotContain("Path.Combine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectorySeparatorChar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using Google.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Google.Apis", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRemainsUnwiredAtTheRemoteBoundary()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveRemoteFileSystem.cs"));

        Assert.Contains(
            "public Task<long> DownloadFileAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Unsupported<long>();", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GoogleDriveBinaryDownloadRequest",
            source,
            StringComparison.Ordinal);
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
            "Could not locate Manager.sln from the test output directory.");
    }
}
