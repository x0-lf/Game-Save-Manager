using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using System.Reflection;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// Milestone T Task 7. Both wrappers are handed the same remote semantics, so
/// any difference in a plan, a result, or the bytes left on disk comes from the
/// wrapper itself rather than from the remote backend. Drive-specific backend
/// equivalence is Milestone S Task 15's subject, not this one.
/// </summary>
public sealed class GoogleDriveSyncProviderParityTests
{
    private const string LocalOnlyRun = "2026-08-17_09-00-00_manual";
    private const string RemoteOnlyRun = "2026-08-17_10-00-00_manual";
    private const string SharedRun = "2026-08-17_11-00-00_manual";

    [Fact]
    public async Task Preview_MatchesLocalFolderItemForItem()
    {
        using var driveSide = new Workspace();
        using var localSide = new Workspace();

        SyncPlan drive = await driveSide.Drive().CreatePreviewAsync(Options());
        SyncPlan local = await localSide.LocalFolder().CreatePreviewAsync(Options());

        Assert.Equal("Google Drive", drive.ProviderName);
        Assert.NotEqual(drive.ProviderName, local.ProviderName);

        // Parity over an empty plan would prove nothing.
        Assert.Equal(3, drive.Items.Count);
        Assert.Equal(1, drive.UploadCount);
        Assert.Equal(1, drive.DownloadCount);

        AssertSamePlan(local, drive);
    }

    [Fact]
    public async Task Execute_MatchesLocalFolderResultForResult()
    {
        using var driveSide = new Workspace();
        using var localSide = new Workspace();

        SyncResult drive = await Run(driveSide, drivePath: true);
        SyncResult local = await Run(localSide, drivePath: false);

        Assert.True(
            drive.Uploaded + drive.Downloaded > 0,
            "parity over a plan that copied nothing would prove nothing");
        Assert.Equal(local.DryRun, drive.DryRun);
        Assert.Equal(local.Uploaded, drive.Uploaded);
        Assert.Equal(local.Downloaded, drive.Downloaded);
        Assert.Equal(local.Skipped, drive.Skipped);
        Assert.Equal(local.BytesCopied, drive.BytesCopied);
        Assert.Equal(local.HasErrors, drive.HasErrors);
        Assert.Equal(
            local.Items.Select(Describe).ToArray(),
            drive.Items.Select(Describe).ToArray());
        Assert.Equal(
            local.Warnings.Select(warning => warning.Code).ToArray(),
            drive.Warnings.Select(warning => warning.Code).ToArray());
    }

    [Fact]
    public async Task Execute_LeavesTheSameBytesOnBothSides()
    {
        using var driveSide = new Workspace();
        using var localSide = new Workspace();

        await Run(driveSide, drivePath: true);
        await Run(localSide, drivePath: false);

        Assert.Equal(localSide.RemoteTree(), driveSide.RemoteTree());
        Assert.Equal(localSide.LocalTree(), driveSide.LocalTree());
    }

    [Fact]
    public async Task Execute_RewritesTheDownloadedManifestIdentically()
    {
        using var driveSide = new Workspace();
        using var localSide = new Workspace();

        await Run(driveSide, drivePath: true);
        await Run(localSide, drivePath: false);

        TransferBackupManifest drive = driveSide.DownloadedManifest();
        TransferBackupManifest local = localSide.DownloadedManifest();

        Assert.Equal(local.SchemaVersion, drive.SchemaVersion);
        Assert.Equal(local.Game, drive.Game);
        Assert.Equal(local.FileCount, drive.FileCount);
        Assert.Equal(local.TotalBytes, drive.TotalBytes);
        Assert.Equal(
            local.Items.Select(item => item.BackupFile).ToArray(),
            drive.Items.Select(item => item.BackupFile).ToArray());
    }

    [Fact]
    public async Task SyncLog_IsAppendedAndReadIdentically()
    {
        using var driveSide = new Workspace();
        using var localSide = new Workspace();

        await Run(driveSide, drivePath: true);
        await Run(localSide, drivePath: false);

        SyncLogEntry drive = Assert.Single(
            await driveSide.Drive().GetSyncLogAsync());
        SyncLogEntry local = Assert.Single(
            await localSide.LocalFolder().GetSyncLogAsync());

        Assert.Equal(local.DeviceName, drive.DeviceName);
        Assert.Equal(local.Uploaded, drive.Uploaded);
        Assert.Equal(local.Downloaded, drive.Downloaded);
        Assert.Equal(local.Conflicts, drive.Conflicts);
        Assert.Equal(local.BytesCopied, drive.BytesCopied);
        Assert.Equal(local.UploadedRuns, drive.UploadedRuns);
        Assert.Equal(local.DownloadedRuns, drive.DownloadedRuns);
    }

