using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Requests;
using System.Net;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadFailureMapperTests
{
    private const string PrivateMarkers =
        "C:\\Users\\Someone\\Saves\\Personal Save.bin " +
        "Private Folder/Personal Save.bin 1a2b3c-private-object-id " +
        "nextPageToken=private-token 'root' in parents " +
        "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable" +
        "&upload_id=private-session access_token=ya29.private " +
        "refresh_token=1//private someone@example.invalid";

    [Fact]
    public void RawProviderFailures_DelegateClassificationToTheSharedMapper()
    {
        Assert.Equal(
            GoogleDriveUploadFailureCategory.ReauthenticationRequired,
            Classify(HttpStatusCode.Unauthorized).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.AccessDenied,
            Classify(HttpStatusCode.Forbidden).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.AccessDenied,
            Classify(HttpStatusCode.Forbidden, "insufficientFilePermissions")
                .Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.RateLimited,
            Classify(HttpStatusCode.TooManyRequests).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.RateLimited,
            Classify(HttpStatusCode.Forbidden, "userRateLimitExceeded")
                .Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.QuotaExceeded,
            Classify(HttpStatusCode.Forbidden, "storageQuotaExceeded")
                .Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.Unavailable,
            Classify(HttpStatusCode.ServiceUnavailable).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.ParentPreparation,
            Classify(HttpStatusCode.NotFound).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.Failed,
            Classify(HttpStatusCode.BadRequest).Category);
    }

    [Fact]
    public void ForbiddenResponses_AreNotTreatedAsRevokedAuthorization()
    {
        GoogleDriveUploadFailureDetails accessDenied =
            Classify(HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal(
            GoogleDriveUploadFailureCategory.AccessDenied,
            accessDenied.Category);
        Assert.Equal(
            "GoogleDriveUploadAccessDenied",
            accessDenied.SafeErrorCode);
        Assert.NotEqual(
            GoogleDriveUploadFailureCategory.ReauthenticationRequired,
            accessDenied.Category);
    }

    [Fact]
    public void TransportAndUnexpectedFailures_UseTheSharedClassification()
    {
        Assert.Equal(
            GoogleDriveUploadFailureCategory.Unavailable,
            GoogleDriveUploadFailureMapper.Classify(
                new HttpRequestException(PrivateMarkers)).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.Unavailable,
            GoogleDriveUploadFailureMapper.Classify(
                new TimeoutException(PrivateMarkers)).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.Failed,
            GoogleDriveUploadFailureMapper.Classify(
                new IOException(PrivateMarkers)).Category);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.Failed,
            GoogleDriveUploadFailureMapper.Classify(
                new InvalidOperationException(PrivateMarkers)).Category);
    }

    [Fact]
    public void MapperSource_AddsNoSecondHttpOrReasonClassifier()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveUploadFailure.cs"));

        Assert.Contains(
            "GoogleDriveApiFailureMapper.Map(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpStatusCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleApiException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using Google.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rateLimitExceeded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("quotaExceeded", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCategory_MapsToOneDistinctStableCode()
    {
        GoogleDriveUploadFailureCategory[] categories =
            Enum.GetValues<GoogleDriveUploadFailureCategory>();
        string[] codes = categories
            .Select(GoogleDriveUploadErrorCodes.ForCategory)
            .ToArray();

        Assert.Equal(12, categories.Length);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
        Assert.Equal(
            [
                "GoogleDriveUploadSourceFailed",
                "GoogleDriveUploadParentFailed",
                "GoogleDriveUploadTargetCollision",
                "GoogleDriveUploadInvalidResponse",
                "GoogleDriveUploadAuthenticationRequired",
                "GoogleDriveUploadAccessDenied",
                "GoogleDriveUploadRateLimited",
                "GoogleDriveUploadQuotaExceeded",
                "GoogleDriveUploadUnavailable",
                "GoogleDriveUploadCancelled",
                "GoogleDriveUploadCompletionIndeterminate",
                "GoogleDriveBinaryUploadFailed"
            ],
            codes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveUploadErrorCodes.ForCategory(
                (GoogleDriveUploadFailureCategory)int.MaxValue));
    }

    [Fact]
    public void OnlyRateLimitedAndUnavailable_AreRetryable()
    {
        Assert.All(
            Enum.GetValues<GoogleDriveUploadFailureCategory>(),
            category =>
            {
                bool expected =
                    category is GoogleDriveUploadFailureCategory.RateLimited or
                        GoogleDriveUploadFailureCategory.Unavailable;
                Assert.Equal(expected, Classify(category).Retryable);
            });
    }

    public static TheoryData<int, string> SourceFailures => new()
    {
        { (int)GoogleDriveLocalUploadSourceFailure.InvalidPath,
            "GoogleDriveUploadSourceInvalidPath" },
        { (int)GoogleDriveLocalUploadSourceFailure.NotFound,
            "GoogleDriveUploadSourceNotFound" },
        { (int)GoogleDriveLocalUploadSourceFailure.NotRegularFile,
            "GoogleDriveUploadSourceNotRegularFile" },
        { (int)GoogleDriveLocalUploadSourceFailure.ReparsePoint,
            "GoogleDriveUploadSourceReparsePoint" },
        { (int)GoogleDriveLocalUploadSourceFailure.Unreadable,
            "GoogleDriveUploadSourceUnreadable" },
        { (int)GoogleDriveLocalUploadSourceFailure.InvalidLength,
            "GoogleDriveUploadSourceInvalidLength" },
        { (int)GoogleDriveLocalUploadSourceFailure.Failed,
            "GoogleDriveUploadSourceFailed" }
    };

    [Theory]
    [MemberData(nameof(SourceFailures))]
    public void SourceFailures_KeepTheirStageCategoryAndCode(
        int failureValue,
        string expectedCode)
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(
                new GoogleDriveLocalUploadSourceException(
                    (GoogleDriveLocalUploadSourceFailure)failureValue));

        Assert.Equal(
            GoogleDriveUploadFailureCategory.InvalidSource,
            details.Category);
        Assert.Equal(expectedCode, details.SafeErrorCode);
    }

    public static TheoryData<int, string> ResponseFailures => new()
    {
        { (int)GoogleDriveUploadResponseFailure.InvalidResponse,
            "GoogleDriveUploadInvalidResponse" },
        { (int)GoogleDriveUploadResponseFailure.NameMismatch,
            "GoogleDriveUploadNameMismatch" },
        { (int)GoogleDriveUploadResponseFailure.MimeTypeMismatch,
            "GoogleDriveUploadMimeTypeMismatch" },
        { (int)GoogleDriveUploadResponseFailure.ParentMismatch,
            "GoogleDriveUploadParentMismatch" },
        { (int)GoogleDriveUploadResponseFailure.Trashed,
            "GoogleDriveUploadTrashed" },
        { (int)GoogleDriveUploadResponseFailure.UnsupportedLocation,
            "GoogleDriveUploadUnsupportedLocation" },
        { (int)GoogleDriveUploadResponseFailure.SizeMismatch,
            "GoogleDriveUploadSizeMismatch" }
    };

    [Theory]
    [MemberData(nameof(ResponseFailures))]
    public void ResponseFailures_KeepTheirStageCategoryAndCode(
        int failureValue,
        string expectedCode)
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(
                new GoogleDriveUploadResponseException(
                    (GoogleDriveUploadResponseFailure)failureValue));

        Assert.Equal(
            GoogleDriveUploadFailureCategory.InvalidResponse,
            details.Category);
        Assert.Equal(expectedCode, details.SafeErrorCode);
    }

    [Theory]
    [InlineData("GoogleDriveUploadTargetAlreadyExists")]
    [InlineData("GoogleDriveUploadTargetCaseCollision")]
    [InlineData("GoogleDriveUploadTargetTypeCollision")]
    public void TargetGuardFailures_KeepTheirCollisionCategoryAndCode(
        string errorCode)
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(RemoteFailure(errorCode));

        Assert.Equal(
            GoogleDriveUploadFailureCategory.TargetCollision,
            details.Category);
        Assert.Equal(errorCode, details.SafeErrorCode);
    }

    [Theory]
    [InlineData("GoogleDriveUploadParentAmbiguous")]
    [InlineData("GoogleDriveUploadParentCaseCollision")]
    [InlineData("GoogleDriveUploadParentTypeCollision")]
    [InlineData("GoogleDriveUploadParentUnsupportedObject")]
    [InlineData("GoogleDriveUploadParentUnsupportedLocation")]
    [InlineData("GoogleDriveUploadParentInvalidMetadata")]
    [InlineData("GoogleDriveUploadParentCreateFailed")]
    [InlineData("GoogleDriveUploadParentInvalidCreateResponse")]
    [InlineData("GoogleDriveUploadParentCacheRejected")]
    public void ParentPreparationFailures_KeepTheirCategoryAndCode(
        string errorCode)
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(RemoteFailure(errorCode));

        Assert.Equal(
            GoogleDriveUploadFailureCategory.ParentPreparation,
            details.Category);
        Assert.Equal(errorCode, details.SafeErrorCode);
    }

    [Fact]
    public void CacheRejection_RemainsAnUnexpectedUploadFailure()
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(
                RemoteFailure("GoogleDriveUploadCacheRejected"));

        Assert.Equal(GoogleDriveUploadFailureCategory.Failed, details.Category);
        Assert.Equal("GoogleDriveUploadCacheRejected", details.SafeErrorCode);
    }

    public static TheoryData<int, int> ValidationStatusCategories => new()
    {
        { (int)GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            (int)GoogleDriveUploadFailureCategory.ReauthenticationRequired },
        { (int)GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
            (int)GoogleDriveUploadFailureCategory.ReauthenticationRequired },
        { (int)GoogleDriveRemoteValidationStatus.NotConnected,
            (int)GoogleDriveUploadFailureCategory.ReauthenticationRequired },
        { (int)GoogleDriveRemoteValidationStatus.UnsupportedScope,
            (int)GoogleDriveUploadFailureCategory.ReauthenticationRequired },
        { (int)GoogleDriveRemoteValidationStatus.AuthenticationCorrupted,
            (int)GoogleDriveUploadFailureCategory.ReauthenticationRequired },
        { (int)GoogleDriveRemoteValidationStatus.RootInaccessible,
            (int)GoogleDriveUploadFailureCategory.AccessDenied },
        { (int)GoogleDriveRemoteValidationStatus.RootCannotListChildren,
            (int)GoogleDriveUploadFailureCategory.AccessDenied },
        { (int)GoogleDriveRemoteValidationStatus.RootCannotAddChildren,
            (int)GoogleDriveUploadFailureCategory.AccessDenied },
        { (int)GoogleDriveRemoteValidationStatus.RootMissing,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation },
        { (int)GoogleDriveRemoteValidationStatus.RootTrashed,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation },
        { (int)GoogleDriveRemoteValidationStatus.RootWrongType,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation },
        { (int)GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation },
        { (int)GoogleDriveRemoteValidationStatus.RateLimited,
            (int)GoogleDriveUploadFailureCategory.RateLimited },
        { (int)GoogleDriveRemoteValidationStatus.QuotaExceeded,
            (int)GoogleDriveUploadFailureCategory.QuotaExceeded },
        { (int)GoogleDriveRemoteValidationStatus.Unavailable,
            (int)GoogleDriveUploadFailureCategory.Unavailable },
        { (int)GoogleDriveRemoteValidationStatus.AuthenticationUnavailable,
            (int)GoogleDriveUploadFailureCategory.Unavailable },
        { (int)GoogleDriveRemoteValidationStatus.Cancelled,
            (int)GoogleDriveUploadFailureCategory.Cancelled },
        { (int)GoogleDriveRemoteValidationStatus.Failed,
            (int)GoogleDriveUploadFailureCategory.Failed }
    };

    [Theory]
    [MemberData(nameof(ValidationStatusCategories))]
    public void ProviderValidationFailures_MapToTheirUploadCategory(
        int statusValue,
        int expectedCategoryValue)
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(
                new GoogleDriveRemoteOperationException(
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        (GoogleDriveRemoteValidationStatus)statusValue)));

        Assert.Equal(
            (GoogleDriveUploadFailureCategory)expectedCategoryValue,
            details.Category);
        Assert.False(string.IsNullOrWhiteSpace(details.SafeErrorCode));
    }

    public static TheoryData<int, int, string> ListingFailures => new()
    {
        { (int)GoogleDriveRecursiveFileListingStatus.Ambiguous,
            (int)GoogleDriveUploadFailureCategory.TargetCollision,
            "GoogleDriveFileListingAmbiguous" },
        { (int)GoogleDriveRecursiveFileListingStatus.CaseCollision,
            (int)GoogleDriveUploadFailureCategory.TargetCollision,
            "GoogleDriveFileListingCaseCollision" },
        { (int)GoogleDriveRecursiveFileListingStatus.TypeCollision,
            (int)GoogleDriveUploadFailureCategory.TargetCollision,
            "GoogleDriveFileListingTypeCollision" },
        { (int)GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation,
            "GoogleDriveFileListingInvalidMetadata" },
        { (int)GoogleDriveRecursiveFileListingStatus.UnsupportedObject,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation,
            "GoogleDriveFileListingUnsupportedObject" },
        { (int)GoogleDriveRecursiveFileListingStatus.TrashedObject,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation,
            "GoogleDriveFileListingTrashed" },
        { (int)GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
            (int)GoogleDriveUploadFailureCategory.ParentPreparation,
            "GoogleDriveFileListingUnsupportedLocation" },
        { (int)GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
            (int)GoogleDriveUploadFailureCategory.ReauthenticationRequired,
            "GoogleDriveFileListingAuthenticationRequired" },
        { (int)GoogleDriveRecursiveFileListingStatus.AccessDenied,
            (int)GoogleDriveUploadFailureCategory.AccessDenied,
            "GoogleDriveFileListingAccessDenied" },
        { (int)GoogleDriveRecursiveFileListingStatus.RateLimited,
            (int)GoogleDriveUploadFailureCategory.RateLimited,
            "GoogleDriveFileListingRateLimited" },
        { (int)GoogleDriveRecursiveFileListingStatus.QuotaExceeded,
            (int)GoogleDriveUploadFailureCategory.QuotaExceeded,
            "GoogleDriveFileListingQuotaExceeded" },
        { (int)GoogleDriveRecursiveFileListingStatus.Unavailable,
            (int)GoogleDriveUploadFailureCategory.Unavailable,
            "GoogleDriveFileListingUnavailable" },
        { (int)GoogleDriveRecursiveFileListingStatus.Cancelled,
            (int)GoogleDriveUploadFailureCategory.Cancelled,
            "GoogleDriveFileListingCancelled" },
        { (int)GoogleDriveRecursiveFileListingStatus.Failed,
            (int)GoogleDriveUploadFailureCategory.Failed,
            "GoogleDriveFileListingFailed" }
    };

    [Theory]
    [MemberData(nameof(ListingFailures))]
    public void ChildEnumerationFailures_KeepTheirCategoryAndCode(
        int statusValue,
        int expectedCategoryValue,
        string expectedCode)
    {
        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(
                GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                    (GoogleDriveRecursiveFileListingStatus)statusValue));

        Assert.Equal(
            (GoogleDriveUploadFailureCategory)expectedCategoryValue,
            details.Category);
        Assert.Equal(expectedCode, details.SafeErrorCode);
    }

    [Fact]
    public void CancellationAndIndeterminateCompletion_HaveTheirOwnCategories()
    {
        GoogleDriveUploadFailureDetails cancelled =
            GoogleDriveUploadFailureMapper.Classify(
                new OperationCanceledException(PrivateMarkers));
        GoogleDriveUploadFailureDetails indeterminate =
            GoogleDriveUploadFailureMapper.Classify(
                new GoogleDriveUploadCompletionIndeterminateException());

        Assert.Equal(
            GoogleDriveUploadFailureCategory.Cancelled,
            cancelled.Category);
        Assert.Equal("GoogleDriveUploadCancelled", cancelled.SafeErrorCode);
        Assert.Equal(
            GoogleDriveUploadFailureCategory.IndeterminateCompletion,
            indeterminate.Category);
        Assert.Equal(
            "GoogleDriveUploadCompletionIndeterminate",
            indeterminate.SafeErrorCode);
    }

    [Fact]
    public void SafeExceptions_AndCancellation_ArePreservedUnchanged()
    {
        Exception[] preserved =
        [
            new OperationCanceledException(),
            new GoogleDriveLocalUploadSourceException(
                GoogleDriveLocalUploadSourceFailure.NotFound),
            new GoogleDriveUploadResponseException(
                GoogleDriveUploadResponseFailure.SizeMismatch),
            new GoogleDriveUploadCompletionIndeterminateException(),
            RemoteFailure("GoogleDriveUploadTargetCaseCollision"),
            GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata)
        ];

        Assert.All(preserved, exception => Assert.Same(
            exception,
            GoogleDriveUploadFailureMapper.ToSafeException(
                exception,
                GoogleDriveUploadFailureMapper.Classify(exception))));
    }

    [Fact]
    public void UnclassifiedFailures_BecomeFixedSafeRemoteFailures()
    {
        var providerError = ProviderError(
            HttpStatusCode.Forbidden,
            "storageQuotaExceeded");

        GoogleDriveUploadFailureDetails details =
            GoogleDriveUploadFailureMapper.Classify(providerError);
        Exception safe = GoogleDriveUploadFailureMapper.ToSafeException(
            providerError,
            details);

        var remote = Assert.IsType<GoogleDriveRemoteOperationException>(safe);
        Assert.Equal(
            GoogleDriveRemoteValidationStatus.QuotaExceeded,
            remote.Result.Status);
        Assert.Equal("GoogleDriveUploadQuotaExceeded", remote.Result.ErrorCode);
        Assert.False(remote.Result.Retryable);
        Assert.Null(remote.Result.RootDisplayName);
        Assert.Null(remote.InnerException);
    }

    [Fact]
    public void EscapingFailureSurfaces_ExposeNoPrivateValue()
    {
        Exception[] unclassified =
        [
            ProviderError(HttpStatusCode.Forbidden, "forbidden"),
            ProviderError(HttpStatusCode.Unauthorized, "invalidCredentials"),
            ProviderError(HttpStatusCode.TooManyRequests, "rateLimitExceeded"),
            ProviderError(HttpStatusCode.ServiceUnavailable, "backendError"),
            ProviderError(HttpStatusCode.NotFound, "private-reason-marker"),
            new HttpRequestException(PrivateMarkers),
            new IOException(PrivateMarkers),
            new InvalidOperationException(
                PrivateMarkers,
                new IOException(PrivateMarkers))
        ];

        foreach (Exception exception in unclassified)
        {
            GoogleDriveUploadFailureDetails details =
                GoogleDriveUploadFailureMapper.Classify(exception);
            Exception safe = GoogleDriveUploadFailureMapper.ToSafeException(
                exception,
                details);

            AssertNoPrivateValue(string.Join(
                Environment.NewLine,
                details.ToSafeDiagnosticString(),
                details.ToString(),
                safe.Message,
                safe.ToString(),
                safe.InnerException?.ToString() ?? string.Empty));
            Assert.Null(safe.InnerException);
        }
    }

    [Fact]
    public void SanitizedStageFailures_ExposeNoPrivateValue()
    {
        Exception[] staged =
        [
            new GoogleDriveLocalUploadSourceException(
                GoogleDriveLocalUploadSourceFailure.Unreadable),
            new GoogleDriveUploadResponseException(
                GoogleDriveUploadResponseFailure.NameMismatch),
            new GoogleDriveUploadCompletionIndeterminateException(),
            RemoteFailure("GoogleDriveUploadTargetAlreadyExists"),
            GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                GoogleDriveRecursiveFileListingStatus.Ambiguous)
        ];

        foreach (Exception exception in staged)
        {
            GoogleDriveUploadFailureDetails details =
                GoogleDriveUploadFailureMapper.Classify(exception);

            AssertNoPrivateValue(string.Join(
                Environment.NewLine,
                details.ToSafeDiagnosticString(),
                exception.Message,
                exception.ToString(),
                details.SafeUserMessage));
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void EverySafeUserMessage_IsFixedAndFreeOfPrivateValues()
    {
        Assert.All(
            Enum.GetValues<GoogleDriveUploadFailureCategory>(),
            category =>
            {
                GoogleDriveUploadFailureDetails details = Classify(category);
                Assert.False(
                    string.IsNullOrWhiteSpace(details.SafeUserMessage));
                AssertNoPrivateValue(details.SafeUserMessage);
            });
    }

    private static void AssertNoPrivateValue(string formatted)
    {
        string[] privateValues =
        [
            "C:\\Users\\Someone\\Saves\\Personal Save.bin",
            "Personal Save.bin",
            "Private Folder",
            "1a2b3c-private-object-id",
            "private-token",
            "in parents",
            "upload_id",
            "uploadType=resumable",
            "googleapis.com",
            "access_token",
            "refresh_token",
            "ya29.private",
            "someone@example.invalid",
            "private-reason-marker"
        ];

        Assert.All(privateValues, value => Assert.DoesNotContain(
            value,
            formatted,
            StringComparison.OrdinalIgnoreCase));
    }

    private static GoogleDriveUploadFailureDetails Classify(
        GoogleDriveUploadFailureCategory category) =>
        category switch
        {
            GoogleDriveUploadFailureCategory.InvalidSource =>
                GoogleDriveUploadFailureMapper.Classify(
                    new GoogleDriveLocalUploadSourceException(
                        GoogleDriveLocalUploadSourceFailure.Failed)),
            GoogleDriveUploadFailureCategory.ParentPreparation =>
                GoogleDriveUploadFailureMapper.Classify(
                    RemoteFailure("GoogleDriveUploadParentCreateFailed")),
            GoogleDriveUploadFailureCategory.TargetCollision =>
                GoogleDriveUploadFailureMapper.Classify(
                    RemoteFailure("GoogleDriveUploadTargetAlreadyExists")),
            GoogleDriveUploadFailureCategory.InvalidResponse =>
                GoogleDriveUploadFailureMapper.Classify(
                    new GoogleDriveUploadResponseException(
                        GoogleDriveUploadResponseFailure.InvalidResponse)),
            GoogleDriveUploadFailureCategory.ReauthenticationRequired =>
                GoogleDriveUploadFailureMapper.Classify(
                    ProviderError(HttpStatusCode.Unauthorized, "authError")),
            GoogleDriveUploadFailureCategory.AccessDenied =>
                GoogleDriveUploadFailureMapper.Classify(
                    ProviderError(HttpStatusCode.Forbidden, "forbidden")),
            GoogleDriveUploadFailureCategory.RateLimited =>
                GoogleDriveUploadFailureMapper.Classify(ProviderError(
                    HttpStatusCode.TooManyRequests,
                    "rateLimitExceeded")),
            GoogleDriveUploadFailureCategory.QuotaExceeded =>
                GoogleDriveUploadFailureMapper.Classify(ProviderError(
                    HttpStatusCode.Forbidden,
                    "quotaExceeded")),
            GoogleDriveUploadFailureCategory.Unavailable =>
                GoogleDriveUploadFailureMapper.Classify(ProviderError(
                    HttpStatusCode.ServiceUnavailable,
                    "backendError")),
            GoogleDriveUploadFailureCategory.Cancelled =>
                GoogleDriveUploadFailureMapper.Classify(
                    new OperationCanceledException()),
            GoogleDriveUploadFailureCategory.IndeterminateCompletion =>
                GoogleDriveUploadFailureMapper.Classify(
                    new GoogleDriveUploadCompletionIndeterminateException()),
            GoogleDriveUploadFailureCategory.Failed =>
                GoogleDriveUploadFailureMapper.Classify(
                    new InvalidOperationException(PrivateMarkers)),
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

    private static GoogleDriveUploadFailureDetails Classify(
        HttpStatusCode status,
        string? reason = null) =>
        GoogleDriveUploadFailureMapper.Classify(ProviderError(status, reason));

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
            "The Google Drive upload could not be completed.",
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
}
