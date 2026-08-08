using System.Text;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveTextFileReadServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("1e619f5f-ddbb-41bb-9096-c72173d0fb8d");

    [Fact]
    public async Task ValidManifest_IsResolvedByIdAndDecodedAsStrictUtf8()
    {
        const string json = "{\"game\":\"Pokémon\",\"files\":[]}";
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(_ => resolver);
        var content = new RecordingTextContentApi
        {
            DefaultContent = Encoding.UTF8.GetBytes(json)
        };
        var service = new GoogleDriveTextFileReadService(
            contexts,
            content,
            new GoogleDriveObjectIdCache());

        string? result = await service.ReadAsync(
            ProfileId,
            "run/manifest.json");

        Assert.Equal(json, result);
        ResolveCall call = Assert.Single(resolver.ResolveCalls);
        Assert.Equal(RecordingContextFactory.RootId, call.RootFolderId);
        Assert.Equal("run/manifest.json", call.Path.Canonical);
        Assert.Equal(GoogleDriveObjectKind.File, call.ExpectedKind);
        Assert.Equal(new[] { "resolved-file-id" }, content.FileIds);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    [Fact]
    public async Task MissingFile_ReturnsNullWithoutDownloadingOrCreating()
    {
        var resolver = new RecordingResolver
        {
            ResultFactory = path => Resolution(
                GoogleDriveObjectResolutionStatus.NotFound,
                path)
        };
        var content = new RecordingTextContentApi();
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => resolver),
            content,
            new GoogleDriveObjectIdCache());

        string? result = await service.ReadAsync(
            ProfileId,
            "missing/manifest.json");

        Assert.Null(result);
        Assert.Empty(content.FileIds);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Theory]
    [InlineData("nested/run/manifest.json")]
    [InlineData("保存/ゲーム/manifest.json")]
    [InlineData("O'Brien/Back\\slash/manifest.json")]
    public async Task NestedUnicodeAndLiteralBackslashPaths_ArePreservedExactly(
        string relativePath)
    {
        var resolver = new RecordingResolver();
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => resolver),
            new RecordingTextContentApi(),
            new GoogleDriveObjectIdCache());

        await service.ReadAsync(ProfileId, relativePath);

        ResolveCall call = Assert.Single(resolver.ResolveCalls);
        Assert.Equal(relativePath, call.Path.Canonical);
        Assert.Equal(relativePath.Split('/'), call.Path.Segments);
        Assert.Equal(GoogleDriveObjectKind.File, call.ExpectedKind);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Fact]
    public async Task Utf8Bom_IsAcceptedAndRemovedFromTheReturnedText()
    {
        const string expected = "{\"name\":\"保存\"}";
        byte[] payload = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected)];
        var service = Service(payload);

        string? result = await service.ReadAsync(
            ProfileId,
            "run/manifest.json");

        Assert.Equal(expected, result);
        Assert.False(result!.StartsWith('\uFEFF'));
    }

    [Fact]
    public async Task InvalidUtf8_FailsWithASafeStableProviderError()
    {
        byte[] invalid = [0xC3, 0x28];
        var service = Service(invalid);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(
                    ProfileId,
                    "private-run/manifest.json"));

        Assert.Equal(
            GoogleDriveTextFileReadErrorCodes.InvalidUtf8,
            exception.Result.ErrorCode);
        Assert.Equal(
            GoogleDriveRemoteValidationStatus.Failed,
            exception.Result.Status);
        Assert.DoesNotContain("private-run", exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(invalid), exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedContentFailure_RemainsDistinctAndDoesNotLeakIds()
    {
        var resolver = new RecordingResolver();
        var content = new RecordingTextContentApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentMetadataGet,
                GoogleDriveApiFailure.Failed,
                GoogleDriveTextContentErrorCodes.DeclaredSizeTooLarge,
                retryable: false)
        };
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => resolver),
            content,
            new GoogleDriveObjectIdCache());

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(ProfileId, "run/manifest.json"));

        Assert.Equal(
            GoogleDriveTextContentErrorCodes.DeclaredSizeTooLarge,
            exception.Result.ErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.DoesNotContain("resolved-file-id", exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Fact]
    public async Task ConfirmedStaleContentFailure_RemovesOnlyTheAffectedFileEntry()
    {
        var cache = SeedSafeFileCache();
        GoogleDriveObjectCacheScope scope = new(
            ProfileId,
            RecordingContextFactory.RootId);
        Assert.True(cache.TryStoreUniqueValidated(
            scope,
            "other-run-id",
            "manifest.json",
            GoogleDriveObjectKind.File,
            File("other-file-id", "manifest.json", "other-run-id")));
        var content = new RecordingTextContentApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentMetadataGet,
                GoogleDriveApiFailure.NotFound,
                "GoogleDriveTextContentNotFound")
        };
        var resolver = new RecordingResolver
        {
            ResultFactory = path => new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Found,
                path,
                GoogleDriveObjectKind.File,
                File("resolved-file-id", "manifest.json", "run-id"))
        };
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => resolver),
            content,
            cache);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.ReadAsync(ProfileId, "run/manifest.json"));

        Assert.True(exception.Result.CacheInvalidated);
        Assert.False(cache.TryGet(
            scope,
            "run-id",
            "manifest.json",
            GoogleDriveObjectKind.File,
            out _));
        Assert.True(cache.TryGet(
            scope,
            "other-run-id",
            "manifest.json",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Theory]
    [InlineData((int)GoogleDriveApiFailure.RateLimited)]
    [InlineData((int)GoogleDriveApiFailure.QuotaExceeded)]
    [InlineData((int)GoogleDriveApiFailure.Unavailable)]
    public async Task TemporaryContentFailure_PreservesValidatedCache(
        int failureValue)
    {
        var cache = SeedSafeFileCache();
        var content = new RecordingTextContentApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentDownload,
                (GoogleDriveApiFailure)failureValue,
                "GoogleDriveTemporaryFailure",
                retryable: true)
        };
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => new RecordingResolver()),
            content,
            cache);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.ReadAsync(ProfileId, "run/manifest.json"));

        Assert.False(exception.Result.CacheInvalidated);
        Assert.True(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RecordingContextFactory.RootId),
            "run-id",
            "manifest.json",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Fact]
    public async Task DuplicateFiles_FailClosedWithoutDownloadingOrCreating()
    {
        var resolver = new RecordingResolver
        {
            ResultFactory = path => new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Ambiguous,
                path,
                GoogleDriveObjectKind.File,
                errorCode: GoogleDriveObjectResolutionErrorCodes.Ambiguous,
                message: "The file name is ambiguous.")
        };
        var content = new RecordingTextContentApi();
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => resolver),
            content,
            new GoogleDriveObjectIdCache());

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(ProfileId, "run/manifest.json"));

        Assert.Equal(
            GoogleDriveObjectResolutionErrorCodes.Ambiguous,
            exception.Result.ErrorCode);
        Assert.Empty(content.FileIds);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Fact]
    public async Task SameNameFolder_FailsAsWrongTypeWithoutDownloading()
    {
        var resolver = new RecordingResolver
        {
            ResultFactory = path => new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.TypeMismatch,
                path,
                GoogleDriveObjectKind.Folder,
                objectId: "private-folder-id",
                errorCode: GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
                message: "The object is not a file.")
        };
        var content = new RecordingTextContentApi();
        var service = new GoogleDriveTextFileReadService(
            new RecordingContextFactory(_ => resolver),
            content,
            new GoogleDriveObjectIdCache());

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ReadAsync(ProfileId, "run/manifest.json"));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootWrongType,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
            exception.Result.ErrorCode);
        Assert.Empty(content.FileIds);
        Assert.DoesNotContain("private-folder-id", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleCachedFileId_IsRevalidatedAndReplacedByExactName()
    {
        var objects = new RecordingObjectApi();
        objects.SetChild(
            RecordingContextFactory.RootId,
            Folder("run-id", "Run", RecordingContextFactory.RootId));
        objects.SetChild(
            "run-id",
            File("old-file-id", "manifest.json", "run-id"));
        var cache = new GoogleDriveObjectIdCache();
        var contexts = new RecordingContextFactory(credential =>
            new GoogleDriveObjectPathResolver(
                objects,
                credential,
                new GoogleDriveObjectCreationCoordinator(),
                cache,
                ProfileId));
        var content = new RecordingTextContentApi();
        content.Contents["old-file-id"] = Encoding.UTF8.GetBytes("old");
        content.Contents["new-file-id"] = Encoding.UTF8.GetBytes("new");
        var service = new GoogleDriveTextFileReadService(contexts, content, cache);

        Assert.Equal("old", await service.ReadAsync(
            ProfileId,
            "Run/manifest.json"));
        int listsAfterFirstRead = objects.ListCalls;
        objects.SetMetadata(File("old-file-id", "renamed.json", "run-id"));
        objects.SetChild(
            "run-id",
            File("new-file-id", "manifest.json", "run-id"));

        Assert.Equal("new", await service.ReadAsync(
            ProfileId,
            "Run/manifest.json"));

        Assert.Equal(listsAfterFirstRead + 1, objects.ListCalls);
        Assert.Equal(2, objects.GetCalls);
        Assert.Equal(new[] { "old-file-id", "new-file-id" }, content.FileIds);
        Assert.Equal(0, objects.CreateCalls);
        Assert.All(contexts.Credentials, credential => Assert.True(credential.IsDisposed));
        Assert.True(cache.TryGet(
            new GoogleDriveObjectCacheScope(
                ProfileId,
                RecordingContextFactory.RootId),
            "run-id",
            "manifest.json",
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? current));
        Assert.Equal("new-file-id", current!.ObjectId);
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDisposesTheOperationContext()
    {
        var resolver = new RecordingResolver { Cancel = true };
        var contexts = new RecordingContextFactory(_ => resolver);
        var content = new RecordingTextContentApi();
        var service = new GoogleDriveTextFileReadService(
            contexts,
            content,
            new GoogleDriveObjectIdCache());
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadAsync(
                ProfileId,
                "run/manifest.json",
                cancellation.Token));

        Assert.Equal(
            cancellation.Token,
            Assert.Single(resolver.ResolveCalls).CancellationToken);
        Assert.Empty(content.FileIds);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    [Fact]
    public async Task LateResolutionAfterCancellationCannotStartContentDownload()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new RecordingResolver
        {
            ResultFactory = path =>
            {
                cancellation.Cancel();
                return Resolution(GoogleDriveObjectResolutionStatus.Found, path);
            }
        };
        var contexts = new RecordingContextFactory(_ => resolver);
        var content = new RecordingTextContentApi();
        var service = new GoogleDriveTextFileReadService(
            contexts,
            content,
            new GoogleDriveObjectIdCache());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReadAsync(
                ProfileId,
                "run/manifest.json",
                cancellation.Token));

        Assert.Empty(content.FileIds);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    [Fact]
    public async Task SyncEngine_ConvertsUnreadableGoogleManifestIntoExistingWarning()
    {
        using var temp = new TemporaryDirectory();
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(_ => resolver);
        var content = new RecordingTextContentApi
        {
            DefaultContent = [0xC3, 0x28]
        };
        var reader = new GoogleDriveTextFileReadService(
            contexts,
            content,
            new GoogleDriveObjectIdCache());
        var remote = new GoogleDriveRemoteFileSystem(
            ProfileId,
            "Google Drive",
            new ValidValidationService(),
            new ExistingRootService(),
            new UnusedFolderExistenceService(),
            new FixedRunFolderNameService("Unreadable Run"),
            reader,
            new UnusedProviderMetadataReadService(),
            new UnusedProviderMetadataReplacementService(),
            new UnusedCreateOnlyTextFileService(),
            new UnusedRecursiveFileListingService());
        var backupHistory = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var engine = new SyncEngine(
            remote,
            "Google Drive",
            "Google Drive",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

        TransferPreviewWarning warning = Assert.Single(
            plan.Warnings,
            item => item.Code == "RemoteRunUnreadable");
        Assert.Equal(TransferWarningSeverity.Warning, warning.Severity);
        Assert.Empty(plan.Items);
        Assert.Single(content.FileIds);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.Credentials.Single().IsDisposed);
    }

    private static GoogleDriveTextFileReadService Service(byte[] content) =>
        new(
            new RecordingContextFactory(_ => new RecordingResolver()),
            new RecordingTextContentApi { DefaultContent = content },
            new GoogleDriveObjectIdCache());

    private static GoogleDriveObjectIdCache SeedSafeFileCache()
    {
        var cache = new GoogleDriveObjectIdCache();
        Assert.True(cache.TryStoreUniqueValidated(
            new GoogleDriveObjectCacheScope(ProfileId, RecordingContextFactory.RootId),
            "run-id",
            "manifest.json",
            GoogleDriveObjectKind.File,
            File("resolved-file-id", "manifest.json", "run-id")));
        return cache;
    }

    private static GoogleDriveObjectResolutionResult Resolution(
        GoogleDriveObjectResolutionStatus status,
        GoogleDriveRelativePath path) =>
        new(
            status,
            path,
            GoogleDriveObjectKind.File,
            objectId: status == GoogleDriveObjectResolutionStatus.Found
                ? "resolved-file-id"
                : null,
            errorCode: status == GoogleDriveObjectResolutionStatus.Found
                ? null
                : GoogleDriveObjectResolutionErrorCodes.NotFound,
            message: status == GoogleDriveObjectResolutionStatus.Found
                ? null
                : "The file was not found.");

    private static GoogleDriveObjectMetadata Folder(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            trashed: false,
            [parentId],
            driveId: null);

    private static GoogleDriveObjectMetadata File(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            "application/json",
            trashed: false,
            [parentId],
            driveId: null);

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

        private readonly Func<GoogleAuthorizedCredential,
            IGoogleDriveObjectPathResolver> _resolverFactory;

        public RecordingContextFactory(
            Func<GoogleAuthorizedCredential,
                IGoogleDriveObjectPathResolver> resolverFactory) =>
            _resolverFactory = resolverFactory;

        public List<GoogleAuthorizedCredential> Credentials { get; } = [];

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GoogleAuthorizedCredential credential = Credential();
            Credentials.Add(credential);
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                credential,
                _resolverFactory(credential)));
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public Func<GoogleDriveRelativePath,
            GoogleDriveObjectResolutionResult> ResultFactory { get; set; } =
            path => Resolution(GoogleDriveObjectResolutionStatus.Found, path);

        public bool Cancel { get; set; }

        public List<ResolveCall> ResolveCalls { get; } = [];

        public int EnsureCalls { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Text reads must resolve the complete relative path.");

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
            return Task.FromResult(ResultFactory(relativePath));
        }

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            throw new InvalidOperationException(
                "Text reads must never create Drive folders.");
        }
    }

    private sealed class RecordingTextContentApi : IGoogleDriveTextContentApi
    {
        public byte[] DefaultContent { get; set; } = Encoding.UTF8.GetBytes("{}");

        public Dictionary<string, byte[]> Contents { get; } =
            new(StringComparer.Ordinal);

        public Exception? Failure { get; set; }

        public List<string> FileIds { get; } = [];

        public Task<GoogleDriveTextContentResult> DownloadTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            FileIds.Add(fileId);
            if (Failure is not null)
                throw Failure;

            byte[] bytes = Contents.TryGetValue(fileId, out byte[]? content)
                ? content
                : DefaultContent;
            return Task.FromResult(new GoogleDriveTextContentResult(bytes));
        }
    }

    private sealed class RecordingObjectApi : IGoogleDriveObjectApi
    {
        private readonly Dictionary<(string ParentId, string Name),
            IReadOnlyList<GoogleDriveObjectMetadata>> _children = new();
        private readonly Dictionary<string, GoogleDriveObjectMetadata> _metadata =
            new(StringComparer.Ordinal);

        public int GetCalls { get; private set; }

        public int ListCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public void SetChild(string parentId, GoogleDriveObjectMetadata child)
        {
            _children[(parentId, child.Name)] = [child];
            _metadata[child.Id] = child;
        }

        public void SetMetadata(GoogleDriveObjectMetadata metadata) =>
            _metadata[metadata.Id] = metadata;

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult(_metadata[objectId]);
        }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            return Task.FromResult(
                _children.TryGetValue((parentId, name), out var children)
                    ? children
                    : (IReadOnlyList<GoogleDriveObjectMetadata>)
                        Array.Empty<GoogleDriveObjectMetadata>());
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException(
                "Text reads must never create Drive folders.");
        }
    }

    private sealed class ValidValidationService
        : IGoogleDriveRemoteValidationService
    {
        public Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid));
    }

    private sealed class ExistingRootService : IGoogleDriveRootExistenceService
    {
        public Task<bool> ExistsAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class UnusedFolderExistenceService
        : IGoogleDriveFolderExistenceService
    {
        public Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Preview must not check a run folder after its manifest is unreadable.");
    }

    private sealed class FixedRunFolderNameService(params string[] names)
        : IGoogleDriveRunFolderNameService
    {
        public Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(names);
    }

    private sealed class UnusedProviderMetadataReadService
        : IGoogleDriveProviderMetadataReadService
    {
        public Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Manifest preview must not read provider metadata.");
    }

    private sealed class UnusedProviderMetadataReplacementService
        : IGoogleDriveProviderMetadataReplacementService
    {
        public Task ReplaceAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Manifest preview must not replace provider metadata.");
    }

    private sealed class UnusedCreateOnlyTextFileService
        : IGoogleDriveCreateOnlyTextFileService
    {
        public Task CreateAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Manifest preview must not create remote text content.");
    }

    private sealed class UnusedRecursiveFileListingService
        : IGoogleDriveRecursiveFileListingService
    {
        public Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Manifest preview must not list remote files.");
    }

    private sealed record ResolveCall(
        string RootFolderId,
        GoogleDriveRelativePath Path,
        GoogleDriveObjectKind? ExpectedKind,
        CancellationToken CancellationToken);
}
