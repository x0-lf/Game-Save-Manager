using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Requests;
using System.Net;

namespace GameSaves.Tests;

public sealed class GoogleDriveRemoteValidationTests
{
    public static IEnumerable<object?[]> StatusMappings()
    {
        yield return Row(GoogleDriveRemoteValidationStatus.Valid, 0, null);
        yield return Row(GoogleDriveRemoteValidationStatus.ProfileNotFound, 1,
            "GoogleDriveProfileNotFound");
        yield return Row(GoogleDriveRemoteValidationStatus.WrongProviderKind, 2,
            "GoogleDriveWrongProvider");
        yield return Row(GoogleDriveRemoteValidationStatus.UnsupportedScope, 3,
            "GoogleDriveUnsupportedScope");
        yield return Row(GoogleDriveRemoteValidationStatus.NotConnected, 4,
            "GoogleDriveNotConnected");
        yield return Row(GoogleDriveRemoteValidationStatus.AuthenticationUnavailable, 5,
            "GoogleDriveAuthenticationUnavailable");
        yield return Row(GoogleDriveRemoteValidationStatus.AuthenticationCorrupted, 6,
            "GoogleDriveAuthenticationCorrupted");
        yield return Row(GoogleDriveRemoteValidationStatus.AuthorizationRevoked, 7,
            "GoogleDriveAuthorizationRevoked");
        yield return Row(GoogleDriveRemoteValidationStatus.ReauthenticationRequired, 8,
            "GoogleDriveReauthenticationRequired");
        yield return Row(GoogleDriveRemoteValidationStatus.RootNotConfigured, 9,
            "GoogleDriveRootNotConfigured");
        yield return Row(GoogleDriveRemoteValidationStatus.RootMissing, 10,
            "GoogleDriveRootMissing");
        yield return Row(GoogleDriveRemoteValidationStatus.RootTrashed, 11,
            "GoogleDriveRootTrashed");
        yield return Row(GoogleDriveRemoteValidationStatus.RootWrongType, 12,
            "GoogleDriveRootWrongType");
        yield return Row(GoogleDriveRemoteValidationStatus.RootMoved, 13,
            "GoogleDriveRootMoved");
        yield return Row(GoogleDriveRemoteValidationStatus.RootUnsupportedLocation, 14,
            "GoogleDriveRootUnsupportedLocation");
        yield return Row(GoogleDriveRemoteValidationStatus.RootInaccessible, 15,
            "GoogleDriveRootInaccessible");
        yield return Row(GoogleDriveRemoteValidationStatus.RootCannotListChildren, 16,
            "GoogleDriveRootCannotListChildren");
        yield return Row(GoogleDriveRemoteValidationStatus.RootCannotAddChildren, 17,
            "GoogleDriveRootCannotAddChildren");
        yield return Row(GoogleDriveRemoteValidationStatus.RateLimited, 18,
            "GoogleDriveRateLimited");
        yield return Row(GoogleDriveRemoteValidationStatus.QuotaExceeded, 19,
            "GoogleDriveQuotaExceeded");
        yield return Row(GoogleDriveRemoteValidationStatus.Unavailable, 20,
            "GoogleDriveUnavailable");
        yield return Row(GoogleDriveRemoteValidationStatus.Cancelled, 21,
            "GoogleDriveValidationCancelled");
        yield return Row(GoogleDriveRemoteValidationStatus.Superseded, 22,
            "GoogleDriveValidationSuperseded");
        yield return Row(GoogleDriveRemoteValidationStatus.Failed, 23,
            "GoogleDriveValidationFailed");
    }

