using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadResponseValidatorTests
{
    private const string ExpectedParent = "expected-parent";
    private const string ExpectedName = "expected-name";
    private const long ExpectedLength = 42;

    [Fact]
    public void ValidIdentityExactNameAndOpaqueMime_Pass()
    {
        GoogleDriveUploadResponseValidator.Validate(
            ValidResponse(),
            ExpectedParent,
            ExpectedName,
            ExpectedLength);
    }

    [Fact]
    public void InvalidExpectedInputs_AreRejectedBeforeResponseValidation()
    {
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveUploadResponseValidator.Validate(
                ValidResponse(),
                " ",
                ExpectedName,
                ExpectedLength));
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveUploadResponseValidator.Validate(
                ValidResponse(),
                ExpectedParent,
                "",
                ExpectedLength));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveUploadResponseValidator.Validate(
                ValidResponse(),
                ExpectedParent,
                ExpectedName,
                -1));
    }

    [Fact]
    public void MissingIdentityNameOrMime_FailsClosedWithFixedCode()
    {
        GoogleDriveMediaUploadMetadata?[] responses =
        [
            null,
            ValidResponse(id: null),
            ValidResponse(id: " "),
            ValidResponse(name: null),
            ValidResponse(mimeType: null)
        ];

        Assert.All(responses, response => AssertFailure(
            response,
            GoogleDriveUploadResponseFailure.InvalidResponse,
            GoogleDriveUploadResponseErrorCodes.InvalidResponse));
    }

    [Fact]
    public void NameMismatch_FailsClosedWithFixedCode()
    {
        AssertFailure(
            ValidResponse(name: "different-name"),
            GoogleDriveUploadResponseFailure.NameMismatch,
            GoogleDriveUploadResponseErrorCodes.NameMismatch);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.folder")]
    [InlineData("application/vnd.google-apps.document")]
    [InlineData("application/vnd.google-apps.shortcut")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-mime-type")]
    public void NonBlobResponseType_FailsClosedWithFixedCode(string mimeType)
    {
        AssertFailure(
            ValidResponse(mimeType: mimeType),
            GoogleDriveUploadResponseFailure.MimeTypeMismatch,
            GoogleDriveUploadResponseErrorCodes.MimeTypeMismatch);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("Application/Octet-Stream")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public void ProviderAssignedBlobType_RemainsValid(string mimeType)
    {
        GoogleDriveUploadResponseValidator.Validate(
            ValidResponse(mimeType: mimeType),
            ExpectedParent,
            ExpectedName,
            ExpectedLength);
    }

    [Fact]
    public void MissingTrashParentOrSize_FailsClosedWithFixedCode()
    {
        GoogleDriveMediaUploadMetadata[] responses =
        [
            new(
                "response-id",
                ExpectedName,
                "application/octet-stream",
                trashed: null,
                [ExpectedParent],
                driveId: null,
                ExpectedLength),
            new(
                "response-id",
                ExpectedName,
                "application/octet-stream",
                trashed: false,
                parentIds: null,
                driveId: null,
                ExpectedLength),
            new(
                "response-id",
                ExpectedName,
                "application/octet-stream",
                trashed: false,
                [ExpectedParent],
                driveId: null,
                size: null)
        ];

        Assert.All(responses, response => AssertFailure(
            response,
            GoogleDriveUploadResponseFailure.InvalidResponse,
            GoogleDriveUploadResponseErrorCodes.InvalidResponse));
    }

    [Fact]
    public void MissingDifferentOrMultipleParents_FailClosedWithFixedCode()
    {
        IEnumerable<string>[] parents =
        [
            [],
            ["different-parent"],
            [ExpectedParent, "second-parent"]
        ];

        Assert.All(parents, parentIds => AssertFailure(
            ValidResponse(parentIds: parentIds),
            GoogleDriveUploadResponseFailure.ParentMismatch,
            GoogleDriveUploadResponseErrorCodes.ParentMismatch));
    }

    [Fact]
    public void TrashedResponse_FailsClosedWithFixedCode()
    {
        AssertFailure(
            ValidResponse(trashed: true),
            GoogleDriveUploadResponseFailure.Trashed,
            GoogleDriveUploadResponseErrorCodes.Trashed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("shared-drive")]
    public void DriveIdResponse_FailsClosedWithFixedCode(string driveId)
    {
        AssertFailure(
            ValidResponse(driveId: driveId),
            GoogleDriveUploadResponseFailure.UnsupportedLocation,
            GoogleDriveUploadResponseErrorCodes.UnsupportedLocation);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ExpectedLength - 1)]
    [InlineData(ExpectedLength + 1)]
    public void SizeMismatch_FailsClosedWithFixedCode(long size)
    {
        AssertFailure(
            ValidResponse(size: size),
            GoogleDriveUploadResponseFailure.SizeMismatch,
            GoogleDriveUploadResponseErrorCodes.SizeMismatch);
    }

    [Fact]
    public void RejectionTaxonomy_IsStableAndDistinct()
    {
        (GoogleDriveUploadResponseFailure Failure, int Value, string Code)[]
            expected =
            [
                (
                    GoogleDriveUploadResponseFailure.InvalidResponse,
                    0,
                    "GoogleDriveUploadInvalidResponse"),
                (
                    GoogleDriveUploadResponseFailure.NameMismatch,
                    1,
                    "GoogleDriveUploadNameMismatch"),
                (
                    GoogleDriveUploadResponseFailure.MimeTypeMismatch,
                    2,
                    "GoogleDriveUploadMimeTypeMismatch"),
                (
                    GoogleDriveUploadResponseFailure.ParentMismatch,
                    3,
                    "GoogleDriveUploadParentMismatch"),
                (
                    GoogleDriveUploadResponseFailure.Trashed,
                    4,
                    "GoogleDriveUploadTrashed"),
                (
                    GoogleDriveUploadResponseFailure.UnsupportedLocation,
                    5,
                    "GoogleDriveUploadUnsupportedLocation"),
                (
                    GoogleDriveUploadResponseFailure.SizeMismatch,
                    6,
                    "GoogleDriveUploadSizeMismatch")
            ];

        Assert.All(expected, item =>
        {
            Assert.Equal(item.Value, (int)item.Failure);
            Assert.Equal(
                item.Code,
                GoogleDriveUploadResponseErrorCodes.ForFailure(item.Failure));
        });
        Assert.Equal(
            expected.Length,
            expected.Select(item => item.Code).Distinct(
                StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveUploadResponseErrorCodes.ForFailure(
                (GoogleDriveUploadResponseFailure)int.MaxValue));
    }

    [Fact]
    public void RejectionDiagnostics_ContainOnlyFixedSafeState()
    {
        const string privateId = "private-id-marker";
        const string privateName = "private-name-marker";
        const string privateParent = "private-parent-marker";
        const string privateDrive = "private-drive-marker";
        var response = new GoogleDriveMediaUploadMetadata(
            privateId,
            privateName,
            "application/octet-stream",
            trashed: false,
            [privateParent],
            privateDrive,
            ExpectedLength);

        GoogleDriveUploadResponseException exception = Assert.Throws<
            GoogleDriveUploadResponseException>(() =>
                GoogleDriveUploadResponseValidator.Validate(
                    response,
                    ExpectedParent,
                    ExpectedName,
                    ExpectedLength));
        string formatted = string.Join(
            Environment.NewLine,
            exception.Message,
            exception.SafeErrorCode,
            exception.ToString());

        Assert.DoesNotContain(privateId, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(privateName, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(privateParent, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(privateDrive, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_IsSynchronousSdkFreeAndCannotIssueAnotherRequest()
    {
        Type[] boundaryTypes =
        [
            typeof(GoogleDriveUploadResponseFailure),
            typeof(GoogleDriveUploadResponseErrorCodes),
            typeof(GoogleDriveUploadResponseException),
            typeof(GoogleDriveUploadResponseValidator)
        ];
        Assert.All(boundaryTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal(
                "GameSaves.Infrastructure.GoogleDrive",
                type.Namespace);
        });

        MethodInfo validate = Assert.Single(
            typeof(GoogleDriveUploadResponseValidator).GetMethods(
                BindingFlags.Public | BindingFlags.Static));

        Assert.Equal(typeof(void), validate.ReturnType);
        Assert.All(validate.GetParameters(), parameter =>
            Assert.False(parameter.ParameterType.Namespace?.StartsWith(
                "Google.",
                StringComparison.Ordinal) == true));

        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveUploadResponseValidator.cs"));

        Assert.DoesNotContain("DriveService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FilesResource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Google.Apis", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Cache", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", source, StringComparison.Ordinal);
    }

    private static void AssertFailure(
        GoogleDriveMediaUploadMetadata? response,
        GoogleDriveUploadResponseFailure expectedFailure,
        string expectedCode)
    {
        GoogleDriveUploadResponseException exception = Assert.Throws<
            GoogleDriveUploadResponseException>(() =>
                GoogleDriveUploadResponseValidator.Validate(
                    response,
                    ExpectedParent,
                    ExpectedName,
                    ExpectedLength));

        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal(expectedCode, exception.SafeErrorCode);
    }

    private static GoogleDriveMediaUploadMetadata ValidResponse(
        string? id = "response-id",
        string? name = ExpectedName,
        string? mimeType = "application/octet-stream",
        bool? trashed = false,
        IEnumerable<string>? parentIds = null,
        string? driveId = null,
        long? size = ExpectedLength) =>
        new(
            id,
            name,
            mimeType,
            trashed,
            parentIds ?? [ExpectedParent],
            driveId,
            size);

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
