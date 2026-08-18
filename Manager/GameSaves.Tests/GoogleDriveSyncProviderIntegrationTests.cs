using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace GameSaves.Tests;

/// <summary>
/// Milestone T Task 8. Drives the provider end to end through the real
/// dependency-injection container: the profile-scoped provider factory, the
/// real Google Drive remote file system, and the shared SyncEngine, over an
/// offline Drive. No account, credential value, browser, or network is used.
/// </summary>
public sealed class GoogleDriveSyncProviderIntegrationTests
{
    private const string RootId = "provider-integration-root-id";
    private const string LocalOnlyRun = "2026-08-17_09-00-00_manual";
    private const string RemoteOnlyRun = "2026-08-17_10-00-00_manual";

    private static readonly Guid ProfileId =
        Guid.Parse("3f1c9a77-58bd-4d0e-9f2a-71c4a5d6e8b3");

    [Fact]
    public async Task Preview_TravelsTheWholeCompositionThroughRealRegistration()
    {
        using var harness = new Harness();

        SyncPlan plan = await harness.Provider.CreatePreviewAsync(new SyncOptions());

        Assert.Equal("Google Drive", plan.ProviderName);
        Assert.Equal("GameSave Manager Backups", plan.RemoteRoot);
        Assert.Equal(1, plan.UploadCount);
        Assert.Equal(1, plan.DownloadCount);
        Assert.True(plan.CanExecute);
        Assert.NotEmpty(harness.ObjectClients.ListRequests);
        Assert.Equal(0, harness.ObjectClients.CreateFolderCalls);
    }

    [Fact]
    public async Task Execute_UploadsAndDownloadsThroughTheRealComposition()
    {
        using var harness = new Harness();

        SyncResult result = await harness.Execute();

        string detail = string.Join("; ", result.Items.Select(
            item => $"{item.Item.RunName}:{item.Status}:{item.Error}"));
        Assert.True(result.Uploaded == 1, detail);
        Assert.True(result.Downloaded == 1, detail);
        Assert.False(result.HasErrors, detail);

        // The local-only run now exists remotely, byte for byte.
        Assert.Equal(
            Harness.Payload(LocalOnlyRun),
            harness.RemoteBytes($"{LocalOnlyRun}/files/save.dat"));
        Assert.NotNull(harness.RemoteBytes($"{LocalOnlyRun}/manifest.json"));

        // The remote-only run now exists locally, byte for byte.
        Assert.Equal(
            Harness.Payload(RemoteOnlyRun),
            File.ReadAllBytes(harness.LocalPath(RemoteOnlyRun, "files", "save.dat")));
        Assert.True(File.Exists(harness.LocalPath(RemoteOnlyRun, "manifest.json")));
        Assert.Empty(harness.TemporaryFiles());
    }

    [Fact]
    public async Task Execute_NeverOverwritesAnExistingRemoteRun()
    {
        using var harness = new Harness();
        harness.AddRemoteRun(LocalOnlyRun, "Already There");
        byte[] before = harness.RemoteBytes($"{LocalOnlyRun}/files/save.dat")!;

        SyncResult result = await harness.Execute();

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(
            before,
            harness.RemoteBytes($"{LocalOnlyRun}/files/save.dat"));
        Assert.Empty(harness.MediaUploads.Calls);
    }

    [Fact]
    public async Task Execute_NeverOverwritesExistingLocalData()
    {
        using var harness = new Harness();
        harness.AddLocalRun(RemoteOnlyRun, "Already There");
        string existing = harness.LocalPath(RemoteOnlyRun, "files", "save.dat");
        byte[] before = File.ReadAllBytes(existing);

        SyncResult result = await harness.Execute();

        Assert.Equal(0, result.Downloaded);
        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.Empty(harness.MediaDownloads.Calls);
    }

    [Fact]
    public async Task SyncLog_IsAppendedAndReadBackThroughProviderMetadata()
    {
        using var harness = new Harness();

        await harness.Execute();
        IReadOnlyList<SyncLogEntry> log = await harness.Provider.GetSyncLogAsync();

        SyncLogEntry entry = Assert.Single(log);
        Assert.Equal(1, entry.Uploaded);
        Assert.Equal(1, entry.Downloaded);
        Assert.Equal([LocalOnlyRun], entry.UploadedRuns);
        Assert.Equal([RemoteOnlyRun], entry.DownloadedRuns);
    }

