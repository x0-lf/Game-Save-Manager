using GameSaves.Core.Transfers;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Implements zero-extraction metadata and manifest reading across backup container formats.
    /// Uncompressed folders, ZIPs, and 7z archives share identical catalog models.
    /// </summary>
    public sealed class BackupMetadataReader : IBackupMetadataReader
    {
        private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        private readonly BackupArchiveSafetyBounds _safetyBounds;

        public BackupMetadataReader(BackupArchiveSafetyBounds? safetyBounds = null)
        {
            _safetyBounds = safetyBounds ?? BackupArchiveSafetyBounds.Default;
        }

        public BackupContainerFormat DetectContainerFormat(string path) => DetectFormat(path);

        public static BackupContainerFormat DetectFormat(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return BackupContainerFormat.Folder;

            if (Directory.Exists(path))
                return BackupContainerFormat.Folder;

            if (File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Span<byte> buffer = stackalloc byte[6];
                    int bytesRead = stream.Read(buffer);

                    if (bytesRead >= 6 && buffer[..6].SequenceEqual(SevenZipSignature))
                    {
                        return BackupContainerFormat.SevenZip;
                    }

                    if (bytesRead >= 4 &&
                        buffer[0] == 0x50 && buffer[1] == 0x4B &&
                        ((buffer[2] == 0x03 && buffer[3] == 0x04) ||
                         (buffer[2] == 0x05 && buffer[3] == 0x06) ||
                         (buffer[2] == 0x07 && buffer[3] == 0x08)))
                    {
                        return BackupContainerFormat.Zip;
                    }
                }
                catch
                {
                    // Fallback to extension check
                }
            }

            string ext = Path.GetExtension(path);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return BackupContainerFormat.Zip;

            if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                return BackupContainerFormat.SevenZip;

            return BackupContainerFormat.Folder;
        }

        public bool TryReadManifest(
            string path,
            out TransferBackupManifest? manifest,
            out string? error,
            bool allowSidecar = true,
            CancellationToken cancellationToken = default)
        {
            return TryReadManifest(path, out manifest, out error, out _, allowSidecar, cancellationToken);
        }

        public bool TryReadManifest(
            string path,
            out TransferBackupManifest? manifest,
            out string? error,
            out bool isSidecar,
            bool allowSidecar = true,
            CancellationToken cancellationToken = default)
        {
            manifest = null;
            error = null;
            isSidecar = false;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Target path is empty.";
                return false;
            }

            BackupContainerFormat format = DetectContainerFormat(path);

            try
            {
                switch (format)
                {
                    case BackupContainerFormat.Folder:
                    {
                        if (!Directory.Exists(path))
                        {
                            error = $"Backup directory does not exist: {path}";
                            return false;
                        }

                        string manifestPath = Path.Combine(path, TransferBackupLocations.ManifestFileName);
                        if (!File.Exists(manifestPath))
                        {
                            error = $"Backup directory does not contain {TransferBackupLocations.ManifestFileName}: {manifestPath}";
                            return false;
                        }

                        var fi = new FileInfo(manifestPath);
                        if (fi.Length > _safetyBounds.MaxManifestBytes)
                        {
                            error = $"Manifest file exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                            return false;
                        }

                        string json = File.ReadAllText(manifestPath);
                        manifest = JsonSerializer.Deserialize<TransferBackupManifest>(json);

                        if (manifest is null)
                        {
                            error = "Failed to deserialize manifest.json (content was null or invalid JSON).";
                            return false;
                        }

                        return manifest.TryValidate(out error);
                    }

                    case BackupContainerFormat.Zip:
                    {
                        if (!File.Exists(path))
                        {
                            error = $"ZIP archive file does not exist: {path}";
                            return false;
                        }

                        string sidecarManifest = path + ".manifest.json";
                        if (allowSidecar && File.Exists(sidecarManifest))
                        {
                            var sidecarFi = new FileInfo(sidecarManifest);
                            if (sidecarFi.Length > _safetyBounds.MaxManifestBytes)
                            {
                                error = $"Manifest file exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                                return false;
                            }

                            string json = File.ReadAllText(sidecarManifest);
                            manifest = JsonSerializer.Deserialize<TransferBackupManifest>(json);
                            if (manifest is not null && manifest.TryValidate(out error))
                            {
                                isSidecar = true;
                                return true;
                            }
                        }

                        using ZipArchive archive = ZipFile.OpenRead(path);
                        if (archive.Entries.Count > _safetyBounds.MaxFileEntries)
                        {
                            error = $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).";
                            return false;
                        }

                        // Root only. Matching on the file name alone accepted a manifest
                        // at any depth on a first-match-wins basis, so an archive could
                        // carry one description at the root and have a different one chosen.
                        ZipArchiveEntry? entry = archive.GetEntry(TransferBackupLocations.ManifestFileName)
                            ?? archive.Entries.FirstOrDefault(e =>
                                e.FullName.Equals(TransferBackupLocations.ManifestFileName, StringComparison.OrdinalIgnoreCase));

                        if (entry is null)
                        {
                            error = $"ZIP archive does not contain a root {TransferBackupLocations.ManifestFileName}.";
                            return false;
                        }

                        if (entry.Length > _safetyBounds.MaxManifestBytes)
                        {
                            error = $"Archive manifest exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                            return false;
                        }

                        using Stream stream = entry.Open();
                        using var memoryStream = new MemoryStream();
                        byte[] buffer = new byte[81920];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalRead += bytesRead;
                            if (totalRead > _safetyBounds.MaxManifestBytes)
                            {
                                error = $"Archive manifest exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                                return false;
                            }
                            memoryStream.Write(buffer, 0, bytesRead);
                        }

                        memoryStream.Position = 0;
                        manifest = JsonSerializer.Deserialize<TransferBackupManifest>(memoryStream);

                        if (manifest is null)
                        {
                            error = "Failed to deserialize archive manifest.json (content was null or invalid JSON).";
                            return false;
                        }

                        return manifest.TryValidate(out error);
                    }

                    case BackupContainerFormat.SevenZip:
                    {
                        if (!File.Exists(path))
                        {
                            error = $"7-Zip archive file does not exist: {path}";
                            return false;
                        }

                        // A sidecar is a convenience for inspecting a container without
                        // opening it. It is not authenticated and it is not part of the
                        // archive, so a caller that is about to trust the payload asks
                        // for the archive's own manifest instead.
                        string sidecarManifest = path + ".manifest.json";
                        if (allowSidecar && File.Exists(sidecarManifest))
                        {
                            var sidecarFi = new FileInfo(sidecarManifest);
                            if (sidecarFi.Length > _safetyBounds.MaxManifestBytes)
                            {
                                error = $"Manifest file exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                                return false;
                            }

                            string json = File.ReadAllText(sidecarManifest);
                            manifest = JsonSerializer.Deserialize<TransferBackupManifest>(json);
                            if (manifest is not null && manifest.TryValidate(out error))
                            {
                                isSidecar = true;
                                return true;
                            }
                        }

                        using var archiveStream = File.OpenRead(path);
                        using IArchive archive = SevenZipArchive.OpenArchive(archiveStream);
                        int entryCount = 0;
                        IArchiveEntry? manifestEntry = null;

                        foreach (IArchiveEntry entry in archive.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            entryCount++;
                            if (entryCount > _safetyBounds.MaxFileEntries)
                            {
                                error = $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).";
                                return false;
                            }

                            if (manifestEntry is null && entry.Key is not null)
                            {
                                string key = entry.Key.Replace('\\', '/').Trim('/');
                                // Root only, for the same reason as the ZIP path above.
                                if (key.Equals(TransferBackupLocations.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    manifestEntry = entry;
                                }
                            }
                        }

                        if (manifestEntry is null)
                        {
                            error = $"7-Zip archive does not contain a root {TransferBackupLocations.ManifestFileName}.";
                            return false;
                        }

                        if (manifestEntry.Size > _safetyBounds.MaxManifestBytes)
                        {
                            error = $"Archive manifest exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                            return false;
                        }

                        using Stream stream = manifestEntry.OpenEntryStream();
                        using var memoryStream = new MemoryStream();
                        byte[] buffer = new byte[81920];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalRead += bytesRead;
                            if (totalRead > _safetyBounds.MaxManifestBytes)
                            {
                                error = $"Archive manifest exceeds maximum allowed size ({_safetyBounds.MaxManifestBytes:N0} bytes).";
                                return false;
                            }
                            memoryStream.Write(buffer, 0, bytesRead);
                        }

                        memoryStream.Position = 0;
                        manifest = JsonSerializer.Deserialize<TransferBackupManifest>(memoryStream);

                        if (manifest is null)
                        {
                            error = "Failed to deserialize archive manifest.json (content was null or invalid JSON).";
                            return false;
                        }

                        return manifest.TryValidate(out error);
                    }

                    default:
                        error = $"Unsupported container format: {format}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Error reading backup manifest: {ex.Message}";
                manifest = null;
                return false;
            }
        }

        public bool TryBuildRunInfo(
            string path,
            out TransferBackupRunInfo? runInfo,
            out string? error,
            CancellationToken cancellationToken = default)
        {
            runInfo = null;

            BackupContainerFormat format = DetectContainerFormat(path);

            if (!TryReadManifest(path, out TransferBackupManifest? manifest, out error, out bool isSidecar, allowSidecar: true, cancellationToken))
            {
                return false;
            }

            string manifestPath = format == BackupContainerFormat.Folder
                ? Path.Combine(path, TransferBackupLocations.ManifestFileName)
                : path + "#" + TransferBackupLocations.ManifestFileName;

            VerificationStrength strength = isSidecar
                ? VerificationStrength.SidecarManifestMatch
                : VerificationStrength.ManifestMatch;

            runInfo = new TransferBackupRunInfo(
                BackupRootPath: path,
                ManifestPath: manifestPath,
                Manifest: manifest!,
                ContainerFormat: format,
                Verification: strength);

            return true;
        }

        public VerificationStrengthResult VerifyPayloadIntegrity(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken = default)
        {
            if (runInfo is null)
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.ManifestMismatch,
                    "Run info is null.");
            }

            if (runInfo.Manifest is null || runInfo.Manifest.Items is null)
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.ManifestMismatch,
                    "Run manifest or manifest items collection is null.");
            }

            if (runInfo.Manifest.Items.Count == 0)
            {
                return VerificationStrengthResult.Success(
                    VerificationStrength.PayloadVerified,
                    0,
                    0);
            }

            try
            {
                switch (runInfo.ContainerFormat)
                {
                    case BackupContainerFormat.Folder:
                        return VerifyFolderPayload(runInfo, cancellationToken);

                    case BackupContainerFormat.Zip:
                        return VerifyZipPayload(runInfo, cancellationToken);

                    case BackupContainerFormat.SevenZip:
                        return VerifySevenZipPayload(runInfo, cancellationToken);

                    default:
                        return VerificationStrengthResult.Failure(
                            VerificationStrength.ManifestMismatch,
                            $"Unsupported container format: {runInfo.ContainerFormat}");
                }
            }
            catch (OperationCanceledException)
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.Cancelled,
                    "Payload verification was cancelled.",
                    0,
                    runInfo.Manifest.Items.Count);
            }
            catch (Exception ex)
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.PayloadMismatch,
                    $"Payload verification failed: {ex.Message}",
                    0,
                    runInfo.Manifest.Items.Count);
            }
        }

        public Task<VerificationStrengthResult> VerifyPayloadIntegrityAsync(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => VerifyPayloadIntegrity(runInfo, cancellationToken), cancellationToken);
        }

        private static VerificationStrengthResult VerifyFolderPayload(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(runInfo.BackupRootPath))
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.MissingLocally,
                    $"Backup folder does not exist: {runInfo.BackupRootPath}",
                    0,
                    runInfo.Manifest.Items.Count);
            }

            var fileResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int verified = 0;

            foreach (TransferOverwriteBackupItem item in runInfo.Manifest.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = item.ResolveBackupFile(runInfo.BackupRootPath);
                if (!File.Exists(path))
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.MissingLocally,
                        $"Backup payload file is missing: {path}",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                using var stream = File.OpenRead(path);
                byte[] hash = SHA256.HashData(stream);
                string computedHash = Convert.ToHexString(hash);

                if (!string.Equals(computedHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Payload hash mismatch for {Path.GetFileName(path)} (expected {item.Sha256}, got {computedHash}).",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                fileResults[item.OriginalFile] = true;
                verified++;
            }

            return VerificationStrengthResult.Success(
                VerificationStrength.PayloadVerified,
                verified,
                runInfo.Manifest.Items.Count,
                fileResults);
        }

        private VerificationStrengthResult VerifyZipPayload(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(runInfo.BackupRootPath))
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.MissingLocally,
                    $"ZIP archive does not exist: {runInfo.BackupRootPath}",
                    0,
                    runInfo.Manifest.Items.Count);
            }

            using ZipArchive archive = ZipFile.OpenRead(runInfo.BackupRootPath);
            if (archive.Entries.Count > _safetyBounds.MaxFileEntries)
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.PayloadMismatch,
                    $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).",
                    0,
                    runInfo.Manifest.Items.Count);
            }

            var fileResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int verified = 0;

            foreach (TransferOverwriteBackupItem item in runInfo.Manifest.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relPath = item.GetRelativePayloadPath();
                ZipArchiveEntry? entry = archive.GetEntry(relPath)
                    ?? archive.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('\\', '/').Trim('/').Equals(relPath, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry missing: {relPath}",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                if (entry.Length > _safetyBounds.MaxSingleFileBytes)
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry exceeds max file size limit: {relPath}",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                using Stream stream = entry.Open();
                byte[] hash = SHA256.HashData(stream);
                string computedHash = Convert.ToHexString(hash);

                if (!string.Equals(computedHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Payload hash mismatch for {relPath} (expected {item.Sha256}, got {computedHash}).",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                fileResults[item.OriginalFile] = true;
                verified++;
            }

            return VerificationStrengthResult.Success(
                VerificationStrength.PayloadVerified,
                verified,
                runInfo.Manifest.Items.Count,
                fileResults);
        }

        private VerificationStrengthResult VerifySevenZipPayload(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(runInfo.BackupRootPath))
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.MissingLocally,
                    $"7-Zip archive does not exist: {runInfo.BackupRootPath}",
                    0,
                    runInfo.Manifest.Items.Count);
            }

            using var archiveStream = File.OpenRead(runInfo.BackupRootPath);
            using IArchive archive = SevenZipArchive.OpenArchive(archiveStream);

            var entriesByKey = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            int entryCount = 0;

            foreach (IArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > _safetyBounds.MaxFileEntries)
                {
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).",
                        0,
                        runInfo.Manifest.Items.Count);
                }

                if (entry.Key is not null)
                {
                    string key = entry.Key.Replace('\\', '/').Trim('/');
                    entriesByKey[key] = entry;
                }
            }

            var fileResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int verified = 0;

            foreach (TransferOverwriteBackupItem item in runInfo.Manifest.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relPath = item.GetRelativePayloadPath();
                if (!entriesByKey.TryGetValue(relPath, out IArchiveEntry? entry))
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"7-Zip payload entry missing: {relPath}",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                if (entry.Size > _safetyBounds.MaxSingleFileBytes)
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry exceeds max file size limit: {relPath}",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                using Stream entryStream = entry.OpenEntryStream();
                byte[] hash = SHA256.HashData(entryStream);
                string computedHash = Convert.ToHexString(hash);

                if (!string.Equals(computedHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    fileResults[item.OriginalFile] = false;
                    return VerificationStrengthResult.Failure(
                        VerificationStrength.PayloadMismatch,
                        $"Payload hash mismatch for {relPath} (expected {item.Sha256}, got {computedHash}).",
                        verified,
                        runInfo.Manifest.Items.Count,
                        fileResults);
                }

                fileResults[item.OriginalFile] = true;
                verified++;
            }

            return VerificationStrengthResult.Success(
                VerificationStrength.PayloadVerified,
                verified,
                runInfo.Manifest.Items.Count,
                fileResults);
        }
    }
}
