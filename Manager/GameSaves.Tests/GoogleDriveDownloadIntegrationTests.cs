using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using System.Security.Cryptography;

namespace GameSaves.Tests;

/// <summary>
/// Exercises the complete Google Drive download composition, from
/// Infrastructure dependency injection through the remote-filesystem factory,
/// the one-file download service, source resolution, and the media client.
/// Every provider seam is hermetic: no account, credential value, browser, or
/// network is involved.
/// </summary>
public sealed class GoogleDriveDownloadIntegrationTests
{
    private const string RootId = "download-integration-root-id";

    private static readonly Guid ProfileId =
        Guid.Parse("74a0c1de-9f2b-4d1c-8a55-6b7c8d9e0f21");

    [Fact]
    public async Task Download_TravelsTheWholeCompositionForANestedSource()
    {
        using var harness = new Harness();
        harness.AddRemoteFile("Run 42/saves/profile/slot1.sav", [1, 2, 3, 4, 5]);
        string destination = harness.LocalPath("restored", "slot1.sav");

        long bytes = await harness.Remote.DownloadFileAsync(
            "Run 42/saves/profile/slot1.sav",
            destination);

        Assert.Equal(5, bytes);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(destination));
        MediaDownloadCall call = Assert.Single(harness.Media.Calls);
        Assert.Equal(5, call.BytesWritten);
        Assert.Empty(harness.TemporaryFiles());
        harness.AssertEveryClientReleased();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024)]
    [InlineData((5 * 1024 * 1024) + 1)]
    public async Task Download_PreservesExactBytesForEverySize(int length)
    {
        using var harness = new Harness();
        byte[] content = new byte[length];
        for (int index = 0; index < content.Length; index++)
            content[index] = (byte)((index * 3) % 251);
        harness.AddRemoteFile("Run 42/payload.bin", content);
        string destination = harness.LocalPath("payload.bin");

        long bytes = await harness.Remote.DownloadFileAsync(
            "Run 42/payload.bin",
            destination);

        Assert.Equal(length, bytes);
        Assert.Equal(
            SHA256.HashData(content),
            SHA256.HashData(File.ReadAllBytes(destination)));
        Assert.Empty(harness.TemporaryFiles());
    }

    [Fact]
    public async Task Download_RefusesAnExistingDestinationAndLeavesItUntouched()
    {
        using var harness = new Harness();
        harness.AddRemoteFile("Run 42/save.bin", [1, 2, 3]);
        string destination = harness.LocalPath("save.bin");
        File.WriteAllBytes(destination, [7, 7]);

        GoogleDriveLocalDownloadDestinationException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalDownloadDestinationException>(
                () => harness.Remote.DownloadFileAsync(
                    "Run 42/save.bin",
                    destination));

        Assert.Equal(
            "GoogleDriveDownloadDestinationExists",
            exception.SafeErrorCode);
        Assert.Equal([7, 7], File.ReadAllBytes(destination));
        Assert.Empty(harness.Media.Calls);
        Assert.Empty(harness.TemporaryFiles());
    }

    public static TheoryData<string, string> UnsafeSources => new()
    {
        { "Run 42/missing.bin", "GoogleDriveDownloadSourceNotFound" },
        { "Run 42/SAVE.BIN", "GoogleDriveDownloadSourceCaseCollision" },
        { "Run 42/saves", "GoogleDriveDownloadSourceTypeCollision" },
        { "Run 42/notes.doc", "GoogleDriveDownloadSourceUnsupportedObject" }
    };

    [Theory]
    [MemberData(nameof(UnsafeSources))]
    public async Task Download_FailsClosedForEveryUnsafeSource(
        string remotePath,
        string expectedErrorCode)
    {
        using var harness = new Harness();
        harness.AddRemoteFile("Run 42/save.bin", [1, 2, 3]);
        harness.AddRemoteFile("Run 42/saves/nested.bin", [4]);
        harness.AddRemoteObject(
            "Run 42/notes.doc",
            "application/vnd.google-apps.document");
        string destination = harness.LocalPath("restored.bin");

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Remote.DownloadFileAsync(remotePath, destination));

        Assert.Equal(expectedErrorCode, exception.Result.ErrorCode);
        Assert.False(File.Exists(destination));
        Assert.Empty(harness.Media.Calls);
        Assert.Empty(harness.TemporaryFiles());
        harness.AssertEveryClientReleased();
    }

    [Fact]
    public async Task Download_ProviderFailureIsSanitizedAndLeavesNoLocalFile()
    {
        using var harness = new Harness();
        harness.AddRemoteFile("Run 42/save.bin", [1, 2, 3]);
        harness.Media.FailureFor = _ => new IOException(
            @"The synthetic provider rejected C:\private\save.bin.");
        string destination = harness.LocalPath("save.bin");

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Remote.DownloadFileAsync("Run 42/save.bin", destination));

        Assert.Equal(
            GoogleDriveBinaryDownloadErrorCodes.Failed,
            exception.Result.ErrorCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "private",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.Empty(harness.TemporaryFiles());
    }

    [Fact]
    public async Task Download_CancellationLeavesNoLocalFileOrTemporaryFile()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        harness.AddRemoteFile("Run 42/save.bin", new byte[32 * 1024]);
        harness.Media.ChunkSize = 4096;
        harness.Media.ChunkWritten = _ => cancellation.Cancel();
        string destination = harness.LocalPath("save.bin");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Remote.DownloadFileAsync(
                "Run 42/save.bin",
                destination,
                cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(harness.TemporaryFiles());
        harness.AssertEveryClientReleased();
    }

    [Fact]
    public async Task DownloadComposition_IssuesNoForbiddenDriveOperation()
    {
        using var harness = new Harness();
        harness.AddRemoteFile("Run 42/save.bin", [1, 2, 3]);

        await harness.Remote.DownloadFileAsync(
            "Run 42/save.bin",
            harness.LocalPath("save.bin"));

        Assert.Equal(0, harness.ObjectClients.CreateFolderCalls);
        Assert.Empty(harness.ObjectClients.CreatedFolders);
        Assert.All(harness.ObjectClients.ListRequests, request =>
        {
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
            Assert.Contains("trashed = false", request.Query, StringComparison.Ordinal);
        });
        Assert.Equal(
            ["UploadAsync"],
            typeof(IGoogleDriveMediaUploadClient)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(method => method.Name)
                .ToArray());
    }

    [Fact]
    public async Task DownloadAndUpload_ShareOneRemoteBoundaryWithoutInterfering()
    {
        using var harness = new Harness();
        harness.AddRemoteFile("Run 42/save.bin", [1, 2, 3]);

        long bytes = await harness.Remote.DownloadFileAsync(
            "Run 42/save.bin",
            harness.LocalPath("save.bin"));

        Assert.Equal(3, bytes);
        Assert.False(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        Assert.DoesNotContain(
            typeof(GoogleDriveRemoteFileSystem).Assembly.GetTypes(),
            type => type.Name == "GoogleDriveSyncProvider");
        Assert.DoesNotContain(
            typeof(SyncProviderFactory).GetMethods(),
            method => method.Name.Contains("Google", StringComparison.Ordinal));
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _localRoot = Path.Combine(
            Path.GetTempPath(),
            $"gamesaves-s16-{Guid.NewGuid():N}");

        public Harness()
        {
            Directory.CreateDirectory(_localRoot);
            Drive = new OfflineDriveStore(RootId);
            ObjectClients = new OfflineDriveObjectClientFactory(
                Drive,
                new GoogleDriveQueryBuilder());
            Media = new OfflineDriveMediaDownloadClientFactory(Drive);
            Sessions = new OfflineSessionFactory();

            var repository = new InMemorySyncRemoteProfileRepository();
            repository.Create(Profile());

            var services = new ServiceCollection();
            services.AddGameSavesInfrastructure();
            services.RemoveAll<ISyncRemoteProfileRepository>();
            services.RemoveAll<IGoogleDriveAuthorizedSessionFactory>();
            services.RemoveAll<IGoogleDriveObjectClientFactory>();
            services.RemoveAll<IGoogleDriveMediaDownloadClientFactory>();
            services.RemoveAll<IGoogleDriveRemoteValidationService>();
            services.AddSingleton<ISyncRemoteProfileRepository>(repository);
            services.AddSingleton<IGoogleDriveAuthorizedSessionFactory>(Sessions);
            services.AddSingleton<IGoogleDriveObjectClientFactory>(ObjectClients);
            services.AddSingleton<IGoogleDriveMediaDownloadClientFactory>(Media);
            services.AddSingleton<IGoogleDriveRemoteValidationService>(
                new AlwaysValidValidationService());

            _provider = services.BuildServiceProvider();
            Remote = _provider
                .GetRequiredService<IGoogleDriveRemoteFileSystemFactory>()
                .Create(ProfileId);
        }

        public OfflineDriveStore Drive { get; }

        public OfflineDriveObjectClientFactory ObjectClients { get; }

        public OfflineDriveMediaDownloadClientFactory Media { get; }

        public OfflineSessionFactory Sessions { get; }

        public IRemoteFileSystem Remote { get; }

        public string LocalPath(params string[] segments) =>
            Path.Combine([_localRoot, .. segments]);

        public string[] TemporaryFiles() =>
            Directory.GetFiles(
                _localRoot,
                $"*{GoogleDriveLocalDownloadDestination.TemporarySuffix}",
                SearchOption.AllDirectories);

        public void AddRemoteFile(string relativePath, byte[] content) =>
            AddRemote(relativePath, "application/octet-stream", content);

        public void AddRemoteObject(string relativePath, string mimeType) =>
            AddRemote(relativePath, mimeType, []);

        public void AssertEveryClientReleased()
        {
            Assert.All(
                Sessions.Credentials,
                credential => Assert.True(credential.IsDisposed));
            Assert.Equal(Media.CreatedClients, Media.DisposedClients);
            Assert.True(ObjectClients.DisposedClients > 0);
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(_localRoot))
                Directory.Delete(_localRoot, recursive: true);
        }

        private void AddRemote(string relativePath, string mimeType, byte[] content)
        {
            string[] segments = relativePath.Split('/');
            string parentId = RootId;
            foreach (string segment in segments[..^1])
            {
                IReadOnlyList<OfflineDriveObject> existing =
                    Drive.FindChildren(parentId, segment);
                parentId = existing.Count == 1
                    ? existing[0].Metadata.Id
                    : Drive.AddGeneratedFolder(segment, parentId).Metadata.Id;
            }

            Drive.AddGeneratedFile(segments[^1], parentId, content, mimeType);
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
                CreatedUtc: DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                UpdatedUtc: DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                LastUsedUtc: null,
                LastSuccessfulConnectionUtc: null,
                RemoteFolderId: RootId);
    }

    private sealed class OfflineSessionFactory : IGoogleDriveAuthorizedSessionFactory
    {
        public List<GoogleAuthorizedCredential> Credentials { get; } = [];

        public Task<GoogleDriveAuthorizedSession> RestoreAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ProfileId, profile.Id);
            cancellationToken.ThrowIfCancellationRequested();
            GoogleAuthorizedCredential credential = Credential();
            Credentials.Add(credential);
            return Task.FromResult(new GoogleDriveAuthorizedSession(
                credential,
                new GoogleDriveAccountInfo("Offline Test", null)));
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
            return new GoogleAuthorizedCredential(new UserCredential(
                flow,
                ProfileId.ToString("D"),
                new TokenResponse
                {
                    AccessToken = "offline-test-access-token",
                    RefreshToken = "offline-test-refresh-token"
                }));
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
