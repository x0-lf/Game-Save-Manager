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

namespace GameSaves.Tests;

/// <summary>
/// Exercises the complete Google Drive upload composition, from Infrastructure
/// dependency injection through the remote-filesystem factory, the one-file
/// upload service, child pagination, and the media client. Every provider seam
/// is hermetic: no account, credential value, browser, or network is involved.
/// </summary>
public sealed class GoogleDriveUploadIntegrationTests
{
    private const string RootId = "integration-root-id";

    private static readonly Guid ProfileId =
        Guid.Parse("6f0e5f61-6c4a-4a1f-9a6a-9e3e2b0f77aa");

    // Characterization test for a known performance defect, measured on
    // 2026-08-18. Every uploaded file rebuilds the whole authorized session: a
    // profile read, a DPAPI decrypt, a credential restore, and an account round
    // trip, and then builds a fresh DriveService. A real run uploads roughly 325
    // files per backup folder, so a single sync pays thousands of avoidable
    // network round trips and TLS handshakes.
    //
    // This test asserts the CURRENT behaviour on purpose, so the cost is visible
    // and measured rather than argued about. When session and client reuse land,
    // this test will fail; that failure is the signal to change the assertion to
    // "fewer than fileCount" rather than to relax it.
    [Fact]
    public async Task UploadingManyFiles_CurrentlyRebuildsTheSessionForEveryFile()
    {
        const int fileCount = 40;

        using var harness = new Harness();

        for (int index = 0; index < fileCount; index++)
        {
            using var source = new TemporaryUploadFile([1, 2, 3]);
            await harness.Remote.UploadFileAsync(
                source.Path,
                $"Run 42/saves/file-{index}.sav");
        }

        // Non-vacuity: the uploads really happened.
        string runId = harness.Drive.GetRequiredFolderId("Run 42", RootId);
        string savesId = harness.Drive.GetRequiredFolderId("saves", runId);
        Assert.Equal(fileCount, harness.Drive.FindChildren(savesId).Count);

        // Measured: one full session restore per file, exactly 1:1. In
        // production each one carries an account round trip that dominates the
        // transfer of a small file.
        Assert.Equal(fileCount, harness.Sessions.Credentials.Count);
    }

