using GameSaves.Core.Transfers;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.IO.Compression;
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

            // Reading a manifest proves only that it parses. ManifestMatch is a comparison
            // of two sides that sync performs, and PayloadVerified needs the bytes re-hashed,
            // so a run listed from one place starts unverified.
            VerificationStrength strength = isSidecar
                ? VerificationStrength.SidecarManifestMatch
                : VerificationStrength.None;

            runInfo = new TransferBackupRunInfo(
                BackupRootPath: path,
                ManifestPath: manifestPath,
                Manifest: manifest!,
                ContainerFormat: format,
                Verification: strength);

            return true;
        }

        public Task<VerificationStrengthResult> VerifyPayloadIntegrityAsync(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => VerifyPayloadIntegrity(runInfo, cancellationToken), cancellationToken);
        }

        private VerificationStrengthResult VerifyPayloadIntegrity(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
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

            // Nothing listed means nothing was checked, and PayloadVerified 0/0 would
            // read as a clean result.
            if (runInfo.Manifest.Items.Count == 0)
            {
                return VerificationStrengthResult.Failure(
                    VerificationStrength.None,
                    "The manifest lists no payload files, so there is nothing to verify.");
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file held open by another program or a folder the user cannot read
                // says nothing about the bytes. PayloadMismatch would tell the user the
                // run is corrupted or tampered with, so it stays unverified instead.
                return VerificationStrengthResult.Failure(
                    VerificationStrength.None,
                    "The backup could not be read, so it was not verified. Close any program using it and try again.",
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

        private static VerificationStrengthResult VerifyFolderPayload(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runInfo.BackupRootPath));
            Dictionary<string, List<TransferOverwriteBackupItem>> payloads = GroupByPayload(
                runInfo.Manifest.Items,
                item => Path.GetFullPath(item.ResolveBackupFile(root)));
            var tally = new PayloadTally(payloads.Count);

            if (!Directory.Exists(root))
            {
                return tally.Fail(
                    VerificationStrength.MissingLocally,
                    $"Backup folder does not exist: {runInfo.BackupRootPath}");
            }

            // One walk of the run folder before anything is opened. A junction or symlink
            // planted in the run would have File.OpenRead hash a file outside it (export
            // skips them for the same reason), so a link anywhere in the tree fails.
            string manifestPath = Path.Combine(root, TransferBackupLocations.ManifestFileName);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var walk = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            };

            foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", walk))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return tally.Fail(
                        VerificationStrength.PayloadMismatch,
                        $"The backup folder contains a link, which verification never follows: {Path.GetRelativePath(root, entry.FullName)}");
                }

                if (entry is FileInfo && !entry.FullName.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
                    present.Add(entry.FullName);
            }

            foreach ((string path, List<TransferOverwriteBackupItem> items) in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!present.Contains(path))
                {
                    return tally.Fail(
                        VerificationStrength.MissingLocally,
                        $"Backup payload file is missing: {path}",
                        items);
                }

                VerificationStrengthResult? failure = tally.Check(items, Sha256Hasher.HashFile(path), Path.GetFileName(path));
                if (failure is not null)
                    return failure;
            }

            return tally.Finish(
                present.Where(path => !payloads.ContainsKey(path))
                    .Select(path => Path.GetRelativePath(root, path)));
        }

        private VerificationStrengthResult VerifyZipPayload(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
        {
            Dictionary<string, List<TransferOverwriteBackupItem>> payloads = GroupByPayload(
                runInfo.Manifest.Items,
                item => item.GetRelativePayloadPath());
            var tally = new PayloadTally(payloads.Count);

            if (!File.Exists(runInfo.BackupRootPath))
            {
                return tally.Fail(
                    VerificationStrength.MissingLocally,
                    $"ZIP archive does not exist: {runInfo.BackupRootPath}");
            }

            using ZipArchive archive = ZipFile.OpenRead(runInfo.BackupRootPath);

            // One case-insensitive index instead of a linear scan per manifest item.
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            string? indexError = IndexPayloadEntries(
                archive.Entries.Select(e => (
                    (string?)e.FullName,
                    e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'),
                    e)),
                entries,
                cancellationToken);

            if (indexError is not null)
                return tally.Fail(VerificationStrength.PayloadMismatch, indexError);

            long remainingBytes = _safetyBounds.MaxTotalUncompressedBytes;

            foreach ((string relPath, List<TransferOverwriteBackupItem> items) in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!entries.TryGetValue(relPath, out ZipArchiveEntry? entry))
                {
                    return tally.Fail(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry missing: {relPath}",
                        items);
                }

                if (!TryReserve(entry.Length, ref remainingBytes))
                {
                    return tally.Fail(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry exceeds max file size limit: {relPath}",
                        items);
                }

                using Stream stream = entry.Open();
                VerificationStrengthResult? failure = tally.Check(
                    items,
                    Sha256Hasher.HashBounded(stream, entry.Length, cancellationToken),
                    relPath);

                if (failure is not null)
                    return failure;
            }

            return tally.Finish(entries.Keys.Where(key => !payloads.ContainsKey(key)));
        }

        private VerificationStrengthResult VerifySevenZipPayload(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken)
        {
            Dictionary<string, List<TransferOverwriteBackupItem>> payloads = GroupByPayload(
                runInfo.Manifest.Items,
                item => item.GetRelativePayloadPath());
            var tally = new PayloadTally(payloads.Count);

            if (!File.Exists(runInfo.BackupRootPath))
            {
                return tally.Fail(
                    VerificationStrength.MissingLocally,
                    $"7-Zip archive does not exist: {runInfo.BackupRootPath}");
            }

            using var archiveStream = File.OpenRead(runInfo.BackupRootPath);
            using IArchive archive = SevenZipArchive.OpenArchive(archiveStream);

            var entries = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            string? indexError = IndexPayloadEntries(
                archive.Entries.Select(e => (e.Key, e.IsDirectory, e)),
                entries,
                cancellationToken);

            if (indexError is not null)
                return tally.Fail(VerificationStrength.PayloadMismatch, indexError);

            foreach ((string relPath, List<TransferOverwriteBackupItem> items) in payloads)
            {
                if (!entries.ContainsKey(relPath))
                {
                    return tally.Fail(
                        VerificationStrength.PayloadMismatch,
                        $"7-Zip payload entry missing: {relPath}",
                        items);
                }
            }

            // Opening entries one at a time re-decodes a solid block from its start for
            // every entry. One reader pass decodes the archive once, in archive order.
            long remainingBytes = _safetyBounds.MaxTotalUncompressedBytes;
            using IReader reader = archive.ExtractAllEntries();

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();

                IEntry entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is null)
                    continue;

                string relPath = NormalizeEntryKey(entry.Key);
                if (!payloads.TryGetValue(relPath, out List<TransferOverwriteBackupItem>? items))
                    continue;

                if (!TryReserve(entry.Size, ref remainingBytes))
                {
                    return tally.Fail(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry exceeds max file size limit: {relPath}",
                        items);
                }

                using Stream entryStream = reader.OpenEntryStream();
                VerificationStrengthResult? failure = tally.Check(
                    items,
                    Sha256Hasher.HashBounded(entryStream, entry.Size, cancellationToken),
                    relPath);

                if (failure is not null)
                    return failure;
            }

            return tally.Finish(entries.Keys.Where(key => !payloads.ContainsKey(key)));
        }

        /// <summary>
        /// Items that name the same payload are one file: it is hashed and counted once,
        /// and every item naming it has to agree with the hash.
        /// </summary>
        private static Dictionary<string, List<TransferOverwriteBackupItem>> GroupByPayload(
            IEnumerable<TransferOverwriteBackupItem> items,
            Func<TransferOverwriteBackupItem, string> payloadKey)
        {
            var groups = new Dictionary<string, List<TransferOverwriteBackupItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (TransferOverwriteBackupItem item in items)
            {
                string key = payloadKey(item);
                if (!groups.TryGetValue(key, out List<TransferOverwriteBackupItem>? group))
                    groups[key] = group = [];

                group.Add(item);
            }

            return groups;
        }

        /// <summary>
        /// Indexes an archive's file entries by normalized path, leaving out directories
        /// and the root manifest. A path stored twice is refused: which copy an extractor
        /// keeps is up to the extractor, so the copy verified need not be the copy restored.
        /// </summary>
        private string? IndexPayloadEntries<TEntry>(
            IEnumerable<(string? Key, bool IsDirectory, TEntry Entry)> archiveEntries,
            Dictionary<string, TEntry> index,
            CancellationToken cancellationToken)
        {
            int entryCount = 0;

            foreach ((string? key, bool isDirectory, TEntry entry) in archiveEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++entryCount > _safetyBounds.MaxFileEntries)
                    return $"Archive exceeds maximum allowed entries limit ({_safetyBounds.MaxFileEntries:N0}).";

                if (key is null || isDirectory)
                    continue;

                string normalized = NormalizeEntryKey(key);
                if (normalized.Length == 0 ||
                    normalized.Equals(TransferBackupLocations.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!index.TryAdd(normalized, entry))
                    return $"Archive stores the same payload path more than once: {normalized}";
            }

            return null;
        }

        private static string NormalizeEntryKey(string key) => key.Replace('\\', '/').Trim('/');

        /// <summary>
        /// Admits an entry by its declared size against the import bounds. The hash then
        /// reads no more than the declared size, so an entry that under-declares cannot
        /// stream past the single-file or cumulative limit.
        /// </summary>
        private bool TryReserve(long declaredBytes, ref long remainingBytes)
        {
            if (declaredBytes < 0 ||
                declaredBytes > _safetyBounds.MaxSingleFileBytes ||
                declaredBytes > remainingBytes)
            {
                return false;
            }

            remainingBytes -= declaredBytes;
            return true;
        }

        private sealed class PayloadTally(int totalFiles)
        {
            private readonly Dictionary<string, bool> _fileResults = new(StringComparer.OrdinalIgnoreCase);
            private int _verified;

            public VerificationStrengthResult Fail(
                VerificationStrength strength,
                string error,
                List<TransferOverwriteBackupItem>? items = null)
            {
                foreach (TransferOverwriteBackupItem item in items ?? [])
                    _fileResults[item.OriginalFile] = false;

                return VerificationStrengthResult.Failure(strength, error, _verified, totalFiles, _fileResults);
            }

            /// <returns>A failure, or null when the hash matches every item naming this payload.</returns>
            public VerificationStrengthResult? Check(
                List<TransferOverwriteBackupItem> items,
                string? computedHash,
                string label)
            {
                if (computedHash is null)
                {
                    return Fail(
                        VerificationStrength.PayloadMismatch,
                        $"Archive payload entry holds more data than it declares: {label}",
                        items);
                }

                foreach (TransferOverwriteBackupItem item in items)
                {
                    if (!string.Equals(computedHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail(
                            VerificationStrength.PayloadMismatch,
                            $"Payload hash mismatch for {label} (expected {item.Sha256}, got {computedHash}).",
                            items);
                    }
                }

                foreach (TransferOverwriteBackupItem item in items)
                    _fileResults[item.OriginalFile] = true;

                _verified++;
                return null;
            }

            /// <summary>
            /// Every listed payload matched. Content the manifest does not describe still
            /// fails: a container with files added after the fact is not the run it claims to be.
            /// </summary>
            public VerificationStrengthResult Finish(IEnumerable<string> unlistedPaths)
            {
                if (_verified != totalFiles)
                {
                    return Fail(
                        VerificationStrength.None,
                        "Not every payload file listed in the manifest could be read, so the run was not verified.");
                }

                string? unlisted = unlistedPaths.FirstOrDefault();
                if (unlisted is not null)
                {
                    return Fail(
                        VerificationStrength.PayloadMismatch,
                        $"The backup contains a file its manifest does not list: {unlisted}");
                }

                return VerificationStrengthResult.Success(
                    VerificationStrength.PayloadVerified,
                    _verified,
                    totalFiles,
                    _fileResults);
            }
        }
    }
}
