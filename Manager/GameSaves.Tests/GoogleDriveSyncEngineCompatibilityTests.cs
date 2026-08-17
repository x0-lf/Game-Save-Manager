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
        Assert.True(rootRequest.SupportsAllDrives);
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

    [Fact]
    public async Task Upload_CreatesEveryPayloadBeforeTheRootManifest()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        LocalRun run = CreateLocalRun(temp, "Run 42");
        var engine = Engine(fixture.Remote, run.History);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal(SyncItemAction.UploadToRemote, item.Action);

        SyncResult result = await engine.ExecuteAsync(plan, ExecuteOptions());

        SyncItemResult uploaded = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Uploaded, uploaded.Status);
        Assert.Equal(run.TotalBytes, uploaded.Bytes);
        string[] created = fixture.MediaUploads.Calls
            .Select(call => call.FileName)
            .ToArray();
        Assert.Equal(3, created.Length);
        Assert.Equal("manifest.json", created[^1]);
        Assert.Equal(
            ["data.bin", "slot1.sav"],
            created[..^1].Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("Run 42/manifest.json", LastUploadedRelativePath(fixture));

        string runFolderId = fixture.Drive.GetRequiredFolderId(
            "Run 42",
            Fixture.RootId);
        string savesFolderId = fixture.Drive.GetRequiredFolderId(
            "saves",
            runFolderId);
        Assert.Equal(
            [
                new FolderCreateCall(Fixture.RootId, "Run 42"),
                new FolderCreateCall(runFolderId, "saves"),
                new FolderCreateCall(savesFolderId, "profile")
            ],
            fixture.ObjectClients.CreatedFolders
                .Where(call => call.Name != ".gamesave-sync")
                .ToArray());
        Assert.Equal(
            [1, 2, 3],
            fixture.Drive.GetRequiredFileBytes(
                "slot1.sav",
                fixture.Drive.GetRequiredFolderId("profile", savesFolderId)));
        Assert.Contains(
            "Run 42",
            await fixture.Remote.ListRunFolderNamesAsync());
        Assert.Empty(fixture.ReplacementApi.Calls);
        Assert.DoesNotContain(
            fixture.Remote.Calls,
            call => call.Name == nameof(IRemoteFileSystem.DownloadFileAsync));
    }

    [Fact]
    public async Task PayloadFailure_LeavesAnIncompleteRunWithoutAManifest()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        LocalRun run = CreateLocalRun(temp, "Run 43", singlePayload: true);
        fixture.MediaUploads.FailureFor = name => name == "data.bin"
            ? new IOException(
                "The synthetic provider rejected C:\\private\\Run 43\\data.bin.")
            : null;
        var engine = Engine(fixture.Remote, run.History);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        SyncResult result = await engine.ExecuteAsync(plan, ExecuteOptions());

        SyncItemResult failed = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Failed, failed.Status);
        Assert.Equal(0, failed.Bytes);
        Assert.Empty(fixture.MediaUploads.Calls);
        Assert.DoesNotContain(
            fixture.Remote.Calls,
            call => call.Path == "Run 43/manifest.json");
        await AssertIncompleteRunPreserved(fixture, "Run 43");
        Assert.DoesNotContain(
            "private",
            failed.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "data.bin",
            failed.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationBeforeTheManifest_LeavesAnIncompleteRun()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        LocalRun run = CreateLocalRun(temp, "Run 44", singlePayload: true);
        fixture.MediaUploads.BeforeCreate = name =>
        {
            if (name == "data.bin")
                cancellation.Cancel();
        };
        var engine = Engine(fixture.Remote, run.History);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ExecuteAsync(plan, ExecuteOptions(), cancellation.Token));

        Assert.Empty(fixture.MediaUploads.Calls);
        await AssertIncompleteRunPreserved(fixture, "Run 44");
    }

    [Fact]
    public async Task ManifestFailure_KeepsPayloadsAndNeverRepairsTheRun()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        LocalRun run = CreateLocalRun(temp, "Run 45", singlePayload: true);
        fixture.MediaUploads.FailureFor = name => name == "manifest.json"
            ? new IOException("The synthetic provider rejected the manifest.")
            : null;
        var engine = Engine(fixture.Remote, run.History);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        SyncResult result = await engine.ExecuteAsync(plan, ExecuteOptions());

        SyncItemResult failed = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Failed, failed.Status);
        MediaUploadCall payload = Assert.Single(fixture.MediaUploads.Calls);
        Assert.Equal("data.bin", payload.FileName);
        Assert.Equal(
            [4, 5],
            fixture.Drive.GetRequiredFileBytes("data.bin", payload.ParentId));
        await AssertIncompleteRunPreserved(fixture, "Run 45");
    }

    private static async Task AssertIncompleteRunPreserved(
        Fixture fixture,
        string runName)
    {
        string runFolderId = fixture.Drive.GetRequiredFolderId(
            runName,
            Fixture.RootId);

        Assert.Empty(fixture.Drive.FindChildren(runFolderId, "manifest.json"));
        Assert.DoesNotContain(
            runName,
            await fixture.Remote.ListRunFolderNamesAsync());
        Assert.Empty(fixture.ReplacementApi.Calls);
        Assert.DoesNotContain(
            fixture.MediaUploads.Calls,
            call => call.FileName == "manifest.json");
        Assert.NotEmpty(fixture.Drive.ObjectIds);
    }

    private static string LastUploadedRelativePath(Fixture fixture) =>
        fixture.Remote.Calls
            .Where(call => call.Name == nameof(IRemoteFileSystem.UploadFileAsync))
            .Select(call => call.Path!)
            .Last();

    private static SyncOptions ExecuteOptions() =>
        new()
        {
            DryRun = false,
            ConfirmExecution = true
        };

    private static LocalRun CreateLocalRun(
        TemporaryDirectory temp,
        string runName,
        bool singlePayload = false)
    {
        string runRoot = temp.GetPath(runName);
        Directory.CreateDirectory(runRoot);
        string manifestPath = Path.Combine(runRoot, "manifest.json");
        TransferBackupManifest manifest = Manifest($"{runName} Game");
        string manifestJson = JsonSerializer.Serialize(manifest);
        File.WriteAllText(manifestPath, manifestJson);
        File.WriteAllBytes(Path.Combine(runRoot, "data.bin"), [4, 5]);
        long totalBytes = 2 + Encoding.UTF8.GetByteCount(manifestJson);

        if (!singlePayload)
        {
            string nested = Path.Combine(runRoot, "saves", "profile");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "slot1.sav"), [1, 2, 3]);
            totalBytes += 3;
        }

        return new LocalRun(
            new StaticBackupHistoryService(
                temp.Path,
                new TransferBackupRunInfo(runRoot, manifestPath, manifest)),
            totalBytes);
    }

    private sealed record LocalRun(
        StaticBackupHistoryService History,
        long TotalBytes);

    [Fact]
    public async Task Download_RewritesTheManifestExactlyLikeLocalFolderDoes()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        RemoteRun run = fixture.AddRemoteRun("Run 50", ("files/saves/slot1.sav", [1, 2, 3]));
        string driveBase = temp.GetPath("drive-base");
        string localBase = temp.GetPath("local-base");
        Directory.CreateDirectory(driveBase);
        Directory.CreateDirectory(localBase);

        var driveEngine = Engine(
            fixture.Remote,
            new StaticBackupHistoryService(driveBase));
        SyncPlan drivePlan = await driveEngine.CreatePreviewAsync(new SyncOptions());
        SyncResult driveResult = await driveEngine.ExecuteAsync(
            drivePlan,
            ExecuteOptions());

        LocalFolderRemoteFileSystem local = run.WriteToLocalFolder(
            temp.GetPath("local-remote"),
            localBase);
        var localEngine = Engine(
            local,
            new StaticBackupHistoryService(localBase));
        SyncPlan localPlan = await localEngine.CreatePreviewAsync(new SyncOptions());
        SyncResult localResult = await localEngine.ExecuteAsync(
            localPlan,
            ExecuteOptions());

        Assert.Equal(
            SyncItemStatus.Downloaded,
            Assert.Single(driveResult.Items).Status);
        Assert.Equal(
            SyncItemStatus.Downloaded,
            Assert.Single(localResult.Items).Status);

        TransferBackupManifest driveManifest = ReadManifest(driveBase, "Run 50");
        TransferBackupManifest localManifest = ReadManifest(localBase, "Run 50");

        // Everything except the machine-specific root must match exactly.
        Assert.Equal(
            localManifest with { Items = [] },
            driveManifest with { Items = [] });
        Assert.Equal(
            localManifest.Items
                .Select(item => item with
                {
                    BackupFile = Relative(item.BackupFile, localBase)
                })
                .ToArray(),
            driveManifest.Items
                .Select(item => item with
                {
                    BackupFile = Relative(item.BackupFile, driveBase)
                })
                .ToArray());
        Assert.Equal(
            [1, 2, 3],
            File.ReadAllBytes(
                Path.Combine(driveBase, "Run 50", "files", "saves", "slot1.sav")));
    }

    [Fact]
    public async Task DownloadedRun_IsDiscoverableAndPassesSha256Verification()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        fixture.AddRemoteRun("Run 51", ("files/saves/slot1.sav", [4, 5, 6, 7]));
        string backupBase = temp.GetPath("backups");
        Directory.CreateDirectory(backupBase);
        var engine = Engine(
            fixture.Remote,
            new StaticBackupHistoryService(backupBase));

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        await engine.ExecuteAsync(plan, ExecuteOptions());

        string runRoot = Path.Combine(backupBase, "Run 51");
        TransferBackupManifest manifest =
            JsonSerializer.Deserialize<TransferBackupManifest>(
                File.ReadAllText(Path.Combine(runRoot, "manifest.json")))!;
        TransferOverwriteBackupItem item = Assert.Single(manifest.Items);

        Assert.True(File.Exists(item.BackupFile));
        Assert.Equal(
            item.Sha256,
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(item.BackupFile))),
            ignoreCase: true);
    }

    [Fact]
    public async Task TamperedRemoteContent_DownloadsButFailsSha256Verification()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        RemoteRun run = fixture.AddRemoteRun("Run 52", ("files/saves/slot1.sav", [8, 8, 8, 8]));
        run.ReplaceRemoteContent("files/saves/slot1.sav", [9, 9, 9, 9]);
        string backupBase = temp.GetPath("backups");
        Directory.CreateDirectory(backupBase);
        var engine = Engine(
            fixture.Remote,
            new StaticBackupHistoryService(backupBase));

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        SyncResult result = await engine.ExecuteAsync(plan, ExecuteOptions());

        Assert.Equal(
            SyncItemStatus.Downloaded,
            Assert.Single(result.Items).Status);

        string runRoot = Path.Combine(backupBase, "Run 52");
        TransferBackupManifest manifest =
            JsonSerializer.Deserialize<TransferBackupManifest>(
                File.ReadAllText(Path.Combine(runRoot, "manifest.json")))!;
        TransferOverwriteBackupItem item = Assert.Single(manifest.Items);

        // SHA-256 in the manifest stays the identity: the tampered payload no
        // longer matches it, so restore refuses this file.
        Assert.NotEqual(
            item.Sha256,
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(item.BackupFile))),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InterruptedDownload_LeavesNoRunPresentedAsComplete()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        fixture.AddRemoteRun(
            "Run 53",
            ("files/saves/slot1.sav", [1, 2, 3]),
            ("files/saves/slot2.sav", [4, 5, 6]));
        string backupBase = temp.GetPath("backups");
        Directory.CreateDirectory(backupBase);
        string existing = Path.Combine(backupBase, "keep.txt");
        File.WriteAllText(existing, "keep");
        fixture.MediaDownloads.FailureFor = fileId =>
            fixture.Drive.GetRequired(fileId).Metadata.Name == "manifest.json"
                ? new IOException("The synthetic provider interrupted the manifest.")
                : null;
        var engine = Engine(
            fixture.Remote,
            new StaticBackupHistoryService(backupBase));

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        SyncResult result = await engine.ExecuteAsync(plan, ExecuteOptions());

        Assert.Equal(SyncItemStatus.Failed, Assert.Single(result.Items).Status);
        string runRoot = Path.Combine(backupBase, "Run 53");
        Assert.False(
            File.Exists(Path.Combine(runRoot, "manifest.json")),
            "An interrupted run must not carry a manifest.");
        Assert.Empty(Directory.GetFiles(
            backupBase,
            $"*{GoogleDriveLocalDownloadDestination.TemporarySuffix}",
            SearchOption.AllDirectories));
        Assert.Equal("keep", File.ReadAllText(existing));

        var history = new GameSaves.Infrastructure.Transfers.BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        Assert.DoesNotContain(
            await history.GetRunsAsync(),
            candidate => candidate.BackupRootPath == runRoot);
    }

    [Fact]
    public async Task RemoteRunWithoutAManifest_IsNeverOfferedForDownload()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new Fixture();
        fixture.Drive.AddFolder("headless-run-id", "Run 54");
        fixture.Drive.AddFile(
            "headless-file-id",
            "slot1.sav",
            "headless-run-id",
            [1],
            "application/octet-stream");
        var engine = Engine(
            fixture.Remote,
            new StaticBackupHistoryService(temp.Path));

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

        Assert.DoesNotContain(plan.Items, item => item.RunName == "Run 54");
        Assert.Empty(fixture.MediaDownloads.Calls);
    }

    private sealed record RemoteRun(
        OfflineDriveStore Drive,
        string RunName,
        string RunFolderId,
        TransferBackupManifest Manifest,
        IReadOnlyList<(string RelativePath, byte[] Content)> Files)
    {
        public void ReplaceRemoteContent(string relativePath, byte[] content)
        {
            string name = relativePath.Split('/')[^1];
            string parentId = RunFolderId;
            foreach (string segment in relativePath.Split('/')[..^1])
                parentId = Drive.GetRequiredFolderId(segment, parentId);

            OfflineDriveObject file = Assert.Single(
                Drive.FindChildren(parentId, name));
            Drive.ReplaceContent(file.Metadata.Id, content);
        }

        public LocalFolderRemoteFileSystem WriteToLocalFolder(
            string remoteRoot,
            string backupBase)
        {
            string runRoot = Path.Combine(remoteRoot, RunName);
            Directory.CreateDirectory(runRoot);
            foreach ((string relativePath, byte[] content) in Files)
            {
                string target = Path.Combine(
                    runRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, content);
            }

            File.WriteAllText(
                Path.Combine(runRoot, "manifest.json"),
                JsonSerializer.Serialize(Manifest));
            return new LocalFolderRemoteFileSystem(remoteRoot, backupBase);
        }
    }

    private static TransferBackupManifest ReadManifest(string basePath, string runName) =>
        JsonSerializer.Deserialize<TransferBackupManifest>(
            File.ReadAllText(Path.Combine(basePath, runName, "manifest.json")))!;

    private static string Relative(string path, string basePath) =>
        Path.GetRelativePath(basePath, path);

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
            ObjectClients = new OfflineDriveObjectClientFactory(Drive, QueryBuilder);
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
                ContentApi,
                cache);
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
            var childEnumeration = new GoogleDriveFolderChildEnumerationService(
                objectApi);
            var recursiveListing = new GoogleDriveRecursiveFileListingService(
                new GoogleDriveRunFolderResolver(ContextFactory),
                new GoogleDriveOneLevelFileListingService(
                    childEnumeration,
                    cache));
            MediaUploads = new OfflineDriveMediaUploadClientFactory(Drive);
            MediaDownloads = new OfflineDriveMediaDownloadClientFactory(Drive);
            var targetGuard = new GoogleDriveCreateOnlyUploadTargetGuard(
                childEnumeration,
                new GoogleDriveObjectCreationCoordinator());
            var binaryUpload = new GoogleDriveBinaryUploadService(
                new GoogleDriveLocalUploadSourceOpener().OpenAsync,
                ContextFactory,
                new GoogleDriveUploadParentPreparationService(
                    childEnumeration,
                    targetGuard,
                    objectApi,
                    cache),
                targetGuard,
                MediaUploads,
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
                createOnly,
                recursiveListing,
                binaryUpload,
                new GoogleDriveBinaryDownloadService(
                    new GoogleDriveLocalDownloadDestinationOpener().OpenAsync,
                    ContextFactory,
                    new GoogleDriveDownloadSourceResolver(childEnumeration),
                    MediaDownloads,
                    new GoogleDriveDownloadContentStreamer()));
            Remote = new RecordingRemoteFileSystem(inner);
        }

        public OfflineDriveStore Drive { get; }

        public GoogleDriveQueryBuilder QueryBuilder { get; }

        public OfflineResolver Resolver { get; }

        public RecordingContextFactory ContextFactory { get; }

        public OfflineDriveObjectClientFactory ObjectClients { get; }

        public RecordingTextContentApi ContentApi { get; }

        public RecordingTextCreationApi CreationApi { get; }

        public RecordingTextReplacementApi ReplacementApi { get; }

        public OfflineDriveMediaUploadClientFactory MediaUploads { get; }

        public OfflineDriveMediaDownloadClientFactory MediaDownloads { get; }

        public RecordingRemoteFileSystem Remote { get; }

        private static string RunRelative(string relativePath) =>
            relativePath.Replace('/', '\\');

        public RemoteRun AddRemoteRun(
            string runName,
            params (string RelativePath, byte[] Content)[] files)
        {
            OfflineDriveObject runFolder = Drive.AddGeneratedFolder(runName, RootId);
            var items = new List<TransferOverwriteBackupItem>();

            foreach ((string relativePath, byte[] content) in files)
            {
                string parentId = runFolder.Metadata.Id;
                string[] segments = relativePath.Split('/');
                foreach (string segment in segments[..^1])
                {
                    IReadOnlyList<OfflineDriveObject> existing =
                        Drive.FindChildren(parentId, segment);
                    parentId = existing.Count == 1
                        ? existing[0].Metadata.Id
                        : Drive.AddGeneratedFolder(segment, parentId).Metadata.Id;
                }

                Drive.AddGeneratedFile(
                    segments[^1],
                    parentId,
                    content,
                    "application/octet-stream");
                items.Add(new TransferOverwriteBackupItem(
                    OriginalFile: $"C:\\original\\{segments[^1]}",
                    BackupFile: $"C:\\remote-run\\{RunRelative(relativePath)}",
                    Bytes: content.LongLength,
                    Sha256: Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(content)),
                    BackedUpUtc: ManifestTimestamp));
            }

            TransferBackupManifest manifest = Manifest($"{runName} Game") with
            {
                FileCount = items.Count,
                TotalBytes = items.Sum(item => item.Bytes),
                Items = items
            };
            Drive.AddGeneratedFile(
                "manifest.json",
                runFolder.Metadata.Id,
                System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(manifest)),
                "application/json");

            return new RemoteRun(
                Drive,
                runName,
                runFolder.Metadata.Id,
                manifest,
                files);
        }

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

}