    [Fact]
    public async Task TheProviderPath_IssuesNoForbiddenDriveOperation()
    {
        using var harness = new Harness();

        await harness.Execute();

        // The object client cannot express deletion, trashing, renaming,
        // moving, sharing, or permission changes at all.
        Assert.Equal(
            new[] { "CreateFolderAsync", "GetAsync", "ListAsync" },
            typeof(IGoogleDriveObjectClient).GetMethods()
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        Assert.All(harness.ObjectClients.ListRequests, request =>
        {
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
        });

        // Only the sync log is ever replaced; run content is create-only.
        Assert.All(harness.TextReplacements, fileId =>
            Assert.Equal(
                "sync-log.json",
                harness.Drive.GetRequired(fileId).Metadata.Name));
    }

    [Fact]
    public async Task TheProviderIsBuiltByTheRegisteredFactory()
    {
        using var harness = new Harness();

        Assert.IsType<GoogleDriveSyncProvider>(harness.Provider);
        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        // Milestone U added the factory case itself, so the surviving
        // invariant is that there is exactly one, of the agreed shape, and
        // that having it activates nothing.
        MethodInfo driveCase = Assert.Single(
            typeof(SyncProviderFactory).GetMethods(),
            method => method.Name.Contains("Google", StringComparison.Ordinal));
        Assert.Equal("CreateGoogleDriveProvider", driveCase.Name);
        Assert.Equal(typeof(Guid), Assert.Single(
            driveCase.GetParameters()).ParameterType);

        await Task.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TemporaryDirectory _root = new();

        public Harness()
        {
            Directory.CreateDirectory(LocalBase);
            Drive = new OfflineDriveStore(RootId);
            ObjectClients = new OfflineDriveObjectClientFactory(
                Drive,
                new GoogleDriveQueryBuilder());
            MediaUploads = new OfflineDriveMediaUploadClientFactory(Drive);
            MediaDownloads = new OfflineDriveMediaDownloadClientFactory(Drive);

            AddLocalRun(LocalOnlyRun, "Local Only Game");
            AddRemoteRun(RemoteOnlyRun, "Remote Only Game");

            var profiles = new InMemorySyncRemoteProfileRepository();
            profiles.Create(Profile());

            var collection = new ServiceCollection();
            collection.AddGameSavesInfrastructure();
            collection.RemoveAll<ISyncRemoteProfileRepository>();
            collection.RemoveAll<IGoogleDriveAuthorizedSessionFactory>();
            collection.RemoveAll<IGoogleDriveObjectClientFactory>();
            collection.RemoveAll<IGoogleDriveMediaUploadClientFactory>();
            collection.RemoveAll<IGoogleDriveMediaDownloadClientFactory>();
            collection.RemoveAll<IGoogleDriveRemoteValidationService>();
            collection.RemoveAll<IGoogleDriveTextContentApi>();
            collection.RemoveAll<IGoogleDriveTextCreationApi>();
            collection.RemoveAll<IGoogleDriveTextReplacementApi>();
            collection.RemoveAll<IBackupHistoryService>();
            collection.RemoveAll<ITransferHistoryRepository>();

            collection.AddSingleton<ISyncRemoteProfileRepository>(profiles);
            collection.AddSingleton<IGoogleDriveAuthorizedSessionFactory>(
                new OfflineSessionFactory());
            collection.AddSingleton<IGoogleDriveObjectClientFactory>(ObjectClients);
            collection.AddSingleton<IGoogleDriveMediaUploadClientFactory>(MediaUploads);
            collection.AddSingleton<IGoogleDriveMediaDownloadClientFactory>(
                MediaDownloads);
            collection.AddSingleton<IGoogleDriveRemoteValidationService>(
                new AlwaysValidValidationService());
            collection.AddSingleton<IGoogleDriveTextContentApi>(
                new OfflineTextContentApi(Drive));
            collection.AddSingleton<IGoogleDriveTextCreationApi>(
                new OfflineTextCreationApi(Drive));
            collection.AddSingleton<IGoogleDriveTextReplacementApi>(
                new OfflineTextReplacementApi(Drive, TextReplacements));
            collection.AddSingleton<IBackupHistoryService>(
                new WorkspaceHistoryService(LocalBase));
            collection.AddSingleton<ITransferHistoryRepository>(
                new RecordingHistoryRepository());

            _services = collection.BuildServiceProvider();
            Provider = _services
                .GetRequiredService<IGoogleDriveSyncProviderFactory>()
                .Create(ProfileId);
        }

        public OfflineDriveStore Drive { get; }

        public OfflineDriveObjectClientFactory ObjectClients { get; }

        public OfflineDriveMediaUploadClientFactory MediaUploads { get; }

        public OfflineDriveMediaDownloadClientFactory MediaDownloads { get; }

        public List<string> TextReplacements { get; } = [];

        public ISyncProvider Provider { get; }

        public IRemoteFileSystem Remote => _services
            .GetRequiredService<IGoogleDriveRemoteFileSystemFactory>()
            .Create(ProfileId);

        public string LocalBase => Path.Combine(_root.Path, "backups");

        public string LocalPath(params string[] segments) =>
            Path.Combine([LocalBase, .. segments]);

        public static byte[] Payload(string runName) =>
            Encoding.UTF8.GetBytes($"payload for {runName}");

        public async Task<SyncResult> Execute()
        {
            SyncPlan plan = await Provider.CreatePreviewAsync(new SyncOptions());
            return await Provider.ExecuteAsync(
                plan,
                new SyncOptions { DryRun = false, ConfirmExecution = true });
        }

        public string[] TemporaryFiles() =>
            Directory.GetFiles(
                LocalBase,
                $"*{GoogleDriveLocalDownloadDestination.TemporarySuffix}",
                SearchOption.AllDirectories);

        public byte[]? RemoteBytes(string relativePath)
        {
            string parentId = RootId;
            string[] segments = relativePath.Split('/');
            foreach (string segment in segments[..^1])
            {
                IReadOnlyList<OfflineDriveObject> children =
                    Drive.FindChildren(parentId, segment);
                if (children.Count != 1)
                    return null;

                parentId = children[0].Metadata.Id;
            }

            IReadOnlyList<OfflineDriveObject> match =
                Drive.FindChildren(parentId, segments[^1]);
            return match.Count == 1 ? match[0].Content : null;
        }

        public void AddLocalRun(string runName, string game)
        {
            string runRoot = LocalPath(runName);
            Directory.CreateDirectory(Path.Combine(runRoot, "files"));
            File.WriteAllBytes(
                Path.Combine(runRoot, "files", "save.dat"),
                Payload(runName));
            File.WriteAllText(
                Path.Combine(runRoot, "manifest.json"),
                JsonSerializer.Serialize(Manifest(game)));
        }

        public void AddRemoteRun(string runName, string game)
        {
            OfflineDriveObject runFolder = Drive.AddGeneratedFolder(runName, RootId);
            OfflineDriveObject files = Drive.AddGeneratedFolder(
                "files",
                runFolder.Metadata.Id);
            Drive.AddGeneratedFile(
                "save.dat",
                files.Metadata.Id,
                Payload(runName),
                "application/octet-stream");
            Drive.AddGeneratedFile(
                "manifest.json",
                runFolder.Metadata.Id,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Manifest(game))),
                "application/json");
        }