    [Fact]
    public void BothWrappers_ExposeTheSameOperationSurface()
    {
        Assert.Equal(
            OperationNames(typeof(LocalFolderSyncProvider)),
            OperationNames(typeof(GoogleDriveSyncProvider)));
    }

    private static string[] OperationNames(Type wrapper) =>
        wrapper.GetMethods(BindingFlags.Instance | BindingFlags.DeclaredOnly |
                           BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static async Task<SyncResult> Run(Workspace workspace, bool drivePath)
    {
        ISyncProvider provider = drivePath
            ? workspace.Drive()
            : workspace.LocalFolder();

        SyncPlan plan = await provider.CreatePreviewAsync(Options());
        return await provider.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true });
    }

    private static SyncOptions Options() => new();

    private static void AssertSamePlan(SyncPlan expected, SyncPlan actual)
    {
        Assert.Equal(expected.CanExecute, actual.CanExecute);
        Assert.Equal(expected.UploadCount, actual.UploadCount);
        Assert.Equal(expected.DownloadCount, actual.DownloadCount);
        Assert.Equal(expected.InSyncCount, actual.InSyncCount);
        Assert.Equal(expected.ConflictCount, actual.ConflictCount);
        Assert.Equal(expected.BytesToUpload, actual.BytesToUpload);
        Assert.Equal(expected.BytesToDownload, actual.BytesToDownload);
        Assert.Equal(
            expected.ProviderValidationSucceeded,
            actual.ProviderValidationSucceeded);
        Assert.Equal(
            expected.Warnings.Select(warning => warning.Code).ToArray(),
            actual.Warnings.Select(warning => warning.Code).ToArray());
        Assert.Equal(
            expected.Items.Select(Describe).ToArray(),
            actual.Items.Select(Describe).ToArray());
    }

    private static string Describe(SyncItem item) =>
        string.Join('|',
            item.RunName,
            item.Action,
            item.ExistsLocally,
            item.ExistsRemotely,
            item.GameName,
            item.FileCount,
            item.TotalBytes,
            item.StatusText);

    private static string Describe(SyncItemResult result) =>
        $"{Describe(result.Item)}|{result.Status}|{result.Bytes}|{result.Error}";

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

    /// <summary>
    /// One local backup base plus one remote root holding the same three runs:
    /// local only, remote only, and present on both sides.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private readonly TemporaryDirectory _root = new();

        public Workspace()
        {
            Directory.CreateDirectory(LocalBase);
            Directory.CreateDirectory(RemoteRoot);

            WriteRun(LocalBase, LocalOnlyRun, "Local Only Game");
            WriteRun(RemoteRoot, RemoteOnlyRun, "Remote Only Game");
            WriteRun(LocalBase, SharedRun, "Shared Game");
            WriteRun(RemoteRoot, SharedRun, "Shared Game");
        }

        public string LocalBase => Path.Combine(_root.Path, "backups");

        public string RemoteRoot => Path.Combine(_root.Path, "remote");

        public ISyncProvider Drive() =>
            new GoogleDriveSyncProvider(
                new LocalFolderRemoteFileSystem(RemoteRoot, LocalBase),
                new WorkspaceHistoryService(LocalBase),
                new RecordingHistoryRepository());

        public ISyncProvider LocalFolder() =>
            new LocalFolderSyncProvider(
                RemoteRoot,
                new WorkspaceHistoryService(LocalBase),
                new RecordingHistoryRepository());

        public string[] RemoteTree() => Tree(RemoteRoot);

        public string[] LocalTree() => Tree(LocalBase);

        public TransferBackupManifest DownloadedManifest() =>
            JsonSerializer.Deserialize<TransferBackupManifest>(
                File.ReadAllText(Path.Combine(
                    LocalBase, RemoteOnlyRun, "manifest.json")))!;

        public void Dispose() => _root.Dispose();

        private string[] Tree(string root) =>
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                // The sync log records a timestamp, so only its presence is
                // comparable between two runs.
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

        private static void WriteRun(string basePath, string runName, string game)
        {
            string runRoot = Path.Combine(basePath, runName);
            Directory.CreateDirectory(Path.Combine(runRoot, "files"));
            File.WriteAllText(
                Path.Combine(runRoot, "files", "save.dat"),
                $"payload for {runName}");
            File.WriteAllText(
                Path.Combine(runRoot, "manifest.json"),
                JsonSerializer.Serialize(Manifest(game)));
        }
    }

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
}
