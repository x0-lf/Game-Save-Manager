using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Text.Json;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// The shared sync engine: planning, manifest comparison, conflict
    /// detection, sync-log handling, and history recording. Backends only
    /// implement IRemoteFileSystem; the engine guarantees the safety model
    /// (copy-only, never overwrite, never delete, conflicts never copied) and
    /// uploads manifest.json last so an interrupted upload can never leave a
    /// folder that passes for a complete run.
    /// </summary>
    internal sealed class SyncEngine
    {
        private const string SyncMetadataFolder = ".gamesave-sync";
        private const string SyncLogFileName = "sync-log.json";
        private const string SyncLogRelativePath = SyncMetadataFolder + "/" + SyncLogFileName;

        private readonly IRemoteFileSystem _remote;
        private readonly string _providerName;
        private readonly string _remoteRootRaw;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public SyncEngine(
            IRemoteFileSystem remote,
            string providerName,
            string remoteRootRaw,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            _remote = remote;
            _providerName = providerName;
            _remoteRootRaw = remoteRootRaw;
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;
        }

        // ---------------------------------------------------------------
        // Preview
        // ---------------------------------------------------------------

        public async Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            var warnings = new List<TransferPreviewWarning>();
            var items = new List<SyncItem>();

            TransferPreviewWarning? invalid = await _remote.ValidateAsync(cancellationToken);

            if (invalid is not null)
                warnings.Add(invalid);

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

            Dictionary<string, TransferBackupManifest> remoteRuns =
                await ReadRemoteRunsAsync(warnings, cancellationToken);

            if (!await _remote.RootExistsAsync(cancellationToken))
            {
                warnings.Add(new TransferPreviewWarning(
                    "RemoteRootMissing",
                    "The sync folder does not exist yet. It will be created when the sync runs.",
                    TransferWarningSeverity.Info));
            }

            var names = local.Keys
                .Union(remoteRuns.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase);

            string localBase = _backupHistoryService.GetBackupBasePath();
            int ignoredByDirection = 0;

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hasLocal = local.TryGetValue(name, out TransferBackupRunInfo? localRun);
                bool hasRemote = remoteRuns.TryGetValue(name, out TransferBackupManifest? remoteManifest);

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
                        RemotePath: _remote.GetDisplayPath(name),
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
                        LocalPath: Path.Combine(localBase, name),
                        RemotePath: _remote.GetDisplayPath(name),
                        GameName: remoteManifest!.Game,
                        FileCount: remoteManifest.FileCount,
                        TotalBytes: remoteManifest.TotalBytes,
                        StatusText: "Copy to the local backup base"));
                }
                else if (hasLocal && hasRemote)
                {
                    bool equivalent = ManifestsEquivalent(
                        localRun!.Manifest,
                        remoteManifest!);

                    if (equivalent)
                    {
                        items.Add(new SyncItem(
                            name, SyncItemAction.InSync, true, true,
                            localRun.BackupRootPath, _remote.GetDisplayPath(name),
                            localRun.Manifest.Game,
                            localRun.Manifest.FileCount,
                            localRun.Manifest.TotalBytes,
                            "In sync"));
                    }
                    else
                    {
                        items.Add(new SyncItem(
                            name, SyncItemAction.Conflict, true, true,
                            localRun.BackupRootPath, _remote.GetDisplayPath(name),
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

        private async Task<Dictionary<string, TransferBackupManifest>> ReadRemoteRunsAsync(
            List<TransferPreviewWarning> warnings,
            CancellationToken cancellationToken)
        {
            var runs = new Dictionary<string, TransferBackupManifest>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in await _remote.ListRunFolderNamesAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    string? manifestText = await _remote.ReadTextFileAsync(
                        $"{name}/{TransferBackupLocations.ManifestFileName}",
                        cancellationToken);

                    // A folder without a manifest is not a backup run; ignore it.
                    if (manifestText is null)
                        continue;

                    TransferBackupManifest? manifest =
                        JsonSerializer.Deserialize<TransferBackupManifest>(manifestText);

                    if (manifest is not null)
                        runs[name] = manifest;
                }
                catch
                {
                    warnings.Add(new TransferPreviewWarning(
                        "RemoteRunUnreadable",
                        $"A folder in the sync location has an unreadable manifest and was ignored: {_remote.GetDisplayPath(name)}",
                        TransferWarningSeverity.Warning));
                }
            }

            return runs;
        }

        private SyncPlan BuildPlan(
            List<SyncItem> items,
            List<TransferPreviewWarning> warnings)
        {
            int uploads = items.Count(i => i.Action == SyncItemAction.UploadToRemote);
            int downloads = items.Count(i => i.Action == SyncItemAction.DownloadToLocal);

            return new SyncPlan(
                ProviderName: _providerName,
                RemoteRoot: _remoteRootRaw,
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
            TransferPreviewWarning? invalid = await _remote.ValidateAsync(cancellationToken);

            if (invalid is not null)
            {
                warnings.Add(invalid);
                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            bool IsSelected(SyncItem item) =>
                options.OnlyRunNames is null ||
                options.OnlyRunNames.Contains(item.RunName, StringComparer.OrdinalIgnoreCase);

            var progressState = new ProgressState
            {
                RunsTotal = plan.Items.Count(i =>
                    i.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal &&
                    IsSelected(i)),
                BytesTotal = plan.Items
                    .Where(i => i.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal &&
                                IsSelected(i))
                    .Sum(i => i.TotalBytes)
            };

            foreach (SyncItem item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (item.Action)
                {
                    case SyncItemAction.UploadToRemote when !IsSelected(item):
                    case SyncItemAction.DownloadToLocal when !IsSelected(item):
                        results.Add(new SyncItemResult(
                            item,
                            0,
                            SyncItemStatus.SkippedDeselected,
                            "Deselected in the sync plan; not copied."));
                        break;

                    case SyncItemAction.UploadToRemote:
                        results.Add(await UploadRunAsync(item, options, progressState, cancellationToken));
                        progressState.RunsDone++;
                        break;

                    case SyncItemAction.DownloadToLocal:
                        results.Add(await DownloadRunAsync(item, options, progressState, cancellationToken));
                        progressState.RunsDone++;
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

            if (!options.DryRun)
                await AppendSyncLogAsync(results, warnings, cancellationToken);

            return RecordAndBuild(plan, options, results, warnings, startedUtc);
        }

        private async Task<SyncItemResult> UploadRunAsync(
            SyncItem item,
            SyncOptions options,
            ProgressState progressState,
            CancellationToken cancellationToken)
        {
            try
            {
                string localRoot = item.LocalPath!;

                if (!Directory.Exists(localRoot) ||
                    !File.Exists(Path.Combine(localRoot, TransferBackupLocations.ManifestFileName)))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.Failed,
                        "The source run folder or its manifest no longer exists.");
                }

                if (await _remote.FolderExistsAsync(item.RunName, cancellationToken))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.SkippedAlreadyExists,
                        "The target appeared since the preview. Nothing is ever overwritten.");
                }

                if (options.DryRun)
                {
                    return new SyncItemResult(
                        item, item.TotalBytes, SyncItemStatus.DryRun,
                        "Would be copied to the sync folder.");
                }

                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                // Upload manifest.json last: a folder without a manifest is
                // never mistaken for a complete run if the upload is interrupted.
                var files = Directory.EnumerateFiles(localRoot, "*", enumeration)
                    .OrderBy(file => IsRunManifest(localRoot, file) ? 1 : 0)
                    .ToList();

                long bytes = 0;

                foreach (string localFile in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string relative = Path.GetRelativePath(localRoot, localFile)
                        .Replace(Path.DirectorySeparatorChar, '/');

                    long fileBytes = await _remote.UploadFileAsync(
                        localFile,
                        $"{item.RunName}/{relative}",
                        cancellationToken);

                    bytes += fileBytes;
                    progressState.BytesDone += fileBytes;
                    ReportProgress(options, progressState, item.RunName, relative);
                }

                return new SyncItemResult(item, bytes, SyncItemStatus.Uploaded, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SyncItemResult(item, 0, SyncItemStatus.Failed, ex.Message);
            }
        }

        private async Task<SyncItemResult> DownloadRunAsync(
            SyncItem item,
            SyncOptions options,
            ProgressState progressState,
            CancellationToken cancellationToken)
        {
            try
            {
                string localTarget = item.LocalPath!;

                if (Directory.Exists(localTarget))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.SkippedAlreadyExists,
                        "The target appeared since the preview. Nothing is ever overwritten.");
                }

                string manifestRelative = $"{item.RunName}/{TransferBackupLocations.ManifestFileName}";

                if (await _remote.ReadTextFileAsync(manifestRelative, cancellationToken) is null)
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.Failed,
                        "The source run folder or its manifest no longer exists.");
                }

                if (options.DryRun)
                {
                    return new SyncItemResult(
                        item, item.TotalBytes, SyncItemStatus.DryRun,
                        "Would be copied to the local backup base.");
                }

                long bytes = 0;

                foreach (string relative in await _remote.ListFilesAsync(item.RunName, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    long fileBytes = await _remote.DownloadFileAsync(
                        $"{item.RunName}/{relative}",
                        Path.Combine(localTarget, relative.Replace('/', Path.DirectorySeparatorChar)),
                        cancellationToken);

                    bytes += fileBytes;
                    progressState.BytesDone += fileBytes;
                    ReportProgress(options, progressState, item.RunName, relative);
                }

                // The downloaded manifest records backup-file paths from the
                // machine the run was created on; rewrite them to this one.
                string manifestPath = Path.Combine(
                    localTarget,
                    TransferBackupLocations.ManifestFileName);

                TransferBackupManifest? manifest =
                    JsonSerializer.Deserialize<TransferBackupManifest>(
                        File.ReadAllText(manifestPath));

                if (manifest is null ||
                    !BackupManifestPathRewriter.TryRewrite(manifest, localTarget, out TransferBackupManifest rewritten))
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

                return new SyncItemResult(item, bytes, SyncItemStatus.Downloaded, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SyncItemResult(item, 0, SyncItemStatus.Failed, ex.Message);
            }
        }

        private sealed class ProgressState
        {
            public int RunsDone;
            public int RunsTotal;
            public long BytesDone;
            public long BytesTotal;
        }

        private static void ReportProgress(
            SyncOptions options,
            ProgressState state,
            string runName,
            string currentFile)
        {
            // Actual bytes can slightly exceed the planned total (manifests are
            // not counted in run sizes), so clamp for a clean progress bar.
            options.Progress?.Report(new SyncProgress(
                RunName: runName,
                CurrentFile: currentFile,
                RunsDone: state.RunsDone,
                RunsTotal: state.RunsTotal,
                BytesDone: Math.Min(state.BytesDone, state.BytesTotal),
                BytesTotal: state.BytesTotal));
        }

        private static bool IsRunManifest(string runRoot, string filePath)
        {
            return string.Equals(
                       Path.GetFileName(filePath),
                       TransferBackupLocations.ManifestFileName,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       Path.GetDirectoryName(Path.GetFullPath(filePath)),
                       Path.GetFullPath(runRoot).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------
        // Sync log (version-history metadata)
        // ---------------------------------------------------------------

        public async Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default)
        {
            List<SyncLogEntry> log = ParseSyncLog(
                await _remote.ReadTextFileAsync(SyncLogRelativePath, cancellationToken));

            return log
                .OrderByDescending(entry => entry.TimestampUtc)
                .ToList();
        }

        private async Task AppendSyncLogAsync(
            IReadOnlyList<SyncItemResult> results,
            List<TransferPreviewWarning> warnings,
            CancellationToken cancellationToken)
        {
            try
            {
                List<SyncLogEntry> log = ParseSyncLog(
                    await _remote.ReadTextFileAsync(SyncLogRelativePath, cancellationToken));

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

                await _remote.WriteTextFileAsync(
                    SyncLogRelativePath,
                    JsonSerializer.Serialize(
                        log,
                        new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(new TransferPreviewWarning(
                    "SyncLogWriteFailed",
                    $"The sync itself succeeded, but the sync log could not be updated: {ex.Message}",
                    TransferWarningSeverity.Warning));
            }
        }

        private static List<SyncLogEntry> ParseSyncLog(string? content)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    return new List<SyncLogEntry>();

                return JsonSerializer.Deserialize<List<SyncLogEntry>>(content)
                       ?? new List<SyncLogEntry>();
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
                    TargetAccountId: _remoteRootRaw,
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
                    or SyncItemStatus.SkippedAlreadyExists
                    or SyncItemStatus.SkippedDeselected);

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
