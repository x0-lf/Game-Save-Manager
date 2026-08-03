using System.Text;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveProviderMetadataReplacementServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("3f582443-ff20-4ec5-9acf-37c4811d42c8");

    private const string RootId = "authoritative-root-id";
    private const string ParentId = "authoritative-metadata-parent-id";
    private const string FileId = "authoritative-sync-log-id";
    private const string ExactFileName = "sync-log.json";

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("run/manifest.json")]
    [InlineData(".gamesave-sync/other.json")]
    [InlineData(".GAMESAVE-SYNC/sync-log.json")]
    [InlineData("/.gamesave-sync/sync-log.json")]
    public async Task NonAllowlistedPath_IsRejectedBeforeAuthenticationOrDriveWork(
        string relativePath)
    {
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(resolver);
        var creation = new RecordingTextCreationApi();
        var replacement = new RecordingTextReplacementApi();
        var service = Service(contexts, creation, replacement);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReplaceAsync(
                ProfileId,
                relativePath,
                "{}",
                CancellationToken.None));

        Assert.Equal(0, contexts.CreateCalls);
        Assert.Empty(resolver.EnsureCalls);
        Assert.Empty(resolver.FindCalls);
        Assert.Equal(0, creation.Calls);
        Assert.Equal(0, replacement.Calls);
    }

    [Fact]
    public async Task MissingMetadata_CreatesOneJsonFileBeneathEnsuredParent()
    {
        const string content = "{\"runs\":[\"保存\"]}";
        var resolver = new RecordingResolver
        {
            EnsureResult = ParentResolution(
                GoogleDriveObjectResolutionStatus.Created),
            FindResult = NotFound()
        };
        var contexts = new RecordingContextFactory(resolver);
        var creation = new RecordingTextCreationApi();
        var replacement = new RecordingTextReplacementApi();
        var cache = new GoogleDriveObjectIdCache();
        var service = Service(contexts, creation, replacement, cache);

        await service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            content,
            CancellationToken.None);

        EnsureCall ensure = Assert.Single(resolver.EnsureCalls);
        Assert.Equal(RootId, ensure.RootId);
        Assert.Equal(".gamesave-sync", ensure.Path.Canonical);
        FindCall find = Assert.Single(resolver.FindCalls);
        Assert.Equal(ParentId, find.ParentId);
        Assert.Equal(ExactFileName, find.ExactName);
        Assert.Equal(GoogleDriveObjectKind.File, find.ExpectedKind);
        Assert.Equal(1, creation.Calls);
        Assert.Equal(ParentId, Assert.Single(creation.ParentIds));
        Assert.Equal(ExactFileName, Assert.Single(creation.FileNames));
        Assert.Equal(Encoding.UTF8.GetBytes(content),
            Assert.Single(creation.Contents));
        Assert.Equal(GoogleDriveTextCreationMediaTypes.Json,
            Assert.Single(creation.MediaTypes));
        Assert.Equal(0, replacement.Calls);
        AssertCached(cache, FileId);
        Assert.True(Assert.Single(contexts.Credentials).IsDisposed);
    }

    [Fact]
    public async Task ExistingMetadata_ReplacesContentByExactIdWithoutCreation()
    {
        const string content = "{\"runs\":[1,2]}";
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(resolver);
        var creation = new RecordingTextCreationApi();
        var replacement = new RecordingTextReplacementApi();
        var cache = new GoogleDriveObjectIdCache();
        var service = Service(contexts, creation, replacement, cache);

        await service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            content,
            CancellationToken.None);

        Assert.Equal(0, creation.Calls);
        Assert.Equal(1, replacement.Calls);
        Assert.Equal(FileId, Assert.Single(replacement.FileIds));
        Assert.Equal(Encoding.UTF8.GetBytes(content),
            Assert.Single(replacement.Contents));
        Assert.Equal(GoogleDriveTextCreationMediaTypes.Json,
            Assert.Single(replacement.MediaTypes));
        AssertCached(cache, FileId);
        Assert.True(Assert.Single(contexts.Credentials).IsDisposed);
    }

    [Fact]
    public async Task UpdateMustReturnTheSameAuthoritativeId()
    {
        var cache = new GoogleDriveObjectIdCache();
        var replacement = new RecordingTextReplacementApi
        {
            Result = new GoogleDriveTextReplacementResult(
                "different-authoritative-file-id")
        };
        var service = Service(
            new RecordingContextFactory(new RecordingResolver()),
            new RecordingTextCreationApi(),
            replacement,
            cache);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReplaceAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog,
                    "{}",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveProviderMetadataReplacementErrorCodes
                .InvalidReplaceResponse,
            exception.Result.ErrorCode);
        Assert.Equal(FileId, Assert.Single(replacement.FileIds));
        AssertNotCached(cache);
        Assert.DoesNotContain(FileId, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, GoogleDriveObjectResolutionErrorCodes.Ambiguous)]
    [InlineData(1, GoogleDriveObjectResolutionErrorCodes.TypeMismatch)]
    public async Task DuplicateOrSameNameFolder_FailsClosedBeforeMutation(
        int resolutionCase,
        string expectedErrorCode)
    {
        var resolver = new RecordingResolver
        {
            FindResult = resolutionCase == 0
                ? Ambiguous()
                : TypeMismatch()
        };
        var creation = new RecordingTextCreationApi();
        var replacement = new RecordingTextReplacementApi();
        var service = Service(
            new RecordingContextFactory(resolver),
            creation,
            replacement);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReplaceAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog,
                    "{}",
                    CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.Result.ErrorCode);
        Assert.Equal(0, creation.Calls);
        Assert.Equal(0, replacement.Calls);
        Assert.DoesNotContain(ParentId, exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(FileId, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentLocalReplacements_AreSerializedPerProfileAndPath()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bothParentsEnsured = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int ensureCalls = 0;
        int active = 0;
        int maximumActive = 0;
        int handlerCalls = 0;
        int replacementCallNumber() => Interlocked.Increment(ref handlerCalls);

        var resolver = new RecordingResolver
        {
            EnsureHandler = (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref ensureCalls) == 2)
                    bothParentsEnsured.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(ParentResolution());
            }
        };
        var replacement = new RecordingTextReplacementApi
        {
            Handler = async (_, _, _, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                if (replacementCallNumber() == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                Interlocked.Decrement(ref active);
                return new GoogleDriveTextReplacementResult(FileId);
            }
        };

        var service = Service(
            new RecordingContextFactory(resolver),
            new RecordingTextCreationApi(),
            replacement,
            coordinator: new GoogleDriveProviderMetadataReplacementCoordinator());

        Task first = service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":1}",
            CancellationToken.None);
        await firstEntered.Task;

        Task second = service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":2}",
            CancellationToken.None);
        await bothParentsEnsured.Task;

        Assert.Equal(1, replacement.Calls);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, replacement.Calls);
        Assert.Equal(1, maximumActive);
        Assert.All(replacement.FileIds, id => Assert.Equal(FileId, id));
    }

    [Fact]
    public async Task ConcurrentMissingMetadata_CreatesOnceThenUpdatesCreatedId()
    {
        var creation = new RecordingTextCreationApi();
        var resolver = new RecordingResolver
        {
            FindHandler = (_, _, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    creation.Calls == 0 ? NotFound() : Found());
            }
        };
        var replacement = new RecordingTextReplacementApi();
        var service = Service(
            new RecordingContextFactory(resolver),
            creation,
            replacement,
            coordinator: new GoogleDriveProviderMetadataReplacementCoordinator());

        Task first = service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":1}",
            CancellationToken.None);
        Task second = service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":2}",
            CancellationToken.None);

        await Task.WhenAll(first, second);

        Assert.Equal(1, creation.Calls);
        Assert.Equal(1, replacement.Calls);
        Assert.Equal(FileId, Assert.Single(replacement.FileIds));
        Assert.Equal(2, resolver.FindCalls.Count);
    }

    [Fact]
    public async Task CancellationWhileWaiting_ReleasesCoordinationState()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEnsured = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int ensureCalls = 0;
        int handlerCalls = 0;
        var resolver = new RecordingResolver
        {
            EnsureHandler = (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref ensureCalls) == 2)
                    secondEnsured.TrySetResult();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(ParentResolution());
            }
        };
        var replacement = new RecordingTextReplacementApi
        {
            Handler = async (_, _, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref handlerCalls) == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                return new GoogleDriveTextReplacementResult(FileId);
            }
        };
        var coordinator = new GoogleDriveProviderMetadataReplacementCoordinator();
        var contexts = new RecordingContextFactory(resolver);
        var service = Service(
            contexts,
            new RecordingTextCreationApi(),
            replacement,
            coordinator: coordinator);

        Task first = service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":1}",
            CancellationToken.None);
        await firstEntered.Task;

        using var cancellation = new CancellationTokenSource();
        Task second = service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":2}",
            cancellation.Token);
        await secondEnsured.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        releaseFirst.TrySetResult();
        await first;

        await service.ReplaceAsync(
            ProfileId,
            RemoteProviderMetadataPath.SyncLog,
            "{\"call\":3}",
            CancellationToken.None);

        Assert.Equal(2, replacement.Calls);
        Assert.Equal(3, contexts.Credentials.Count);
        Assert.All(contexts.Credentials, credential => Assert.True(credential.IsDisposed));
    }

    [Fact]
    public async Task QuotaFailure_IsMappedSafelyAndPreservesValidatedCache()
    {
        var cache = new GoogleDriveObjectIdCache();
        var replacement = new RecordingTextReplacementApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentReplace,
                GoogleDriveApiFailure.QuotaExceeded,
                "GoogleDriveTextReplacementQuotaExceeded",
                retryable: false)
        };
        var service = Service(
            new RecordingContextFactory(new RecordingResolver()),
            new RecordingTextCreationApi(),
            replacement,
            cache);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReplaceAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog,
                    "{}",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.QuotaExceeded,
            exception.Result.Status);
        Assert.Equal(
            "GoogleDriveTextReplacementQuotaExceeded",
            exception.Result.ErrorCode);
        Assert.False(exception.Result.Retryable);
        AssertCached(cache, FileId);
        Assert.DoesNotContain(FileId, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedContent_IsRejectedBeforeAuthentication()
    {
        string oversized = new(
            'x',
            GoogleDriveTextReplacementApi.MaxTextContentBytes + 1);
        var contexts = new RecordingContextFactory(new RecordingResolver());
        var service = Service(
            contexts,
            new RecordingTextCreationApi(),
            new RecordingTextReplacementApi());

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReplaceAsync(
                    ProfileId,
                    RemoteProviderMetadataPath.SyncLog,
                    oversized,
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextReplacementErrorCodes.ContentTooLarge,
            exception.Result.ErrorCode);
        Assert.Equal(0, contexts.CreateCalls);
    }

    [Fact]
    public void DependencyInjection_ResolvesReplacementServiceAndCoordinator()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveProviderMetadataReplacementService>(
            provider.GetRequiredService<
                IGoogleDriveProviderMetadataReplacementService>());
        Assert.Same(
            provider.GetRequiredService<
                GoogleDriveProviderMetadataReplacementCoordinator>(),
            provider.GetRequiredService<
                GoogleDriveProviderMetadataReplacementCoordinator>());
    }

    private static GoogleDriveProviderMetadataReplacementService Service(
        RecordingContextFactory contexts,
        RecordingTextCreationApi creation,
        RecordingTextReplacementApi replacement,
        IGoogleDriveObjectIdCache? cache = null,
        GoogleDriveProviderMetadataReplacementCoordinator? coordinator = null) =>
        new(
            contexts,
            creation,
            replacement,
            coordinator ?? new GoogleDriveProviderMetadataReplacementCoordinator(),
            cache ?? new GoogleDriveObjectIdCache());

    private static GoogleDriveObjectResolutionResult ParentResolution(
        GoogleDriveObjectResolutionStatus status =
            GoogleDriveObjectResolutionStatus.Found) =>
        new(
            status,
            GoogleDriveRelativePath.Parse(".gamesave-sync"),
            GoogleDriveObjectKind.Folder,
            objectId: ParentId);

    private static GoogleDriveObjectResolutionResult Found() =>
        new(
            GoogleDriveObjectResolutionStatus.Found,
            GoogleDriveRelativePath.Parse(ExactFileName),
            GoogleDriveObjectKind.File,
            Metadata(),
            FileId);

    private static GoogleDriveObjectResolutionResult NotFound() =>
        new(
            GoogleDriveObjectResolutionStatus.NotFound,
            GoogleDriveRelativePath.Parse(ExactFileName),
            GoogleDriveObjectKind.File,
            errorCode: GoogleDriveObjectResolutionErrorCodes.NotFound,
            message: "Provider metadata was not found.");

    private static GoogleDriveObjectResolutionResult Ambiguous() =>
        new(
            GoogleDriveObjectResolutionStatus.Ambiguous,
            GoogleDriveRelativePath.Parse(ExactFileName),
            GoogleDriveObjectKind.File,
            errorCode: GoogleDriveObjectResolutionErrorCodes.Ambiguous,
            message: "Provider metadata is ambiguous.");

    private static GoogleDriveObjectResolutionResult TypeMismatch() =>
        new(
            GoogleDriveObjectResolutionStatus.TypeMismatch,
            GoogleDriveRelativePath.Parse(ExactFileName),
            GoogleDriveObjectKind.Folder,
            new GoogleDriveObjectMetadata(
                "private-folder-id",
                ExactFileName,
                GoogleDriveApplicationRoot.FolderMimeType,
                trashed: false,
                parentIds: [ParentId],
                driveId: null),
            errorCode: GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
            message: "Provider metadata is a folder.");

    private static GoogleDriveObjectMetadata Metadata() =>
        new(
            FileId,
            ExactFileName,
            GoogleDriveTextCreationMediaTypes.Json,
            trashed: false,
            parentIds: [ParentId],
            driveId: null);

    private static void AssertCached(
        IGoogleDriveObjectIdCache cache,
        string expectedId)
    {
        Assert.True(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootId),
            ParentId,
            ExactFileName,
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? entry));
        Assert.NotNull(entry);
        Assert.Equal(expectedId, entry.ObjectId);
    }

    private static void AssertNotCached(IGoogleDriveObjectIdCache cache) =>
        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootId),
            ParentId,
            ExactFileName,
            GoogleDriveObjectKind.File,
            out _));

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            int current = Volatile.Read(ref maximum);
            if (current >= candidate ||
                Interlocked.CompareExchange(ref maximum, candidate, current) == current)
            {
                return;
            }
        }
    }

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

    private sealed class RecordingContextFactory(
        RecordingResolver resolver)
        : IGoogleDriveRemoteOperationContextFactory
    {
        private int _createCalls;
        private readonly object _gate = new();

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public List<GoogleAuthorizedCredential> Credentials { get; } = [];

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _createCalls);
            GoogleAuthorizedCredential credential = Credential();
            lock (_gate)
                Credentials.Add(credential);
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                credential,
                resolver));
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        private readonly object _gate = new();

        public GoogleDriveObjectResolutionResult EnsureResult { get; set; } =
            ParentResolution();

        public GoogleDriveObjectResolutionResult FindResult { get; set; } =
            Found();

        public Func<string, GoogleDriveRelativePath, CancellationToken,
            Task<GoogleDriveObjectResolutionResult>>? EnsureHandler { get; set; }

        public Func<string, string, GoogleDriveObjectKind, CancellationToken,
            Task<GoogleDriveObjectResolutionResult>>? FindHandler { get; set; }

        public List<EnsureCall> EnsureCalls { get; } = [];

        public List<FindCall> FindCalls { get; } = [];

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                FindCalls.Add(new FindCall(parentId, exactName, expectedKind));
            cancellationToken.ThrowIfCancellationRequested();
            return FindHandler is null
                ? Task.FromResult(FindResult)
                : FindHandler(parentId, exactName, expectedKind, cancellationToken);
        }

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Provider metadata replacement resolves its parent and child explicitly.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                EnsureCalls.Add(new EnsureCall(rootFolderId, relativeFolderPath));
            cancellationToken.ThrowIfCancellationRequested();
            return EnsureHandler is null
                ? Task.FromResult(EnsureResult)
                : EnsureHandler(rootFolderId, relativeFolderPath, cancellationToken);
        }
    }

    private sealed class RecordingTextCreationApi : IGoogleDriveTextCreationApi
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public GoogleDriveTextCreationResult Result { get; set; } = new(FileId);

        public Exception? Failure { get; set; }

        public List<string> ParentIds { get; } = [];

        public List<string> FileNames { get; } = [];

        public List<byte[]> Contents { get; } = [];

        public List<string> MediaTypes { get; } = [];

        public Task<GoogleDriveTextCreationResult> CreateTextFileAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            string exactFileName,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            Interlocked.Increment(ref _calls);
            ParentIds.Add(parentFolderId);
            FileNames.Add(exactFileName);
            Contents.Add(contentBytes.ToArray());
            MediaTypes.Add(mediaType);
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingTextReplacementApi
        : IGoogleDriveTextReplacementApi
    {
        private int _calls;
        private readonly object _gate = new();

        public int Calls => Volatile.Read(ref _calls);

        public GoogleDriveTextReplacementResult Result { get; set; } =
            new(FileId);

        public Exception? Failure { get; set; }

        public Func<GoogleAuthorizedCredential, string, ReadOnlyMemory<byte>,
            CancellationToken, Task<GoogleDriveTextReplacementResult>>?
            Handler { get; set; }

        public List<string> FileIds { get; } = [];

        public List<byte[]> Contents { get; } = [];

        public List<string> MediaTypes { get; } = [];

        public Task<GoogleDriveTextReplacementResult> ReplaceTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            Interlocked.Increment(ref _calls);
            lock (_gate)
            {
                FileIds.Add(fileId);
                Contents.Add(contentBytes.ToArray());
                MediaTypes.Add(mediaType);
            }
            if (Failure is not null)
                throw Failure;
            return Handler is null
                ? Task.FromResult(Result)
                : Handler(credential, fileId, contentBytes, cancellationToken);
        }
    }

    private sealed record EnsureCall(
        string RootId,
        GoogleDriveRelativePath Path);

    private sealed record FindCall(
        string ParentId,
        string ExactName,
        GoogleDriveObjectKind ExpectedKind);
}
