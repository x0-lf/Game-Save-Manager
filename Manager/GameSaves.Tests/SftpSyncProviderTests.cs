using GameSaves.Core.Platform;
using GameSaves.Core.Save;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// Verifies the SFTP injectable remote boundary seam (MAINT-001).
/// Tests deterministic upload, download, conflict, cancellation, disposal,
/// and factory construction without requiring a live SSH server.
/// </summary>
public sealed class SftpSyncProviderTests
{
    private const string DefaultSftpRoot = "sftp://backup-user@sftp.example.invalid:2222/gamesave-sync";

    [Fact]
    public void Provider_UsesSftpNameAndDisplayRoot()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        using var provider = CreateProvider(fakeRemote);

        Assert.Equal("SFTP", provider.ProviderName);
        Assert.Equal(DefaultSftpRoot, provider.RemoteRoot);
    }

    [Fact]
    public void Construction_IssuesNoRemoteCall()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        using var provider = CreateProvider(fakeRemote);

        Assert.Empty(fakeRemote.Calls);
    }

    [Fact]
    public async Task CreatePreviewAsync_RunsThroughSharedEngine()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        using var provider = CreateProvider(fakeRemote);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());

        Assert.Equal("SFTP", plan.ProviderName);
        Assert.Equal(DefaultSftpRoot, plan.RemoteRoot);
        Assert.Contains(nameof(IRemoteFileSystem.ValidateAsync), fakeRemote.Calls);
        Assert.Contains(nameof(IRemoteFileSystem.ListRunFolderNamesAsync), fakeRemote.Calls);
        Assert.Contains(nameof(IRemoteFileSystem.RootExistsAsync), fakeRemote.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_UploadRun_TransfersFilesAndRecordsHistory()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = CreateBackupHistory(temp);
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "run-sftp-upload");
        TransferBackupRunInfo localRun = TestData.CreateBackupRun(runRoot, temp.GetPath("save.dat"), "sftp save payload");

        var fakeRemote = new FakeSftpRemoteFileSystem();
        var historyRepo = new RecordingHistoryRepository();
        using var provider = CreateProvider(fakeRemote, backupHistory, historyRepo);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions { Upload = true });
        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal(SyncItemAction.UploadToRemote, item.Action);
        Assert.Equal("run-sftp-upload", item.RunName);

        SyncResult result = await provider.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true
            });

        Assert.Equal(1, result.Uploaded);
        Assert.Contains("run-sftp-upload/files/payload.sav", fakeRemote.UploadedFiles);
        Assert.EndsWith("/manifest.json", fakeRemote.UploadedFiles[^1], StringComparison.OrdinalIgnoreCase);
        Assert.True(fakeRemote.TextFiles.ContainsKey(".gamesave-sync/sync-log.json"));

        TransferRunRecord record = Assert.Single(historyRepo.Records);
        Assert.Equal(TransferRunKind.Sync, record.Kind);
        Assert.Equal(DefaultSftpRoot, record.TargetAccountId);
    }

    [Fact]
    public async Task ExecuteAsync_DownloadRun_TransfersRemoteFilesToLocalHistory()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = CreateBackupHistory(temp);

        var fakeRemote = new FakeSftpRemoteFileSystem();
        string remoteRunName = "run-sftp-remote";
        fakeRemote.RunFolderNamesList.Add(remoteRunName);

        byte[] payloadBytes = Encoding.UTF8.GetBytes("remote sftp content");
        string payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes));

        var remoteManifest = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Remote SFTP Game",
            SteamAppId: "440",
            SourceAccountId: "remote-src",
            TargetAccountId: "remote-tgt",
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedUtc: DateTimeOffset.UtcNow.AddMinutes(-4),
            FileCount: 1,
            TotalBytes: payloadBytes.LongLength,
            Items:
            [
                new TransferOverwriteBackupItem(
                    OriginalFile: temp.GetPath("game.sav"),
                    BackupFile: Path.Combine(temp.GetPath("backup"), remoteRunName, "files", "game.sav"),
                    Bytes: payloadBytes.LongLength,
                    Sha256: payloadHash,
                    BackedUpUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
                    RelativePath: "files/game.sav")
            ]);

        fakeRemote.TextFiles[$"{remoteRunName}/manifest.json"] = JsonSerializer.Serialize(remoteManifest);
        fakeRemote.BinaryFiles[$"{remoteRunName}/files/game.sav"] = payloadBytes;

        var historyRepo = new RecordingHistoryRepository();
        using var provider = CreateProvider(fakeRemote, backupHistory, historyRepo);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions { Download = true });
        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal(SyncItemAction.DownloadToLocal, item.Action);
        Assert.Equal(remoteRunName, item.RunName);

        SyncResult result = await provider.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Download = true
            });

        Assert.Equal(1, result.Downloaded);
        string downloadedRunPath = Path.Combine(backupHistory.GetBackupBasePath(), remoteRunName);
        Assert.True(Directory.Exists(downloadedRunPath));
        Assert.True(File.Exists(Path.Combine(downloadedRunPath, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(downloadedRunPath, "files", "game.sav")));
        Assert.Equal("remote sftp content", File.ReadAllText(Path.Combine(downloadedRunPath, "files", "game.sav")));
    }

    [Fact]
    public async Task ExecuteAsync_ArchiveSync_UploadsContainerAndSidecar()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = CreateBackupHistory(temp);
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "run-sftp-zip");
        TestData.CreateBackupRun(runRoot, temp.GetPath("save.sav"), "sftp archive payload");

        var fakeRemote = new FakeSftpRemoteFileSystem();
        using var provider = CreateProvider(fakeRemote, backupHistory);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions
        {
            Upload = true,
            ArchiveSync = true,
            ArchiveFormat = BackupContainerFormat.Zip
        });

        Assert.Equal(SyncItemAction.UploadToRemote, Assert.Single(plan.Items).Action);

        SyncResult result = await provider.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true,
                ArchiveSync = true,
                ArchiveFormat = BackupContainerFormat.Zip
            });

        Assert.Equal(1, result.Uploaded);
        Assert.Contains("run-sftp-zip.zip", fakeRemote.UploadedFiles);
        Assert.Contains("run-sftp-zip.zip.manifest.json", fakeRemote.UploadedFiles);
        Assert.True(fakeRemote.TextFiles.ContainsKey("run-sftp-zip.zip.manifest.json"));
    }

    [Fact]
    public async Task Conflict_WhenLocalAndRemoteDiffer_DetectsConflictWithoutOverwriting()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = CreateBackupHistory(temp);
        string sharedRunName = "shared-run";
        string localRunRoot = Path.Combine(backupHistory.GetBackupBasePath(), sharedRunName);
        TestData.CreateBackupRun(localRunRoot, temp.GetPath("file.sav"), "local content");

        var fakeRemote = new FakeSftpRemoteFileSystem();
        fakeRemote.RunFolderNamesList.Add(sharedRunName);

        // Remote has different manifest / different game name
        var conflictingManifest = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Different Conflicting Game",
            SteamAppId: "9999",
            SourceAccountId: "other-src",
            TargetAccountId: "other-tgt",
            StartedUtc: DateTimeOffset.UtcNow.AddHours(-1),
            CompletedUtc: DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(10),
            FileCount: 1,
            TotalBytes: 42,
            Items: []);

        fakeRemote.TextFiles[$"{sharedRunName}/manifest.json"] = JsonSerializer.Serialize(conflictingManifest);

        using var provider = CreateProvider(fakeRemote, backupHistory);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions { Upload = true, Download = true });
        SyncItem item = Assert.Single(plan.Items);

        // Item marked as conflict, neither uploaded nor downloaded
        Assert.Equal(SyncItemAction.Conflict, item.Action);
        Assert.Equal(1, plan.ConflictCount);

        SyncResult result = await provider.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true,
                Download = true
            });

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(0, result.Downloaded);
    }

    [Fact]
    public async Task Cancellation_PropagatesImmediatelyWithoutSideEffects()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        using var provider = CreateProvider(fakeRemote);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CreatePreviewAsync(new SyncOptions(), cts.Token));

        var plan = new SyncPlan(
            ProviderName: "SFTP",
            RemoteRoot: DefaultSftpRoot,
            Items: [],
            Warnings: [],
            CanExecute: true,
            UploadCount: 0,
            DownloadCount: 0,
            InSyncCount: 0,
            ConflictCount: 0,
            BytesToUpload: 0,
            BytesToDownload: 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ExecuteAsync(plan, new SyncOptions(), cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetSyncLogAsync(cts.Token));
    }

    [Fact]
    public void Dispose_WhenOwningFileSystem_DisposesUnderlyingFileSystem()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        var provider = CreateProvider(fakeRemote);

        Assert.False(fakeRemote.IsDisposed);
        provider.Dispose();
        Assert.True(fakeRemote.IsDisposed);
        Assert.Equal(1, fakeRemote.DisposeCount);
    }

    [Fact]
    public async Task DisposedProvider_ThrowsObjectDisposedException()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        var provider = CreateProvider(fakeRemote);
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.CreatePreviewAsync(new SyncOptions()));

        var emptyPlan = new SyncPlan(
            ProviderName: "SFTP",
            RemoteRoot: DefaultSftpRoot,
            Items: [],
            Warnings: [],
            CanExecute: true,
            UploadCount: 0,
            DownloadCount: 0,
            InSyncCount: 0,
            ConflictCount: 0,
            BytesToUpload: 0,
            BytesToDownload: 0);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.ExecuteAsync(emptyPlan, new SyncOptions()));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.GetSyncLogAsync());
    }

    [Fact]
    public void DisposedProvider_DisposeIsIdempotent()
    {
        var fakeRemote = new FakeSftpRemoteFileSystem();
        var provider = CreateProvider(fakeRemote);

        provider.Dispose();
        provider.Dispose();

        Assert.True(fakeRemote.IsDisposed);
        Assert.Equal(1, fakeRemote.DisposeCount);
    }

    [Fact]
    public void SyncProviderFactory_CreateSftpProvider_DefaultCreatesRealProviderWithoutConnecting()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = CreateBackupHistory(temp);
        var historyRepo = new RecordingHistoryRepository();

        var factory = new SyncProviderFactory(
            backupHistory,
            historyRepo,
            new TestDatabasePathProvider(temp.GetPath("app.db")),
            new GoogleDriveSyncProviderFactory(
                new InMemorySyncRemoteProfileRepository(),
                new RecordingRemoteFileSystemFactory(),
                backupHistory,
                historyRepo),
            new UnusedOneDriveSyncProviderFactory());

        using ISyncProvider provider = factory.CreateSftpProvider(SftpSettings());

        Assert.IsType<EngineSyncProvider>(provider);
        Assert.Equal("SFTP", provider.ProviderName);
        Assert.Equal(DefaultSftpRoot, provider.RemoteRoot);
    }

    [Fact]
    public void SyncProviderFactory_ForgetSftpHostKey_Succeeds()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = CreateBackupHistory(temp);
        var historyRepo = new RecordingHistoryRepository();

        var factory = new SyncProviderFactory(
            backupHistory,
            historyRepo,
            new TestDatabasePathProvider(temp.GetPath("app.db")),
            new GoogleDriveSyncProviderFactory(
                new InMemorySyncRemoteProfileRepository(),
                new RecordingRemoteFileSystemFactory(),
                backupHistory,
                historyRepo),
            new UnusedOneDriveSyncProviderFactory());

        // Must succeed without throwing
        factory.ForgetSftpHostKey("sftp.example.invalid", 2222);
    }

    // -------------------------------------------------------------------------
    // Test Helpers & Test Double
    // -------------------------------------------------------------------------

    // The same construction SyncProviderFactory.CreateSftpProvider performs,
    // with the fake in place of the real SftpRemoteFileSystem.
    private static EngineSyncProvider CreateProvider(
        FakeSftpRemoteFileSystem remote,
        IBackupHistoryService? backupHistory = null,
        ITransferHistoryRepository? historyRepository = null)
    {
        return new EngineSyncProvider(
            "SFTP",
            SftpSettings().DisplayRoot,
            remote,
            backupHistory ?? new EmptyBackupHistoryService(),
            historyRepository ?? new RecordingHistoryRepository());
    }

    private static BackupHistoryService CreateBackupHistory(TemporaryDirectory temp)
    {
        return new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
    }

    private static SftpConnectionSettings SftpSettings() =>
        new(
            Host: "sftp.example.invalid",
            Port: 2222,
            Username: "backup-user",
            AuthMethod: SftpAuthMethod.Password,
            Password: "secret-password",
            PrivateKeyPath: null,
            PrivateKeyPassphrase: null,
            RemotePath: "gamesave-sync",
            TrustNewHostKey: false);

    private sealed class FakeSftpRemoteFileSystem : IRemoteFileSystem, IDisposable
    {
        public List<string> Calls { get; } = new();
        public Dictionary<string, string> TextFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BinaryFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UploadedFiles { get; } = new();
        public List<string> DownloadedFiles { get; } = new();
        public List<string> RunFolderNamesList { get; } = new();
        public List<string> ArchiveNamesList { get; } = new();

        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }

        public string DisplayRoot { get; set; } = DefaultSftpRoot;
        public bool SupportsArchiveContainers { get; set; } = true;
        public TransferPreviewWarning? ValidationWarning { get; set; }
        public bool RootExists { get; set; } = true;

        public string GetDisplayPath(string relativePath) => $"{DisplayRoot.TrimEnd('/')}/{relativePath}";

        public Task<TransferPreviewWarning?> ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ValidateAsync));
            return Task.FromResult(ValidationWarning);
        }

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(RootExistsAsync));
            return Task.FromResult(RootExists);
        }

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ListRunFolderNamesAsync));
            return Task.FromResult<IReadOnlyList<string>>(RunFolderNamesList);
        }

        public Task<IReadOnlyList<string>> ListRunArchiveNamesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ListRunArchiveNamesAsync));
            return Task.FromResult<IReadOnlyList<string>>(ArchiveNamesList);
        }

        public Task<bool> FolderExistsAsync(string relativeFolder, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(FolderExistsAsync));
            string prefix = relativeFolder.TrimEnd('/') + "/";
            bool exists = RunFolderNamesList.Contains(relativeFolder) ||
                          TextFiles.Keys.Concat(BinaryFiles.Keys).Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(exists);
        }

        public Task<bool> FileExistsAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(FileExistsAsync));
            return Task.FromResult(TextFiles.ContainsKey(relativePath) || BinaryFiles.ContainsKey(relativePath));
        }

        public Task<string?> ReadTextFileAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ReadTextFileAsync));
            TextFiles.TryGetValue(relativePath, out string? content);
            return Task.FromResult(content);
        }

        public Task CreateTextFileIfMissingAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(CreateTextFileIfMissingAsync));
            if (!TextFiles.TryAdd(relativePath, content))
                throw new IOException($"Remote file '{relativePath}' already exists.");

            UploadedFiles.Add(relativePath);
            return Task.CompletedTask;
        }

        public Task<string?> ReadProviderMetadataAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ReadProviderMetadataAsync));
            TextFiles.TryGetValue(relativePath, out string? content);
            return Task.FromResult(content);
        }

        public Task ReplaceProviderMetadataAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ReplaceProviderMetadataAsync));
            TextFiles[relativePath] = content;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string relativeFolder, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(ListFilesAsync));
            string prefix = relativeFolder.TrimEnd('/') + "/";
            IReadOnlyList<string> files = TextFiles.Keys.Concat(BinaryFiles.Keys)
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[prefix.Length..])
                .ToList();
            return Task.FromResult(files);
        }

        public async Task<long> UploadFileAsync(string localFilePath, string relativeRemotePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(UploadFileAsync));
            if (TextFiles.ContainsKey(relativeRemotePath) || BinaryFiles.ContainsKey(relativeRemotePath))
                throw new IOException($"Remote file '{relativeRemotePath}' already exists.");

            byte[] bytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
            BinaryFiles.Add(relativeRemotePath, bytes);
            UploadedFiles.Add(relativeRemotePath);
            return bytes.LongLength;
        }

        public async Task<long> DownloadFileAsync(string relativeRemotePath, string localFilePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(nameof(DownloadFileAsync));

            byte[] bytes;
            if (BinaryFiles.TryGetValue(relativeRemotePath, out byte[]? binBytes))
            {
                bytes = binBytes;
            }
            else if (TextFiles.TryGetValue(relativeRemotePath, out string? text))
            {
                bytes = Encoding.UTF8.GetBytes(text);
            }
            else
            {
                throw new FileNotFoundException($"Remote file '{relativeRemotePath}' not found.");
            }

            DownloadedFiles.Add(relativeRemotePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            await File.WriteAllBytesAsync(localFilePath, bytes, cancellationToken);
            return bytes.LongLength;
        }

        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
        }
    }
}
