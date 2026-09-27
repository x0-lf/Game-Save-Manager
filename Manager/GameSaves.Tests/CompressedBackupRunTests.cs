using System.IO.Compression;
using GameSaves.App.Services;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Tests;

/// <summary>
/// BACKUP-001. New backup runs are compressed containers by default: packed
/// only after every payload is verified against the manifest, never over an
/// existing file, restored straight from the container through a verified
/// private copy, and alongside folder runs, which keep working unchanged.
/// </summary>
public sealed class CompressedBackupRunTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string BasePath => Path.Combine(_temp.Path, "app", "TransferBackups");

    // ---- writing ----

    [Theory]
    [InlineData(BackupContainerFormat.Zip, ".zip")]
    [InlineData(BackupContainerFormat.SevenZip, ".7z")]
    public async Task ACompressedRun_IsAVerifiedContainer_AndTheStagedFolderIsGone(
        BackupContainerFormat format,
        string extension)
    {
        string save = Original("slot1.sav", "slot one");
        string nested = Original(Path.Combine("profiles", "cfg.ini"), "config");

        // A copy keeps the read-only attribute; the staged copy must still go.
        File.SetAttributes(nested, FileAttributes.ReadOnly);

        ITransferOverwriteBackupSession session = Service(format).BeginSession(Context());
        string stagedRoot = session.BackupRootPath;
        TransferOverwriteBackupItem saveItem = session.BackUpFile(save);
        session.BackUpFile(nested);
        session.Complete();
        File.SetAttributes(nested, FileAttributes.Normal);

        Assert.Equal(stagedRoot + extension, session.BackupRootPath);
        Assert.True(File.Exists(session.BackupRootPath));
        Assert.False(Directory.Exists(stagedRoot));
        Assert.Empty(Directory.GetFiles(BasePath, ".export_*"));
        Assert.Equal(
            Path.Combine(session.BackupRootPath, Path.GetRelativePath(stagedRoot, saveItem.BackupFile)),
            session.LocateBackupFile(saveItem.BackupFile));

        BackupHistoryService history = History();
        TransferBackupRunInfo run = Assert.Single(await history.GetRunsAsync());
        Assert.Equal(format, run.ContainerFormat);
        Assert.Equal(TransferBackupManifest.CurrentSchemaVersion, run.Manifest.SchemaVersion);
        Assert.Equal(extension.TrimStart('.'), run.Manifest.Format);
        Assert.Equal("Optimal", run.Manifest.Compression);
        Assert.Equal(2, run.Manifest.FileCount);
        Assert.All(run.Manifest.Items, item => Assert.StartsWith(
            session.BackupRootPath + Path.DirectorySeparatorChar,
            item.BackupFile,
            StringComparison.Ordinal));

        VerificationStrengthResult verified = await history.VerifyRunIntegrityAsync(run);
        Assert.Equal(VerificationStrength.PayloadVerified, verified.Strength);
    }

    [Fact]
    public async Task TheFolderChoice_AndNoPreferenceAtAll_WriteAFolderRunAsBefore()
    {
        string save = Original("slot1.sav", "slot one");

        foreach (TransferOverwriteBackupService service in new[]
                 {
                     Service(BackupContainerFormat.Folder),
                     new TransferOverwriteBackupService(new TestDatabasePathProvider(_temp.GetPath("app", "gamesave.db")))
                 })
        {
            ITransferOverwriteBackupSession session = service.BeginSession(Context());
            session.BackUpFile(save);
            session.Complete();

            Assert.True(File.Exists(Path.Combine(session.BackupRootPath, "manifest.json")));
            Assert.False(File.Exists(session.BackupRootPath + ".zip"));
            Directory.Move(session.BackupRootPath, session.BackupRootPath + "_" + Guid.NewGuid().ToString("N"));
        }

        Assert.All(await History().GetRunsAsync(), run => Assert.Equal(BackupContainerFormat.Folder, run.ContainerFormat));
    }

    [Fact]
    public async Task AFileWithTheContainerName_IsNeverReplaced_AndTheRunStaysAFolder()
    {
        string save = Original("slot1.sav", "slot one");
        ITransferOverwriteBackupSession session = Service(BackupContainerFormat.Zip).BeginSession(Context());
        session.BackUpFile(save);
        string occupied = session.BackupRootPath + ".zip";
        File.WriteAllText(occupied, "not a backup");

        session.Complete();

        Assert.Equal("not a backup", File.ReadAllText(occupied));
        Assert.True(File.Exists(Path.Combine(session.BackupRootPath, "manifest.json")));
        Assert.Equal(session.BackupRootPath, session.LocateBackupFile(session.BackupRootPath));
        TransferBackupRunInfo run = Assert.Single(await History().GetRunsAsync());
        Assert.Equal(BackupContainerFormat.Folder, run.ContainerFormat);
        Assert.Equal("folder", run.Manifest.Format);
    }

    [Fact]
    public async Task ATransfer_ReportsWhereTheReplacedFileLivesInsideTheContainer()
    {
        string source = Original("source.sav", "new save");
        string target = Original("target.sav", "existing save");
        var service = new SaveTransferService(Service(BackupContainerFormat.Zip), new RecordingHistoryRepository());

        SaveTransferResult result = await service.ExecuteAsync(
            TestData.CreateTransferPlan(source, target),
            new SaveTransferOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                OverwriteExisting = true,
                BackupBeforeOverwrite = true
            });

        Assert.Equal("new save", File.ReadAllText(target));
        Assert.EndsWith(".zip", result.BackupRootPath, StringComparison.Ordinal);
        string backupFile = Assert.Single(result.Items).BackupFile!;
        Assert.StartsWith(result.BackupRootPath + Path.DirectorySeparatorChar, backupFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManualBackup_ReportsTheContainer_ItsManifest_AndEachCopyInsideIt()
    {
        string save = Original("slot1.sav", "slot one");
        TransferPreviewPlan transfer = TestData.CreateTransferPlan(save, _temp.GetPath("unused.sav"));
        var service = new ManualBackupService(
            new EmptySteamDiscoveryService(),
            new EmptyMappingRepository(),
            new WindowsPlatformProvider(),
            Service(BackupContainerFormat.SevenZip),
            new RecordingHistoryRepository());

        ManualBackupResult result = await service.ExecuteAsync(
            new ManualBackupPlan(
                transfer.Game,
                transfer.SourceProfile,
                BasePath,
                transfer.Items,
                Array.Empty<TransferPreviewWarning>(),
                CanExecute: true,
                TotalFiles: 1,
                TotalBytes: transfer.TotalBytes),
            new ManualBackupExecuteOptions { DryRun = false, ConfirmExecution = true });

        Assert.Equal(1, result.FilesBackedUp);
        Assert.EndsWith(".7z", result.BackupRootPath, StringComparison.Ordinal);
        Assert.Equal(result.BackupRootPath + "#manifest.json", result.ManifestPath);
        Assert.StartsWith(
            result.BackupRootPath + Path.DirectorySeparatorChar,
            Assert.Single(result.Items).TargetFile,
            StringComparison.Ordinal);
        Assert.Equal(BackupContainerFormat.SevenZip, Assert.Single(await History().GetRunsAsync()).ContainerFormat);
    }

    // ---- restoring ----

    [Theory]
    [InlineData(BackupContainerFormat.Zip)]
    [InlineData(BackupContainerFormat.SevenZip)]
    public async Task ARestoreFromAContainer_ReplacesTheFileFromAVerifiedCopy_AndCompressesItsOwnSafetyBackup(
        BackupContainerFormat format)
    {
        string save = Original("slot1.sav", "version one");
        TransferBackupRunInfo run = await CompressedRun(format, save);
        File.WriteAllText(save, "version two");

        BackupRestoreResult result = await Restore(run, format);

        Assert.Equal(1, result.FilesRestored);
        Assert.Equal("version one", File.ReadAllText(save));
        Assert.NotNull(result.BackupRootPath);
        Assert.True(File.Exists(result.BackupRootPath));
        Assert.StartsWith(
            result.BackupRootPath + Path.DirectorySeparatorChar,
            Assert.Single(result.Items).PreRestoreBackupFile,
            StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(BasePath, ".staging_*"));
        Assert.Equal(2, (await History().GetRunsAsync()).Count);
    }

    [Fact]
    public async Task ATamperedContainer_IsRefusedBeforeAnyFileIsTouched()
    {
        string save = Original("slot1.sav", "version one");
        TransferBackupRunInfo run = await CompressedRun(BackupContainerFormat.Zip, save);
        File.WriteAllText(save, "version two");

        using (ZipArchive zip = ZipFile.Open(run.BackupRootPath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry payload = zip.Entries.Single(entry => entry.FullName.StartsWith("files/", StringComparison.Ordinal));
            string name = payload.FullName;
            payload.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("forged content");
        }

        BackupRestoreResult result = await Restore(run, BackupContainerFormat.Zip);

        Assert.Equal(0, result.FilesRestored);
        Assert.Contains(result.Warnings, warning => warning.Code == "ContainerVerificationFailed");
        Assert.Equal("version two", File.ReadAllText(save));
        Assert.Empty(Directory.GetDirectories(BasePath, ".staging_*"));
    }

    [Theory]
    [InlineData(BackupContainerFormat.Zip)]
    [InlineData(BackupContainerFormat.SevenZip)]
    public async Task ATruncatedContainer_IsRefused_AndNothingIsReplaced(BackupContainerFormat format)
    {
        string save = Original("slot1.sav", string.Concat(Enumerable.Repeat("version one ", 400)));
        TransferBackupRunInfo run = await CompressedRun(format, save);
        File.WriteAllText(save, "version two");

        byte[] bytes = File.ReadAllBytes(run.BackupRootPath);
        File.WriteAllBytes(run.BackupRootPath, bytes[..(bytes.Length / 2)]);

        BackupRestoreResult result = await Restore(run, format);

        Assert.Equal(0, result.FilesRestored);
        TransferPreviewWarning refusal = Assert.Single(result.Warnings);
        Assert.StartsWith("Container", refusal.Code, StringComparison.Ordinal);
        Assert.Equal(TransferWarningSeverity.Error, refusal.Severity);
        Assert.Equal("version two", File.ReadAllText(save));
        Assert.Empty(Directory.GetDirectories(BasePath, ".staging_*"));
    }

    [Fact]
    public async Task Cancellation_StopsExtraction_AndARestoreThatWasCancelledChangesNothing()
    {
        string save = Original("slot1.sav", "version one");
        TransferBackupRunInfo run = await CompressedRun(BackupContainerFormat.Zip, save);
        File.WriteAllText(save, "version two");
        string staging = _temp.GetPath("cancelled-staging");
        Directory.CreateDirectory(staging);

        Assert.Throws<OperationCanceledException>(() => BackupArchiveService.ExtractContainer(
            run.BackupRootPath,
            BackupContainerFormat.Zip,
            staging,
            BackupArchiveSafetyBounds.Default,
            new CancellationToken(canceled: true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RestoreService(BackupContainerFormat.Zip).RestoreAsync(
            run,
            RestoreOptions(),
            new CancellationToken(canceled: true)));

        Assert.Equal("version two", File.ReadAllText(save));
        Assert.Empty(Directory.GetDirectories(BasePath, ".staging_*"));
    }

    // ---- compatibility ----

    [Fact]
    public async Task FolderRunsBesideCompressedOnes_StayListedAndRestorable_AndACompressedRunExportsAsItIs()
    {
        string folderSave = Original("folder.sav", "from a folder run");
        TestData.CreateBackupRun(Path.Combine(BasePath, "old_folder_run"), folderSave, "from a folder run");
        string zipSave = Original("zip.sav", "from a zip run");
        TransferBackupRunInfo zipRun = await CompressedRun(BackupContainerFormat.Zip, zipSave);
        File.Delete(folderSave);

        IReadOnlyList<TransferBackupRunInfo> runs = await History().GetRunsAsync();
        Assert.Equal(
            new[] { BackupContainerFormat.Folder, BackupContainerFormat.Zip },
            runs.Select(run => run.ContainerFormat).Order());

        BackupRestoreResult folderRestore = await Restore(
            runs.Single(run => run.ContainerFormat == BackupContainerFormat.Folder),
            BackupContainerFormat.Zip);
        Assert.Equal(1, folderRestore.FilesRestored);
        Assert.Equal("from a folder run", File.ReadAllText(folderSave));

        BackupArchiveExportResult export = await new BackupArchiveService(History())
            .ExportRunAsync(zipRun, _temp.GetPath("exported"), BackupContainerFormat.SevenZip);
        Assert.True(export.Success, export.Message);
        Assert.Equal(Path.GetFileName(zipRun.BackupRootPath), Path.GetFileName(export.ArchivePath));
        Assert.Equal(File.ReadAllBytes(zipRun.BackupRootPath), File.ReadAllBytes(export.ArchivePath!));
        Assert.Contains("copied as it is", export.Message);
    }

    // ---- the setting ----

    [Fact]
    public void TheSetting_DefaultsToZip_RoundTrips_AndReadsAnUnknownValueAsZip()
    {
        string path = _temp.GetPath("ui-settings.json");
        var store = new UiSettingsStore(path);

        Assert.Equal(AppUiSettings.BackupFormatZip, store.Load().NewBackupFormat);

        File.WriteAllText(path, "{\"SchemaVersion\":9,\"ThemeChoice\":\"dark\"}");
        Assert.Equal(AppUiSettings.BackupFormatZip, store.Load().NewBackupFormat);

        store.Save(store.Load() with { NewBackupFormat = AppUiSettings.BackupFormatFolder });
        Assert.Equal(AppUiSettings.BackupFormatFolder, store.Load().NewBackupFormat);

        File.WriteAllText(path, "{\"NewBackupFormat\":\"rar\"}");
        Assert.Equal(AppUiSettings.BackupFormatZip, store.Load().NewBackupFormat);

        Assert.Equal(BackupContainerFormat.Zip, AppUiSettings.ToContainerFormat(AppUiSettings.BackupFormatZip));
        Assert.Equal(BackupContainerFormat.SevenZip, AppUiSettings.ToContainerFormat(AppUiSettings.BackupFormatSevenZip));
        Assert.Equal(BackupContainerFormat.Folder, AppUiSettings.ToContainerFormat(AppUiSettings.BackupFormatFolder));
    }

    [Fact]
    public void SettingsView_OffersTheThreeChoicesInWords()
    {
        DirectoryInfo? solution = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(solution!.FullName, "Manager.sln")))
            solution = solution.Parent;

        string xaml = File.ReadAllText(Path.Combine(solution.FullName, "GameSaves.App", "Views", "SettingsView.axaml"));

        Assert.Contains("Text=\"Backup storage\"", xaml);
        Assert.Contains("ConverterParameter={x:Static s:AppUiSettings.BackupFormatZip}", xaml);
        Assert.Contains("ConverterParameter={x:Static s:AppUiSettings.BackupFormatSevenZip}", xaml);
        Assert.Contains("ConverterParameter={x:Static s:AppUiSettings.BackupFormatFolder}", xaml);
        Assert.Contains("AutomationProperties.Name=\"Store new backups as uncompressed folders\"", xaml);
    }

    // ---- helpers ----

    private string Original(string relativePath, string content)
    {
        string path = _temp.GetPath("game", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private TransferOverwriteBackupService Service(BackupContainerFormat format) =>
        new(
            new TestDatabasePathProvider(_temp.GetPath("app", "gamesave.db")),
            new BackupStoragePreference(() => format));

    private BackupHistoryService History() =>
        new(new TestDatabasePathProvider(_temp.GetPath("app", "gamesave.db")));

    private static OverwriteBackupContext Context() =>
        new(OverwriteBackupContext.ManualKind, "Test Game", "1234", "source", "target");

    private async Task<TransferBackupRunInfo> CompressedRun(BackupContainerFormat format, string file)
    {
        ITransferOverwriteBackupSession session = Service(format).BeginSession(Context());
        session.BackUpFile(file);
        session.Complete();

        return (await History().GetRunsAsync()).Single(run => run.BackupRootPath == session.BackupRootPath);
    }

    private BackupRestoreService RestoreService(BackupContainerFormat format) =>
        new(
            Service(format),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

    private static BackupRestoreOptions RestoreOptions() =>
        new()
        {
            DryRun = false,
            ConfirmExecution = true,
            OverwriteExisting = true,
            BackupBeforeOverwrite = true
        };

    private Task<BackupRestoreResult> Restore(TransferBackupRunInfo run, BackupContainerFormat format) =>
        RestoreService(format).RestoreAsync(run, RestoreOptions());
}
