using GameSaves.Core.Transfers;
using System.Security.Cryptography;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Restores one backup run to its original locations. Copy-only, gated by
    /// explicit confirmation, integrity-checked against the manifest SHA-256,
    /// and protected by Safe Mode: a current file is only overwritten after it
    /// has itself been backed up.
    /// </summary>
    public sealed class BackupRestoreService : IBackupRestoreService
    {
        private readonly ITransferOverwriteBackupService _overwriteBackupService;

        public BackupRestoreService(ITransferOverwriteBackupService overwriteBackupService)
        {
            _overwriteBackupService = overwriteBackupService;
        }

        public Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Restore(run, options, cancellationToken),
                cancellationToken);
        }

        private BackupRestoreResult Restore(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>();
            var results = new List<BackupRestoreItemResult>();

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Restore was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return BuildResult(run, options, results, warnings);
            }

            if (run.Manifest.Items.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoItems",
                    "Restore was blocked because the backup run contains no files.",
                    TransferWarningSeverity.Error));

                return BuildResult(run, options, results, warnings);
            }

            ITransferOverwriteBackupSession? preRestoreSession = null;

            ITransferOverwriteBackupSession GetPreRestoreSession() =>
                preRestoreSession ??= _overwriteBackupService.BeginSession(
                    new OverwriteBackupContext(
                        Kind: OverwriteBackupContext.RestoreKind,
                        Game: run.Manifest.Game,
                        SteamAppId: run.Manifest.SteamAppId,
                        SourceAccountId: run.Manifest.SourceAccountId,
                        TargetAccountId: run.Manifest.TargetAccountId));

            try
            {
                foreach (TransferOverwriteBackupItem backupItem in run.Manifest.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    results.Add(RestoreOneFile(
                        backupItem,
                        options,
                        GetPreRestoreSession));
                }
            }
            finally
            {
                preRestoreSession?.Complete();
            }

            if (preRestoreSession is not null && preRestoreSession.FilesBackedUp > 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "PreRestoreBackups",
                    $"Backed up {preRestoreSession.FilesBackedUp} current file(s) before restoring over them to: {preRestoreSession.BackupRootPath}",
                    TransferWarningSeverity.Info));
            }

            return BuildResult(
                run,
                options,
                results,
                warnings,
                preRestoreSession?.FilesBackedUp ?? 0,
                preRestoreSession?.FilesBackedUp > 0 ? preRestoreSession.BackupRootPath : null);
        }

        private static BackupRestoreItemResult RestoreOneFile(
            TransferOverwriteBackupItem backupItem,
            BackupRestoreOptions options,
            Func<ITransferOverwriteBackupSession> getPreRestoreSession)
        {
            string backupFile = backupItem.BackupFile;
            string targetFile = backupItem.OriginalFile;

            try
            {
                if (!File.Exists(backupFile))
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        0,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedBackupMissing,
                        "The backup file no longer exists.");
                }

                if (options.VerifyHashes &&
                    !ComputeSha256(backupFile).Equals(backupItem.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        0,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedHashMismatch,
                        "The backup file does not match the SHA-256 recorded in the manifest. It may be corrupted and was not restored.");
                }

                bool targetExists = File.Exists(targetFile);
                long backupBytes = new FileInfo(backupFile).Length;

                if (targetExists &&
                    ComputeSha256(targetFile).Equals(backupItem.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        backupBytes,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedAlreadyMatches,
                        "The current file already has the backed-up content.");
                }

                if (targetExists && !options.OverwriteExisting)
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        backupBytes,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedTargetExists,
                        "The current file exists and differs from the backup. Overwrite is disabled.");
                }

                if (options.DryRun)
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        backupBytes,
                        Restored: false,
                        BackupRestoreItemStatus.DryRun,
                        null);
                }

                string? preRestoreBackupFile = null;

                if (targetExists && options.BackupBeforeOverwrite)
                {
                    try
                    {
                        preRestoreBackupFile =
                            getPreRestoreSession().BackUpFile(targetFile).BackupFile;
                    }
                    catch (Exception backupEx)
                    {
                        // Safe Mode: never restore over a file that could not be backed up.
                        return new BackupRestoreItemResult(
                            backupItem,
                            targetFile,
                            backupBytes,
                            Restored: false,
                            BackupRestoreItemStatus.SkippedBackupFailed,
                            $"Pre-restore backup failed, restore refused: {backupEx.Message}");
                    }
                }

                string? targetDirectory = Path.GetDirectoryName(targetFile);

                if (!string.IsNullOrWhiteSpace(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                File.Copy(backupFile, targetFile, overwrite: targetExists);

                if (options.PreserveTimestamps)
                {
                    File.SetCreationTimeUtc(targetFile, File.GetCreationTimeUtc(backupFile));
                    File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(backupFile));
                }

                return new BackupRestoreItemResult(
                    backupItem,
                    targetFile,
                    backupBytes,
                    Restored: true,
                    BackupRestoreItemStatus.Restored,
                    null,
                    PreRestoreBackupFile: preRestoreBackupFile);
            }
            catch (Exception ex)
            {
                return new BackupRestoreItemResult(
                    backupItem,
                    targetFile,
                    0,
                    Restored: false,
                    BackupRestoreItemStatus.Failed,
                    ex.Message);
            }
        }

        private static BackupRestoreResult BuildResult(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            IReadOnlyList<BackupRestoreItemResult> results,
            IReadOnlyList<TransferPreviewWarning> warnings,
            int filesBackedUp = 0,
            string? backupRootPath = null)
        {
            int filesRestored = results.Count(item => item.Restored);

            int filesSkipped = results.Count(item =>
                !item.Restored &&
                item.Status != BackupRestoreItemStatus.Unknown);

            long bytesRestored = results
                .Where(item => item.Restored)
                .Sum(item => item.Bytes);

            return new BackupRestoreResult(
                Run: run,
                DryRun: options.DryRun,
                FilesConsidered: results.Count,
                FilesRestored: filesRestored,
                FilesSkipped: filesSkipped,
                BytesRestored: bytesRestored,
                Items: results,
                Warnings: warnings,
                FilesBackedUp: filesBackedUp,
                BackupRootPath: backupRootPath);
        }

        private static string ComputeSha256(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
    }
}
