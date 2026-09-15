using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class VerificationStrengthTests
{
    [Fact]
    public void VerifyPayloadIntegrity_FolderBackup_MatchesHashes_ReturnsPayloadVerified()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run_folder");
        string original = temp.GetPath("save.dat");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "verified payload content");

        var reader = new BackupMetadataReader();
        VerificationStrengthResult result = reader.VerifyPayloadIntegrity(run);

        Assert.Equal(VerificationStrength.PayloadVerified, result.Strength);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(1, result.VerifiedFiles);
        Assert.Equal(1, result.TotalFiles);
        Assert.NotNull(result.FileResults);
        Assert.True(result.FileResults[run.Manifest.Items[0].OriginalFile]);
    }

    [Fact]
    public void VerifyPayloadIntegrity_FolderBackup_TamperedFile_ReturnsPayloadMismatch()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run_tampered");
        string original = temp.GetPath("save.dat");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "legitimate content");

        // Tamper with payload byte
        File.WriteAllText(run.Manifest.Items[0].BackupFile, "tampered malicious content");

        var reader = new BackupMetadataReader();
        VerificationStrengthResult result = reader.VerifyPayloadIntegrity(run);

        Assert.Equal(VerificationStrength.PayloadMismatch, result.Strength);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("hash mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Equal(1, result.TotalFiles);
        Assert.NotNull(result.FileResults);
        Assert.False(result.FileResults[run.Manifest.Items[0].OriginalFile]);
    }

    [Fact]
    public async Task VerifyPayloadIntegrity_ZipBackup_MatchesHashes_ReturnsPayloadVerified()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run_src");
        string original = temp.GetPath("save.dat");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "archive payload bytes");

        string exportDir = temp.GetPath("exports");
        Directory.CreateDirectory(exportDir);
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(history);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(
            run,
            exportDir,
            BackupContainerFormat.Zip,
            BackupCompressionPreset.Optimal);

        Assert.True(export.Success);
        Assert.NotNull(export.ArchivePath);

        var reader = new BackupMetadataReader();
        bool built = reader.TryBuildRunInfo(export.ArchivePath, out TransferBackupRunInfo? zipRun, out string? error);
        Assert.True(built, error);
        Assert.NotNull(zipRun);
        Assert.Equal(BackupContainerFormat.Zip, zipRun.ContainerFormat);

        VerificationStrengthResult result = reader.VerifyPayloadIntegrity(zipRun);
        Assert.Equal(VerificationStrength.PayloadVerified, result.Strength);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(1, result.VerifiedFiles);
        Assert.Equal(1, result.TotalFiles);
    }

    [Fact]
    public async Task VerifyPayloadIntegrity_ZipBackup_TamperedEntry_ReturnsPayloadMismatch()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run_src_tamper");
        string original = temp.GetPath("save.dat");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "original zip payload");

        string exportDir = temp.GetPath("exports");
        Directory.CreateDirectory(exportDir);
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(history);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(
            run,
            exportDir,
            BackupContainerFormat.Zip,
            BackupCompressionPreset.Fast);

        Assert.True(export.Success);
        Assert.NotNull(export.ArchivePath);

        // Tamper with the zip archive entry content
        using (var zipArchive = ZipFile.Open(export.ArchivePath, ZipArchiveMode.Update))
        {
            string payloadRelativePath = run.Manifest.Items[0].GetRelativePayloadPath();
            var entry = zipArchive.GetEntry(payloadRelativePath);
            Assert.NotNull(entry);
            entry.Delete();

            var newEntry = zipArchive.CreateEntry(payloadRelativePath);
            using var stream = newEntry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write("tampered entry bytes");
        }

        var reader = new BackupMetadataReader();
        bool built = reader.TryBuildRunInfo(export.ArchivePath, out TransferBackupRunInfo? zipRun, out string? error);
        Assert.True(built, error);
        Assert.NotNull(zipRun);

        VerificationStrengthResult result = reader.VerifyPayloadIntegrity(zipRun);
        Assert.Equal(VerificationStrength.PayloadMismatch, result.Strength);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("hash mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyPayloadIntegrity_SevenZipBackup_MatchesHashes_ReturnsPayloadVerified()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run_7z_src");
        string original = temp.GetPath("save.dat");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "7z verified payload content");

        string exportDir = temp.GetPath("exports_7z");
        Directory.CreateDirectory(exportDir);
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(history);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(
            run,
            exportDir,
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Fast);

        Assert.True(export.Success);
        Assert.NotNull(export.ArchivePath);

        var reader = new BackupMetadataReader();
        bool built = reader.TryBuildRunInfo(export.ArchivePath, out TransferBackupRunInfo? sevenZipRun, out string? error);
        Assert.True(built, error);
        Assert.NotNull(sevenZipRun);
        Assert.Equal(BackupContainerFormat.SevenZip, sevenZipRun.ContainerFormat);

        VerificationStrengthResult result = reader.VerifyPayloadIntegrity(sevenZipRun);
        Assert.Equal(VerificationStrength.PayloadVerified, result.Strength);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(1, result.VerifiedFiles);
    }

    [Fact]
    public void Discovery_DetectsSidecarVsEmbeddedManifest()
    {
        using var temp = new TemporaryDirectory();
        string archivePath = temp.GetPath("test_sidecar.zip");
        string sidecarPath = temp.GetPath("test_sidecar.zip.manifest.json");

        // Create dummy zip archive
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("payload.sav");
            using var stream = entry.Open();
            stream.WriteByte(0x42);
        }

        // Create external sidecar manifest
        var item = new TransferOverwriteBackupItem(
            OriginalFile: "save.sav",
            BackupFile: "payload.sav",
            Bytes: 1,
            Sha256: "0000000000000000000000000000000000000000000000000000000000000000",
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "payload.sav");

        var manifest = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Test",
            SteamAppId: "100",
            SourceAccountId: "src",
            TargetAccountId: "tgt",
            StartedUtc: DateTimeOffset.UtcNow,
            CompletedUtc: DateTimeOffset.UtcNow,
            FileCount: 1,
            TotalBytes: 1,
            Items: [item]);

        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(manifest));

        var reader = new BackupMetadataReader();

        // Reading via sidecar
        bool sidecarRead = reader.TryReadManifest(archivePath, out TransferBackupManifest? readManifest, out string? readError, out bool isSidecar);
        Assert.True(sidecarRead, readError);
        Assert.True(isSidecar);
        Assert.NotNull(readManifest);

        // Discovery builds run with SidecarManifestMatch
        bool built = reader.TryBuildRunInfo(archivePath, out TransferBackupRunInfo? runInfo, out string? buildError);
        Assert.True(built, buildError);
        Assert.NotNull(runInfo);
        Assert.Equal(VerificationStrength.SidecarManifestMatch, runInfo.Verification);
        Assert.True(runInfo.IsSidecarMatch);
        Assert.False(runInfo.IsPayloadVerified);
    }

    [Fact]
    public void SyncItemResultRowViewModel_DisclosesManifestMatchWithoutClaimingPayloadVerification()
    {
        var syncItem = new SyncItem(
            RunName: "run_001",
            Action: SyncItemAction.InSync,
            ExistsLocally: true,
            ExistsRemotely: true,
            LocalPath: "loc",
            RemotePath: "rem",
            GameName: "Game",
            FileCount: 1,
            TotalBytes: 1024,
            StatusText: "In sync (manifest match)",
            Verification: VerificationStrength.ManifestMatch);

        var result = new SyncItemResult(
            Item: syncItem,
            Bytes: 1024,
            Status: SyncItemStatus.Uploaded,
            Error: null,
            Verification: VerificationStrength.ManifestMatch);

        var rowVm = new SyncItemResultRowViewModel(result, "Remote");

        Assert.Equal("Manifest match", rowVm.StateText);
        Assert.Equal("✓", rowVm.StateGlyph);
        Assert.Equal(SyncStateSeverity.Success, rowVm.Severity);
        Assert.Contains("payload bytes were not re-read", rowVm.StateDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Payload verified", rowVm.StateText);
    }

    [Fact]
    public void SyncItemResultRowViewModel_DisclosesSidecarManifestMatchWithWarningSeverity()
    {
        var syncItem = new SyncItem(
            RunName: "run_sidecar",
            Action: SyncItemAction.InSync,
            ExistsLocally: true,
            ExistsRemotely: true,
            LocalPath: "loc",
            RemotePath: "rem",
            GameName: "Game",
            FileCount: 1,
            TotalBytes: 2048,
            StatusText: "In sync (sidecar manifest match)",
            Verification: VerificationStrength.SidecarManifestMatch);

        var result = new SyncItemResult(
            Item: syncItem,
            Bytes: 2048,
            Status: SyncItemStatus.Uploaded,
            Error: null,
            Verification: VerificationStrength.SidecarManifestMatch);

        var rowVm = new SyncItemResultRowViewModel(result, "Remote");

        Assert.Equal("Sidecar manifest match", rowVm.StateText);
        Assert.Equal("⚠", rowVm.StateGlyph);
        Assert.Equal(SyncStateSeverity.Warning, rowVm.Severity);
        Assert.Contains("sidecar descriptor", rowVm.StateDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransferRunRowViewModel_ExposesTruthfulVerificationLevel()
    {
        var normalRun = new TransferRunInfo(
            Id: 1,
            Kind: TransferRunKind.TransferCopy,
            GameName: "Elden Ring",
            SteamAppId: "1245620",
            SourceAccountId: "111",
            TargetAccountId: "222",
            DryRun: false,
            OverwriteEnabled: false,
            BackupEnabled: true,
            FilesConsidered: 5,
            FilesCopied: 5,
            FilesSkipped: 0,
            FilesFailed: 0,
            BytesCopied: 1048576,
            FilesBackedUp: 0,
            BackupRootPath: null,
            BlockedReason: null,
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedUtc: DateTimeOffset.UtcNow);

        var normalVm = new TransferRunRowViewModel(normalRun);
        Assert.Equal("Copied (unverified)", normalVm.VerificationDisplay);
        Assert.Equal("✓", normalVm.VerificationGlyph);
        Assert.Contains("Cryptographic payload bytes were not re-read", normalVm.VerificationTooltip);

        var dryRun = normalRun with { DryRun = true };
        var dryVm = new TransferRunRowViewModel(dryRun);
        Assert.Equal("Dry run", dryVm.VerificationDisplay);
        Assert.Equal("◷", dryVm.VerificationGlyph);

        var blockedRun = normalRun with { BlockedReason = "Target folder locked by another process" };
        var blockedVm = new TransferRunRowViewModel(blockedRun);
        Assert.Equal("Blocked", blockedVm.VerificationDisplay);
        Assert.Equal("⚠", blockedVm.VerificationGlyph);

        var failedRun = normalRun with { FilesFailed = 2 };
        var failedVm = new TransferRunRowViewModel(failedRun);
        Assert.Equal("Transfer failed", failedVm.VerificationDisplay);
        Assert.Equal("✕", failedVm.VerificationGlyph);
    }

    [Fact]
    public async Task BackupHistoryViewModel_VerifySelectedRunCommand_ExecutesFullIntegrityCheck()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run_full_verify");
        string original = temp.GetPath("save.dat");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "integrity check content");

        var metadataReader = new BackupMetadataReader();
        var fakeHistory = new FakeSingleRunHistoryService(run, metadataReader);

        var vm = new BackupHistoryViewModel(
            fakeHistory,
            new FakeBackupRestoreService(),
            new FakeBackupCleanupService(),
            new BackupArchiveService(fakeHistory),
            new FakeFolderPickerService(),
            new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector(), SyncProviderSelectionTests.NewWorkspaceLayout()),
            SyncProviderSelectionTests.NewWorkspaceLayout());

        await vm.InitializeAsync();
        Assert.Single(vm.Runs);

        vm.SelectedRun = vm.Runs[0];
        Assert.True(vm.CanVerifySelectedRun);
        Assert.Equal(VerificationStrength.ManifestMatch, vm.SelectedRun.Verification);
        Assert.Single(vm.RunItems);
        Assert.Null(vm.RunItems[0].IsVerified);

        // Execute verification
        await vm.VerifySelectedRunCommand.ExecuteAsync(null);

        Assert.Equal(VerificationStrength.PayloadVerified, vm.SelectedRun.Verification);
        Assert.True(vm.SelectedRun.IsPayloadVerified);
        Assert.True(vm.RunItems[0].IsVerified);
        Assert.Equal("Verified", vm.RunItems[0].StatusDisplay);
        Assert.Equal("✓", vm.RunItems[0].StatusGlyph);
        Assert.Contains("Payload verified", vm.FileListStatusMessage);
    }

    private sealed class FakeSingleRunHistoryService : IBackupHistoryService
    {
        private readonly TransferBackupRunInfo _run;
        private readonly IBackupMetadataReader _reader;

        public FakeSingleRunHistoryService(TransferBackupRunInfo run, IBackupMetadataReader reader)
        {
            _run = run;
            _reader = reader;
        }

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>([_run]);

        public string GetBackupBasePath() => _run.BackupRootPath;

        public Task<VerificationStrengthResult> VerifyRunIntegrityAsync(TransferBackupRunInfo run, CancellationToken cancellationToken = default) =>
            _reader.VerifyPayloadIntegrityAsync(run, cancellationToken);
    }

    private sealed class FakeBackupRestoreService : IBackupRestoreService
    {
        public Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupRestoreResult(run, options.DryRun, 0, 0, 0, 0, [], []));

        public Task<IReadOnlyList<RestoreMappingTargetOption>> GetApprovedMappingTargetsAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RestoreMappingTargetOption>>([]);
    }

    private sealed class FakeBackupCleanupService : IBackupCleanupService
    {
        public Task<BackupCleanupResult> CleanupAsync(
            BackupCleanupOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupCleanupResult(options.DryRun, 0, 0, 0, 0, [], []));

        public Task<BackupCleanupResult> DeleteRunAsync(
            TransferBackupRunInfo run,
            bool confirmExecution,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupCleanupResult(false, 1, 1, 0, 0, [], []));
    }

    private sealed class FakeFolderPickerService : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startLocation = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeSteamProfileDetector : ISteamProfileDetector
    {
        public IReadOnlyList<SteamProfile> DetectProfiles(SteamDiscoveryResult discovery, CancellationToken cancellationToken = default) =>
            [];

        public IReadOnlyList<SteamProfile> DetectProfiles(string steamRoot, CancellationToken cancellationToken = default) =>
            [];
    }
}
