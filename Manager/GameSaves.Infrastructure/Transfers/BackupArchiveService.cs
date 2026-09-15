using GameSaves.Core.Transfers;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
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
            CancellationToken cancellationToken)
            => ExportRunAsync(run, destinationFolder, BackupContainerFormat.Zip, BackupCompressionPreset.Optimal, cancellationToken);

        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            BackupContainerFormat format = BackupContainerFormat.Zip,
            BackupCompressionPreset preset = BackupCompressionPreset.Optimal,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ExportRun(run, destinationFolder, format, preset, cancellationToken), cancellationToken);
        }

        public Task<BackupArchiveImportResult> ImportArchiveAsync(
            string archivePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ImportArchive(archivePath, cancellationToken), cancellationToken);
        }

        // ---------------------------------------------------------------
        // Export
        // ---------------------------------------------------------------

        private static BackupArchiveExportResult ExportRun(
            TransferBackupRunInfo run,
            string destinationFolder,
            BackupContainerFormat format,
            BackupCompressionPreset preset,
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

                // Zipping/compressing a folder into itself would try to include the
                // partially written archive.
                if (TransferPathGuard.IsUnderRoot(normalizedDestination, run.BackupRootPath))
                {
                    return new BackupArchiveExportResult(
                        false, null, 0,
                        "The export destination is inside the backup run folder. Choose a destination outside it.");
                }

                string extension = format == BackupContainerFormat.SevenZip ? ".7z" : ".zip";
                string archivePath = Path.Combine(
                    normalizedDestination,
                    Path.GetFileName(run.BackupRootPath) + extension);

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

                var rootDir = new DirectoryInfo(run.BackupRootPath);

                // Following a junction or symlink planted in the run folder would pull
                // files from outside the run into the archive, and a directory cycle
                // would never terminate. The folder-upload path already skips these.
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                if (format == BackupContainerFormat.SevenZip)
                {
                    (CompressionType compressionType, int level) = preset switch
                    {
                        BackupCompressionPreset.Store => (CompressionType.LZMA, 1),
                        BackupCompressionPreset.Fast => (CompressionType.LZMA, 3),
                        BackupCompressionPreset.Optimal => (CompressionType.LZMA2, 6),
                        BackupCompressionPreset.Ultra => (CompressionType.LZMA2, 9),
                        _ => (CompressionType.LZMA2, 6)
                    };

                    var options = new SevenZipWriterOptions(compressionType)
                    {
                        CompressionLevel = level,
                        CompressHeader = true
                    };

                    using (var fs = new FileStream(tempArchivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    using (var writer = new SevenZipWriter(fs, options))
                    {
                        foreach (FileInfo file in rootDir.EnumerateFiles("*", enumeration))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string relativePath = Path.GetRelativePath(run.BackupRootPath, file.FullName).Replace('\\', '/');
                            using FileStream sourceStream = file.OpenRead();
                            writer.Write(relativePath, sourceStream, file.LastWriteTimeUtc);
                        }
                    }
                }
                else
                {
                    CompressionLevel zipLevel = preset switch
                    {
                        BackupCompressionPreset.Store => CompressionLevel.NoCompression,
                        BackupCompressionPreset.Fast => CompressionLevel.Fastest,
                        BackupCompressionPreset.Optimal => CompressionLevel.Optimal,
                        BackupCompressionPreset.Ultra => CompressionLevel.SmallestSize,
                        _ => CompressionLevel.Optimal
                    };

                    using (var fs = new FileStream(tempArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                    {
                        byte[] buffer = new byte[81920];

                        foreach (FileInfo file in rootDir.EnumerateFiles("*", enumeration))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string relativePath = Path.GetRelativePath(run.BackupRootPath, file.FullName).Replace('\\', '/');
                            ZipArchiveEntry entry = archive.CreateEntry(relativePath, zipLevel);

                            using FileStream sourceStream = file.OpenRead();
                            using Stream entryStream = entry.Open();
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

        private BackupArchiveImportResult ImportArchive(string archivePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? stagingDirectory = null;
            bool committed = false;

            try
            {
                if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The selected archive file does not exist.");
                }

                BackupContainerFormat format = _metadataReader.DetectContainerFormat(archivePath);
                if (format != BackupContainerFormat.Zip && format != BackupContainerFormat.SevenZip)
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        $"Unsupported archive format: {format}");
                }

                // The archive must be a backup run: manifest.json at its root. The
                // description has to come from the same file as the payload, so a
                // manifest sitting beside the archive does not count here.
                if (!_metadataReader.TryReadManifest(
                        archivePath,
                        out TransferBackupManifest? manifest,
                        out string? manifestError,
                        allowSidecar: false,
                        cancellationToken))
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        $"This archive is not a valid backup archive: {manifestError}");
                }

                if (manifest is null || manifest.Items.Count == 0)
                {
                    return new BackupArchiveImportResult(
                        false, null, 0,
                        "The archive's manifest is empty or unreadable.");
                }

                string basePath = _backupHistoryService.GetBackupBasePath();

                string runFolderName = TransferBackupLocations.MakeSafeName(
                    Path.GetFileNameWithoutExtension(archivePath));

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

                byte[] buffer = new byte[81920];
                long totalUncompressedBytes = 0;

                if (format == BackupContainerFormat.SevenZip)
                {
                    using var archiveStream = File.OpenRead(archivePath);
                    using IArchive archive = SevenZipArchive.OpenArchive(archiveStream);

                    // Judge the whole archive before writing any of it. Counting inside
                    // the extraction loop let an over-sized archive land MaxFileEntries
                    // files on disk before the bound fired, and the ZIP path already
                    // checks its count up front.
                    if (!TryValidateDeclaredBounds(
                            archive.Entries.Select(e => (e.IsDirectory, e.Size)),
                            cancellationToken,
                            out string? boundsError))
                    {
                        return new BackupArchiveImportResult(false, null, 0, boundsError!);
                    }

                    foreach (IArchiveEntry entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (entry.Key is null)
                            continue;

                        if (!ValidateArchiveEntryPath(entry.Key, stagingDirectory, out string destinationPath, out string? entryError))
                        {
                            return new BackupArchiveImportResult(
                                false, null, 0,
                                $"Archive extraction rejected due to security policy: {entryError}");
                        }

                        if (entry.IsDirectory || entry.Key.EndsWith('/') || entry.Key.EndsWith('\\'))
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
                        using Stream entryStream = entry.OpenEntryStream();
                        using var destStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

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
                                    $"Archive entry '{entry.Key}' exceeds maximum allowed single file size ({_safetyBounds.MaxSingleFileBytes:N0} bytes).");
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
                else
                {
                    using ZipArchive archive = ZipFile.OpenRead(archivePath);

                    if (!TryValidateDeclaredBounds(
                            archive.Entries.Select(e => (
                                e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'),
                                e.Length)),
                            cancellationToken,
                            out string? boundsError))
                    {
                        return new BackupArchiveImportResult(false, null, 0, boundsError!);
                    }

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
                        using Stream entryStream = entry.Open();
                        using var destStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

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

        /// <summary>
        /// Checks the entry count and the sizes the archive declares for itself before
        /// a single byte is written. The per-chunk accounting during extraction still
        /// catches an archive that lies about its sizes; this stops the honest bomb
        /// from ever touching the disk, and stops a huge entry list from being written
        /// out one file at a time before the count is judged.
        /// </summary>
        private bool TryValidateDeclaredBounds(
            IEnumerable<(bool IsDirectory, long Size)> entries,
            CancellationToken cancellationToken,
            out string? error)
        {
            error = null;

            int count = 0;
            long declaredTotal = 0;

            foreach ((bool isDirectory, long size) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                count++;
                if (count > _safetyBounds.MaxFileEntries)
                {
                    error = $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).";
                    return false;
                }

                if (isDirectory || size <= 0)
                    continue;

                // Same wording as the streaming guard below: which of the two caught
                // it is an implementation detail the user should not have to read.
                if (size > _safetyBounds.MaxSingleFileBytes)
                {
                    error = $"An archive entry exceeds maximum allowed single file size ({_safetyBounds.MaxSingleFileBytes:N0} bytes).";
                    return false;
                }

                declaredTotal += size;

                if (declaredTotal > _safetyBounds.MaxTotalUncompressedBytes)
                {
                    error = $"Archive uncompressed payload exceeds maximum allowed size ({_safetyBounds.MaxTotalUncompressedBytes:N0} bytes).";
                    return false;
                }
            }

            return true;
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
