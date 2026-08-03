using System.Text;
using System.Text.Json;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveSyncEngineCompatibilityTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("72000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset ManifestTimestamp =
        new(2026, 8, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Preview_UsesPaginatedMilestonePReadsAndKeepsValidRunsVisible()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        fixture.Drive.AddFolder("incomplete-folder-id", "incomplete-run");
        fixture.Drive.AddFolder("unreadable-folder-id", "unreadable-run");
        fixture.Drive.AddFolder("valid-folder-id", "valid-run");
        fixture.Drive.AddFile(
            "unreadable-manifest-id",
            "manifest.json",
            "unreadable-folder-id",
            [0xC3, 0x28]);

        TransferBackupManifest validManifest = Manifest("Valid Game");
        fixture.Drive.AddFile(
            "valid-manifest-id",
            "manifest.json",
            "valid-folder-id",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(validManifest)));

        var engine = Engine(
            fixture.Remote,
            new StaticBackupHistoryService(temp.Path));

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

        Assert.True(plan.ProviderValidationSucceeded);
        SyncItem valid = Assert.Single(plan.Items);
        Assert.Equal("valid-run", valid.RunName);
        Assert.Equal("Valid Game", valid.GameName);
        Assert.Equal(SyncItemAction.DownloadToLocal, valid.Action);
        TransferPreviewWarning unreadable = Assert.Single(
            plan.Warnings,
            warning => warning.Code == "RemoteRunUnreadable");
        Assert.Equal(TransferWarningSeverity.Warning, unreadable.Severity);
        Assert.DoesNotContain(plan.Items, item => item.RunName == "incomplete-run");

        Assert.Equal(
            [
                new RemoteCall(nameof(IRemoteFileSystem.ValidateAsync), null),
                new RemoteCall(nameof(IRemoteFileSystem.ListRunFolderNamesAsync), null),
                new RemoteCall(
                    nameof(IRemoteFileSystem.ReadTextFileAsync),
                    "unreadable-run/manifest.json"),
                new RemoteCall(
                    nameof(IRemoteFileSystem.ReadTextFileAsync),
                    "valid-run/manifest.json"),
                new RemoteCall(nameof(IRemoteFileSystem.RootExistsAsync), null)
            ],
            fixture.Remote.Calls);

        string rootQuery = fixture.QueryBuilder.BuildDirectChildrenQuery(
            Fixture.RootId,
            GoogleDriveObjectKind.Folder);
        GoogleDriveObjectListRequest[] rootPages = fixture.ObjectClients.ListRequests
            .Where(request => request.Query == rootQuery)
            .ToArray();
        Assert.Equal(3, rootPages.Length);
        Assert.Equal(
            new string?[] { null, "page-1", "page-2" },
            rootPages.Select(request => request.PageToken).ToArray());

        Assert.All(fixture.ObjectClients.ListRequests, AssertRequiredListContract);
        GoogleDriveObjectGetRequest rootRequest =
            Assert.Single(fixture.ObjectClients.GetRequests);
        Assert.Equal(GoogleDriveRequestContract.MetadataFields, rootRequest.Fields);
        Assert.False(rootRequest.SupportsAllDrives);
        Assert.Equal(Fixture.RootId, rootRequest.ObjectId);
        Assert.Equal(
            ["unreadable-manifest-id", "valid-manifest-id"],
            fixture.ContentApi.FileIds);
        Assert.Equal(0, fixture.ObjectClients.CreateFolderCalls);
        AssertNoTransferCalls(fixture.Remote);
        Assert.All(fixture.ContextFactory.Credentials,
            credential => Assert.True(credential.IsDisposed));
    }

    [Fact]
    public async Task SyncLog_MissingMetadataIsCreatedThenReplacedWithoutLosingHistory()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        TransferBackupManifest manifest = Manifest("Stable Game");
        fixture.Drive.AddFolder("stable-folder-id", "stable-run");
        fixture.Drive.AddFile(
            "stable-manifest-id",
            "manifest.json",
            "stable-folder-id",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

        var history = new StaticBackupHistoryService(
            temp.Path,
            new TransferBackupRunInfo(
                Path.Combine(temp.Path, "stable-run"),
                Path.Combine(temp.Path, "stable-run", "manifest.json"),
                manifest));
        var engine = Engine(fixture.Remote, history);

        Assert.Null(await fixture.Remote.ReadProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog));
        Assert.Empty(await engine.GetSyncLogAsync());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        Assert.Equal(SyncItemAction.InSync, Assert.Single(plan.Items).Action);

        var executeOptions = new SyncOptions
        {
            DryRun = false,
            ConfirmExecution = true
        };
        SyncResult firstResult = await engine.ExecuteAsync(plan, executeOptions);
        Assert.DoesNotContain(firstResult.Warnings,
            warning => warning.Code == "SyncLogWriteFailed");
        SyncLogEntry firstEntry = Assert.Single(await engine.GetSyncLogAsync());

        OfflineDriveObject metadata = Assert.Single(
            fixture.Drive.FindChildren(
                fixture.Drive.GetRequiredFolderId(".gamesave-sync", Fixture.RootId),
                "sync-log.json"));
        string authoritativeMetadataId = metadata.Metadata.Id;
        Assert.Single(fixture.CreationApi.Calls);
        Assert.Empty(fixture.ReplacementApi.Calls);

        SyncResult secondResult = await engine.ExecuteAsync(plan, executeOptions);
        Assert.DoesNotContain(secondResult.Warnings,
            warning => warning.Code == "SyncLogWriteFailed");
        IReadOnlyList<SyncLogEntry> finalLog = await engine.GetSyncLogAsync();

        Assert.Equal(2, finalLog.Count);
        Assert.Contains(
            finalLog,
            entry => entry.DeviceName == firstEntry.DeviceName
                && entry.TimestampUtc == firstEntry.TimestampUtc
                && entry.Uploaded == firstEntry.Uploaded
                && entry.Downloaded == firstEntry.Downloaded
                && entry.Conflicts == firstEntry.Conflicts
                && entry.BytesCopied == firstEntry.BytesCopied
                && entry.UploadedRuns.SequenceEqual(firstEntry.UploadedRuns)
                && entry.DownloadedRuns.SequenceEqual(firstEntry.DownloadedRuns));
        TextReplacementCall replacement =
            Assert.Single(fixture.ReplacementApi.Calls);
        Assert.Equal(authoritativeMetadataId, replacement.FileId);
        Assert.Equal(
            authoritativeMetadataId,
            Assert.Single(fixture.Drive.FindChildren(
                metadata.Metadata.ParentIds.Single(),
                "sync-log.json")).Metadata.Id);
        Assert.All(
            fixture.Remote.Calls.Where(call =>
                call.Name is nameof(IRemoteFileSystem.ReadProviderMetadataAsync) or
                    nameof(IRemoteFileSystem.ReplaceProviderMetadataAsync)),
            call => Assert.Equal(RemoteProviderMetadataPath.SyncLog, call.Path));
        Assert.DoesNotContain(
            fixture.Remote.Calls,
            call => call.Name == nameof(IRemoteFileSystem.CreateTextFileIfMissingAsync));
        AssertNoTransferCalls(fixture.Remote);
    }

    [Fact]
    public async Task ManifestContentStaysCreateOnlyAndCannotUseMetadataReplacement()
    {
        using var fixture = new Fixture();
        fixture.Drive.AddFolder("immutable-run-id", "immutable-run");
        const string manifestPath = "immutable-run/manifest.json";
        const string original = "{\"immutable\":true}";

        await fixture.Remote.CreateTextFileIfMissingAsync(manifestPath, original);

        GoogleDriveRemoteOperationException exists =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => fixture.Remote.CreateTextFileIfMissingAsync(
                    manifestPath,
                    "{\"immutable\":false}"));
        Assert.Equal(
            GoogleDriveCreateOnlyTextFileErrorCodes.AlreadyExists,
            exists.Result.ErrorCode);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Remote.ReplaceProviderMetadataAsync(
                manifestPath,
                "{\"immutable\":false}"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Remote.ReplaceProviderMetadataAsync(
                ".gamesave-sync/other.json",
                "{}"));

        Assert.Equal(original, await fixture.Remote.ReadTextFileAsync(manifestPath));
        Assert.Empty(fixture.ReplacementApi.Calls);
        Assert.Equal(
            original,
            Encoding.UTF8.GetString(
                fixture.Drive.GetRequiredFileBytes("manifest.json", "immutable-run-id")));
        AssertNoTransferCalls(fixture.Remote);
    }

    [Fact]
    public async Task ExistingLocalAndSftpTextSafetyBehaviorRemainsUnchanged()
    {
        using var temp = new TemporaryDirectory();
        string localRoot = temp.GetPath("local-remote");
        Directory.CreateDirectory(localRoot);
        var local = new LocalFolderRemoteFileSystem(
            localRoot,
            temp.GetPath("local-backups"));

        await local.CreateTextFileIfMissingAsync("run/manifest.json", "local-original");
        await Assert.ThrowsAsync<IOException>(() =>
            local.CreateTextFileIfMissingAsync("run/manifest.json", "local-changed"));
        await local.ReplaceProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog,
            "local-log-one");
        await local.ReplaceProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog,
            "local-log-two");

        Assert.Equal(
            "local-original",
            await local.ReadTextFileAsync("run/manifest.json"));
        Assert.Equal(
            "local-log-two",
            await local.ReadProviderMetadataAsync(RemoteProviderMetadataPath.SyncLog));

        var sftpClient = new InMemorySftpTextFileClient();
        var sftp = new SftpTextFileOperations(sftpClient);
        sftp.CreateTextFileIfMissing(
            "/remote/run/manifest.json",
            "sftp-original",
            CancellationToken.None);
        Assert.Throws<IOException>(() => sftp.CreateTextFileIfMissing(
            "/remote/run/manifest.json",
            "sftp-changed",
            CancellationToken.None));
        sftp.ReplaceProviderMetadata(
            "/remote/.gamesave-sync/sync-log.json",
            "sftp-log-one",
            CancellationToken.None);
        sftp.ReplaceProviderMetadata(
            "/remote/.gamesave-sync/sync-log.json",
            "sftp-log-two",
            CancellationToken.None);

        Assert.Equal(
            "sftp-original",
            sftpClient.ReadAllText("/remote/run/manifest.json"));
        Assert.Equal(
            "sftp-log-two",
            sftp.ReadProviderMetadata(
                "/remote/.gamesave-sync/sync-log.json",
                CancellationToken.None));

        var catalog = new GameSaves.Infrastructure.Sync.SyncProviderCatalog();
        Assert.True(catalog.IsImplemented(SyncProviderKind.LocalFolder));
        Assert.True(catalog.IsImplemented(SyncProviderKind.Sftp));
        Assert.False(catalog.IsImplemented(SyncProviderKind.GoogleDrive));
    }

    private static SyncEngine Engine(
        IRemoteFileSystem remote,
        IBackupHistoryService history) =>
        new(
            remote,
            "Google Drive",
            "Google Drive",
            history,
            new RecordingHistoryRepository());

    private static TransferBackupManifest Manifest(string game) =>
        new(
            SchemaVersion: 1,
            Kind: "manual",
            Game: game,
            SteamAppId: "424242",
            SourceAccountId: "source-profile",
            TargetAccountId: "target-profile",
            StartedUtc: ManifestTimestamp,
            CompletedUtc: ManifestTimestamp.AddMinutes(1),
            FileCount: 0,
            TotalBytes: 0,
            Items: []);

    private static void AssertRequiredListContract(
        GoogleDriveObjectListRequest request)
    {
        Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
        Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
        Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
        Assert.False(request.IncludeItemsFromAllDrives);
        Assert.False(request.SupportsAllDrives);
    }

    private static void AssertNoTransferCalls(RecordingRemoteFileSystem remote)
    {
        Assert.DoesNotContain(
            remote.Calls,
            call => call.Name == nameof(IRemoteFileSystem.ListFilesAsync));
        Assert.DoesNotContain(
            remote.Calls,
            call => call.Name == nameof(IRemoteFileSystem.UploadFileAsync));
        Assert.DoesNotContain(
            remote.Calls,
            call => call.Name == nameof(IRemoteFileSystem.DownloadFileAsync));
    }

    private sealed class Fixture : IDisposable
    {
        public const string RootId = "authoritative-root-id";

        public Fixture()
        {
            Drive = new OfflineDriveStore(RootId);
            QueryBuilder = new GoogleDriveQueryBuilder();
            Resolver = new OfflineResolver(Drive);
            ContextFactory = new RecordingContextFactory(Resolver);
            ObjectClients = new RecordingObjectClientFactory(Drive, QueryBuilder);
            var objectApi = new GoogleDriveObjectApi(QueryBuilder, ObjectClients);
            var cache = new GoogleDriveObjectIdCache();
            ContentApi = new RecordingTextContentApi(Drive);
            CreationApi = new RecordingTextCreationApi(Drive);
            ReplacementApi = new RecordingTextReplacementApi(Drive);

            var discovery = new GoogleDriveRunFolderDiscoveryService(
                ContextFactory,
                objectApi);
            var runFolders = new GoogleDriveRunFolderNameService(
                ContextFactory,
                discovery,
                objectApi);
            var textReader = new GoogleDriveTextFileReadService(
                ContextFactory,
                ContentApi);
            var providerReader = new GoogleDriveProviderMetadataReadService(
                textReader);
            var createOnly = new GoogleDriveCreateOnlyTextFileService(
                ContextFactory,
                CreationApi,
                new GoogleDriveObjectCreationCoordinator(),
                cache);
            var providerReplacement =
                new GoogleDriveProviderMetadataReplacementService(
                    ContextFactory,
                    CreationApi,
                    ReplacementApi,
                    new GoogleDriveProviderMetadataReplacementCoordinator(),
                    cache);
            var inner = new GoogleDriveRemoteFileSystem(
                ProfileId,
                "Google Drive",
                new RecordingValidationService(),
                new GoogleDriveRootExistenceService(
                    ContextFactory,
                    objectApi,
                    cache),
                new GoogleDriveFolderExistenceService(ContextFactory),
                runFolders,
                textReader,
                providerReader,
                providerReplacement,
                createOnly);
            Remote = new RecordingRemoteFileSystem(inner);
        }

        public OfflineDriveStore Drive { get; }

        public GoogleDriveQueryBuilder QueryBuilder { get; }

        public OfflineResolver Resolver { get; }

        public RecordingContextFactory ContextFactory { get; }

        public RecordingObjectClientFactory ObjectClients { get; }

        public RecordingTextContentApi ContentApi { get; }

        public RecordingTextCreationApi CreationApi { get; }

        public RecordingTextReplacementApi ReplacementApi { get; }

        public RecordingRemoteFileSystem Remote { get; }

        public void Dispose()
        {
            Assert.All(ContextFactory.Credentials,
                credential => Assert.True(credential.IsDisposed));
        }
    }

    private sealed class RecordingRemoteFileSystem(IRemoteFileSystem inner)
        : IRemoteFileSystem
    {
        public List<RemoteCall> Calls { get; } = [];

        public string DisplayRoot => inner.DisplayRoot;

        public string GetDisplayPath(string relativePath) =>
            inner.GetDisplayPath(relativePath);

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            Record(nameof(ValidateAsync));
            return inner.ValidateAsync(cancellationToken);
        }

        public Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default)
        {
            Record(nameof(RootExistsAsync));
            return inner.RootExistsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            Record(nameof(ListRunFolderNamesAsync));
            return inner.ListRunFolderNamesAsync(cancellationToken);
        }

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(FolderExistsAsync), relativeFolder);
            return inner.FolderExistsAsync(relativeFolder, cancellationToken);
        }

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(ReadTextFileAsync), relativePath);
            return inner.ReadTextFileAsync(relativePath, cancellationToken);
        }

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(CreateTextFileIfMissingAsync), relativePath);
            return inner.CreateTextFileIfMissingAsync(
                relativePath,
                content,
                cancellationToken);
        }

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(ReadProviderMetadataAsync), relativePath);
            return inner.ReadProviderMetadataAsync(relativePath, cancellationToken);
        }

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(ReplaceProviderMetadataAsync), relativePath);
            return inner.ReplaceProviderMetadataAsync(
                relativePath,
                content,
                cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(ListFilesAsync), relativeFolder);
            return inner.ListFilesAsync(relativeFolder, cancellationToken);
        }

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(UploadFileAsync), relativeRemotePath);
            return inner.UploadFileAsync(
                localFilePath,
                relativeRemotePath,
                cancellationToken);
        }

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            Record(nameof(DownloadFileAsync), relativeRemotePath);
            return inner.DownloadFileAsync(
                relativeRemotePath,
                localFilePath,
                cancellationToken);
        }

        private void Record(string name, string? path = null) =>
            Calls.Add(new RemoteCall(name, path));
    }

    private sealed class RecordingValidationService
        : IGoogleDriveRemoteValidationService
    {
        public Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(ProfileId, remoteProfileId);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid));
        }
    }

    private sealed class RecordingContextFactory(OfflineResolver resolver)
        : IGoogleDriveRemoteOperationContextFactory
    {
        public List<GoogleAuthorizedCredential> Credentials { get; } = [];

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(ProfileId, remoteProfileId);
            cancellationToken.ThrowIfCancellationRequested();
            GoogleAuthorizedCredential credential = Credential();
            Credentials.Add(credential);
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                Fixture.RootId,
                credential,
                resolver));
        }
    }

    private sealed class OfflineResolver(OfflineDriveStore drive)
        : IGoogleDriveObjectPathResolver
    {
        public List<GoogleDriveRelativePath> ResolvePaths { get; } = [];

        public List<GoogleDriveRelativePath> EnsurePaths { get; } = [];

        public List<(string ParentId, string Name)> FindCalls { get; } = [];

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindCalls.Add((parentId, exactName));
            return Task.FromResult(ResolveChild(
                GoogleDriveRelativePath.Parse(exactName),
                parentId,
                exactName,
                expectedKind));
        }

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvePaths.Add(relativePath);
            if (relativePath.IsRoot)
            {
                OfflineDriveObject root = drive.GetRequired(rootFolderId);
                return Task.FromResult(Found(
                    relativePath,
                    root,
                    expectedFinalKind));
            }

            string parentId = rootFolderId;
            for (int index = 0; index < relativePath.Segments.Count; index++)
            {
                string segment = relativePath.Segments[index];
                GoogleDriveObjectKind expected =
                    index == relativePath.Segments.Count - 1
                        ? expectedFinalKind ?? GoogleDriveObjectKind.File
                        : GoogleDriveObjectKind.Folder;
                GoogleDriveObjectResolutionResult result = ResolveChild(
                    relativePath,
                    parentId,
                    segment,
                    expected);
                if (result.Status != GoogleDriveObjectResolutionStatus.Found)
                    return Task.FromResult(result);
                parentId = result.ObjectId!;
            }

            return Task.FromResult(Found(
                relativePath,
                drive.GetRequired(parentId),
                expectedFinalKind));
        }

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePaths.Add(relativeFolderPath);
            if (relativeFolderPath.IsRoot)
            {
                return Task.FromResult(Found(
                    relativeFolderPath,
                    drive.GetRequired(rootFolderId),
                    GoogleDriveObjectKind.Folder));
            }

            string parentId = rootFolderId;
            bool created = false;
            foreach (string segment in relativeFolderPath.Segments)
            {
                IReadOnlyList<OfflineDriveObject> matches =
                    drive.FindChildren(parentId, segment);
                if (matches.Count > 1)
                    return Task.FromResult(Ambiguous(relativeFolderPath));
                if (matches.Count == 1)
                {
                    if (matches[0].Metadata.Kind != GoogleDriveObjectKind.Folder)
                        return Task.FromResult(TypeMismatch(
                            relativeFolderPath,
                            matches[0]));
                    parentId = matches[0].Metadata.Id;
                    continue;
                }

                parentId = drive.AddGeneratedFolder(segment, parentId).Metadata.Id;
                created = true;
            }

            OfflineDriveObject folder = drive.GetRequired(parentId);
            return Task.FromResult(new GoogleDriveObjectResolutionResult(
                created
                    ? GoogleDriveObjectResolutionStatus.Created
                    : GoogleDriveObjectResolutionStatus.Found,
                relativeFolderPath,
                GoogleDriveObjectKind.Folder,
                folder.Metadata,
                folder.Metadata.Id));
        }

        private GoogleDriveObjectResolutionResult ResolveChild(
            GoogleDriveRelativePath path,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind)
        {
            IReadOnlyList<OfflineDriveObject> matches =
                drive.FindChildren(parentId, exactName);
            if (matches.Count == 0)
            {
                return new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.NotFound,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.NotFound,
                    message: "The object was not found.");
            }
            if (matches.Count > 1)
                return Ambiguous(path);
            if (matches[0].Metadata.Kind != expectedKind)
                return TypeMismatch(path, matches[0]);
            return Found(path, matches[0], expectedKind);
        }

        private static GoogleDriveObjectResolutionResult Found(
            GoogleDriveRelativePath path,
            OfflineDriveObject value,
            GoogleDriveObjectKind? expectedKind)
        {
            if (expectedKind is not null && value.Metadata.Kind != expectedKind)
                return TypeMismatch(path, value);
            return new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Found,
                path,
                value.Metadata.Kind,
                value.Metadata,
                value.Metadata.Id);
        }

        private static GoogleDriveObjectResolutionResult Ambiguous(
            GoogleDriveRelativePath path) =>
            new(
                GoogleDriveObjectResolutionStatus.Ambiguous,
                path,
                errorCode: GoogleDriveObjectResolutionErrorCodes.Ambiguous,
                message: "The object name is ambiguous.");

        private static GoogleDriveObjectResolutionResult TypeMismatch(
            GoogleDriveRelativePath path,
            OfflineDriveObject value) =>
            new(
                GoogleDriveObjectResolutionStatus.TypeMismatch,
                path,
                value.Metadata.Kind,
                value.Metadata,
                value.Metadata.Id,
                GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
                "The object has the wrong type.");
    }

    private sealed class RecordingObjectClientFactory(
        OfflineDriveStore drive,
        GoogleDriveQueryBuilder queryBuilder)
        : IGoogleDriveObjectClientFactory
    {
        private int _disposedClients;
        private int _createFolderCalls;

        public List<GoogleDriveObjectListRequest> ListRequests { get; } = [];

        public List<GoogleDriveObjectGetRequest> GetRequests { get; } = [];

        public int DisposedClients => Volatile.Read(ref _disposedClients);

        public int CreateFolderCalls => Volatile.Read(ref _createFolderCalls);

        public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential)
        {
            Assert.False(credential.IsDisposed);
            return new Client(this, drive, queryBuilder);
        }

        private sealed class Client(
            RecordingObjectClientFactory owner,
            OfflineDriveStore drive,
            GoogleDriveQueryBuilder queryBuilder)
            : IGoogleDriveObjectClient
        {
            private bool _disposed;

            public Task<GoogleDriveObjectMetadata> GetAsync(
                GoogleDriveObjectGetRequest request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.GetRequests.Add(request);
                return Task.FromResult(drive.GetRequired(request.ObjectId).Metadata);
            }

            public Task<GoogleDriveObjectListPage> ListAsync(
                GoogleDriveObjectListRequest request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.ListRequests.Add(request);
                IReadOnlyList<GoogleDriveObjectMetadata> objects =
                    ResolveQuery(request.Query);
                int offset = request.PageToken is null
                    ? 0
                    : int.Parse(request.PageToken["page-".Length..]);
                GoogleDriveObjectMetadata[] page = objects
                    .Skip(offset)
                    .Take(1)
                    .ToArray();
                string? next = offset + page.Length < objects.Count
                    ? $"page-{offset + page.Length}"
                    : null;
                return Task.FromResult(new GoogleDriveObjectListPage(
                    page,
                    next,
                    IncompleteSearch: false));
            }

            public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
                GoogleDriveFolderCreateRequest request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._createFolderCalls);
                throw new InvalidOperationException(
                    "SyncEngine compatibility reads must not create folders through the object API.");
            }

            private IReadOnlyList<GoogleDriveObjectMetadata> ResolveQuery(string query)
            {
                foreach (string parentId in drive.ObjectIds)
                {
                    if (query == queryBuilder.BuildDirectChildrenQuery(
                            parentId,
                            GoogleDriveObjectKind.Folder))
                    {
                        return drive.FindChildren(parentId)
                            .Where(value =>
                                value.Metadata.Kind == GoogleDriveObjectKind.Folder)
                            .Select(value => value.Metadata)
                            .ToArray();
                    }

                    if (query == queryBuilder.BuildExactNameChildQuery(
                            parentId,
                            "manifest.json"))
                    {
                        return drive.FindChildren(parentId, "manifest.json")
                            .Select(value => value.Metadata)
                            .ToArray();
                    }
                }

                throw new InvalidOperationException(
                    "The offline Drive client received an unexpected query.");
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                Interlocked.Increment(ref owner._disposedClients);
            }
        }
    }

    private sealed class RecordingTextContentApi(OfflineDriveStore drive)
        : IGoogleDriveTextContentApi
    {
        public List<string> FileIds { get; } = [];

        public Task<GoogleDriveTextContentResult> DownloadTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            FileIds.Add(fileId);
            return Task.FromResult(new GoogleDriveTextContentResult(
                drive.GetRequired(fileId).Content ?? []));
        }
    }

    private sealed class RecordingTextCreationApi(OfflineDriveStore drive)
        : IGoogleDriveTextCreationApi
    {
        public List<TextCreationCall> Calls { get; } = [];

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
            OfflineDriveObject created = drive.AddGeneratedFile(
                exactFileName,
                parentFolderId,
                contentBytes.ToArray(),
                mediaType);
            Calls.Add(new TextCreationCall(
                created.Metadata.Id,
                parentFolderId,
                exactFileName,
                contentBytes.ToArray(),
                mediaType));
            return Task.FromResult(
                new GoogleDriveTextCreationResult(created.Metadata.Id));
        }
    }

    private sealed class RecordingTextReplacementApi(OfflineDriveStore drive)
        : IGoogleDriveTextReplacementApi
    {
        public List<TextReplacementCall> Calls { get; } = [];

        public Task<GoogleDriveTextReplacementResult> ReplaceTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            drive.ReplaceContent(fileId, contentBytes.ToArray());
            Calls.Add(new TextReplacementCall(
                fileId,
                contentBytes.ToArray(),
                mediaType));
            return Task.FromResult(new GoogleDriveTextReplacementResult(fileId));
        }
    }

    private sealed class OfflineDriveStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, OfflineDriveObject> _objects = [];
        private int _nextFolderId;
        private int _nextFileId;

        public OfflineDriveStore(string rootId)
        {
            RootId = rootId;
            AddFolder(rootId, "Application Root", GoogleDriveRequestContract.MyDriveRootId);
        }

        public string RootId { get; }

        public IReadOnlyList<string> ObjectIds
        {
            get
            {
                lock (_gate)
                    return _objects.Keys.ToArray();
            }
        }

        public void AddFolder(string id, string name, string? parentId = null) =>
            Add(new OfflineDriveObject(
                new GoogleDriveObjectMetadata(
                    id,
                    name,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    trashed: false,
                    parentIds: [parentId ?? RootId],
                    driveId: null),
                Content: null));

        public void AddFile(
            string id,
            string name,
            string parentId,
            byte[] content,
            string mediaType = "application/json") =>
            Add(new OfflineDriveObject(
                new GoogleDriveObjectMetadata(
                    id,
                    name,
                    mediaType,
                    trashed: false,
                    parentIds: [parentId],
                    driveId: null),
                content.ToArray()));

        public OfflineDriveObject AddGeneratedFolder(
            string name,
            string parentId)
        {
            string id = $"created-folder-{Interlocked.Increment(ref _nextFolderId)}";
            AddFolder(id, name, parentId);
            return GetRequired(id);
        }

        public OfflineDriveObject AddGeneratedFile(
            string name,
            string parentId,
            byte[] content,
            string mediaType)
        {
            string id = $"created-file-{Interlocked.Increment(ref _nextFileId)}";
            AddFile(id, name, parentId, content, mediaType);
            return GetRequired(id);
        }

        public OfflineDriveObject GetRequired(string id)
        {
            lock (_gate)
                return _objects[id];
        }

        public IReadOnlyList<OfflineDriveObject> FindChildren(
            string parentId,
            string? exactName = null)
        {
            lock (_gate)
            {
                return _objects.Values
                    .Where(value => value.Metadata.ParentIds.Contains(
                        parentId,
                        StringComparer.Ordinal))
                    .Where(value => exactName is null || string.Equals(
                        value.Metadata.Name,
                        exactName,
                        StringComparison.Ordinal))
                    .OrderBy(value => value.Metadata.Name, StringComparer.Ordinal)
                    .ThenBy(value => value.Metadata.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public string GetRequiredFolderId(string name, string parentId)
        {
            OfflineDriveObject value = Assert.Single(FindChildren(parentId, name));
            Assert.Equal(GoogleDriveObjectKind.Folder, value.Metadata.Kind);
            return value.Metadata.Id;
        }

        public byte[] GetRequiredFileBytes(string name, string parentId)
        {
            OfflineDriveObject value = Assert.Single(FindChildren(parentId, name));
            Assert.Equal(GoogleDriveObjectKind.File, value.Metadata.Kind);
            return value.Content!.ToArray();
        }

        public void ReplaceContent(string fileId, byte[] content)
        {
            lock (_gate)
            {
                OfflineDriveObject existing = _objects[fileId];
                Assert.Equal(GoogleDriveObjectKind.File, existing.Metadata.Kind);
                _objects[fileId] = existing with { Content = content.ToArray() };
            }
        }

        private void Add(OfflineDriveObject value)
        {
            lock (_gate)
                _objects.Add(value.Metadata.Id, value);
        }
    }

    private sealed class StaticBackupHistoryService
        : IBackupHistoryService
    {
        private readonly IReadOnlyList<TransferBackupRunInfo> _runs;

        public StaticBackupHistoryService(
            string backupBasePath,
            params TransferBackupRunInfo[] runs)
        {
            BackupBasePath = backupBasePath;
            _runs = runs;
        }

        public string BackupBasePath { get; }

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_runs);
        }

        public string GetBackupBasePath() => BackupBasePath;
    }

    private sealed class InMemorySftpTextFileClient : ISftpTextFileClient
    {
        private readonly Dictionary<string, byte[]> _files =
            new(StringComparer.Ordinal);

        public bool Exists(string path) => _files.ContainsKey(path);

        public Stream Open(string path, FileMode mode, FileAccess access)
        {
            Assert.Equal(FileMode.CreateNew, mode);
            Assert.Equal(FileAccess.Write, access);
            if (_files.ContainsKey(path))
                throw new IOException("The remote file already exists.");
            return new CommitMemoryStream(bytes => _files.Add(path, bytes));
        }

        public string ReadAllText(string path) =>
            Encoding.UTF8.GetString(_files[path]);

        public void WriteAllText(string path, string content) =>
            _files[path] = Encoding.UTF8.GetBytes(content);

        public void RenameFile(string oldPath, string newPath, bool isPosix)
        {
            Assert.True(isPosix);
            _files[newPath] = _files[oldPath];
            _files.Remove(oldPath);
        }

        public void DeleteFile(string path) => _files.Remove(path);
    }

    private sealed class CommitMemoryStream(Action<byte[]> commit) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                commit(ToArray());
            }
            base.Dispose(disposing);
        }
    }

    private static GoogleAuthorizedCredential Credential()
    {
        var flow = new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = "offline-test-client-id",
                    ClientSecret = "offline-test-client-secret"
                }
            });
        var user = new UserCredential(
            flow,
            ProfileId.ToString("D"),
            new TokenResponse
            {
                AccessToken = "offline-test-access-token",
                RefreshToken = "offline-test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private sealed record RemoteCall(string Name, string? Path);

    private sealed record TextCreationCall(
        string FileId,
        string ParentId,
        string FileName,
        byte[] Content,
        string MediaType);

    private sealed record TextReplacementCall(
        string FileId,
        byte[] Content,
        string MediaType);

    private sealed record OfflineDriveObject(
        GoogleDriveObjectMetadata Metadata,
        byte[]? Content);
}
