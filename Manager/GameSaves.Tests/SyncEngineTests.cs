using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class SyncEngineTests
{
    [Fact]
    public async Task Upload_UsesTheSharedEngine_UploadsManifestLast_AndRecordsHistory()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string runRoot = System.IO.Path.Combine(backupHistory.GetBackupBasePath(), "run-one");
        TestData.CreateBackupRun(runRoot, temp.GetPath("original.sav"), "sync payload");

        var remote = new RecordingRemoteFileSystem();
        var history = new RecordingHistoryRepository();
        var engine = new SyncEngine(
            remote,
            "Test remote",
            "test://remote",
            backupHistory,
            history);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());
        Assert.Equal(SyncItemAction.UploadToRemote, Assert.Single(plan.Items).Action);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true
            });

        Assert.Equal(1, result.Uploaded);
        Assert.EndsWith("/manifest.json", remote.UploadedPaths[^1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run-one/files/payload.sav", remote.UploadedPaths);
        Assert.True(remote.TextFiles.ContainsKey(".gamesave-sync/sync-log.json"));
        Assert.Equal(TransferRunKind.Sync, Assert.Single(history.Records).Kind);
    }

    [Fact]
    public async Task Preview_ReportsConflictAndIgnoresIncompleteRemoteFolders()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string runRoot = System.IO.Path.Combine(backupHistory.GetBackupBasePath(), "same-name");
        TransferBackupRunInfo localRun = TestData.CreateBackupRun(
            runRoot,
            temp.GetPath("original.sav"),
            "local payload");

        var remote = new RecordingRemoteFileSystem();
        TransferBackupManifest different = localRun.Manifest with
        {
            Items = localRun.Manifest.Items
                .Select(item => item with { Sha256 = new string('0', 64) })
                .ToList()
        };
        remote.TextFiles["same-name/manifest.json"] = JsonSerializer.Serialize(different);
        remote.BinaryFiles["incomplete/files/payload.sav"] = new byte[] { 1, 2, 3 };

        var engine = new SyncEngine(
            remote,
            "Test remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

        SyncItem conflict = Assert.Single(plan.Items);
        Assert.Equal("same-name", conflict.RunName);
        Assert.Equal(SyncItemAction.Conflict, conflict.Action);
        Assert.Equal(1, plan.ConflictCount);
        Assert.DoesNotContain(plan.Items, item => item.RunName == "incomplete");
    }

    private sealed class RecordingRemoteFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, string> TextFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BinaryFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UploadedPaths { get; } = new();

        public string DisplayRoot => "test://remote";

        public string GetDisplayPath(string relativePath) => $"{DisplayRoot}/{relativePath}";

        public Task<TransferPreviewWarning?> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferPreviewWarning?>(null);

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TextFiles.Count > 0 || BinaryFiles.Count > 0);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> names = TextFiles.Keys
                .Concat(BinaryFiles.Keys)
                .Select(path => path.Split('/')[0])
                .Where(name => !name.StartsWith(".", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult(names);
        }

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string prefix = relativeFolder.TrimEnd('/') + "/";
            bool exists = TextFiles.Keys.Concat(BinaryFiles.Keys)
                .Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(exists);
        }

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            TextFiles.TryGetValue(relativePath, out string? value);
            return Task.FromResult(value);
        }

        public Task WriteTextFileAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            TextFiles[relativePath] = content;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string prefix = relativeFolder.TrimEnd('/') + "/";
            IReadOnlyList<string> files = TextFiles.Keys.Concat(BinaryFiles.Keys)
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => path[prefix.Length..])
                .ToList();
            return Task.FromResult(files);
        }

        public async Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            if (TextFiles.ContainsKey(relativeRemotePath) || BinaryFiles.ContainsKey(relativeRemotePath))
                throw new IOException("Remote file already exists.");

            byte[] content = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
            BinaryFiles.Add(relativeRemotePath, content);
            UploadedPaths.Add(relativeRemotePath);
            return content.LongLength;
        }

        public async Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            byte[] content = BinaryFiles[relativeRemotePath];
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localFilePath)!);
            await File.WriteAllBytesAsync(localFilePath, content, cancellationToken);
            return content.LongLength;
        }
    }
}
