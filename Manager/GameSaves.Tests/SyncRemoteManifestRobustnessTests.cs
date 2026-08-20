using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// A remote manifest is untrusted JSON. System.Text.Json does not enforce the
/// non-nullable reference members of <see cref="TransferBackupManifest"/>, so an
/// interrupted upload can leave a file that parses into a manifest whose members
/// are null. Comparing one used to throw a NullReferenceException, which
/// surfaced as "Check failed: Object reference not set to an instance of an
/// object" on every later preview and could not be cleared by reconnecting,
/// because the damaged object was in the remote rather than in the credentials.
/// </summary>
public sealed class SyncRemoteManifestRobustnessTests
{
    // A run folder that exists locally under the same name is what forces the
    // engine into manifest comparison, which is where the crash happened.
    private const string RunName = "run-2026-08-18-120000";

    public static TheoryData<string, string> PartialManifests() => new()
    {
        { "no items array", """{"SchemaVersion":1,"Kind":"backup","Game":"G","SteamAppId":"1"}""" },
        { "null items", """{"SchemaVersion":1,"Kind":"backup","Game":"G","SteamAppId":"1","Items":null}""" },
        { "null kind", """{"SchemaVersion":1,"Kind":null,"Game":"G","SteamAppId":"1","Items":[]}""" },
        { "null app id", """{"SchemaVersion":1,"Kind":"backup","Game":"G","SteamAppId":null,"Items":[]}""" },
        { "null game", """{"SchemaVersion":1,"Kind":"backup","Game":null,"SteamAppId":"1","Items":[]}""" },
        {
            "item with null hash",
            """{"SchemaVersion":1,"Kind":"backup","Game":"G","SteamAppId":"1","Items":[{"OriginalFile":"a.sav","Sha256":null}]}"""
        },
        {
            "item with null original file",
            """{"SchemaVersion":1,"Kind":"backup","Game":"G","SteamAppId":"1","Items":[{"OriginalFile":null,"Sha256":"AA"}]}"""
        },
        { "empty object", "{}" }
    };

    [Theory]
    [MemberData(nameof(PartialManifests))]
    public async Task APartialRemoteManifest_DoesNotCrashThePreview(
        string scenario,
        string manifestJson)
    {
        Assert.False(string.IsNullOrWhiteSpace(scenario));

        using var backups = new TempDir();
        CreateLocalRun(backups.Path);

        var remote = new PartialManifestRemoteFileSystem(RunName, manifestJson);
        var engine = new SyncEngine(
            remote,
            "Local folder",
            backups.Path,
            new ManifestBackupHistoryService(backups.Path, RunName),
            new RecordingHistoryRepository());

        // Before the fix this threw NullReferenceException, which the view model
        // reported as "Check failed: Object reference not set to an instance of
        // an object".
        SyncPlan plan = await engine.CreatePreviewAsync(
            new SyncOptions { Upload = true, Download = true });

        Assert.NotNull(plan);

        // The damaged remote folder is never treated as a backup run, and the
        // warning names it so the operator knows which folder to remove. An
        // unnamed warning is one the operator cannot act on.
        TransferPreviewWarning warning = Assert.Single(
            plan.Warnings,
            candidate => candidate.Code == "RemoteManifestUnreadable");
        Assert.Contains(RunName, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWellFormedRemoteManifest_IsStillCompared()
    {
        // Non-vacuity: the guard must reject only damaged manifests. A complete
        // one still reaches comparison and reports the run as in sync.
        using var backups = new TempDir();
        CreateLocalRun(backups.Path);

        var remote = new PartialManifestRemoteFileSystem(RunName, GoodManifestJson());
        var engine = new SyncEngine(
            remote,
            "Local folder",
            backups.Path,
            new ManifestBackupHistoryService(backups.Path, RunName),
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(
            new SyncOptions { Upload = true, Download = true });

        Assert.DoesNotContain(
            plan.Warnings,
            candidate => candidate.Code == "RemoteManifestUnreadable");
        Assert.Contains(
            plan.Items,
            item => item.RunName == RunName && item.Action == SyncItemAction.InSync);
    }

    private static string GoodManifestJson() =>
        """
        {"SchemaVersion":1,"Kind":"backup","Game":"G","SteamAppId":"1",
         "SourceAccountId":"s","TargetAccountId":"t",
         "StartedUtc":"2026-08-18T12:00:00+00:00","CompletedUtc":"2026-08-18T12:00:01+00:00",
         "FileCount":0,"TotalBytes":0,"Items":[]}
        """;

    private static void CreateLocalRun(string backupBase)
    {
        string runFolder = Path.Combine(backupBase, RunName);
        Directory.CreateDirectory(runFolder);
        File.WriteAllText(
            Path.Combine(runFolder, "manifest.json"),
            GoodManifestJson());
    }

    private static TransferBackupManifest LocalManifest() =>
        new(1, "backup", "G", "1", "s", "t",
            DateTimeOffset.Parse("2026-08-18T12:00:00+00:00"),
            DateTimeOffset.Parse("2026-08-18T12:00:01+00:00"),
            0, 0, []);

    private sealed class PartialManifestRemoteFileSystem : IRemoteFileSystem
    {
        private readonly string _runName;
        private readonly string _manifestJson;

        public PartialManifestRemoteFileSystem(string runName, string manifestJson)
        {
            _runName = runName;
            _manifestJson = manifestJson;
        }

        public string DisplayRoot => "remote";

        public string GetDisplayPath(string relativePath) => $"remote/{relativePath}";

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferPreviewWarning?>(null);

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([_runName]);

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(
                relativePath.EndsWith("manifest.json", StringComparison.Ordinal)
                    ? _manifestJson
                    : null);

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["manifest.json"]);

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default) => Task.FromResult(0L);
    }

    private sealed class ManifestBackupHistoryService : IBackupHistoryService
    {
        private readonly string _basePath;
        private readonly string _runName;

        public ManifestBackupHistoryService(string basePath, string runName)
        {
            _basePath = basePath;
            _runName = runName;
        }

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(
            [
                new TransferBackupRunInfo(
                    Path.Combine(_basePath, _runName),
                    Path.Combine(_basePath, _runName, "manifest.json"),
                    LocalManifest())
            ]);

        public string GetBackupBasePath() => _basePath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gamesaves-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
