using GameSaves.Core.Platform;
using GameSaves.Core.Transfers;
using System.Text.Json;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class BackupHistoryService : IBackupHistoryService
    {
        private readonly IAppDatabasePathProvider _databasePathProvider;
        private readonly IBackupMetadataReader _metadataReader;

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

            // Delete orphaned temporary directories: .staging_*, .export_*, .download_*
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(basePath, ".*"))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".staging_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(".export_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(".download_", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (Directory.GetLastWriteTimeUtc(dir) < cutoff.UtcDateTime)
                            {
                                Directory.Delete(dir, recursive: true);
                            }
                        }
                        catch
                        {
                            // Best effort
                        }
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

        private List<TransferBackupRunInfo> GetRuns(CancellationToken cancellationToken)
        {
            var runs = new List<TransferBackupRunInfo>();

            string basePath = TransferBackupLocations.GetBackupBasePath(_databasePathProvider);

            if (!Directory.Exists(basePath))
                return runs;

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
