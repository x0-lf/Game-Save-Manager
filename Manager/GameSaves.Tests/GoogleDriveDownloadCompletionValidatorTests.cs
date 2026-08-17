using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadCompletionValidatorTests
{
    private const string FileId = "file-id";
    private const string ParentId = "parent-id";
    private const string ExactName = "save.bin";
    private const long ExpectedLength = 2048;

    [Fact]
    public void MatchingMetadataAndLength_Pass()
    {
        GoogleDriveDownloadCompletionValidator.Validate(
            Metadata(),
            Source(),
            ExpectedLength);
    }

    [Fact]
    public void ZeroByteSource_Passes()
    {
        GoogleDriveDownloadCompletionValidator.Validate(
            Metadata(size: 0),
            Source(),
            0);
    }

    [Fact]
    public void InvalidExpectedInputs_AreRejectedBeforeValidation()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GoogleDriveDownloadCompletionValidator.Validate(
                Metadata(),
                null!,
                ExpectedLength));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveDownloadCompletionValidator.Validate(
                Metadata(),
                Source(),
                -1));
    }

    [Fact]
    public void MissingMetadata_FailsClosedWithFixedCode()
    {
        GoogleDriveMediaDownloadMetadata?[] responses =
        [
            null,
            Metadata(id: " "),
            Metadata(name: null),
            Metadata(mimeType: null),
            Metadata(trashed: null),
            new GoogleDriveMediaDownloadMetadata(
                FileId,
                ExactName,
                "application/octet-stream",
                trashed: false,
                parentIds: null,
                driveId: null,
                size: ExpectedLength),
            Metadata(size: null)
        ];

        Assert.All(responses, response => AssertFailure(
            response,
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.InvalidMetadata,
            GoogleDriveDownloadCompletionErrorCodes.InvalidMetadata));
    }

    [Fact]
    public void ChangedIdentity_FailsClosedWithFixedCode()
    {
        AssertFailure(
            Metadata(id: "other-file-id"),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.IdentityMismatch,
            GoogleDriveDownloadCompletionErrorCodes.IdentityMismatch);
    }

    [Theory]
    [InlineData("SAVE.BIN")]
    [InlineData("renamed.bin")]
    public void ChangedName_FailsClosedWithFixedCode(string name)
    {
        AssertFailure(
            Metadata(name: name),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.NameMismatch,
            GoogleDriveDownloadCompletionErrorCodes.NameMismatch);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.document")]
    [InlineData("application/vnd.google-apps.folder")]
    [InlineData("application/vnd.google-apps.shortcut")]
    [InlineData("not-a-mime-type")]
    public void UnsupportedSourceType_FailsClosedWithFixedCode(string mimeType)
    {
        AssertFailure(
            Metadata(mimeType: mimeType),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.UnsupportedType,
            GoogleDriveDownloadCompletionErrorCodes.UnsupportedType);
    }

    [Fact]
    public void TrashedSource_FailsClosedWithFixedCode()
    {
        AssertFailure(
            Metadata(trashed: true),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.Trashed,
            GoogleDriveDownloadCompletionErrorCodes.Trashed);
    }

    [Fact]
    public void SharedDriveOrUnexpectedParent_FailsClosedWithFixedCode()
    {
        AssertFailure(
            Metadata(driveId: "shared-drive-id"),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.UnsupportedLocation,
            GoogleDriveDownloadCompletionErrorCodes.UnsupportedLocation);
        AssertFailure(
            Metadata(parentIds: ["other-parent-id"]),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.UnsupportedLocation,
            GoogleDriveDownloadCompletionErrorCodes.UnsupportedLocation);
        AssertFailure(
            Metadata(parentIds: [ParentId, "second-parent-id"]),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.UnsupportedLocation,
            GoogleDriveDownloadCompletionErrorCodes.UnsupportedLocation);
    }

    [Theory]
    [InlineData(ExpectedLength - 1)]
    [InlineData(ExpectedLength + 1)]
    [InlineData(0)]
    public void ShortOrLongBody_FailsClosedWithFixedCode(long writtenBytes)
    {
        AssertFailure(
            Metadata(),
            Source(),
            writtenBytes,
            GoogleDriveDownloadCompletionFailure.SizeMismatch,
            GoogleDriveDownloadCompletionErrorCodes.SizeMismatch);
    }

    [Fact]
    public void NegativeReportedSize_FailsClosedWithFixedCode()
    {
        AssertFailure(
            Metadata(size: -1),
            Source(),
            ExpectedLength,
            GoogleDriveDownloadCompletionFailure.SizeMismatch,
            GoogleDriveDownloadCompletionErrorCodes.SizeMismatch);
    }

    [Fact]
    public void RejectionTaxonomy_IsStableDistinctAndSafe()
    {
        GoogleDriveDownloadCompletionFailure[] failures =
            Enum.GetValues<GoogleDriveDownloadCompletionFailure>();
        string[] codes = failures
            .Select(GoogleDriveDownloadCompletionErrorCodes.ForFailure)
            .ToArray();

        Assert.Equal(
            [
                "GoogleDriveDownloadInvalidSourceMetadata",
                "GoogleDriveDownloadIdentityMismatch",
                "GoogleDriveDownloadNameMismatch",
                "GoogleDriveDownloadUnsupportedSourceType",
                "GoogleDriveDownloadSourceTrashed",
                "GoogleDriveDownloadUnsupportedSourceLocation",
                "GoogleDriveDownloadSizeMismatch"
            ],
            codes);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(failures, failure =>
        {
            var exception = new GoogleDriveDownloadCompletionException(failure);
            string formatted = string.Join(
                Environment.NewLine,
                exception.Message,
                exception.ToString());

            Assert.DoesNotContain(FileId, formatted, StringComparison.Ordinal);
            Assert.DoesNotContain(ParentId, formatted, StringComparison.Ordinal);
            Assert.DoesNotContain(ExactName, formatted, StringComparison.Ordinal);
            Assert.DoesNotContain("2048", formatted, StringComparison.Ordinal);
            Assert.Null(exception.InnerException);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveDownloadCompletionErrorCodes.ForFailure(
                (GoogleDriveDownloadCompletionFailure)int.MaxValue));
    }

    [Fact]
    public void ValidatorSource_IssuesNoFurtherRequestAndPlacesNothing()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveDownloadCompletionValidator.cs"));

        Assert.DoesNotContain("await ", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Client", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", source, StringComparison.Ordinal);
    }

    private static void AssertFailure(
        GoogleDriveMediaDownloadMetadata? metadata,
        GoogleDriveDownloadSource source,
        long writtenBytes,
        GoogleDriveDownloadCompletionFailure expectedFailure,
        string expectedCode)
    {
        GoogleDriveDownloadCompletionException exception =
            Assert.Throws<GoogleDriveDownloadCompletionException>(() =>
                GoogleDriveDownloadCompletionValidator.Validate(
                    metadata,
                    source,
                    writtenBytes));

        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal(expectedCode, exception.SafeErrorCode);
    }

    private static GoogleDriveDownloadSource Source() =>
        new(FileId, ParentId, ExactName, "application/octet-stream");

    private static GoogleDriveMediaDownloadMetadata Metadata(
        string? id = FileId,
        string? name = ExactName,
        string? mimeType = "application/octet-stream",
        bool? trashed = false,
        string[]? parentIds = null,
        string? driveId = null,
        long? size = ExpectedLength) =>
        new(
            id,
            name,
            mimeType,
            trashed,
            parentIds ?? [ParentId],
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
