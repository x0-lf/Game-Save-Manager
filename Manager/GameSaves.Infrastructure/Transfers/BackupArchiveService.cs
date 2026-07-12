using GameSaves.Core.Transfers;
using System.IO.Compression;
using System.Text.Json;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class BackupArchiveService : IBackupArchiveService
    {
        private readonly IBackupHistoryService _backupHistoryService;

        public BackupArchiveService(IBackupHistoryService backupHistoryService)
        {
            _backupHistoryService = backupHistoryService;
        }

        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ExportRun(run, destinationFolder), cancellationToken);
        }

        public Task<BackupArchiveImportResult> ImportArchiveAsync(
            string zipPath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ImportArchive(zipPath), cancellationToken);
        }

        // ---------------------------------------------------------------
        // Export
        // ---------------------------------------------------------------

        private static BackupArchiveExportResult ExportRun(
            TransferBackupRunInfo run,
            string destinationFolder)
        {
            try
            {
                if (!Directory.Exists(run.BackupRootPath) ||
                    !File.Exists(run.ManifestPath))
                {
                    return new BackupArchiveExportResult(
                        false, null, 0,
                        "The backup run folder or its manifest no longer exists.");
                }

                string? normalizedDestination = TransferPathGuard.TryNormalize(destinationFolder);

                if (normalizedDestination is null)
                {
                    return new BackupArchiveExportResult(
                        false, null, 0,
                        "The export destination is empty or not a valid folder path.");
                }

                // Zipping a folder into itself would try to include the
                // partially written archive.
                if (TransferPathGuard.IsUnderRoot(normalizedDestination, run.BackupRootPath))
                {
                    return new BackupArchiveExportResult(
                        false, null, 0,
                        "The export destination is inside the backup run folder. Choose a destination outside it.");
                }

                string archivePath = Path.Combine(
                    normalizedDestination,
                    Path.GetFileName(run.BackupRootPath) + ".zip");

                if (File.Exists(archivePath))
                {
                    return new BackupArchiveExportResult(
                        false, archivePath, 0,
                        $"An archive with this name already exists and is never overwritten: {archivePath}");
                }

                Directory.CreateDirectory(normalizedDestination);

                ZipFile.CreateFromDirectory(
                    run.BackupRootPath,
                    archivePath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);

                long bytes = new FileInfo(archivePath).Length;

                return new BackupArchiveExportResult(
                    true, archivePath, bytes,
                    $"Backup run exported to: {archivePath}");
            }
            catch (Exception ex)
            {
                return new BackupArchiveExportResult(
                    false, null, 0,
                    $"Export failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------
        // Import
        // ---------------------------------------------------------------

        private BackupArchiveImportResult ImportArchive(string zipPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The selected archive file does not exist.");
                }

                // The archive must be a backup run: manifest.json at its root.
                TransferBackupManifest? manifest;

                using (ZipArchive probe = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry? manifestEntry = probe.GetEntry(
                        TransferBackupLocations.ManifestFileName);

                    if (manifestEntry is null)
                    {
                        return new BackupArchiveImportResult(
                            false, null, 0,
                            "This ZIP is not a backup archive: it has no manifest.json at its root.");
                    }

                    using Stream stream = manifestEntry.Open();
                    manifest = JsonSerializer.Deserialize<TransferBackupManifest>(stream);
                }

                if (manifest is null || manifest.Items.Count == 0)
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The archive's manifest is empty or unreadable.");
                }

                string basePath = _backupHistoryService.GetBackupBasePath();

                string runFolderName = TransferBackupLocations.MakeSafeName(
                    Path.GetFileNameWithoutExtension(zipPath));

                string targetRoot = Path.Combine(basePath, runFolderName);

                if (!TransferPathGuard.IsStrictlyUnderRoot(targetRoot, basePath))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The archive name does not produce a valid folder inside the backup base.");
                }

                if (Directory.Exists(targetRoot))
                {
                    return new BackupArchiveImportResult(
                        false, targetRoot, 0,
                        $"A backup run folder with this name already exists and is never overwritten: {targetRoot}");
                }

                // .NET's ExtractToDirectory refuses entries that escape the
                // target directory (zip-slip).
                ZipFile.ExtractToDirectory(zipPath, targetRoot, overwriteFiles: false);

                // The manifest records absolute backup-file paths from the
                // machine/location the backup was created on. Rewrite them to
                // the extracted location so the run is restorable here, and
                // verify every rewritten path against the extracted files.
                if (!TryRewriteManifestPaths(manifest, targetRoot, out TransferBackupManifest rewritten))
                {
                    return new BackupArchiveImportResult(
                        false, targetRoot, 0,
                        "The archive was extracted, but its manifest paths could not be matched to the extracted files. The run may not be restorable; it was left in place for inspection.");
                }

                File.WriteAllText(
                    Path.Combine(targetRoot, TransferBackupLocations.ManifestFileName),
                    JsonSerializer.Serialize(
                        rewritten,
                        new JsonSerializerOptions { WriteIndented = true }));

                return new BackupArchiveImportResult(
                    true, targetRoot, rewritten.Items.Count,
                    $"Backup archive imported. It now appears in the backup history and can be restored: {targetRoot}");
            }
            catch (Exception ex)
            {
                return new BackupArchiveImportResult(
                    false, null, 0,
                    $"Import failed: {ex.Message}");
            }
        }

        // The original run root is not stored in the manifest, but every
        // backup-file path is <oldRoot>\files\<mirror>. Try each prefix that
        // ends right before a "files" segment; the correct one makes every
        // item's relative path point at an existing extracted file.
        private static bool TryRewriteManifestPaths(
            TransferBackupManifest manifest,
            string targetRoot,
            out TransferBackupManifest rewritten)
        {
            rewritten = manifest;

            string firstPath = manifest.Items[0].BackupFile;
            string[] segments = firstPath.Split(Path.DirectorySeparatorChar);

            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (!segments[i].Equals("files", StringComparison.OrdinalIgnoreCase))
                    continue;

                int prefixLength = segments.Take(i)
                    .Sum(segment => segment.Length + 1);

                if (!TryRewriteWithPrefix(manifest, prefixLength, targetRoot, out rewritten))
                    continue;

                return true;
            }

            return false;
        }

        private static bool TryRewriteWithPrefix(
            TransferBackupManifest manifest,
            int prefixLength,
            string targetRoot,
            out TransferBackupManifest rewritten)
        {
            rewritten = manifest;

            var newItems = new List<TransferOverwriteBackupItem>(manifest.Items.Count);

            foreach (TransferOverwriteBackupItem item in manifest.Items)
            {
                if (item.BackupFile.Length <= prefixLength)
                    return false;

                string relative = item.BackupFile[prefixLength..];

                if (!relative.StartsWith("files" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string newBackupFile = Path.Combine(targetRoot, relative);

                if (!File.Exists(newBackupFile))
                    return false;

                newItems.Add(item with { BackupFile = newBackupFile });
            }

            rewritten = manifest with { Items = newItems };
            return true;
        }
    }
}