        public void Dispose()
        {
            _services.Dispose();
            _root.Dispose();
        }

        private static SyncRemoteProfile Profile() =>
            new(
                ProfileId,
                "Google Drive profile",
                SyncProviderKind.GoogleDrive,
                AccountDisplayName: "Offline Test",
                RemoteRootDisplayName: "GameSave Manager Backups",
                ProviderSettings: new GoogleDriveSyncRemoteSettings(
                    "offline@example.invalid",
                    GoogleDriveAuthorizationScopes.DriveFile),
                CreatedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
                UpdatedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
                LastUsedUtc: null,
                LastSuccessfulConnectionUtc: null,
                RemoteFolderId: RootId);
    }

    private static TransferBackupManifest Manifest(string game) =>
        new(
            SchemaVersion: 1,
            Kind: "manual",
            Game: game,
            SteamAppId: "424242",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            CompletedUtc: DateTimeOffset.Parse("2026-08-17T10:01:00Z"),
            FileCount: 0,
            TotalBytes: 0,
            Items: []);

    private sealed class WorkspaceHistoryService(string basePath)
        : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runs = Directory.GetDirectories(basePath)
                .Select(runRoot => new TransferBackupRunInfo(
                    runRoot,
                    Path.Combine(runRoot, "manifest.json"),
                    JsonSerializer.Deserialize<TransferBackupManifest>(
                        File.ReadAllText(
                            Path.Combine(runRoot, "manifest.json")))!))
                .ToList();

            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(runs);
        }

        public string GetBackupBasePath() => basePath;
    }

    private sealed class OfflineTextContentApi(OfflineDriveStore drive)
        : IGoogleDriveTextContentApi
    {
        public Task<GoogleDriveTextContentResult> DownloadTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(credential.IsDisposed);
            return Task.FromResult(new GoogleDriveTextContentResult(
                drive.GetRequired(fileId).Content ?? []));
        }
    }

    private sealed class OfflineTextCreationApi(OfflineDriveStore drive)
        : IGoogleDriveTextCreationApi
    {
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
            return Task.FromResult(
                new GoogleDriveTextCreationResult(created.Metadata.Id));
        }
    }

    private sealed class OfflineTextReplacementApi(
        OfflineDriveStore drive,
        List<string> replacements)
        : IGoogleDriveTextReplacementApi
    {
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
            replacements.Add(fileId);
            return Task.FromResult(new GoogleDriveTextReplacementResult(fileId));
        }
    }

    private sealed class OfflineSessionFactory : IGoogleDriveAuthorizedSessionFactory
    {
        public Task<GoogleDriveAuthorizedSession> RestoreAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ProfileId, profile.Id);
            cancellationToken.ThrowIfCancellationRequested();

            var flow = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = "offline-test-client-id",
                        ClientSecret = "offline-test-client-secret"
                    }
                });

            return Task.FromResult(new GoogleDriveAuthorizedSession(
                new GoogleAuthorizedCredential(new UserCredential(
                    flow,
                    ProfileId.ToString("D"),
                    new TokenResponse
                    {
                        AccessToken = "offline-test-access-token",
                        RefreshToken = "offline-test-refresh-token"
                    })),
                new GoogleDriveAccountInfo("Offline Test", null)));
        }
    }

    private sealed class AlwaysValidValidationService
        : IGoogleDriveRemoteValidationService
    {
        public Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid));
        }
    }
}