    [Theory]
    [MemberData(nameof(StatusMappings))]
    public void Statuses_HaveStableValuesAndSafeWarningMappings(
        int statusValue,
        int expectedValue,
        string? expectedCode)
    {
        var status = (GoogleDriveRemoteValidationStatus)statusValue;
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromStatus(status);
        TransferPreviewWarning? warning =
            GoogleDriveRemoteValidationMapper.ToTransferPreviewWarning(result);

        Assert.Equal(expectedValue, (int)status);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.UserMessage));

        if (status == GoogleDriveRemoteValidationStatus.Valid)
        {
            Assert.Null(warning);
        }
        else
        {
            Assert.NotNull(warning);
            Assert.Equal(expectedCode, warning.Code);
            Assert.Equal(result.UserMessage, warning.Message);
            Assert.Equal(
                status == GoogleDriveRemoteValidationStatus.RootMoved
                    ? TransferWarningSeverity.Warning
                    : TransferWarningSeverity.Error,
                warning.Severity);
        }
    }

    [Fact]
    public void EveryDefinedStatus_HasExactlyOneDeclaredMapping()
    {
        GoogleDriveRemoteValidationStatus[] statuses =
            Enum.GetValues<GoogleDriveRemoteValidationStatus>();
        object?[][] mappings = StatusMappings().ToArray();

        Assert.Equal(statuses.Length, mappings.Length);
        Assert.Equal(
            statuses,
            mappings.Select(row =>
                (GoogleDriveRemoteValidationStatus)(int)row[0]!).ToArray());
    }

    [Theory]
    [MemberData(nameof(StatusMappings))]
    public void ResultMessagesAndWarnings_DoNotExposeInjectedDisplayOrProviderValues(
        int statusValue,
        int expectedValue,
        string? expectedCode)
    {
        var status = (GoogleDriveRemoteValidationStatus)statusValue;
        const string privateDisplayName =
            "private-folder-id-marker token-marker user@example.invalid";
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromStatus(
                status,
                privateDisplayName,
                wasAuthenticationRefreshed: true,
                cacheInvalidated: true);
        TransferPreviewWarning? warning =
            GoogleDriveRemoteValidationMapper.ToTransferPreviewWarning(result);

        Assert.Equal(expectedValue, (int)result.Status);
        Assert.Equal(privateDisplayName, result.RootDisplayName);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.DoesNotContain("private-folder-id-marker", result.UserMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("token-marker", result.UserMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", result.UserMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-folder-id-marker", result.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("token-marker", result.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", result.ToString(),
            StringComparison.Ordinal);

        if (warning is not null)
        {
            Assert.DoesNotContain("private-folder-id-marker", warning.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("token-marker", warning.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("example.invalid", warning.Message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToString_ContainsOnlyFixedStatusAndBooleanState()
    {
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.RootMissing,
                "private-root-id-marker",
                wasAuthenticationRefreshed: true,
                cacheInvalidated: true);

        Assert.Equal(
            "Google Drive remote validation: status=RootMissing; " +
            "retryable=False; authenticationRefreshed=True; cacheInvalidated=True",
            result.ToString());
        Assert.DoesNotContain(result.ErrorCode!, result.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(result.UserMessage, result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaExceeded_IsDistinctFromRateLimited()
    {
        GoogleDriveRemoteValidationResult quota = ApiFailure(
            GoogleDriveApiFailure.QuotaExceeded,
            retryable: false);
        GoogleDriveRemoteValidationResult rate = ApiFailure(
            GoogleDriveApiFailure.RateLimited,
            retryable: true);

        Assert.Equal(GoogleDriveRemoteValidationStatus.QuotaExceeded, quota.Status);
        Assert.Equal(GoogleDriveRemoteValidationErrorCodes.QuotaExceeded, quota.ErrorCode);
        Assert.False(quota.Retryable);
        Assert.Equal(GoogleDriveRemoteValidationStatus.RateLimited, rate.Status);
        Assert.Equal(GoogleDriveRemoteValidationErrorCodes.RateLimited, rate.ErrorCode);
        Assert.True(rate.Retryable);
    }

    [Fact]
    public void AuthorizationRevoked_IsDistinctFromAccessDenied()
    {
        GoogleDriveRemoteValidationResult revoked = ApiFailure(
            GoogleDriveApiFailure.AuthorizationRevoked,
            retryable: false);
        GoogleDriveRemoteValidationResult denied = ApiFailure(
            GoogleDriveApiFailure.AccessDenied,
            retryable: false);

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            revoked.Status);
        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootInaccessible,
            denied.Status);
        Assert.NotEqual(revoked.ErrorCode, denied.ErrorCode);
    }

    [Fact]
    public void GenericForbiddenProviderResponse_DoesNotBecomeRevocation()
    {
        var providerError = new GoogleApiException(
            "Drive",
            "private request URL and object-id-marker")
        {
            HttpStatusCode = HttpStatusCode.Forbidden,
            Error = new RequestError()
        };
        GoogleDriveApiException classified = GoogleDriveApiFailureMapper.Map(
            providerError,
            GoogleDriveApiOperation.ObjectMetadataGet,
            _ => "safe-code");

        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(classified.Details);

        Assert.Equal(GoogleDriveApiFailure.AccessDenied, classified.Failure);
        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootInaccessible,
            result.Status);
        Assert.NotEqual(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            result.Status);
        Assert.DoesNotContain("object-id-marker", result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelled_IsDistinctFromFailed()
    {
        GoogleDriveRemoteValidationResult cancelled =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Cancelled);
        GoogleDriveRemoteValidationResult failed =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Failed);

        Assert.NotEqual(cancelled.Status, failed.Status);
        Assert.NotEqual(cancelled.ErrorCode, failed.ErrorCode);
    }

    [Fact]
    public void Superseded_IsDistinctFromCancelled()
    {
        GoogleDriveRemoteValidationResult superseded =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Superseded);
        GoogleDriveRemoteValidationResult cancelled =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Cancelled);

        Assert.NotEqual(superseded.Status, cancelled.Status);
        Assert.NotEqual(superseded.ErrorCode, cancelled.ErrorCode);
    }

    [Fact]
    public void RootMoved_HasExplicitNonBlockingWarningMapping()
    {
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromRootFolderResult(
                new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Moved,
                    Guid.Parse("10101010-2020-3030-4040-505050505050"),
                    FolderId: "private-root-id-marker",
                    DisplayName: "Renamed folder",
                    WasMoved: true));
        TransferPreviewWarning warning = Assert.IsType<TransferPreviewWarning>(
            GoogleDriveRemoteValidationMapper.ToTransferPreviewWarning(result));

        Assert.Equal(GoogleDriveRemoteValidationStatus.RootMoved, result.Status);
        Assert.Equal(TransferWarningSeverity.Warning, warning.Severity);
        Assert.DoesNotContain("private-root-id-marker", warning.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CannotListAndCannotAddChildren_AreDistinct()
    {
        GoogleDriveRemoteValidationResult cannotList =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.RootCannotListChildren);
        GoogleDriveRemoteValidationResult cannotAdd =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.RootCannotAddChildren);

        Assert.NotEqual(cannotList.Status, cannotAdd.Status);
        Assert.NotEqual(cannotList.ErrorCode, cannotAdd.ErrorCode);
        Assert.Contains("read", cannotList.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create", cannotAdd.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionFailures_UseTheCentralValidationTranslation()
    {
        var mappings = new[]
        {
            (GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication,
                GoogleDriveRemoteValidationStatus.NotConnected),
            (GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable,
                GoogleDriveRemoteValidationStatus.AuthenticationUnavailable),
            (GoogleDriveAuthorizedSessionFailure.TokenCorrupted,
                GoogleDriveRemoteValidationStatus.AuthenticationCorrupted),
            (GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked,
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked),
            (GoogleDriveAuthorizedSessionFailure.ReauthenticationRequired,
                GoogleDriveRemoteValidationStatus.ReauthenticationRequired)
        };

        Assert.All(mappings, mapping =>
        {
            GoogleDriveRemoteValidationResult result =
                GoogleDriveRemoteValidationMapper.FromSessionFailure(mapping.Item1);
            Assert.Equal(mapping.Item2, result.Status);
        });
    }

    [Fact]
    public void ObjectResolutionFailures_UseTheCentralValidationTranslation()
    {
        var mappings = new[]
        {
            (GoogleDriveObjectResolutionStatus.NotFound,
                GoogleDriveRemoteValidationStatus.RootMissing),
            (GoogleDriveObjectResolutionStatus.Trashed,
                GoogleDriveRemoteValidationStatus.RootTrashed),
            (GoogleDriveObjectResolutionStatus.TypeMismatch,
                GoogleDriveRemoteValidationStatus.RootWrongType),
            (GoogleDriveObjectResolutionStatus.UnsupportedLocation,
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation)
        };

        Assert.All(mappings, mapping =>
        {
            var resolution = new GoogleDriveObjectResolutionResult(mapping.Item1);
            GoogleDriveRemoteValidationResult result =
                GoogleDriveRemoteValidationMapper.FromObjectResolution(resolution);
            Assert.Equal(mapping.Item2, result.Status);
        });
    }

    [Fact]
    public void CoreAndAppRemainFreeOfGoogleSdkAssemblyReferences()
    {
        string[] coreReferences = typeof(SyncProviderKind).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        string[] appReferences = typeof(SyncViewModel).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            coreReferences,
            name => name.StartsWith("Google.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            appReferences,
            name => name.StartsWith("Google.", StringComparison.Ordinal));
    }

    private static object?[] Row(
        GoogleDriveRemoteValidationStatus status,
        int expectedValue,
        string? code) =>
        new object?[] { (int)status, expectedValue, code };

    private static GoogleDriveRemoteValidationResult ApiFailure(
        GoogleDriveApiFailure failure,
        bool retryable) =>
        GoogleDriveRemoteValidationMapper.FromApiFailure(
            new GoogleDriveApiFailureDetails(
                GoogleDriveApiOperation.ObjectMetadataGet,
                null,
                null,
                failure,
                "untrusted-provider-code-object-id-marker",
                retryable));
}
