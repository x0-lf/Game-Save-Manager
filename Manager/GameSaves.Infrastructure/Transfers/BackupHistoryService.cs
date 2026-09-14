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

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<TransferBackupRunInfo>>(
                () => GetRuns(cancellationToken),
                cancellationToken);
        }

        private List<TransferBackupRunInfo> GetRuns(CancellationToken cancellationToken)
        {
            var runs = new List<TransferBackupRunInfo>();

            string basePath = TransferBackupLocations.GetBackupBasePath(_databasePathProvider);

            if (!Directory.Exists(basePath))
                return runs;

            // Enumerate folder runs
            foreach (string runFolder in Directory.EnumerateDirectories(basePath))
            {
                cancellationToken.ThrowIfCancellationRequested();

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
