using GameSaves.Core.Transfers;
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

        public BackupContainerFormat DetectContainerFormat(string path)
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

                string ext = Path.GetExtension(path);
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    return BackupContainerFormat.Zip;

                if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                    return BackupContainerFormat.SevenZip;
            }

            return BackupContainerFormat.Folder;
        }

        public bool TryReadManifest(
            string path,
            out TransferBackupManifest? manifest,
            out string? error)
        {
            manifest = null;
            error = null;

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

                        using ZipArchive archive = ZipFile.OpenRead(path);
                        ZipArchiveEntry? entry = archive.GetEntry(TransferBackupLocations.ManifestFileName)
                            ?? archive.Entries.FirstOrDefault(e =>
                                e.FullName.Equals(TransferBackupLocations.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                                e.Name.Equals(TransferBackupLocations.ManifestFileName, StringComparison.OrdinalIgnoreCase));

                        if (entry is null)
                        {
                            error = $"ZIP archive does not contain a root {TransferBackupLocations.ManifestFileName}.";
                            return false;
                        }

                        using Stream stream = entry.Open();
                        manifest = JsonSerializer.Deserialize<TransferBackupManifest>(stream);

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

                        // Check for adjacent/sidecar manifest descriptor if available
                        string sidecarManifest = path + ".manifest.json";
                        if (File.Exists(sidecarManifest))
                        {
                            string json = File.ReadAllText(sidecarManifest);
                            manifest = JsonSerializer.Deserialize<TransferBackupManifest>(json);
                            if (manifest is not null && manifest.TryValidate(out error))
                                return true;
                        }

                        error = "7-Zip direct manifest extraction requires 7-Zip decompression engine (OBS-006).";
                        return false;
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
            out string? error)
        {
            runInfo = null;

            BackupContainerFormat format = DetectContainerFormat(path);

            if (!TryReadManifest(path, out TransferBackupManifest? manifest, out error))
            {
                return false;
            }

            string manifestPath = format == BackupContainerFormat.Folder
                ? Path.Combine(path, TransferBackupLocations.ManifestFileName)
                : path + "#" + TransferBackupLocations.ManifestFileName;

            runInfo = new TransferBackupRunInfo(
                BackupRootPath: path,
                ManifestPath: manifestPath,
                Manifest: manifest!,
                ContainerFormat: format);

            return true;
        }
    }
}
