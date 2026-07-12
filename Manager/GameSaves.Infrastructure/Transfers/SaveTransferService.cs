using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Executes a transfer preview plan as a copy-only operation.
    /// This service never deletes or moves source files. Existing target files
    /// are skipped unless overwrite is explicitly enabled, and execution is
    /// blocked unless it was explicitly confirmed.
    /// </summary>
    public sealed class SaveTransferService : ISaveTransferService
    {
        private readonly ITransferOverwriteBackupService _overwriteBackupService;
        private readonly ITransferHistoryRepository _historyRepository;

        public SaveTransferService(
            ITransferOverwriteBackupService overwriteBackupService,
            ITransferHistoryRepository historyRepository)
        {
            _overwriteBackupService = overwriteBackupService;
            _historyRepository = historyRepository;
        }

        public Task<SaveTransferResult> ExecuteAsync(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Execute(plan, options, cancellationToken),
                cancellationToken);
        }

        private SaveTransferResult Execute(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

            SaveTransferResult result = ExecuteCore(plan, options, cancellationToken);

            TryRecordHistory(plan, options, result, startedUtc);

            return result;
        }

        private void TryRecordHistory(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            SaveTransferResult result,
            DateTimeOffset startedUtc)
        {
            try
            {
                string? blockedReason = result.Warnings
                    .Skip(plan.Warnings.Count)
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?
                    .Message;

                _historyRepository.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.TransferCopy,
                    GameName: plan.Game.Name,
                    SteamAppId: plan.Game.AppId,
                    SourceAccountId: plan.SourceProfile.AccountId,
                    TargetAccountId: plan.TargetProfile.AccountId,
                    DryRun: options.DryRun,
                    OverwriteEnabled: options.OverwriteExisting,
                    BackupEnabled: options.BackupBeforeOverwrite,
                    FilesConsidered: result.FilesConsidered,
                    FilesCopied: result.FilesCopied,
                    FilesSkipped: result.FilesSkipped,
                    FilesFailed: result.Items.Count(i => i.Status == SaveTransferItemStatus.Failed),
                    BytesCopied: result.BytesCopied,
                    FilesBackedUp: result.FilesBackedUp,
                    BackupRootPath: result.BackupRootPath,
                    BlockedReason: blockedReason,
                    StartedUtc: startedUtc,
                    CompletedUtc: DateTimeOffset.UtcNow,
                    Items: result.Items
                        .Select(i => new TransferRunItemRecord(
                            i.SourceFile,
                            i.TargetFile,
                            i.Bytes,
                            i.Copied,
                            i.Status.ToString(),
                            i.Error,
                            i.BackupFile))
                        .ToList()));
            }
            catch
            {
                // History is an audit trail; a recording failure must never
                // fail the copy itself.
            }
        }

        private SaveTransferResult ExecuteCore(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>(plan.Warnings);
            var results = new List<SaveTransferItemResult>();

            TransferPreviewWarning? blocker = FindExecutionBlocker(plan, options);

            if (blocker is not null)
            {
                warnings.Add(blocker);
                return BuildResult(plan, options, results, warnings);
            }

            // Evaluate per-item blocks (same source and target path, containment
            // failures). By default any blocked item refuses the whole copy; the
            // user can explicitly opt in to skipping blocked items instead.
            var blockedItems = new Dictionary<TransferPreviewItem, string>();
            bool anyContainmentViolation = false;

            foreach (TransferPreviewItem item in plan.Items)
            {
                string? reason = GetExecutionBlockReason(plan, item, out bool containment);

                if (reason is null)
                    continue;

                blockedItems[item] = reason;
                anyContainmentViolation |= containment;
            }

            if (blockedItems.Count > 0 && !options.SkipBlockedItems)
            {
                warnings.Add(anyContainmentViolation
                    ? new TransferPreviewWarning(
                        "PathContainmentViolation",
                        $"Copy was blocked: a path failed containment checks. {blockedItems.Values.First()}",
                        TransferWarningSeverity.Error)
                    : new TransferPreviewWarning(
                        "BlockedItemsPresent",
                        $"Copy was blocked because {blockedItems.Count} item(s) are blocked by errors. Enable \"Skip blocked items and copy the rest\" to copy the remaining safe items, or exclude the transfer source that causes the error.",
                        TransferWarningSeverity.Error));

                return BuildResult(plan, options, results, warnings);
            }

            // Created lazily on the first overwrite so runs that overwrite
            // nothing leave no backup folder behind.
            ITransferOverwriteBackupSession? backupSession = null;

            ITransferOverwriteBackupSession GetBackupSession() =>
                backupSession ??= _overwriteBackupService.BeginSession(
                    new OverwriteBackupContext(
                        Kind: OverwriteBackupContext.TransferKind,
                        Game: plan.Game.Name,
                        SteamAppId: plan.Game.AppId,
                        SourceAccountId: plan.SourceProfile.AccountId,
                        TargetAccountId: plan.TargetProfile.AccountId));

            try
            {
                foreach (TransferPreviewItem previewItem in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (blockedItems.TryGetValue(previewItem, out string? blockReason))
                    {
                        results.Add(new SaveTransferItemResult(
                            previewItem,
                            previewItem.SourcePath,
                            previewItem.TargetPath,
                            0,
                            Copied: false,
                            SaveTransferItemStatus.SkippedBlocked,
                            $"Blocked item skipped: {blockReason}"));

                        continue;
                    }

                    foreach (FileTransferPair pair in EnumerateFilePairs(previewItem))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        SaveTransferItemResult result = TransferOneFile(
                            previewItem,
                            pair,
                            options,
                            GetBackupSession);

                        results.Add(result);
                    }
                }
            }
            finally
            {
                backupSession?.Complete();
            }

            if (backupSession is not null && backupSession.FilesBackedUp > 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "OverwriteBackups",
                    $"Backed up {backupSession.FilesBackedUp} target file(s) before overwriting to: {backupSession.BackupRootPath}",
                    TransferWarningSeverity.Info));
            }

            return BuildResult(
                plan,
                options,
                results,
                warnings,
                backupSession?.FilesBackedUp ?? 0,
                backupSession?.FilesBackedUp > 0 ? backupSession.BackupRootPath : null);
        }

        // ---------------------------------------------------------------
        // Execution guards
        // ---------------------------------------------------------------

        private static TransferPreviewWarning? FindExecutionBlocker(
            TransferPreviewPlan plan,
            SaveTransferOptions options)
        {
            if (!options.DryRun && !options.ConfirmExecution)
            {
                return new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Copy was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error);
            }

            if (plan.HasErrors)
            {
                return new TransferPreviewWarning(
                    "PlanHasErrors",
                    "Copy was blocked because the preview plan contains errors.",
                    TransferWarningSeverity.Error);
            }

            if (plan.SourceProfile.AccountId.Equals(
                    plan.TargetProfile.AccountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new TransferPreviewWarning(
                    "SameProfile",
                    "Copy was blocked because source and target profiles are the same.",
                    TransferWarningSeverity.Error);
            }

            if (plan.Items.Count == 0)
            {
                return new TransferPreviewWarning(
                    "NoItems",
                    "Copy was blocked because the plan contains no items.",
                    TransferWarningSeverity.Error);
            }

            if (!plan.Items.Any(item => item.SourceExists))
            {
                return new TransferPreviewWarning(
                    "NothingToCopy",
                    "Copy was blocked because no source path exists. There are no files to copy.",
                    TransferWarningSeverity.Error);
            }

            return null;
        }

        // Determines whether an item must not be copied. Userdata containment is
        // re-validated at execution time: the plan may be stale, and preview-time
        // checks must never be the only defense.
        private static string? GetExecutionBlockReason(
            TransferPreviewPlan plan,
            TransferPreviewItem item,
            out bool isContainmentViolation)
        {
            isContainmentViolation = false;

            if (item.SourceType == TransferSourceType.SteamUserDataGameFolder)
            {
                bool sourceContained = TransferPathGuard.IsValidUserDataGameFolderRoot(
                    item.SourceRoot,
                    plan.SourceProfile.UserDataPath,
                    plan.Game.AppId);

                bool targetContained = TransferPathGuard.IsValidUserDataGameFolderRoot(
                    item.TargetRoot,
                    plan.TargetProfile.UserDataPath,
                    plan.Game.AppId);

                if (!sourceContained || !targetContained)
                {
                    isContainmentViolation = true;
                    return $"Userdata game-folder path failed containment checks. Source: {item.SourceRoot} Target: {item.TargetRoot}";
                }

                if (TransferPathGuard.PathsEqual(item.SourceRoot, item.TargetRoot))
                    return $"Userdata source and target resolve to the same folder: {item.SourceRoot}";
            }

            if (item.IsBlocked)
            {
                isContainmentViolation =
                    item.ConflictStatus == TransferConflictStatus.OutsideExpectedRoot;

                return item.ConflictStatus switch
                {
                    TransferConflictStatus.SameSourceAndTarget =>
                        $"Source and target resolve to the same path: {item.SourcePath}",
                    TransferConflictStatus.OutsideExpectedRoot =>
                        $"Path failed containment checks. Source: {item.SourceRoot} Target: {item.TargetRoot}",
                    _ => "The item is blocked by an error."
                };
            }

            return null;
        }

        // ---------------------------------------------------------------
        // File pair enumeration (respects CopyScope)
        // ---------------------------------------------------------------

        private static IEnumerable<FileTransferPair> EnumerateFilePairs(
            TransferPreviewItem item)
        {
            string sourceRoot = string.IsNullOrWhiteSpace(item.SourceRoot)
                ? item.SourcePath
                : item.SourceRoot;

            string targetRoot = string.IsNullOrWhiteSpace(item.TargetRoot)
                ? item.TargetPath
                : item.TargetRoot;

            if (string.IsNullOrWhiteSpace(sourceRoot) ||
                string.IsNullOrWhiteSpace(targetRoot))
            {
                yield break;
            }

            switch (item.CopyScope)
            {
                case TransferCopyScope.SingleFile:
                    foreach (FileTransferPair pair in EnumerateSingleFile(sourceRoot, targetRoot))
                        yield return pair;
                    break;

                case TransferCopyScope.WholeDirectoryAsDirectory:
                {
                    string nestedTargetRoot = Path.Combine(
                        targetRoot,
                        Path.GetFileName(TransferPathGuard.TryNormalize(sourceRoot) ?? sourceRoot));

                    foreach (FileTransferPair pair in EnumerateDirectoryContents(sourceRoot, nestedTargetRoot))
                        yield return pair;
                    break;
                }

                case TransferCopyScope.DirectoryContents:
                default:
                    if (File.Exists(sourceRoot))
                    {
                        // Mapping resolved to a single file even though the scope
                        // is directory-based; copy it as one file.
                        foreach (FileTransferPair pair in EnumerateSingleFile(sourceRoot, targetRoot))
                            yield return pair;
                    }
                    else
                    {
                        foreach (FileTransferPair pair in EnumerateDirectoryContents(sourceRoot, targetRoot))
                            yield return pair;
                    }
                    break;
            }
        }

        private static IEnumerable<FileTransferPair> EnumerateSingleFile(
            string sourceFile,
            string targetPath)
        {
            if (!File.Exists(sourceFile))
                yield break;

            string targetFile = Directory.Exists(targetPath)
                ? Path.Combine(targetPath, Path.GetFileName(sourceFile))
                : targetPath;

            yield return new FileTransferPair(
                sourceFile,
                targetFile,
                TargetRoot: null);
        }

        private static IEnumerable<FileTransferPair> EnumerateDirectoryContents(
            string sourceRoot,
            string targetRoot)
        {
            if (!Directory.Exists(sourceRoot))
                yield break;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(sourceRoot, "*", options);
            }
            catch
            {
                yield break;
            }

            foreach (string sourceFile in files)
            {
                string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
                string targetFile = Path.Combine(targetRoot, relativePath);

                yield return new FileTransferPair(
                    sourceFile,
                    targetFile,
                    targetRoot);
            }
        }

        // ---------------------------------------------------------------
        // Single file copy
        // ---------------------------------------------------------------

        private static SaveTransferItemResult TransferOneFile(
            TransferPreviewItem previewItem,
            FileTransferPair pair,
            SaveTransferOptions options,
            Func<ITransferOverwriteBackupSession> getBackupSession)
        {
            try
            {
                if (!File.Exists(pair.SourceFile))
                {
                    return new SaveTransferItemResult(
                        previewItem,
                        pair.SourceFile,
                        pair.TargetFile,
                        0,
                        Copied: false,
                        SaveTransferItemStatus.SkippedSourceMissing,
                        "Source file does not exist.");
                }

                if (TransferPathGuard.PathsEqual(pair.SourceFile, pair.TargetFile))
                {
                    return new SaveTransferItemResult(
                        previewItem,
                        pair.SourceFile,
                        pair.TargetFile,
                        0,
                        Copied: false,
                        SaveTransferItemStatus.SkippedSamePath,
                        "Source and target file are the same.");
                }

                // Path traversal guard: a computed target file must never
                // escape the target root of its preview item.
                if (pair.TargetRoot is not null &&
                    !TransferPathGuard.IsUnderRoot(pair.TargetFile, pair.TargetRoot))
                {
                    return new SaveTransferItemResult(
                        previewItem,
                        pair.SourceFile,
                        pair.TargetFile,
                        0,
                        Copied: false,
                        SaveTransferItemStatus.SkippedUnsafePath,
                        "Target file path escaped the target root. Skipped for safety.");
                }

                var sourceInfo = new FileInfo(pair.SourceFile);
                bool targetExists = File.Exists(pair.TargetFile);

                if (targetExists && !options.OverwriteExisting)
                {
                    return new SaveTransferItemResult(
                        previewItem,
                        pair.SourceFile,
                        pair.TargetFile,
                        sourceInfo.Length,
                        Copied: false,
                        SaveTransferItemStatus.SkippedTargetExists,
                        "Target file already exists. Overwrite is disabled.");
                }

                if (options.DryRun)
                {
                    return new SaveTransferItemResult(
                        previewItem,
                        pair.SourceFile,
                        pair.TargetFile,
                        sourceInfo.Length,
                        Copied: false,
                        SaveTransferItemStatus.DryRun,
                        null);
                }

                string? backupFile = null;

                if (targetExists && options.OverwriteExisting && options.BackupBeforeOverwrite)
                {
                    try
                    {
                        TransferOverwriteBackupItem backupItem =
                            getBackupSession().BackUpFile(pair.TargetFile);

                        backupFile = backupItem.BackupFile;
                    }
                    catch (Exception backupEx)
                    {
                        // Safe Mode: never overwrite a file that could not be backed up.
                        return new SaveTransferItemResult(
                            previewItem,
                            pair.SourceFile,
                            pair.TargetFile,
                            sourceInfo.Length,
                            Copied: false,
                            SaveTransferItemStatus.SkippedBackupFailed,
                            $"Pre-overwrite backup failed, overwrite refused: {backupEx.Message}");
                    }
                }

                string? targetDirectory = Path.GetDirectoryName(pair.TargetFile);

                if (!string.IsNullOrWhiteSpace(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                File.Copy(
                    pair.SourceFile,
                    pair.TargetFile,
                    overwrite: options.OverwriteExisting);

                if (options.PreserveTimestamps)
                {
                    File.SetCreationTimeUtc(
                        pair.TargetFile,
                        File.GetCreationTimeUtc(pair.SourceFile));

                    File.SetLastWriteTimeUtc(
                        pair.TargetFile,
                        File.GetLastWriteTimeUtc(pair.SourceFile));

                    File.SetLastAccessTimeUtc(
                        pair.TargetFile,
                        File.GetLastAccessTimeUtc(pair.SourceFile));
                }

                return new SaveTransferItemResult(
                    previewItem,
                    pair.SourceFile,
                    pair.TargetFile,
                    sourceInfo.Length,
                    Copied: true,
                    SaveTransferItemStatus.Copied,
                    null,
                    BackupFile: backupFile);
            }
            catch (Exception ex)
            {
                return new SaveTransferItemResult(
                    previewItem,
                    pair.SourceFile,
                    pair.TargetFile,
                    0,
                    Copied: false,
                    SaveTransferItemStatus.Failed,
                    ex.Message);
            }
        }

        private static SaveTransferResult BuildResult(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            IReadOnlyList<SaveTransferItemResult> results,
            IReadOnlyList<TransferPreviewWarning> warnings,
            int filesBackedUp = 0,
            string? backupRootPath = null)
        {
            int filesCopied = results.Count(item => item.Copied);

            int filesSkipped = results.Count(item =>
                !item.Copied &&
                item.Status != SaveTransferItemStatus.Unknown);

            long bytesCopied = results
                .Where(item => item.Copied)
                .Sum(item => item.Bytes);

            return new SaveTransferResult(
                plan,
                options.DryRun,
                FilesConsidered: results.Count,
                FilesCopied: filesCopied,
                FilesSkipped: filesSkipped,
                BytesCopied: bytesCopied,
                Items: results,
                Warnings: warnings,
                FilesBackedUp: filesBackedUp,
                BackupRootPath: backupRootPath);
        }

        private sealed record FileTransferPair(
            string SourceFile,
            string TargetFile,
            string? TargetRoot);
    }
}
