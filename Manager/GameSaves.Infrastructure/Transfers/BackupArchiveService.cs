using GameSaves.Core.Transfers;
using System.IO.Compression;
using System.Text.Json;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class BackupArchiveService : IBackupArchiveService
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly IBackupMetadataReader _metadataReader;
        private readonly BackupArchiveSafetyBounds _safetyBounds;

        public BackupArchiveService(
            IBackupHistoryService backupHistoryService,
            IBackupMetadataReader? metadataReader = null,
            BackupArchiveSafetyBounds? safetyBounds = null)
        {
            _backupHistoryService = backupHistoryService;
            _safetyBounds = safetyBounds ?? BackupArchiveSafetyBounds.Default;
            _metadataReader = metadataReader ?? new BackupMetadataReader(_safetyBounds);
        }

        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ExportRun(run, destinationFolder, cancellationToken), cancellationToken);
        }

        public Task<BackupArchiveImportResult> ImportArchiveAsync(
            string zipPath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ImportArchive(zipPath, cancellationToken), cancellationToken);
        }

        // ---------------------------------------------------------------
        // Export
        // ---------------------------------------------------------------

        private static BackupArchiveExportResult ExportRun(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? tempArchivePath = null;
            bool committed = false;

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

                tempArchivePath = Path.Combine(
                    normalizedDestination,
                    ".export_" + Guid.NewGuid().ToString("N") + ".tmp");

                cancellationToken.ThrowIfCancellationRequested();

                using (var fs = new FileStream(tempArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var rootDir = new DirectoryInfo(run.BackupRootPath);
                    byte[] buffer = new byte[81920];

                    foreach (FileInfo file in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string relativePath = Path.GetRelativePath(run.BackupRootPath, file.FullName).Replace('\\', '/');
                        ZipArchiveEntry entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                        using (FileStream sourceStream = file.OpenRead())
                        using (Stream entryStream = entry.Open())
                        {
                            int bytesRead;
                            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                entryStream.Write(buffer, 0, bytesRead);
                            }
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                File.Move(tempArchivePath, archivePath);
                committed = true;

                long bytes = new FileInfo(archivePath).Length;

                return new BackupArchiveExportResult(
                    true, archivePath, bytes,
                    $"Backup run exported to: {archivePath}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BackupArchiveExportResult(
                    false, null, 0,
                    $"Export failed: {ex.Message}");
            }
            finally
            {
                if (!committed && tempArchivePath is not null && File.Exists(tempArchivePath))
                {
                    try
                    {
                        File.Delete(tempArchivePath);
                    }
                    catch
                    {
                        // Best-effort cleanup
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        // Import
        // ---------------------------------------------------------------

        private BackupArchiveImportResult ImportArchive(string zipPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? stagingDirectory = null;
            bool committed = false;

            try
            {
                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The selected archive file does not exist.");
                }

                // The archive must be a backup run: manifest.json at its root.
                if (!_metadataReader.TryReadManifest(zipPath, out TransferBackupManifest? manifest, out string? manifestError))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        $"This ZIP is not a valid backup archive: {manifestError}");
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

                stagingDirectory = Path.Combine(basePath, ".staging_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingDirectory);

                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    if (archive.Entries.Count > _safetyBounds.MaxFileEntries)
                    {
                        return new BackupArchiveImportResult(
                            false, null, 0,
                            $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).");
                    }

                    long totalUncompressedBytes = 0;
                    byte[] buffer = new byte[81920];

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!ValidateArchiveEntryPath(entry.FullName, stagingDirectory, out string destinationPath, out string? entryError))
                        {
                            return new BackupArchiveImportResult(
                                false, null, 0,
                                $"Archive extraction rejected due to security policy: {entryError}");
                        }

                        bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                        if (isDirectory)
                        {
                            Directory.CreateDirectory(destinationPath);
                            continue;
                        }

                        string? parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        long entryBytes = 0;
                        using (Stream entryStream = entry.Open())
                        using (var destStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            int bytesRead;
                            while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                entryBytes += bytesRead;
                                totalUncompressedBytes += bytesRead;

                                if (entryBytes > _safetyBounds.MaxSingleFileBytes)
                                {
                                    return new BackupArchiveImportResult(
                                        false, null, 0,
                                        $"Archive entry '{entry.FullName}' exceeds maximum allowed single file size ({_safetyBounds.MaxSingleFileBytes:N0} bytes).");
                                }

                                if (totalUncompressedBytes > _safetyBounds.MaxTotalUncompressedBytes)
                                {
                                    return new BackupArchiveImportResult(
                                        false, null, 0,
                                        $"Archive uncompressed payload exceeds maximum allowed size ({_safetyBounds.MaxTotalUncompressedBytes:N0} bytes).");
                                }

                                destStream.Write(buffer, 0, bytesRead);
                            }
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // The manifest records absolute backup-file paths from the
                // machine/location the backup was created on. Rewrite them to
                // the extracted location so the run is restorable here, and
                // verify every rewritten path against the extracted files in staging.
                if (!BackupManifestPathRewriter.TryRewrite(manifest, targetRoot, out TransferBackupManifest rewritten, stagingDirectory))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The archive was extracted, but its manifest paths could not be matched to the extracted files. The run was rolled back.");
                }

                string stagedManifestPath = Path.Combine(stagingDirectory, TransferBackupLocations.ManifestFileName);
                File.WriteAllText(
                    stagedManifestPath,
                    JsonSerializer.Serialize(
                        rewritten,
                        new JsonSerializerOptions { WriteIndented = true }));

                cancellationToken.ThrowIfCancellationRequested();

                // Atomic commit: rename staging directory to final targetRoot
                Directory.Move(stagingDirectory, targetRoot);
                committed = true;

                return new BackupArchiveImportResult(
                    true, targetRoot, rewritten.Items.Count,
                    $"Backup archive imported. It now appears in the backup history and can be restored: {targetRoot}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BackupArchiveImportResult(
                    false, null, 0,
                    $"Import failed: {ex.Message}");
            }
            finally
            {
                if (!committed && stagingDirectory is not null && Directory.Exists(stagingDirectory))
                {
                    try
                    {
                        Directory.Delete(stagingDirectory, recursive: true);
                    }
                    catch
                    {
                        // Best-effort cleanup
                    }
                }
            }
        }

        private static bool ValidateArchiveEntryPath(
            string entryFullName,
            string stagingDirectory,
            out string destinationPath,
            out string? error)
        {
            destinationPath = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(entryFullName))
            {
                error = "Archive contains an entry with an empty or whitespace name.";
                return false;
            }

            // Reject rooted entries (e.g. /foo, \foo, or drive letter C:\)
            if (entryFullName.StartsWith('/') || entryFullName.StartsWith('\\') || Path.IsPathRooted(entryFullName))
            {
                error = $"Archive contains an invalid rooted entry: {entryFullName}";
                return false;
            }

            if (entryFullName.Length >= 2 && char.IsLetter(entryFullName[0]) && entryFullName[1] == ':')
            {
                error = $"Archive contains an invalid drive-specified entry: {entryFullName}";
                return false;
            }

            // Inspect individual segments for traversal, invalid characters, or invalid dot/space endings
            string[] segments = entryFullName.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];

                // For trailing slash/backslash on directory entries, the last segment will be empty, which is allowed.
                if (i == segments.Length - 1 && string.IsNullOrEmpty(segment))
                    continue;

                if (string.IsNullOrEmpty(segment))
                {
                    error = $"Archive entry '{entryFullName}' contains consecutive directory separators.";
                    return false;
                }

                if (segment is "." or "..")
                {
                    error = $"Archive entry '{entryFullName}' contains path traversal segment '{segment}'.";
                    return false;
                }

                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    error = $"Archive entry '{entryFullName}' contains invalid characters in segment '{segment}'.";
                    return false;
                }

                if (segment != segment.TrimEnd('.', ' '))
                {
                    error = $"Archive entry '{entryFullName}' contains invalid trailing dots or spaces in segment '{segment}'.";
                    return false;
                }
            }

            // Path containment validation
            string normalizedStaging = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                destinationPath = Path.GetFullPath(Path.Combine(normalizedStaging, entryFullName));
            }
            catch (Exception ex)
            {
                error = $"Archive entry '{entryFullName}' cannot be resolved: {ex.Message}";
                return false;
            }

            bool isDirectory = entryFullName.EndsWith('/') || entryFullName.EndsWith('\\');
            if (isDirectory)
            {
                if (!destinationPath.StartsWith(normalizedStaging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !destinationPath.Equals(normalizedStaging, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Archive directory entry '{entryFullName}' escapes the staging directory.";
                    return false;
                }
            }
            else
            {
                if (!destinationPath.StartsWith(normalizedStaging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Archive file entry '{entryFullName}' escapes the staging directory.";
                    return false;
                }
            }

            return true;
        }
    }
}
