using GameSaves.Core.Platform;
using GameSaves.Core.Transfers;
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
    /// <remarks>
    /// A compressed run (BACKUP-001) is staged in the same folder, without a
    /// manifest, so it is never listed half-built. On completion the files and
    /// the manifest are packed into a temporary container beside it, every
    /// payload is re-read and hashed against the manifest, and only then is
    /// the container renamed to &lt;run&gt;.zip or &lt;run&gt;.7z and the staging
    /// folder removed. If any step fails, or a file of that name already
    /// exists, the manifest is written into the folder instead: the run is
    /// then an ordinary folder run and nothing is lost. A crash before
    /// completion leaves the staged files in a folder that is never listed and
    /// never cleaned up automatically, exactly as before.
    /// </remarks>
    public sealed class TransferOverwriteBackupService : ITransferOverwriteBackupService
    {
        private readonly IAppDatabasePathProvider _databasePathProvider;
        private readonly BackupStoragePreference? _storagePreference;

        /// <param name="databasePathProvider">Locates the application backup base.</param>
        /// <param name="storagePreference">
        /// The user's choice of container for new runs. Without one, runs are
        /// written as folders, which is what a caller that does not opt in gets.
        /// </param>
        public TransferOverwriteBackupService(
            IAppDatabasePathProvider databasePathProvider,
            BackupStoragePreference? storagePreference = null)
        {
            _databasePathProvider = databasePathProvider;
            _storagePreference = storagePreference;
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

            return new Session(
                backupRoot,
                context,
                _storagePreference?.NewRunFormat ?? BackupContainerFormat.Folder);
        }

        private sealed class Session : ITransferOverwriteBackupSession
        {
            private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

            private readonly OverwriteBackupContext _context;
            private readonly BackupContainerFormat _format;
            private readonly string _stagingRoot;
            private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
            private readonly List<TransferOverwriteBackupItem> _items = new();
            private readonly object _gate = new();
            private bool _completed;
            private string? _containerPath;

            public Session(string backupRootPath, OverwriteBackupContext context, BackupContainerFormat format)
            {
                _stagingRoot = backupRootPath;
                _context = context;
                _format = format;
            }

            public string BackupRootPath => _containerPath ?? _stagingRoot;

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

                    string relativePayload = BuildRelativeBackupPath(targetFile);
                    string backupFile = Path.Combine(
                        _stagingRoot,
                        "files",
                        relativePayload);

                    string? backupDirectory = Path.GetDirectoryName(backupFile);

                    if (!string.IsNullOrWhiteSpace(backupDirectory))
                        Directory.CreateDirectory(backupDirectory);

                    File.Copy(targetFile, backupFile, overwrite: false);

                    File.SetCreationTimeUtc(backupFile, File.GetCreationTimeUtc(targetFile));
                    File.SetLastWriteTimeUtc(backupFile, File.GetLastWriteTimeUtc(targetFile));

                    string relativePath = Path.Combine("files", relativePayload).Replace('\\', '/');

                    var item = new TransferOverwriteBackupItem(
                        OriginalFile: targetFile,
                        BackupFile: backupFile,
                        Bytes: new FileInfo(backupFile).Length,
                        Sha256: Sha256Hasher.HashFile(backupFile),
                        BackedUpUtc: DateTimeOffset.UtcNow,
                        RelativePath: relativePath);

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
                        SchemaVersion: TransferBackupManifest.CurrentSchemaVersion,
                        Kind: _context.Kind,
                        Game: _context.Game,
                        SteamAppId: _context.SteamAppId,
                        SourceAccountId: _context.SourceAccountId,
                        TargetAccountId: _context.TargetAccountId,
                        StartedUtc: _startedUtc,
                        CompletedUtc: DateTimeOffset.UtcNow,
                        FileCount: _items.Count,
                        TotalBytes: _items.Sum(item => item.Bytes),
                        Items: _items,
                        Format: "folder");

                    string manifestPath = Path.Combine(
                        _stagingRoot,
                        TransferBackupLocations.ManifestFileName);

                    if (_format != BackupContainerFormat.Folder && TryPackContainer(manifest))
                        return;

                    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson));
                }
            }

            public string LocateBackupFile(string backupFile) =>
                _containerPath is not null && TransferPathGuard.IsStrictlyUnderRoot(backupFile, _stagingRoot)
                    ? Path.Combine(_containerPath, Path.GetRelativePath(_stagingRoot, backupFile))
                    : backupFile;

            // True once the verified container is the run. False leaves the
            // staged folder untouched for the caller to finish as a folder run.
            private bool TryPackContainer(TransferBackupManifest folderManifest)
            {
                string containerPath = _stagingRoot + (_format == BackupContainerFormat.SevenZip ? ".7z" : ".zip");

                // Each payload is described where it will live, inside the
                // container; readers locate it by its relative path either way.
                TransferBackupManifest manifest = folderManifest with
                {
                    Format = _format == BackupContainerFormat.SevenZip ? "7z" : "zip",
                    Compression = nameof(BackupCompressionPreset.Optimal),
                    Items = folderManifest.Items
                        .Select(item => item with
                        {
                            BackupFile = Path.Combine(containerPath, Path.GetRelativePath(_stagingRoot, item.BackupFile))
                        })
                        .ToList()
                };
                string tempPath = Path.Combine(
                    Path.GetDirectoryName(_stagingRoot)!,
                    ".export_" + Guid.NewGuid().ToString("N") + ".tmp");
                bool committed = false;

                try
                {
                    if (File.Exists(containerPath) || Directory.Exists(containerPath))
                        return false;

                    BackupArchiveService.WriteContainer(
                        _stagingRoot,
                        tempPath,
                        _format,
                        BackupCompressionPreset.Optimal,
                        JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson),
                        CancellationToken.None);

                    // Only a container that lists as a run and whose every byte
                    // matches the manifest may take the staged files' place.
                    var reader = new BackupMetadataReader();
                    if (!reader.TryReadManifest(tempPath, out TransferBackupManifest? written, out _, allowSidecar: false) ||
                        written!.Items.Count != manifest.Items.Count)
                    {
                        return false;
                    }

                    VerificationStrengthResult check = reader.VerifyPayloadIntegrityAsync(
                            new TransferBackupRunInfo(
                                tempPath,
                                tempPath + "#" + TransferBackupLocations.ManifestFileName,
                                written,
                                _format))
                        .GetAwaiter()
                        .GetResult();

                    if (check.Strength != VerificationStrength.PayloadVerified)
                        return false;

                    File.Move(tempPath, containerPath);
                    committed = true;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    if (!committed)
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }

                _containerPath = containerPath;

                // The staged folder never held a manifest, so it was never a run;
                // its files now live, verified, in the container. A copy keeps a
                // read-only save's attribute, which would stop the delete. A
                // folder that still cannot be removed (a file held open) stays
                // unlisted and harmless.
                try
                {
                    foreach (string staged in Directory.EnumerateFiles(_stagingRoot, "*", SearchOption.AllDirectories))
                        File.SetAttributes(staged, FileAttributes.Normal);

                    Directory.Delete(_stagingRoot, recursive: true);
                }
                catch (Exception)
                {
                }

                return true;
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
        }
    }
}
