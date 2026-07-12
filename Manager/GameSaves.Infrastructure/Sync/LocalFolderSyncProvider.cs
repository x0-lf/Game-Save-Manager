using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Text.Json;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Syncs backup runs between the local backup base and another local or
    /// mounted folder (NAS share, USB drive, Syncthing folder, ...). Copy-only:
    /// runs are copied to whichever side is missing them, conflicts are
    /// reported and never resolved automatically, and nothing is ever deleted
    /// or overwritten. Each executed sync is appended to a sync log stored
    /// alongside the remote data as version-history metadata.
    /// </summary>
    public sealed class LocalFolderSyncProvider : ISyncProvider
    {
        private const string SyncMetadataFolder = ".gamesave-sync";
        private const string SyncLogFileName = "sync-log.json";

        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public LocalFolderSyncProvider(
            string remoteRoot,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            RemoteRoot = remoteRoot;
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;
        }

        public string ProviderName => "Local folder";

        public string RemoteRoot { get; }

        // ---------------------------------------------------------------
        // Preview
        // ---------------------------------------------------------------

        public async Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            var warnings = new List<TransferPreviewWarning>();
            var items = new List<SyncItem>();

            string? remote = ValidateRemoteRoot(warnings);

            if (!options.Upload && !options.Download)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoSyncDirectionSelected",
                    "No sync direction is selected. Enable upload, download, or both.",
                    TransferWarningSeverity.Error));
            }

            if (warnings.Any(w => w.Severity == TransferWarningSeverity.Error))
                return BuildPlan(items, warnings);

            IReadOnlyList<TransferBackupRunInfo> localRuns =
                await _backupHistoryService.GetRunsAsync(cancellationToken);

            Dictionary<string, TransferBackupRunInfo> local = localRuns
                .ToDictionary(
                    run => Path.GetFileName(run.BackupRootPath),
                    StringComparer.OrdinalIgnoreCase);

            Dictionary<string, TransferBackupRunInfo> remoteRuns =
                await Task.Run(() => ReadRemoteRuns(remote!, warnings), cancellationToken);

            if (!Directory.Exists(remote))
            {
                warnings.Add(new TransferPreviewWarning(
                    "RemoteRootMissing",
                    "The sync folder does not exist yet. It will be created when the sync runs.",
                    TransferWarningSeverity.Info));
            }

            var names = local.Keys
                .Union(remoteRuns.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase);

            int ignoredByDirection = 0;

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hasLocal = local.TryGetValue(name, out TransferBackupRunInfo? localRun);
                bool hasRemote = remoteRuns.TryGetValue(name, out TransferBackupRunInfo? remoteRun);

                if (hasLocal && !hasRemote)
                {
                    if (!options.Upload)
                    {
                        ignoredByDirection++;
                        continue;
                    }

                    items.Add(new SyncItem(
                        RunName: name,
                        Action: SyncItemAction.UploadToRemote,
                        ExistsLocally: true,
                        ExistsRemotely: false,
                        LocalPath: localRun!.BackupRootPath,
                        RemotePath: Path.Combine(remote!, name),
                        GameName: localRun.Manifest.Game,
                        FileCount: localRun.Manifest.FileCount,
                        TotalBytes: localRun.Manifest.TotalBytes,
                        StatusText: "Copy to the sync folder"));
                }
                else if (!hasLocal && hasRemote)
                {
                    if (!options.Download)
                    {
                        ignoredByDirection++;
                        continue;
                    }

                    items.Add(new SyncItem(
                        RunName: name,
                        Action: SyncItemAction.DownloadToLocal,
                        ExistsLocally: false,
                        ExistsRemotely: true,
                        LocalPath: Path.Combine(_backupHistoryService.GetBackupBasePath(), name),
                        RemotePath: remoteRun!.BackupRootPath,
                        GameName: remoteRun.Manifest.Game,
                        FileCount: remoteRun.Manifest.FileCount,
                        TotalBytes: remoteRun.Manifest.TotalBytes,
                        StatusText: "Copy to the local backup base"));
                }
                else if (hasLocal && hasRemote)
                {
                    bool equivalent = ManifestsEquivalent(
                        localRun!.Manifest,
                        remoteRun!.Manifest);

                    if (equivalent)
                    {
                        items.Add(new SyncItem(
                            name, SyncItemAction.InSync, true, true,
                            localRun.BackupRootPath, remoteRun.BackupRootPath,
                            localRun.Manifest.Game,
                            localRun.Manifest.FileCount,
                            localRun.Manifest.TotalBytes,
                            "In sync"));
                    }
                    else
                    {
                        items.Add(new SyncItem(
                            name, SyncItemAction.Conflict, true, true,
                            localRun.BackupRootPath, remoteRun.BackupRootPath,
                            localRun.Manifest.Game,
                            localRun.Manifest.FileCount,
                            localRun.Manifest.TotalBytes,
                            "Conflict: same name, different content. Never copied automatically."));

                        warnings.Add(new TransferPreviewWarning(
                            "SyncConflict",
                            $"Run \"{name}\" differs between the local base and the sync folder. It will not be copied; resolve it manually (for example export one side as ZIP, or delete one side via Cleanup).",
                            TransferWarningSeverity.Warning));
                    }
                }
            }

            if (ignoredByDirection > 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "IgnoredByDirection",
                    $"{ignoredByDirection} run(s) were ignored because the corresponding sync direction is disabled.",
                    TransferWarningSeverity.Info));
            }

            SyncPlan plan = BuildPlan(items, warnings);

            if (plan.UploadCount + plan.DownloadCount == 0 &&
                warnings.All(w => w.Severity != TransferWarningSeverity.Error))
            {
                warnings.Add(new TransferPreviewWarning(
                    "NothingToSync",
                    plan.ConflictCount > 0
                        ? "Nothing can be synced automatically; only conflicts remain."
                        : "Everything is in sync. There is nothing to copy.",
                    TransferWarningSeverity.Info));

                plan = BuildPlan(items, warnings);
            }

            return plan;
        }

        private SyncPlan BuildPlan(
            List<SyncItem> items,
            List<TransferPreviewWarning> warnings)
        {
            int uploads = items.Count(i => i.Action == SyncItemAction.UploadToRemote);
            int downloads = items.Count(i => i.Action == SyncItemAction.DownloadToLocal);

            return new SyncPlan(
                ProviderName: ProviderName,
                RemoteRoot: RemoteRoot,
                Items: items,
                Warnings: warnings.ToList(),
                CanExecute:
                    warnings.All(w => w.Severity != TransferWarningSeverity.Error) &&
                    uploads + downloads > 0,
                UploadCount: uploads,
                DownloadCount: downloads,
                InSyncCount: items.Count(i => i.Action == SyncItemAction.InSync),
                ConflictCount: items.Count(i => i.Action == SyncItemAction.Conflict),
                BytesToUpload: items.Where(i => i.Action == SyncItemAction.UploadToRemote).Sum(i => i.TotalBytes),
                BytesToDownload: items.Where(i => i.Action == SyncItemAction.DownloadToLocal).Sum(i => i.TotalBytes));
        }

        // ---------------------------------------------------------------
        // Execution
        // ---------------------------------------------------------------

        public async Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

            var warnings = new List<TransferPreviewWarning>();
            var results = new List<SyncItemResult>();

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Sync was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            if (plan.HasErrors)
            {
                warnings.Add(new TransferPreviewWarning(
                    "PlanHasErrors",
                    "Sync was blocked because the preview plan contains errors.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            // Re-validate the remote at execution time; the plan may be stale.
            var revalidation = new List<TransferPreviewWarning>();

            if (ValidateRemoteRoot(revalidation) is not string remote ||
                revalidation.Any(w => w.Severity == TransferWarningSeverity.Error))
            {
                warnings.AddRange(revalidation);
                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            string localBase = _backupHistoryService.GetBackupBasePath();

            await Task.Run(() =>
            {
                foreach (SyncItem item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    switch (item.Action)
                    {
                        case SyncItemAction.UploadToRemote:
                            results.Add(CopyRun(
                                item,
                                sourceRoot: item.LocalPath!,
                                targetRoot: Path.Combine(remote, item.RunName),
                                options.DryRun,
                                rewriteManifest: false,
                                SyncItemStatus.Uploaded));
                            break;

                        case SyncItemAction.DownloadToLocal:
                            results.Add(CopyRun(
                                item,
                                sourceRoot: item.RemotePath!,
                                targetRoot: Path.Combine(localBase, item.RunName),
                                options.DryRun,
                                rewriteManifest: true,
                                SyncItemStatus.Downloaded));
                            break;

                        case SyncItemAction.Conflict:
                            results.Add(new SyncItemResult(
                                item,
                                0,
                                SyncItemStatus.SkippedConflict,
                                "Conflicts are never copied automatically."));
                            break;
                    }
                }
            }, cancellationToken);

            if (!options.DryRun)
                AppendSyncLog(remote, results, warnings);

            return RecordAndBuild(plan, options, results, warnings, startedUtc);
        }

        private static SyncItemResult CopyRun(
            SyncItem item,
            string sourceRoot,
            string targetRoot,
            bool dryRun,
            bool rewriteManifest,
            SyncItemStatus successStatus)
        {
            try
            {
                if (!Directory.Exists(sourceRoot) ||
                    !File.Exists(Path.Combine(sourceRoot, TransferBackupLocations.ManifestFileName)))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.Failed,
                        "The source run folder or its manifest no longer exists.");
                }

                if (Directory.Exists(targetRoot))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.SkippedAlreadyExists,
                        "The target appeared since the preview. Nothing is ever overwritten.");
                }

                if (dryRun)
                {
                    return new SyncItemResult(
                        item, item.TotalBytes, SyncItemStatus.DryRun,
                        successStatus == SyncItemStatus.Uploaded
                            ? "Would be copied to the sync folder."
                            : "Would be copied to the local backup base.");
                }

                long bytes = CopyDirectory(sourceRoot, targetRoot);

                if (rewriteManifest)
                {
                    string manifestPath = Path.Combine(
                        targetRoot,
                        TransferBackupLocations.ManifestFileName);

                    TransferBackupManifest? manifest =
                        JsonSerializer.Deserialize<TransferBackupManifest>(
                            File.ReadAllText(manifestPath));

                    if (manifest is null ||
                        !BackupManifestPathRewriter.TryRewrite(manifest, targetRoot, out TransferBackupManifest rewritten))
                    {
                        return new SyncItemResult(
                            item, bytes, SyncItemStatus.Failed,
                            "The run was copied, but its manifest paths could not be rewritten. It may not be restorable; it was left in place for inspection.");
                    }

                    File.WriteAllText(
                        manifestPath,
                        JsonSerializer.Serialize(
                            rewritten,
                            new JsonSerializerOptions { WriteIndented = true }));
                }

                return new SyncItemResult(item, bytes, successStatus, null);
            }
            catch (Exception ex)
            {
                return new SyncItemResult(item, 0, SyncItemStatus.Failed, ex.Message);
            }
        }

        private static long CopyDirectory(string sourceRoot, string targetRoot)
        {
            long bytes = 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", options))
            {
                string relative = Path.GetRelativePath(sourceRoot, sourceFile);
                string targetFile = Path.Combine(targetRoot, relative);

                string? targetDirectory = Path.GetDirectoryName(targetFile);

                if (!string.IsNullOrWhiteSpace(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                File.Copy(sourceFile, targetFile, overwrite: false);

                File.SetCreationTimeUtc(targetFile, File.GetCreationTimeUtc(sourceFile));
                File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(sourceFile));

                bytes += new FileInfo(targetFile).Length;
            }

            return bytes;
        }

        // ---------------------------------------------------------------
        // Sync log (version-history metadata)
        // ---------------------------------------------------------------

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<SyncLogEntry>>(() =>
            {
                List<SyncLogEntry> log = ReadSyncLog(GetSyncLogPath(RemoteRoot));

                return log
                    .OrderByDescending(entry => entry.TimestampUtc)
                    .ToList();
            }, cancellationToken);
        }

        private void AppendSyncLog(
            string remote,
            IReadOnlyList<SyncItemResult> results,
            List<TransferPreviewWarning> warnings)
        {
            try
            {
                string logPath = GetSyncLogPath(remote);

                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

                List<SyncLogEntry> log = ReadSyncLog(logPath);

                log.Add(new SyncLogEntry(
                    DeviceName: Environment.MachineName,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Uploaded: results.Count(r => r.Status == SyncItemStatus.Uploaded),
                    Downloaded: results.Count(r => r.Status == SyncItemStatus.Downloaded),
                    Conflicts: results.Count(r => r.Status == SyncItemStatus.SkippedConflict),
                    BytesCopied: results
                        .Where(r => r.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded)
                        .Sum(r => r.Bytes),
                    UploadedRuns: results
                        .Where(r => r.Status == SyncItemStatus.Uploaded)
                        .Select(r => r.Item.RunName)
                        .ToList(),
                    DownloadedRuns: results
                        .Where(r => r.Status == SyncItemStatus.Downloaded)
                        .Select(r => r.Item.RunName)
                        .ToList()));

                File.WriteAllText(
                    logPath,
                    JsonSerializer.Serialize(
                        log,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                warnings.Add(new TransferPreviewWarning(
                    "SyncLogWriteFailed",
                    $"The sync itself succeeded, but the sync log could not be updated: {ex.Message}",
                    TransferWarningSeverity.Warning));
            }
        }

        private static string GetSyncLogPath(string remote)
        {
            return Path.Combine(remote, SyncMetadataFolder, SyncLogFileName);
        }

        private static List<SyncLogEntry> ReadSyncLog(string logPath)
        {
            try
            {
                if (!File.Exists(logPath))
                    return new List<SyncLogEntry>();

                return JsonSerializer.Deserialize<List<SyncLogEntry>>(
                    File.ReadAllText(logPath)) ?? new List<SyncLogEntry>();
            }
            catch
            {
                // An unreadable log never blocks syncing.
                return new List<SyncLogEntry>();
            }
        }

        // ---------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------

        private string? ValidateRemoteRoot(List<TransferPreviewWarning> warnings)
        {
            string? remote = TransferPathGuard.TryNormalize(RemoteRoot);

            if (remote is null)
            {
                warnings.Add(new TransferPreviewWarning(
                    "RemoteInvalid",
                    "The sync folder is empty or not a valid folder path.",
                    TransferWarningSeverity.Error));

                return null;
            }

            string basePath = _backupHistoryService.GetBackupBasePath();

            if (TransferPathGuard.IsUnderRoot(remote, basePath) ||
                TransferPathGuard.IsUnderRoot(basePath, remote))
            {
                warnings.Add(new TransferPreviewWarning(
                    "RemoteOverlapsLocal",
                    "The sync folder overlaps the local backup base. Choose a folder outside it.",
                    TransferWarningSeverity.Error));

                return null;
            }

            return remote;
        }

        private Dictionary<string, TransferBackupRunInfo> ReadRemoteRuns(
            string remote,
            List<TransferPreviewWarning> warnings)
        {
            var runs = new Dictionary<string, TransferBackupRunInfo>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(remote))
                return runs;

            foreach (string folder in Directory.EnumerateDirectories(remote))
            {
                string manifestPath = Path.Combine(folder, TransferBackupLocations.ManifestFileName);

                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    TransferBackupManifest? manifest =
                        JsonSerializer.Deserialize<TransferBackupManifest>(
                            File.ReadAllText(manifestPath));

                    if (manifest is not null)
                    {
                        runs[Path.GetFileName(folder)] = new TransferBackupRunInfo(
                            BackupRootPath: folder,
                            ManifestPath: manifestPath,
                            Manifest: manifest);
                    }
                }
                catch
                {
                    warnings.Add(new TransferPreviewWarning(
                        "RemoteRunUnreadable",
                        $"A folder in the sync location has an unreadable manifest and was ignored: {folder}",
                        TransferWarningSeverity.Warning));
                }
            }

            return runs;
        }

        // Equivalence ignores backup-file paths (they are machine-specific)
        // and compares identity, counts, and per-file content hashes.
        private static bool ManifestsEquivalent(
            TransferBackupManifest left,
            TransferBackupManifest right)
        {
            if (!left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase) ||
                !left.SteamAppId.Equals(right.SteamAppId, StringComparison.OrdinalIgnoreCase) ||
                left.StartedUtc != right.StartedUtc ||
                left.FileCount != right.FileCount ||
                left.TotalBytes != right.TotalBytes)
            {
                return false;
            }

            var leftFiles = left.Items
                .Select(i => (i.OriginalFile.ToUpperInvariant(), i.Sha256.ToUpperInvariant()))
                .ToHashSet();

            var rightFiles = right.Items
                .Select(i => (i.OriginalFile.ToUpperInvariant(), i.Sha256.ToUpperInvariant()))
                .ToHashSet();

            return leftFiles.SetEquals(rightFiles);
        }

        private void RecordHistory(
            SyncOptions options,
            SyncResult result,
            DateTimeOffset startedUtc)
        {
            try
            {
                string? blockedReason = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?
                    .Message;

                _historyRepository.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.Sync,
                    GameName: "(backup sync)",
                    SteamAppId: "-",
                    SourceAccountId: Environment.MachineName,
                    TargetAccountId: RemoteRoot,
                    DryRun: options.DryRun,
                    OverwriteEnabled: false,
                    BackupEnabled: false,
                    FilesConsidered: result.Items.Count,
                    FilesCopied: result.Uploaded + result.Downloaded,
                    FilesSkipped: result.Skipped,
                    FilesFailed: result.Items.Count(i => i.Status == SyncItemStatus.Failed),
                    BytesCopied: result.BytesCopied,
                    FilesBackedUp: 0,
                    BackupRootPath: null,
                    BlockedReason: blockedReason,
                    StartedUtc: startedUtc,
                    CompletedUtc: DateTimeOffset.UtcNow,
                    Items: result.Items
                        .Select(i => new TransferRunItemRecord(
                            i.Item.LocalPath ?? i.Item.RunName,
                            i.Item.RemotePath ?? string.Empty,
                            i.Bytes,
                            i.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded,
                            i.Status.ToString(),
                            i.Error,
                            BackupFile: null))
                        .ToList()));
            }
            catch
            {
                // History is an audit trail; a recording failure must never
                // fail the sync itself.
            }
        }

        private SyncResult RecordAndBuild(
            SyncPlan plan,
            SyncOptions options,
            IReadOnlyList<SyncItemResult> results,
            List<TransferPreviewWarning> warnings,
            DateTimeOffset startedUtc)
        {
            int uploaded = results.Count(r => r.Status == SyncItemStatus.Uploaded);
            int downloaded = results.Count(r => r.Status == SyncItemStatus.Downloaded);

            int skipped = results.Count(r =>
                r.Status is SyncItemStatus.SkippedConflict
                    or SyncItemStatus.SkippedAlreadyExists);

            long bytesCopied = results
                .Where(r => r.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded)
                .Sum(r => r.Bytes);

            var result = new SyncResult(
                Plan: plan,
                DryRun: options.DryRun,
                Uploaded: uploaded,
                Downloaded: downloaded,
                Skipped: skipped,
                BytesCopied: bytesCopied,
                Items: results,
                Warnings: warnings);

            RecordHistory(options, result, startedUtc);

            return result;
        }
    }
}
