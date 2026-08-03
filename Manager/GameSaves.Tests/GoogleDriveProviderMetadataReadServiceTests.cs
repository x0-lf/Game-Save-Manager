using System.Text;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveProviderMetadataReadServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("4c165875-92bf-43c6-b574-d0860528c06c");

    [Theory]
    [InlineData("manifest.json")]
    [InlineData(".gamesave-sync/other.json")]
    [InlineData(".GAMESAVE-SYNC/sync-log.json")]
    [InlineData("/.gamesave-sync/sync-log.json")]
    [InlineData(".gamesave-sync/sync-log.json/extra")]
    public async Task NonAllowlistedPath_IsRejectedBeforeAuthenticationOrDriveWork(
        string relativePath)
    {
        var contexts = new RecordingContextFactory(new RecordingResolver());
        var content = new RecordingTextContentApi();
        var service = Service(contexts, content);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReadAsync(ProfileId, relativePath));

        Assert.Equal(0, contexts.CreateCalls);
        Assert.Equal(0, content.Calls);
        Assert.Equal(0, contexts.Resolver.EnsureCalls);
    }

    [Theory]
    [InlineData("metadata folder")]
    [InlineData("metadata file")]
    public async Task AbsentMetadataFolderOrFile_ReturnsNullWithoutCreation(
        string missingObject)
    {
        var resolver = new RecordingResolver
        {
            Result = Resolution(GoogleDriveObjectResolutionStatus.NotFound)
        };
        var contexts = new RecordingContextFactory(resolver);
        var content = new RecordingTextContentApi();
        var service = Service(contexts, content);

        string? result = await service.ReadAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(missingObject));
        Assert.Equal(RemoteProviderMetadataPath.SyncLog,
            Assert.Single(resolver.ResolveCalls).Path.Canonical);
        Assert.Equal(0, content.Calls);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    [Fact]
    public async Task ValidMetadata_IsReadThroughTheBoundedStrictUtf8Reader()
    {
        const string expected = "{\"runs\":[\"保存\"]}";
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(resolver);
        var content = new RecordingTextContentApi
        {
            Content = Encoding.UTF8.GetBytes(expected)
        };
        var service = Service(contexts, content);

        string? result = await service.ReadAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog);

        Assert.Equal(expected, result);
        ResolveCall call = Assert.Single(resolver.ResolveCalls);
        Assert.Equal(RecordingContextFactory.RootId, call.RootFolderId);
        Assert.Equal(RemoteProviderMetadataPath.SyncLog, call.Path.Canonical);
        Assert.Equal(GoogleDriveObjectKind.File, call.ExpectedKind);
        Assert.Equal("resolved-metadata-id", Assert.Single(content.FileIds));
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    [Theory]
    [InlineData("duplicate metadata folders")]
    [InlineData("duplicate metadata files")]
    public async Task DuplicateMetadataObjects_FailClosedWithoutDownloadOrCreation(
        string ambiguitySource)
    {
        var resolver = new RecordingResolver
        {
            Result = new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Ambiguous,
                GoogleDriveRelativePath.Parse(RemoteProviderMetadataPath.SyncLog),
                GoogleDriveObjectKind.File,
                errorCode: GoogleDriveObjectResolutionErrorCodes.Ambiguous,
                message: "The metadata path is ambiguous.")
        };
        var contexts = new RecordingContextFactory(resolver);
        var content = new RecordingTextContentApi();
        var service = Service(contexts, content);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog));

        Assert.Equal(
            GoogleDriveObjectResolutionErrorCodes.Ambiguous,
            exception.Result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(ambiguitySource));
        Assert.Equal(0, content.Calls);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.DoesNotContain("resolved-metadata-id", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongObjectType_FailsClosedWithoutDownloadOrCreation()
    {
        var resolver = new RecordingResolver
        {
            Result = new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.TypeMismatch,
                GoogleDriveRelativePath.Parse(RemoteProviderMetadataPath.SyncLog),
                GoogleDriveObjectKind.Folder,
                objectId: "private-folder-id",
                errorCode: GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
                message: "The metadata object has the wrong type.")
        };
        var contexts = new RecordingContextFactory(resolver);
        var content = new RecordingTextContentApi();
        var service = Service(contexts, content);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog));

        Assert.Equal(
            GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
            exception.Result.ErrorCode);
        Assert.Equal(0, content.Calls);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.DoesNotContain("private-folder-id", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUtf8_FailsWithTheExistingSafeStableError()
    {
        var contexts = new RecordingContextFactory(new RecordingResolver());
        var content = new RecordingTextContentApi { Content = [0xC3, 0x28] };
        var service = Service(contexts, content);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog));

        Assert.Equal(
            GoogleDriveTextFileReadErrorCodes.InvalidUtf8,
            exception.Result.ErrorCode);
        Assert.Equal(0, contexts.Resolver.EnsureCalls);
        Assert.DoesNotContain(Convert.ToHexString(content.Content),
            exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedMetadata_PreservesTheBoundedDownloadError()
    {
        var contexts = new RecordingContextFactory(new RecordingResolver());
        var content = new RecordingTextContentApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentMetadataGet,
                GoogleDriveApiFailure.Failed,
                GoogleDriveTextContentErrorCodes.DeclaredSizeTooLarge,
                retryable: false)
        };
        var service = Service(contexts, content);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog));

        Assert.Equal(
            GoogleDriveTextContentErrorCodes.DeclaredSizeTooLarge,
            exception.Result.ErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Equal(0, contexts.Resolver.EnsureCalls);
        Assert.DoesNotContain("resolved-metadata-id", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationFailure_IsNotConvertedToMissingMetadata()
    {
        var contexts = new RecordingContextFactory(new RecordingResolver())
        {
            Failure = Failure(
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
                "GoogleDriveAuthorizationRevoked",
                retryable: false)
        };
        var content = new RecordingTextContentApi();
        var service = Service(contexts, content);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            exception.Result.Status);
        Assert.Equal("GoogleDriveAuthorizationRevoked", exception.Result.ErrorCode);
        Assert.Equal(0, content.Calls);
        Assert.Empty(contexts.Resolver.ResolveCalls);
        Assert.Equal(0, contexts.Resolver.EnsureCalls);
    }

    [Fact]
    public async Task TemporaryProviderFailure_IsMappedSafelyInsteadOfReturningNull()
    {
        var contexts = new RecordingContextFactory(new RecordingResolver());
        var content = new RecordingTextContentApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentDownload,
                GoogleDriveApiFailure.Unavailable,
                GoogleDriveRemoteValidationErrorCodes.Unavailable,
                retryable: true)
        };
        var service = Service(contexts, content);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog));

        Assert.Equal(
            GoogleDriveRemoteValidationErrorCodes.Unavailable,
            exception.Result.ErrorCode);
        Assert.True(exception.Result.Retryable);
        Assert.Equal(0, contexts.Resolver.EnsureCalls);
        Assert.DoesNotContain("resolved-metadata-id", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDisposesTheOperationContext()
    {
        var resolver = new RecordingResolver { Cancel = true };
        var contexts = new RecordingContextFactory(resolver);
        var content = new RecordingTextContentApi();
        var service = Service(contexts, content);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadAsync(
                ProfileId,
                RemoteProviderMetadataPath.SyncLog,
                cancellation.Token));

        Assert.Equal(
            cancellation.Token,
            Assert.Single(resolver.ResolveCalls).CancellationToken);
        Assert.Equal(0, content.Calls);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    private static GoogleDriveProviderMetadataReadService Service(
        RecordingContextFactory contexts,
        RecordingTextContentApi content) =>
        new(new GoogleDriveTextFileReadService(
            contexts,
            content,
            new GoogleDriveObjectIdCache()));

    private static GoogleDriveObjectResolutionResult Resolution(
        GoogleDriveObjectResolutionStatus status) =>
        new(
            status,
            GoogleDriveRelativePath.Parse(RemoteProviderMetadataPath.SyncLog),
            GoogleDriveObjectKind.File,
            objectId: status == GoogleDriveObjectResolutionStatus.Found
                ? "resolved-metadata-id"
                : null,
            errorCode: status == GoogleDriveObjectResolutionStatus.Found
                ? null
                : GoogleDriveObjectResolutionErrorCodes.NotFound,
            message: status == GoogleDriveObjectResolutionStatus.Found
                ? null
                : "The metadata object was not found.");

    private static GoogleDriveRemoteOperationException Failure(
        GoogleDriveRemoteValidationStatus status,
        string errorCode,
        bool retryable) =>
        new(new GoogleDriveRemoteValidationResult(
            status,
            errorCode,
            "Google Drive metadata could not be read.",
            retryable,
            rootDisplayName: null,
            wasAuthenticationRefreshed: false,
            cacheInvalidated: false));

    private static GoogleAuthorizedCredential Credential()
    {
        var flow = new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = "test-client-id",
                    ClientSecret = "test-client-secret"
                }
            });
        var user = new UserCredential(
            flow,
            ProfileId.ToString("D"),
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        public const string RootId = "authoritative-root-id";

        public RecordingContextFactory(RecordingResolver resolver) =>
            Resolver = resolver;

        public RecordingResolver Resolver { get; }

        public int CreateCalls { get; private set; }

        public Exception? Failure { get; set; }

        public List<GoogleAuthorizedCredential> Credentials { get; } = [];

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            if (Failure is not null)
                throw Failure;

            GoogleAuthorizedCredential credential = Credential();
            Credentials.Add(credential);
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                credential,
                Resolver));
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public GoogleDriveObjectResolutionResult Result { get; set; } =
            Resolution(GoogleDriveObjectResolutionStatus.Found);

        public bool Cancel { get; set; }

        public List<ResolveCall> ResolveCalls { get; } = [];

        public int EnsureCalls { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Metadata reads resolve the complete allowlisted path.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls.Add(new ResolveCall(
                rootFolderId,
                relativePath,
                expectedFinalKind,
                cancellationToken));
            if (Cancel)
                throw new OperationCanceledException(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            throw new InvalidOperationException(
                "Provider metadata reads must never create Drive folders.");
        }
    }

    private sealed class RecordingTextContentApi : IGoogleDriveTextContentApi
    {
        public byte[] Content { get; set; } = Encoding.UTF8.GetBytes("{}");

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public List<string> FileIds { get; } = [];

        public Task<GoogleDriveTextContentResult> DownloadTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            Calls++;
            FileIds.Add(fileId);
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(new GoogleDriveTextContentResult(Content));
        }
    }

    private sealed record ResolveCall(
        string RootFolderId,
        GoogleDriveRelativePath Path,
        GoogleDriveObjectKind? ExpectedKind,
        CancellationToken CancellationToken);
}
