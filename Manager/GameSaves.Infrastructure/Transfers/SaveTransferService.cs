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
        public Task<SaveTransferResult> ExecuteAsync(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Execute(plan, options, cancellationToken),
                cancellationToken);
        }

        private static SaveTransferResult Execute(
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

            foreach (TransferPreviewItem previewItem in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (FileTransferPair pair in EnumerateFilePairs(previewItem))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SaveTransferItemResult result = TransferOneFile(
                        previewItem,
                        pair,
                        options);

                    results.Add(result);
                }
            }

            return BuildResult(plan, options, results, warnings);
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

            // Re-validate userdata containment at execution time. The plan may
            // be stale, and preview-time checks must never be the only defense.
            foreach (TransferPreviewItem item in plan.Items)
            {
                if (item.SourceType != TransferSourceType.SteamUserDataGameFolder)
                    continue;

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
                    return new TransferPreviewWarning(
                        "PathContainmentViolation",
                        $"Copy was blocked: a userdata game-folder path failed containment checks. Source: {item.SourceRoot} Target: {item.TargetRoot}",
                        TransferWarningSeverity.Error);
                }

                if (TransferPathGuard.PathsEqual(item.SourceRoot, item.TargetRoot))
                {
                    return new TransferPreviewWarning(
                        "SamePath",
                        $"Copy was blocked: userdata source and target resolve to the same folder: {item.SourceRoot}",
                        TransferWarningSeverity.Error);
                }
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
            SaveTransferOptions options)
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
                    null);
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
            IReadOnlyList<TransferPreviewWarning> warnings)
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
                Warnings: warnings);
        }

        private sealed record FileTransferPair(
            string SourceFile,
            string TargetFile,
            string? TargetRoot);
    }
}