    [Fact]
    public async Task Upload_TravelsTheWholeCompositionAndCreatesNestedParents()
    {
        using var harness = new Harness();
        using var source = new TemporaryUploadFile([1, 2, 3, 4, 5]);

        long bytes = await harness.Remote.UploadFileAsync(
            source.Path,
            "Run 42/saves/profile/slot1.sav");

        Assert.Equal(5, bytes);
        string runId = harness.Drive.GetRequiredFolderId("Run 42", RootId);
        string savesId = harness.Drive.GetRequiredFolderId("saves", runId);
        string profileId = harness.Drive.GetRequiredFolderId("profile", savesId);
        Assert.Equal(
            [
                new FolderCreateCall(RootId, "Run 42"),
                new FolderCreateCall(runId, "saves"),
                new FolderCreateCall(savesId, "profile")
            ],
            harness.ObjectClients.CreatedFolders);
        MediaUploadCall upload = Assert.Single(harness.Media.Calls);
        Assert.Equal("slot1.sav", upload.FileName);
        Assert.Equal(profileId, upload.ParentId);
        Assert.Equal(
            [1, 2, 3, 4, 5],
            harness.Drive.GetRequiredFileBytes("slot1.sav", profileId));
        Assert.True(harness.Cache.TryGet(
            harness.Scope,
            profileId,
            "slot1.sav",
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? cached));
        Assert.Equal(upload.FileId, cached!.Metadata.Id);
        harness.AssertEveryClientReleased();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024)]
    [InlineData((5 * 1024 * 1024) + 1)]
    public async Task Upload_PreservesExactBytesForEverySize(int length)
    {
        using var harness = new Harness();
        byte[] content = new byte[length];
        for (int index = 0; index < content.Length; index++)
            content[index] = (byte)(index % 251);
        using var source = new TemporaryUploadFile(content);

        long bytes = await harness.Remote.UploadFileAsync(
            source.Path,
            "Run 42/payload.bin");

        Assert.Equal(length, bytes);
        MediaUploadCall upload = Assert.Single(harness.Media.Calls);
        Assert.Equal(length, upload.Bytes);
        Assert.Equal(
            content,
            harness.Drive.GetRequiredFileBytes("payload.bin", upload.ParentId));
    }

    public static TheoryData<string, bool, string> TargetCollisions => new()
    {
        { "save.bin", false, "GoogleDriveUploadTargetAlreadyExists" },
        { "SAVE.BIN", false, "GoogleDriveUploadTargetCaseCollision" },
        { "save.bin", true, "GoogleDriveUploadTargetTypeCollision" },
        { "Save.Bin", true, "GoogleDriveUploadTargetTypeCollision" }
    };

    [Theory]
    [MemberData(nameof(TargetCollisions))]
    public async Task Upload_RefusesEveryWindowsEquivalentTarget(
        string existingName,
        bool existingIsFolder,
        string expectedErrorCode)
    {
        using var harness = new Harness();
        using var source = new TemporaryUploadFile([9]);
        harness.Drive.AddFolder("run-folder-id", "Run 42");
        if (existingIsFolder)
            harness.Drive.AddFolder("existing-folder-id", existingName, "run-folder-id");
        else
            harness.Drive.AddFile("existing-file-id", existingName, "run-folder-id", [7]);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Remote.UploadFileAsync(source.Path, "Run 42/save.bin"));

        Assert.Equal(expectedErrorCode, exception.Result.ErrorCode);
        Assert.Empty(harness.Media.Calls);
        if (!existingIsFolder)
        {
            Assert.Equal(
                [7],
                harness.Drive.GetRequiredFileBytes(existingName, "run-folder-id"));
        }

        Assert.Single(harness.Drive.FindChildren("run-folder-id"));
        Assert.False(harness.Cache.TryGet(
            harness.Scope,
            "run-folder-id",
            "save.bin",
            GoogleDriveObjectKind.File,
            out _));
        harness.AssertEveryClientReleased();
    }

    [Fact]
    public async Task Upload_RefusesACollisionThatOnlyAppearsOnALaterPage()
    {
        using var harness = new Harness();
        using var source = new TemporaryUploadFile([9]);
        harness.Drive.AddFolder("run-folder-id", "Run 42");
        harness.Drive.AddFile("first-id", "aaa.bin", "run-folder-id", [1]);
        harness.Drive.AddFile("second-id", "bbb.bin", "run-folder-id", [2]);
        harness.Drive.AddFile("third-id", "ccc.bin", "run-folder-id", [3]);
        harness.Drive.AddFile("colliding-id", "save.BIN", "run-folder-id", [4]);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Remote.UploadFileAsync(source.Path, "Run 42/save.bin"));

        Assert.Equal(
            "GoogleDriveUploadTargetCaseCollision",
            exception.Result.ErrorCode);
        Assert.Empty(harness.Media.Calls);
        Assert.True(
            harness.ObjectClients.ListRequests.Count(request =>
                request.PageToken is not null) >= 3,
            "The guard must consume every child page before refusing.");
    }

    [Fact]
    public async Task Upload_CancellationLeavesNoRemoteFileAndNoCacheEntry()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        using var source = new TemporaryUploadFile([1, 2, 3]);
        harness.Media.BeforeCreate = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Remote.UploadFileAsync(
                source.Path,
                "Run 42/save.bin",
                cancellation.Token));

        Assert.Empty(harness.Media.Calls);
        string runId = harness.Drive.GetRequiredFolderId("Run 42", RootId);
        Assert.Empty(harness.Drive.FindChildren(runId, "save.bin"));
        Assert.False(harness.Cache.TryGet(
            harness.Scope,
            runId,
            "save.bin",
            GoogleDriveObjectKind.File,
            out _));
        harness.AssertEveryClientReleased();
    }

    [Fact]
    public async Task Upload_ProviderFailureIsSanitizedAndWritesNoCacheEntry()
    {
        using var harness = new Harness();
        using var source = new TemporaryUploadFile([1, 2, 3]);
        harness.Media.FailureFor = _ => new IOException(
            "The synthetic provider rejected C:\\private\\save.bin.");

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Remote.UploadFileAsync(source.Path, "Run 42/save.bin"));

        Assert.Equal(
            GoogleDriveBinaryUploadErrorCodes.Failed,
            exception.Result.ErrorCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "private",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        string runId = harness.Drive.GetRequiredFolderId("Run 42", RootId);
        Assert.False(harness.Cache.TryGet(
            harness.Scope,
            runId,
            "save.bin",
            GoogleDriveObjectKind.File,
            out _));
        harness.AssertEveryClientReleased();
    }

    [Fact]
    public async Task Upload_InvalidResponseNeverBecomesSuccessOrCacheState()
    {
        using var harness = new Harness();
        using var source = new TemporaryUploadFile([1, 2, 3]);
        harness.Media.ResponseFor = name => new GoogleDriveMediaUploadMetadata(
            "unexpected-id",
            name,
            GoogleDriveMediaUploadClient.OpaqueMediaType,
            trashed: false,
            parentIds: ["some-other-parent-id"],
            driveId: null,
            size: 3);

        GoogleDriveUploadResponseException exception =
            await Assert.ThrowsAsync<GoogleDriveUploadResponseException>(() =>
                harness.Remote.UploadFileAsync(source.Path, "Run 42/save.bin"));

        Assert.Equal(
            GoogleDriveUploadResponseErrorCodes.ParentMismatch,
            exception.SafeErrorCode);
        string runId = harness.Drive.GetRequiredFolderId("Run 42", RootId);
        Assert.False(harness.Cache.TryGet(
            harness.Scope,
            runId,
            "save.bin",
            GoogleDriveObjectKind.File,
            out _));
        harness.AssertEveryClientReleased();
    }

    [Fact]
    public async Task Upload_ReusesExistingParentsWithoutCreatingDuplicates()
    {
        using var harness = new Harness();
        using var first = new TemporaryUploadFile([1]);
        using var second = new TemporaryUploadFile([2, 2]);

        await harness.Remote.UploadFileAsync(first.Path, "Run 42/saves/one.bin");
        await harness.Remote.UploadFileAsync(second.Path, "Run 42/saves/two.bin");

        Assert.Equal(
            ["Run 42", "saves"],
            harness.ObjectClients.CreatedFolders
                .Select(call => call.Name)
                .ToArray());
        string savesId = harness.Drive.GetRequiredFolderId(
            "saves",
            harness.Drive.GetRequiredFolderId("Run 42", RootId));
        Assert.Equal([1], harness.Drive.GetRequiredFileBytes("one.bin", savesId));
        Assert.Equal([2, 2], harness.Drive.GetRequiredFileBytes("two.bin", savesId));
        Assert.Equal(2, harness.Media.Calls.Count);
    }

    [Fact]
    public async Task UploadComposition_IssuesNoForbiddenDriveOperation()
    {
        using var harness = new Harness();
        using var source = new TemporaryUploadFile([1, 2, 3]);

        await harness.Remote.UploadFileAsync(source.Path, "Run 42/save.bin");

        string[] objectClientOperations = typeof(IGoogleDriveObjectClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] mediaClientOperations = typeof(IGoogleDriveMediaUploadClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["CreateFolderAsync", "GetAsync", "ListAsync"],
            objectClientOperations);
        Assert.Equal(["UploadAsync"], mediaClientOperations);
        Assert.True(typeof(IGoogleDriveObjectClient).IsAssignableTo(
            typeof(IDisposable)));
        Assert.True(typeof(IGoogleDriveMediaUploadClient).IsAssignableTo(
            typeof(IDisposable)));
        Assert.All(harness.ObjectClients.ListRequests, request =>
        {
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
            Assert.Contains("trashed = false", request.Query, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Composition_KeepsTheWrapperInternalAfterActivation()
    {
        using var harness = new Harness();

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
        // Milestone T added the wrapper itself, so the surviving invariant is
        // that it stays internal and unactivated, not that it is absent.
        Type wrapper = Assert.Single(
            typeof(GoogleDriveRemoteFileSystem).Assembly.GetTypes(),
            type => type.Name == "GoogleDriveSyncProvider");
        Assert.False(wrapper.IsPublic);
        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        Assert.Empty(harness.Media.Calls);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness()
        {
            Drive = new OfflineDriveStore(RootId);
            ObjectClients = new OfflineDriveObjectClientFactory(
                Drive,
                new GoogleDriveQueryBuilder());
            Media = new OfflineDriveMediaUploadClientFactory(Drive);
            Sessions = new OfflineSessionFactory();

            var repository = new InMemorySyncRemoteProfileRepository();
            repository.Create(Profile());

            var services = new ServiceCollection();
            services.AddGameSavesInfrastructure();
            services.RemoveAll<ISyncRemoteProfileRepository>();
            services.RemoveAll<IGoogleDriveAuthorizedSessionFactory>();
            services.RemoveAll<IGoogleDriveObjectClientFactory>();
            services.RemoveAll<IGoogleDriveMediaUploadClientFactory>();
            services.RemoveAll<IGoogleDriveRemoteValidationService>();
            services.AddSingleton<ISyncRemoteProfileRepository>(repository);
            services.AddSingleton<IGoogleDriveAuthorizedSessionFactory>(Sessions);
            services.AddSingleton<IGoogleDriveObjectClientFactory>(ObjectClients);
            services.AddSingleton<IGoogleDriveMediaUploadClientFactory>(Media);
            services.AddSingleton<IGoogleDriveRemoteValidationService>(
                new AlwaysValidValidationService());

            _provider = services.BuildServiceProvider();
            Remote = _provider
                .GetRequiredService<IGoogleDriveRemoteFileSystemFactory>()
                .Create(ProfileId);
            Cache = _provider.GetRequiredService<IGoogleDriveObjectIdCache>();
            Scope = new GoogleDriveObjectCacheScope(ProfileId, RootId);
        }

        public OfflineDriveStore Drive { get; }

        public OfflineDriveObjectClientFactory ObjectClients { get; }

        public OfflineDriveMediaUploadClientFactory Media { get; }

        public OfflineSessionFactory Sessions { get; }

        public IRemoteFileSystem Remote { get; }

        public IGoogleDriveObjectIdCache Cache { get; }

        public GoogleDriveObjectCacheScope Scope { get; }

        public void AssertEveryClientReleased()
        {
            Assert.All(
                Sessions.Credentials,
                credential => Assert.True(credential.IsDisposed));
            Assert.Equal(Media.CreatedClients, Media.DisposedClients);
            Assert.True(ObjectClients.DisposedClients > 0);
        }

        public void Dispose() => _provider.Dispose();
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

    private sealed class TemporaryUploadFile : IDisposable
    {
        public TemporaryUploadFile(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gamesaves-r19-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
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
