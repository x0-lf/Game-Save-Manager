using GameSave.Data;
using GameSave.SavePaths;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace GameSave.Backup
{
    public sealed class BackupManager
    {
        private readonly SavePathDatabase _database;

        public BackupManager(SavePathDatabase database)
        {
            _database = database;
        }

        public List<BackupItemResult> BackupVerifiedPaths(
            IEnumerable<SavePathVerificationResult> verifiedPaths,
            string destinationRoot,
            bool dryRun,
            bool computeHashes)
        {
            if (!dryRun)
                Directory.CreateDirectory(destinationRoot);

            long backupRunId = _database.CreateBackupRun(destinationRoot, dryRun);

            var results = new List<BackupItemResult>();
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            foreach (SavePathVerificationResult verified in verifiedPaths)
            {
                if (!verified.Exists || verified.Confidence < 60)
                    continue;

                List<BackupItemResult> items = BackupOneVerifiedPath(
                    verified,
                    destinationRoot,
                    timestamp,
                    dryRun,
                    computeHashes);

                foreach (BackupItemResult item in items)
                {
                    results.Add(item);

                    _database.SaveBackupItem(
                        backupRunId,
                        item.SteamAppId,
                        item.GameName,
                        item.SourcePath,
                        item.DestinationPath,
                        item.Copied,
                        item.Bytes,
                        item.Sha256,
                        item.Error);
                }
            }

            _database.CompleteBackupRun(
                backupRunId,
                results.Count,
                results.Sum(item => item.Bytes));

            return results;
        }

        private static List<BackupItemResult> BackupOneVerifiedPath(
            SavePathVerificationResult verified,
            string destinationRoot,
            string timestamp,
            bool dryRun,
            bool computeHashes)
        {
            var results = new List<BackupItemResult>();

            if (File.Exists(verified.NormalizedPath))
            {
                BackupItemResult item = BackupFile(
                    verified,
                    verified.NormalizedPath,
                    Path.GetDirectoryName(verified.NormalizedPath) ?? verified.NormalizedPath,
                    destinationRoot,
                    timestamp,
                    dryRun,
                    computeHashes);

                results.Add(item);
                return results;
            }

            if (!Directory.Exists(verified.NormalizedPath))
                return results;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            try
            {
                foreach (string file in Directory.EnumerateFiles(verified.NormalizedPath, "*", options))
                {
                    BackupItemResult item = BackupFile(
                        verified,
                        file,
                        verified.NormalizedPath,
                        destinationRoot,
                        timestamp,
                        dryRun,
                        computeHashes);

                    results.Add(item);
                }
            }
            catch (Exception ex)
            {
                results.Add(new BackupItemResult(
                    verified.SteamAppId,
                    verified.GameName,
                    verified.NormalizedPath,
                    string.Empty,
                    false,
                    0,
                    null,
                    ex.Message));
            }

            return results;
        }

        private static BackupItemResult BackupFile(
            SavePathVerificationResult verified,
            string sourceFile,
            string sourceRoot,
            string destinationRoot,
            string timestamp,
            bool dryRun,
            bool computeHashes)
        {
            try
            {
                var sourceInfo = new FileInfo(sourceFile);

                string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
                string safeGameName = MakeSafeFileName(verified.GameName);
                string safeSourceRoot = MakeSafeFileName(sourceRoot);

                string destinationFile = Path.Combine(
                    destinationRoot,
                    $"{verified.SteamAppId}_{safeGameName}",
                    timestamp,
                    safeSourceRoot,
                    relativePath);

                string? destinationDirectory = Path.GetDirectoryName(destinationFile);

                string? sha256 = null;

                if (computeHashes)
                    sha256 = ComputeSha256(sourceFile);

                if (!dryRun)
                {
                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    File.Copy(sourceFile, destinationFile, overwrite: true);

                    File.SetCreationTimeUtc(destinationFile, File.GetCreationTimeUtc(sourceFile));
                    File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));
                    File.SetAttributes(destinationFile, File.GetAttributes(sourceFile));
                }

                return new BackupItemResult(
                    verified.SteamAppId,
                    verified.GameName,
                    sourceFile,
                    destinationFile,
                    !dryRun,
                    sourceInfo.Length,
                    sha256,
                    null);
            }
            catch (Exception ex)
            {
                return new BackupItemResult(
                    verified.SteamAppId,
                    verified.GameName,
                    sourceFile,
                    string.Empty,
                    false,
                    0,
                    null,
                    ex.Message);
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }

        private static string MakeSafeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();

            string cleaned = new string(
                value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            return string.IsNullOrWhiteSpace(cleaned)
                ? "Unknown"
                : cleaned;
        }
    }
}