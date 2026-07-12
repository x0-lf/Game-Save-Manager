using GameSaves.Core.Platform;
using GameSaves.Core.Transfers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Stores pre-overwrite backups under the application data directory:
    /// &lt;AppData&gt;\TransferBackups\&lt;timestamp&gt;_&lt;kind&gt;_&lt;appid&gt;_&lt;source&gt;_to_&lt;target&gt;\files\...
    /// Each run that backs up at least one file also writes a manifest.json
    /// (see <see cref="TransferBackupManifest"/>) with the original path,
    /// backup path, size, and SHA-256 of every file.
    /// </summary>
    public sealed class TransferOverwriteBackupService : ITransferOverwriteBackupService
    {
        private readonly IAppDatabasePathProvider _databasePathProvider;

        public TransferOverwriteBackupService(IAppDatabasePathProvider databasePathProvider)
        {
            _databasePathProvider = databasePathProvider;
        }

        public ITransferOverwriteBackupSession BeginSession(
            OverwriteBackupContext context,
            string? baseDirectory = null)
        {
            string kindSlug = context.Kind switch
            {
                OverwriteBackupContext.RestoreKind => "restore",
                OverwriteBackupContext.ManualKind => "manual",
                _ => "transfer"
            };

            string runFolderName =
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{kindSlug}" +
                $"_{TransferBackupLocations.MakeSafeName(context.SteamAppId)}" +
                $"_{TransferBackupLocations.MakeSafeName(context.SourceAccountId)}" +
                $"_to_{TransferBackupLocations.MakeSafeName(context.TargetAccountId)}";

            string backupRoot = Path.Combine(
                string.IsNullOrWhiteSpace(baseDirectory)
                    ? TransferBackupLocations.GetBackupBasePath(_databasePathProvider)
                    : baseDirectory,
                runFolderName);

            return new Session(backupRoot, context);
        }

        private sealed class Session : ITransferOverwriteBackupSession
        {
            private readonly OverwriteBackupContext _context;
            private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
            private readonly List<TransferOverwriteBackupItem> _items = new();
            private readonly object _gate = new();
            private bool _completed;

            public Session(string backupRootPath, OverwriteBackupContext context)
            {
                BackupRootPath = backupRootPath;
                _context = context;
            }

            public string BackupRootPath { get; }

            public int FilesBackedUp
            {
                get
                {
                    lock (_gate)
                        return _items.Count;
                }
            }

            public TransferOverwriteBackupItem BackUpFile(string targetFile)
            {
                lock (_gate)
                {
                    if (_completed)
                        throw new InvalidOperationException("The backup session is already completed.");

                    TransferOverwriteBackupItem? existing = _items.FirstOrDefault(item =>
                        TransferPathGuard.PathsEqual(item.OriginalFile, targetFile));

                    if (existing is not null)
                        return existing;

                    string backupFile = Path.Combine(
                        BackupRootPath,
                        "files",
                        BuildRelativeBackupPath(targetFile));

                    string? backupDirectory = Path.GetDirectoryName(backupFile);

                    if (!string.IsNullOrWhiteSpace(backupDirectory))
                        Directory.CreateDirectory(backupDirectory);

                    File.Copy(targetFile, backupFile, overwrite: false);

                    File.SetCreationTimeUtc(backupFile, File.GetCreationTimeUtc(targetFile));
                    File.SetLastWriteTimeUtc(backupFile, File.GetLastWriteTimeUtc(targetFile));

                    var item = new TransferOverwriteBackupItem(
                        OriginalFile: targetFile,
                        BackupFile: backupFile,
                        Bytes: new FileInfo(backupFile).Length,
                        Sha256: ComputeSha256(backupFile),
                        BackedUpUtc: DateTimeOffset.UtcNow);

                    _items.Add(item);
                    return item;
                }
            }

            public void Complete()
            {
                lock (_gate)
                {
                    if (_completed)
                        return;

                    _completed = true;

                    if (_items.Count == 0)
                        return;

                    var manifest = new TransferBackupManifest(
                        SchemaVersion: 1,
                        Kind: _context.Kind,
                        Game: _context.Game,
                        SteamAppId: _context.SteamAppId,
                        SourceAccountId: _context.SourceAccountId,
                        TargetAccountId: _context.TargetAccountId,
                        StartedUtc: _startedUtc,
                        CompletedUtc: DateTimeOffset.UtcNow,
                        FileCount: _items.Count,
                        TotalBytes: _items.Sum(item => item.Bytes),
                        Items: _items);

                    string manifestPath = Path.Combine(
                        BackupRootPath,
                        TransferBackupLocations.ManifestFileName);

                    File.WriteAllText(
                        manifestPath,
                        JsonSerializer.Serialize(
                            manifest,
                            new JsonSerializerOptions { WriteIndented = true }));
                }
            }

            public void Dispose() => Complete();

            // Mirrors the full original path inside the run folder so a backup
            // is unambiguous and restorable, e.g. C:\Steam\userdata\1\2\a.dat
            // becomes files\C\Steam\userdata\1\2\a.dat.
            private static string BuildRelativeBackupPath(string targetFile)
            {
                string fullPath = Path.GetFullPath(targetFile);
                string? root = Path.GetPathRoot(fullPath);

                string withoutRoot = string.IsNullOrEmpty(root)
                    ? fullPath
                    : fullPath[root.Length..];

                string driveFolder = string.IsNullOrEmpty(root)
                    ? "Unrooted"
                    : new string(root.Where(char.IsLetterOrDigit).ToArray());

                if (string.IsNullOrWhiteSpace(driveFolder))
                    driveFolder = "Unrooted";

                return Path.Combine(driveFolder, withoutRoot);
            }

            private static string ComputeSha256(string filePath)
            {
                using FileStream stream = File.OpenRead(filePath);
                byte[] hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash);
            }
        }
    }
}
