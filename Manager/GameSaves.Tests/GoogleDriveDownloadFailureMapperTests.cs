using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Requests;
using System.Diagnostics;
using System.Net;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadFailureMapperTests
{
    private const string PrivateMarkers =
        "C:\\Users\\Someone\\Saves\\Personal Save.bin " +
        "Private Folder/Personal Save.bin 1a2b3c-private-object-id " +
        "nextPageToken=private-token 'root' in parents " +
        "https://www.googleapis.com/drive/v3/files/private?alt=media " +
        "access_token=ya29.private someone@example.invalid";

    [Fact]
    public void RawProviderFailures_DelegateClassificationToTheSharedMapper()
    {
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.ReauthenticationRequired,
            Classify(HttpStatusCode.Unauthorized).Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.AccessDenied,
            Classify(HttpStatusCode.Forbidden, "forbidden").Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.RateLimited,
            Classify(HttpStatusCode.TooManyRequests).Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.QuotaExceeded,
            Classify(HttpStatusCode.Forbidden, "storageQuotaExceeded").Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.Unavailable,
            Classify(HttpStatusCode.ServiceUnavailable).Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.SourceUnavailable,
            Classify(HttpStatusCode.NotFound).Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.Failed,
            Classify(HttpStatusCode.BadRequest).Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.Unavailable,
            GoogleDriveDownloadFailureMapper.Classify(
                new HttpRequestException(PrivateMarkers)).Category);
    }

    [Fact]
    public void ForbiddenResponses_AreNotTreatedAsRevokedAuthorization()
    {
        GoogleDriveDownloadFailureDetails details =
            Classify(HttpStatusCode.Forbidden, "insufficientFilePermissions");

        Assert.Equal(
            GoogleDriveDownloadFailureCategory.AccessDenied,
            details.Category);
        Assert.Equal("GoogleDriveDownloadAccessDenied", details.SafeErrorCode);
    }

    [Fact]
    public void EveryCategory_MapsToOneDistinctStableCode()
    {
        GoogleDriveDownloadFailureCategory[] categories =
            Enum.GetValues<GoogleDriveDownloadFailureCategory>();
        string[] codes = categories
            .Select(GoogleDriveDownloadErrorCodes.ForCategory)
            .ToArray();

        Assert.Equal(11, categories.Length);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                "GoogleDriveDownloadInvalidSourcePath",
                "GoogleDriveDownloadDestinationFailed",
                "GoogleDriveDownloadSourceNotFound",
                "GoogleDriveDownloadInvalidSourceMetadata",
                "GoogleDriveDownloadAuthenticationRequired",
                "GoogleDriveDownloadAccessDenied",
                "GoogleDriveDownloadRateLimited",
                "GoogleDriveDownloadQuotaExceeded",
                "GoogleDriveDownloadUnavailable",
                "GoogleDriveDownloadCancelled",
                "GoogleDriveBinaryDownloadFailed"
            ],
            codes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveDownloadErrorCodes.ForCategory(
                (GoogleDriveDownloadFailureCategory)int.MaxValue));
    }

    [Fact]
    public void StageFailures_KeepTheirOwnCategoryAndCode()
    {
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.DestinationUnavailable,
            GoogleDriveDownloadFailureMapper.Classify(
                new GoogleDriveLocalDownloadDestinationException(
                    GoogleDriveLocalDownloadDestinationFailure.AlreadyExists))
                .Category);
        Assert.Equal(
            "GoogleDriveDownloadDestinationExists",
            GoogleDriveDownloadFailureMapper.Classify(
                new GoogleDriveLocalDownloadDestinationException(
                    GoogleDriveLocalDownloadDestinationFailure.AlreadyExists))
                .SafeErrorCode);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.InvalidCompletion,
            GoogleDriveDownloadFailureMapper.Classify(
                new GoogleDriveDownloadCompletionException(
                    GoogleDriveDownloadCompletionFailure.SizeMismatch))
                .Category);
        Assert.Equal(
            "GoogleDriveDownloadSizeMismatch",
            GoogleDriveDownloadFailureMapper.Classify(
                new GoogleDriveDownloadCompletionException(
                    GoogleDriveDownloadCompletionFailure.SizeMismatch))
                .SafeErrorCode);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.Cancelled,
            GoogleDriveDownloadFailureMapper.Classify(
                new OperationCanceledException(PrivateMarkers)).Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.InvalidRequest,
            GoogleDriveDownloadFailureMapper.Classify(
                new ArgumentException(PrivateMarkers)).Category);
    }

    [Theory]
    [InlineData("GoogleDriveDownloadSourceNotFound")]
    [InlineData("GoogleDriveDownloadSourceAmbiguous")]
    [InlineData("GoogleDriveDownloadSourceCaseCollision")]
    [InlineData("GoogleDriveDownloadSourceTypeCollision")]
    [InlineData("GoogleDriveDownloadSourceUnsupportedObject")]
    public void SourceResolutionFailures_KeepTheirCategoryAndCode(string code)
    {
        GoogleDriveDownloadFailureDetails details =
            GoogleDriveDownloadFailureMapper.Classify(RemoteFailure(code));

        Assert.Equal(
            GoogleDriveDownloadFailureCategory.SourceUnavailable,
            details.Category);
        Assert.Equal(code, details.SafeErrorCode);
    }

    [Fact]
    public void ListingFailures_MapToSourceOrProviderCategories()
    {
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.SourceUnavailable,
            GoogleDriveDownloadFailureMapper.Classify(
                GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                    GoogleDriveRecursiveFileListingStatus.TrashedObject))
                .Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.ReauthenticationRequired,
            GoogleDriveDownloadFailureMapper.Classify(
                GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                    GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired))
                .Category);
        Assert.Equal(
            GoogleDriveDownloadFailureCategory.RateLimited,
            GoogleDriveDownloadFailureMapper.Classify(
                GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                    GoogleDriveRecursiveFileListingStatus.RateLimited))
                .Category);
    }

    [Fact]
    public void OnlyRateLimitedAndUnavailable_AreRetryable()
    {
        Assert.True(Classify(HttpStatusCode.TooManyRequests).Retryable);
        Assert.True(Classify(HttpStatusCode.ServiceUnavailable).Retryable);
        Assert.False(Classify(HttpStatusCode.Forbidden, "forbidden").Retryable);
        Assert.False(
            GoogleDriveDownloadFailureMapper.Classify(
                new OperationCanceledException()).Retryable);
    }

    [Fact]
    public void SanitizedFailures_ArePreservedAndOthersReplaced()
    {
        Exception[] preserved =
        [
            new OperationCanceledException(),
            new GoogleDriveLocalDownloadDestinationException(
                GoogleDriveLocalDownloadDestinationFailure.AlreadyExists),
            new GoogleDriveDownloadCompletionException(
                GoogleDriveDownloadCompletionFailure.SizeMismatch),
            RemoteFailure("GoogleDriveDownloadSourceNotFound"),
            GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata)
        ];

        Assert.All(preserved, exception => Assert.Same(
            exception,
            GoogleDriveDownloadFailureMapper.ToSafeException(
                exception,
                GoogleDriveDownloadFailureMapper.Classify(exception))));

        var provider = ProviderError(HttpStatusCode.Forbidden, "forbidden");
        Exception safe = GoogleDriveDownloadFailureMapper.ToSafeException(
            provider,
            GoogleDriveDownloadFailureMapper.Classify(provider));
        var remote = Assert.IsType<GoogleDriveRemoteOperationException>(safe);
        Assert.Equal(
            "GoogleDriveDownloadAccessDenied",
            remote.Result.ErrorCode);
        Assert.Null(remote.InnerException);
    }

    [Fact]
    public void EscapingFailureSurfaces_ExposeNoPrivateValue()
    {
        Exception[] unclassified =
        [
            ProviderError(HttpStatusCode.Forbidden, "forbidden"),
            ProviderError(HttpStatusCode.NotFound, "private-reason-marker"),
            new HttpRequestException(PrivateMarkers),
            new IOException(PrivateMarkers),
            new InvalidOperationException(
                PrivateMarkers,
                new IOException(PrivateMarkers))
        ];

        foreach (Exception exception in unclassified)
        {
            GoogleDriveDownloadFailureDetails details =
                GoogleDriveDownloadFailureMapper.Classify(exception);
            Exception safe = GoogleDriveDownloadFailureMapper.ToSafeException(
                exception,
                details);

            AssertNoPrivateValue(string.Join(
                Environment.NewLine,
                details.ToSafeDiagnosticString(),
                details.SafeUserMessage,
                safe.Message,
                safe.ToString()));
            Assert.Null(safe.InnerException);
        }
    }

    [Fact]
    public void LifecycleLogging_WritesOnlyStagesCodesAndByteCounts()
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Started);
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Transferred,
                4096);
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Failed,
                0,
                GoogleDriveDownloadFailureMapper.Classify(
                    new IOException(PrivateMarkers)));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        string written = string.Join(Environment.NewLine, listener.Messages);
        Assert.Contains("stage=Started", written, StringComparison.Ordinal);
        Assert.Contains("bytes=4096", written, StringComparison.Ordinal);
        Assert.Contains("stage=Failed", written, StringComparison.Ordinal);
        Assert.Contains(
            "code=GoogleDriveBinaryDownloadFailed",
            written,
            StringComparison.Ordinal);
        AssertNoPrivateValue(written);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Started,
                -1));
    }

    [Fact]
    public void MapperSource_AddsNoSecondHttpOrReasonClassifier()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveDownloadFailure.cs"));

        Assert.Contains(
            "GoogleDriveApiFailureMapper.Map(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpStatusCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleApiException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rateLimitExceeded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("quotaExceeded", source, StringComparison.Ordinal);
    }

    private static void AssertNoPrivateValue(string formatted)
    {
        string[] privateValues =
        [
            "C:\\Users\\Someone",
            "Personal Save.bin",
            "Private Folder",
            "1a2b3c-private-object-id",
            "private-token",
            "in parents",
            "googleapis.com",
            "alt=media",
            "access_token",
            "ya29.private",
            "someone@example.invalid",
            "private-reason-marker"
        ];

        Assert.All(privateValues, value => Assert.DoesNotContain(
            value,
            formatted,
            StringComparison.OrdinalIgnoreCase));
    }

    private static GoogleDriveDownloadFailureDetails Classify(
        HttpStatusCode status,
        string? reason = null) =>
        GoogleDriveDownloadFailureMapper.Classify(ProviderError(status, reason));

    private static GoogleApiException ProviderError(
        HttpStatusCode status,
        string? reason) =>
        new("Drive", PrivateMarkers)
        {
            HttpStatusCode = status,
            Error = reason is null
                ? null
                : new RequestError
                {
                    Errors = new List<SingleError> { new() { Reason = reason } }
                }
        };

    private static GoogleDriveRemoteOperationException RemoteFailure(
        string errorCode) =>
        new(new GoogleDriveRemoteValidationResult(
            GoogleDriveRemoteValidationStatus.Failed,
            errorCode,
            "The Google Drive download could not be completed.",
            retryable: false,
            rootDisplayName: null,
            wasAuthenticationRefreshed: false,
            cacheInvalidated: false));

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

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
            if (message is not null)
                Messages.Add(message);
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
                Messages.Add(message);
        }
    }
}
