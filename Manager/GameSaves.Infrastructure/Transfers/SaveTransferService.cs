using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Transfers
{
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

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Real transfer was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return BuildResult(plan, options, results, warnings);
            }

            if (plan.HasErrors)
            {
                warnings.Add(new TransferPreviewWarning(
                    "PlanHasErrors",
                    "Transfer was blocked because the preview plan contains errors.",
                    TransferWarningSeverity.Error));

                return BuildResult(plan, options, results, warnings);
            }

            if (plan.SourceProfile.AccountId.Equals(
                    plan.TargetProfile.AccountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new TransferPreviewWarning(
                    "SameProfile",
                    "Transfer was blocked because source and target profiles are the same.",
                    TransferWarningSeverity.Error));

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

        private static IEnumerable<FileTransferPair> EnumerateFilePairs(
            TransferPreviewItem item)
        {
            if (string.IsNullOrWhiteSpace(item.SourcePath) ||
                string.IsNullOrWhiteSpace(item.TargetPath))
            {
                yield break;
            }

            if (File.Exists(item.SourcePath))
            {
                string targetFile = Directory.Exists(item.TargetPath)
                    ? Path.Combine(item.TargetPath, Path.GetFileName(item.SourcePath))
                    : item.TargetPath;

                yield return new FileTransferPair(
                    item.SourcePath,
                    targetFile);

                yield break;
            }

            if (!Directory.Exists(item.SourcePath))
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
                files = Directory.EnumerateFiles(
                    item.SourcePath,
                    "*",
                    options);
            }
            catch
            {
                yield break;
            }

            foreach (string sourceFile in files)
            {
                string relativePath = Path.GetRelativePath(
                    item.SourcePath,
                    sourceFile);

                string targetFile = Path.Combine(
                    item.TargetPath,
                    relativePath);

                yield return new FileTransferPair(
                    sourceFile,
                    targetFile);
            }
        }

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

                if (PathsEqual(pair.SourceFile, pair.TargetFile))
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

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                string normalizedLeft = Path.GetFullPath(left)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string normalizedRight = Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return normalizedLeft.Equals(
                    normalizedRight,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return left.Equals(right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed record FileTransferPair(
            string SourceFile,
            string TargetFile);
    }
}