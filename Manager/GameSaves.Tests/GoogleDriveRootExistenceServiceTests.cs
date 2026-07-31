using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveRootExistenceServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("56c0c22c-04d2-46c6-8ff9-a2616c4cac52");

    private const string RootId = "private-authoritative-root-id";

    [Fact]
    public async Task Exists_UsesAuthoritativeIdAndReturnsTrueForMyDriveFolder()
    {
        Context context = CreateContext();
        context.Api.Result = Metadata();

        bool exists = await context.Service.ExistsAsync(ProfileId);

        Assert.True(exists);
        Assert.Equal(new[] { RootId }, context.Api.RootIds);
        Assert.Equal(ProfileId, context.ContextFactory.ProfileIds.Single());
        Assert.Equal(0, context.ContextFactory.Resolver.OperationCalls);
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.Empty(context.Cache.ProfileInvalidations);
        Assert.True(context.ContextFactory.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task Exists_ReturnsFalseAndInvalidatesScopeWhenRootIsMissing()
    {
        Context context = CreateContext();
        context.Api.Failure = GoogleDriveApiFailure.NotFound;

        bool exists = await context.Service.ExistsAsync(ProfileId);

        Assert.False(exists);
        AssertScopeInvalidation(
            context,
            GoogleDriveObjectCacheInvalidationReason.RootMissing);
        Assert.True(context.ContextFactory.LastCredential!.IsDisposed);
    }

    [Theory]
    [InlineData(
        0,
        (int)GoogleDriveRemoteValidationStatus.RootTrashed,
        (int)GoogleDriveObjectCacheInvalidationReason.RootTrashed)]
    [InlineData(
        1,
        (int)GoogleDriveRemoteValidationStatus.RootWrongType,
        (int)GoogleDriveObjectCacheInvalidationReason.RootTypeChanged)]
    [InlineData(
        2,
        (int)GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
        (int)GoogleDriveObjectCacheInvalidationReason.RootUnsupportedLocation)]
    public async Task Exists_FailsClosedAndInvalidatesConfirmedStaleRoot(
        int scenario,
        int expectedStatusValue,
        int expectedReasonValue)
    {
        Context context = CreateContext();
        context.Api.Result = scenario switch
        {
            0 => Metadata(trashed: true),
            1 => Metadata(mimeType: "application/json"),
            2 => Metadata(driveId: "private-shared-drive-id"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            (GoogleDriveRemoteValidationStatus)expectedStatusValue,
            exception.Result.Status);
        Assert.True(exception.Result.CacheInvalidated);
        AssertScopeInvalidation(
            context,
            (GoogleDriveObjectCacheInvalidationReason)expectedReasonValue);
        AssertSafeFailure(exception);
        Assert.True(context.ContextFactory.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task Exists_FailsClosedAndInvalidatesScopeWhenRootIsInaccessible()
    {
        Context context = CreateContext();
        context.Api.Failure = GoogleDriveApiFailure.AccessDenied;

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootInaccessible,
            exception.Result.Status);
        AssertScopeInvalidation(
            context,
            GoogleDriveObjectCacheInvalidationReason.RootInaccessible);
        AssertSafeFailure(exception);
    }

    [Fact]
    public async Task Exists_RejectsMismatchedResponseIdWithoutTreatingItAsMissing()
    {
        Context context = CreateContext();
        context.Api.Result = Metadata(id: "different-private-object-id");

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.Failed,
            exception.Result.Status);
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.Empty(context.Cache.ProfileInvalidations);
        AssertSafeFailure(exception);
    }

    [Fact]
    public async Task Exists_InvalidatesProfileWhenAuthorizationIsRevoked()
    {
        Context context = CreateContext();
        context.ContextFactory.Failure =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            exception.Result.Status);
        Assert.True(exception.Result.CacheInvalidated);
        Assert.Equal(
            (ProfileId,
                GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation),
            Assert.Single(context.Cache.ProfileInvalidations));
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.Equal(0, context.Api.GetCalls);
        AssertSafeFailure(exception);
    }

    [Fact]
    public async Task Exists_ApiRevocationInvalidatesProfileAndDisposesContext()
    {
        Context context = CreateContext();
        context.Api.Failure = GoogleDriveApiFailure.AuthorizationRevoked;

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            exception.Result.Status);
        Assert.Equal(
            (ProfileId,
                GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation),
            Assert.Single(context.Cache.ProfileInvalidations));
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.True(context.ContextFactory.LastCredential!.IsDisposed);
        AssertSafeFailure(exception);
    }

    [Fact]
    public async Task Exists_AuthenticationUnavailableFailsWithoutClearingCache()
    {
        Context context = CreateContext();
        context.ContextFactory.Failure =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.AuthenticationUnavailable);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthenticationUnavailable,
            exception.Result.Status);
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.Empty(context.Cache.ProfileInvalidations);
        Assert.Equal(0, context.Api.GetCalls);
        AssertSafeFailure(exception);
    }

    [Theory]
    [InlineData(
        (int)GoogleDriveApiFailure.RateLimited,
        (int)GoogleDriveRemoteValidationStatus.RateLimited)]
    [InlineData(
        (int)GoogleDriveApiFailure.QuotaExceeded,
        (int)GoogleDriveRemoteValidationStatus.QuotaExceeded)]
    [InlineData(
        (int)GoogleDriveApiFailure.Unavailable,
        (int)GoogleDriveRemoteValidationStatus.Unavailable)]
    public async Task Exists_TemporaryProviderFailurePreservesCache(
        int failureValue,
        int expectedStatusValue)
    {
        Context context = CreateContext();
        context.Api.Failure = (GoogleDriveApiFailure)failureValue;

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAnyAsync<GoogleDriveRemoteOperationException>(() =>
                context.Service.ExistsAsync(ProfileId));

        Assert.Equal(
            (GoogleDriveRemoteValidationStatus)expectedStatusValue,
            exception.Result.Status);
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.Empty(context.Cache.ProfileInvalidations);
        Assert.False(exception.Result.CacheInvalidated);
        AssertSafeFailure(exception);
        Assert.True(context.ContextFactory.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task Exists_CancellationDisposesContextAndPreservesCache()
    {
        Context context = CreateContext();
        context.Api.Cancel = true;
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.ExistsAsync(ProfileId, cancellation.Token));

        Assert.Equal(cancellation.Token, context.Api.CancellationTokens.Single());
        Assert.Empty(context.Cache.ScopeInvalidations);
        Assert.Empty(context.Cache.ProfileInvalidations);
        Assert.True(context.ContextFactory.LastCredential!.IsDisposed);
    }

    private static Context CreateContext()
    {
        var contextFactory = new RecordingContextFactory();
        var api = new RecordingObjectApi();
        var cache = new RecordingObjectIdCache();
        var service = new GoogleDriveRootExistenceService(
            contextFactory,
            api,
            cache);
        return new Context(contextFactory, api, cache, service);
    }

    private static GoogleDriveObjectMetadata Metadata(
        string id = RootId,
        string mimeType = GoogleDriveApplicationRoot.FolderMimeType,
        bool trashed = false,
        string? driveId = null) =>
        new(
            id,
            "Private display name",
            mimeType,
            trashed,
            new[] { "private-parent-id" },
            driveId);

    private static void AssertScopeInvalidation(
        Context context,
        GoogleDriveObjectCacheInvalidationReason expectedReason)
    {
        var invalidation = Assert.Single(context.Cache.ScopeInvalidations);
        Assert.Equal(ProfileId, invalidation.Scope.RemoteProfileId);
        Assert.Equal(RootId, invalidation.Scope.RootFolderId);
        Assert.Equal(expectedReason, invalidation.Reason);
        Assert.Empty(context.Cache.ProfileInvalidations);
    }

    private static void AssertSafeFailure(
        GoogleDriveRemoteOperationException exception)
    {
        string diagnostic = exception.ToString();
        Assert.DoesNotContain(RootId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-parent-id", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-shared-drive-id", diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain("different-private-object-id", diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain("fake-access-token", diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", diagnostic,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Context(
        RecordingContextFactory ContextFactory,
        RecordingObjectApi Api,
        RecordingObjectIdCache Cache,
        GoogleDriveRootExistenceService Service);

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        public GoogleDriveRemoteValidationResult? Failure { get; set; }

        public List<Guid> ProfileIds { get; } = new();

        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public RecordingResolver Resolver { get; } = new();

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is not null)
                throw new GoogleDriveRemoteOperationContextException(Failure);

            var flow = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = "fake-client-id",
                        ClientSecret = "fake-client-secret"
                    }
                });
            var token = new TokenResponse
            {
                AccessToken = "fake-access-token",
                RefreshToken = "fake-refresh-token",
                ExpiresInSeconds = 3600,
                IssuedUtc = DateTime.UtcNow
            };
            LastCredential = new GoogleAuthorizedCredential(
                new UserCredential(flow, remoteProfileId.ToString("D"), token));

            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                LastCredential,
                Resolver));
        }
    }

    private sealed class RecordingObjectApi
        : IGoogleDriveObjectApi
    {
        public GoogleDriveObjectMetadata Result { get; set; } = Metadata();

        public GoogleDriveApiFailure? Failure { get; set; }

        public bool Cancel { get; set; }

        public int GetCalls { get; private set; }

        public List<string> RootIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            RootIds.Add(objectId);
            CancellationTokens.Add(cancellationToken);

            if (Cancel)
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is { } failure)
            {
                throw GoogleDriveApiFailureMapper.Create(
                    GoogleDriveApiOperation.RootValidationMetadataGet,
                    failure,
                    "GoogleDriveTestRootExistenceFailure");
            }

            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Root existence must not list Drive children.");

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Root existence must not create Drive folders.");
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public int OperationCalls { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) => Unexpected();

        private Task<GoogleDriveObjectResolutionResult> Unexpected()
        {
            OperationCalls++;
            throw new InvalidOperationException(
                "Root existence must not invoke path resolution or mutation.");
        }
    }

    private sealed class RecordingObjectIdCache : IGoogleDriveObjectIdCache
    {
        public List<(GoogleDriveObjectCacheScope Scope,
            GoogleDriveObjectCacheInvalidationReason Reason)> ScopeInvalidations
            { get; } = new();

        public List<(Guid ProfileId,
            GoogleDriveObjectCacheInvalidationReason Reason)> ProfileInvalidations
            { get; } = new();

        public bool TryGet(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            out GoogleDriveObjectIdCacheEntry? entry)
        {
            entry = null;
            return false;
        }

        public bool TryStoreUniqueValidated(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata) => false;

        public void Remove(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind) =>
            throw new InvalidOperationException("No entry removal is expected.");

        public void ClearScope(GoogleDriveObjectCacheScope scope) =>
            throw new InvalidOperationException("No unclassified clear is expected.");

        public void InvalidateScope(
            GoogleDriveObjectCacheScope scope,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            ScopeInvalidations.Add((scope, reason));

        public void InvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            ProfileInvalidations.Add((remoteProfileId, reason));
    }
}
