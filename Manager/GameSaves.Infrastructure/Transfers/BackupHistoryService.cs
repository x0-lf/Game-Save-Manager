using GameSaves.Core.Platform;
using GameSaves.Core.Transfers;
using System.Text.RegularExpressions;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class BackupHistoryService : IBackupHistoryService
    {
        private readonly IAppDatabasePathProvider _databasePathProvider;
        private readonly IBackupMetadataReader _metadataReader;
        private int _purgeDone;

        private static readonly Regex WorkingDirectoryName =
            new(@"^\.(staging|export|download)_[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        private static readonly Regex ExportTempFileName =
            new(@"^\.export_[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant);

        public BackupHistoryService(
            IAppDatabasePathProvider databasePathProvider,
            IBackupMetadataReader? metadataReader = null)
        {
            _databasePathProvider = databasePathProvider;
            _metadataReader = metadataReader ?? new BackupMetadataReader();
        }

        public string GetBackupBasePath()
        {
            return TransferBackupLocations.GetBackupBasePath(_databasePathProvider);
        }

        public Task<VerificationStrengthResult> VerifyRunIntegrityAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default)
        {
            return _metadataReader.VerifyPayloadIntegrityAsync(run, cancellationToken);
        }

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<TransferBackupRunInfo>>(
                () => GetRuns(cancellationToken),
                cancellationToken);
        }

        public void PurgeStaleWorkingDirectories(TimeSpan? olderThan = null)
        {
            string basePath = TransferBackupLocations.GetBackupBasePath(_databasePathProvider);
            if (!Directory.Exists(basePath))
                return;

            TimeSpan age = olderThan ?? TimeSpan.FromHours(1);
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - age;

            // Delete orphaned working directories. Only the exact names the writers
            // generate (".staging_" / ".export_" / ".download_" + Guid "N") qualify: a
            // prefix match would also take an imported run that happens to be called
            // ".staging_backup". Another app instance or the CLI may be mid-import, and a
            // directory's own write time stops moving once its children exist, so a
            // candidate is stale only when nothing anywhere inside it was written recently.
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(basePath, ".*"))
                {
                    if (!WorkingDirectoryName.IsMatch(Path.GetFileName(dir)))
                        continue;

                    try
                    {
                        if (NewestWriteTimeUtc(dir) < cutoff.UtcDateTime)
                        {
                            Directory.Delete(dir, recursive: true);
                        }
                    }
                    catch
                    {
                        // Best effort; an unreadable tree is left alone.
                    }
                }
            }
            catch
            {
                // Best effort
            }

            // Delete orphaned temporary export files: .export_*.tmp
            try
            {
                foreach (string file in Directory.EnumerateFiles(basePath, ".export_*.tmp"))
                {
                    if (!ExportTempFileName.IsMatch(Path.GetFileName(file)))
                        continue;

                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Best effort
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }

        private static DateTime NewestWriteTimeUtc(string directory)
        {
            DateTime newest = Directory.GetLastWriteTimeUtc(directory);
            var walk = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };

            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", walk))
            {
                // The enumeration's cached time comes from the directory entry, which
                // can lag behind a file still open for writing.
                entry.Refresh();
                if (entry.LastWriteTimeUtc > newest)
                    newest = entry.LastWriteTimeUtc;
            }

            return newest;
        }

        private List<TransferBackupRunInfo> GetRuns(CancellationToken cancellationToken)
        {
            var runs = new List<TransferBackupRunInfo>();

            string basePath = TransferBackupLocations.GetBackupBasePath(_databasePathProvider);

            if (!Directory.Exists(basePath))
                return runs;

            // Listing history is a read. Orphan cleanup rides along once per service
            // instance (effectively once per process), not on every refresh.
            if (Interlocked.Exchange(ref _purgeDone, 1) == 0)
                PurgeStaleWorkingDirectories();

            // Enumerate folder runs. The import, export and download paths stage work
            // in dot-prefixed siblings inside this base; a staged run carries a real
            // manifest for the moment before it is committed, so cataloguing it would
            // publish a half-built run to history, restore and cleanup.
            foreach (string runFolder in Directory.EnumerateDirectories(basePath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Path.GetFileName(runFolder).StartsWith('.'))
                    continue;

                try
                {
                    if (_metadataReader.TryBuildRunInfo(runFolder, out TransferBackupRunInfo? run, out _))
                    {
                        runs.Add(run!);
                    }
                }
                catch
                {
                    // An unreadable manifest never breaks the whole history view.
                }
            }

            // Enumerate archive runs (.zip, .7z)
            foreach (string archiveFile in Directory.EnumerateFiles(basePath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Path.GetFileName(archiveFile).StartsWith('.'))
                    continue;

                string ext = Path.GetExtension(archiveFile);
                if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (_metadataReader.TryBuildRunInfo(archiveFile, out TransferBackupRunInfo? run, out _))
                    {
                        runs.Add(run!);
                    }
                }
                catch
                {
                    // An unreadable manifest never breaks the whole history view.
                }
            }

            return runs
                .OrderByDescending(run => run.Manifest.StartedUtc)
                .ToList();
        }
    }
}
