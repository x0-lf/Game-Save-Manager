using System.Text.Json;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Tests;

/// <summary>
/// Milestone Y Task 3. The mapping of the twenty-three acceptance items to
/// coverage found one with none at all: **restoring a run that arrived by
/// download.**
///
/// `GoogleDriveSyncEngineCompatibilityTests.DownloadedRun_IsDiscoverableAndPassesSha256Verification`
/// proves a downloaded run is discoverable and hashes correctly, and
/// `BackupRestoreArchiveTests` proves the restore service works on a locally
/// created run. Nothing joined the two, so the last link in the chain a user
/// actually cares about, getting a save back from Drive, was covered only by
/// inference.
///
/// It was marked live-only in the first draft of the Y plan. It is not: the
/// download and the restore are both hermetic, so this belongs in the suite and
/// the live session only has to confirm it against a real account.
/// </summary>
public sealed class DownloadedRunRestoreTests
{
    [Fact]
    public async Task ARunDownloadedFromTheRemote_CanBeRestoredToItsOriginalLocation()
    {
        using var workspace = new DownloadWorkspace();

        // Download through the real engine, exactly as a sync does.
        SyncResult result = await workspace.SyncAsync();

        Assert.Equal(
            SyncItemStatus.Downloaded,
            Assert.Single(result.Items).Status);

        // The downloaded run is a real backup run: the history service finds it.
        TransferBackupRunInfo run = Assert.Single(
            await new BackupHistoryService(workspace.DatabasePaths).GetRunsAsync());

        // Non-vacuity: the original file does not exist before the restore, so
        // its appearance afterwards cannot be left over from the fixture.
        Assert.False(File.Exists(workspace.OriginalFile));

        BackupRestoreResult restored = await workspace.RestoreAsync(run);

        Assert.Equal(1, restored.FilesRestored);
        Assert.True(File.Exists(workspace.OriginalFile));
        Assert.Equal(
            DownloadWorkspace.Payload,
            File.ReadAllText(workspace.OriginalFile));
    }

    [Fact]
    public async Task ADownloadedRunWhoseContentWasTampered_IsRefusedByRestore()
    {
        using var workspace = new DownloadWorkspace();

        await workspace.SyncAsync();

        TransferBackupRunInfo run = Assert.Single(
            await new BackupHistoryService(workspace.DatabasePaths).GetRunsAsync());

        // Corrupt the downloaded payload after it landed, which is what a
        // damaged disk or a careless edit looks like.
        File.WriteAllText(run.Manifest.Items[0].BackupFile, "tampered save");

        BackupRestoreResult restored = await workspace.RestoreAsync(run);

        // The SHA-256 manifest is the authoritative content identity, so a
        // downloaded run that no longer matches it is never restored.
        Assert.Equal(0, restored.FilesRestored);
        Assert.Equal(
            BackupRestoreItemStatus.SkippedHashMismatch,
            Assert.Single(restored.Items).Status);
        Assert.False(File.Exists(workspace.OriginalFile));
    }

    // ---- helpers ----

    /// <summary>
    /// A remote holding one complete run, a local backup base that starts
    /// empty, and the restore service wired the way the composition root wires
    /// it. Everything lives in one temporary directory that is deleted on
    /// dispose.
    /// </summary>
    private sealed class DownloadWorkspace : IDisposable
    {
        public const string Payload = "downloaded save data";

        private const string RunName = "2026-08-20_16-00-00_manual";

        private readonly TemporaryDirectory _root = new();

        public DownloadWorkspace()
        {
            Directory.CreateDirectory(LocalBase);
            Directory.CreateDirectory(RemoteRoot);

            // Build the run on the remote side by creating it locally, in the
            // shape the backup writer produces, then moving it across.
            string staging = Path.Combine(_root.Path, "staging", RunName);
            TestData.CreateBackupRun(staging, OriginalFile, Payload);
            Directory.Move(staging, Path.Combine(RemoteRoot, RunName));

            DatabasePaths = new TestDatabasePathProvider(
                Path.Combine(LocalBase, "app", "gamesave.db"));
        }

        public string OriginalFile => Path.Combine(_root.Path, "saves", "slot.sav");

        public TestDatabasePathProvider DatabasePaths { get; }

        private string LocalBase => Path.Combine(_root.Path, "local");

        private string RemoteRoot => Path.Combine(_root.Path, "remote");

        public Task<SyncResult> SyncAsync()
        {
            var engine = new SyncEngine(
                new LocalFolderRemoteFileSystem(RemoteRoot, BackupBase),
                "Local folder",
                RemoteRoot,
                new BackupHistoryService(DatabasePaths),
                new RecordingHistoryRepository());

            return ExecuteAsync(engine);
        }

        private string BackupBase =>
            new BackupHistoryService(DatabasePaths).GetBackupBasePath();

        private async Task<SyncResult> ExecuteAsync(SyncEngine engine)
        {
            Directory.CreateDirectory(BackupBase);

            SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

            return await engine.ExecuteAsync(
                plan,
                new SyncOptions { DryRun = false, ConfirmExecution = true });
        }

        public Task<BackupRestoreResult> RestoreAsync(TransferBackupRunInfo run) =>
            new BackupRestoreService(
                new TransferOverwriteBackupService(DatabasePaths),
                new RecordingHistoryRepository(),
                new EmptyMappingRepository(),
                new EmptySteamDiscoveryService(),
                new WindowsPlatformProvider())
            .RestoreAsync(
                run,
                new BackupRestoreOptions
                {
                    DryRun = false,
                    ConfirmExecution = true,
                    VerifyHashes = true
                });

        public void Dispose() => _root.Dispose();
    }
}
