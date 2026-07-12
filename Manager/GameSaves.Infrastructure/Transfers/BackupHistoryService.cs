using GameSaves.Core.Platform;
using GameSaves.Core.Transfers;
using System.Text.Json;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class BackupHistoryService : IBackupHistoryService
    {
        private readonly IAppDatabasePathProvider _databasePathProvider;

        public BackupHistoryService(IAppDatabasePathProvider databasePathProvider)
        {
            _databasePathProvider = databasePathProvider;
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

            foreach (string runFolder in Directory.EnumerateDirectories(basePath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string manifestPath = Path.Combine(
                    runFolder,
                    TransferBackupLocations.ManifestFileName);

                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    TransferBackupManifest? manifest =
                        JsonSerializer.Deserialize<TransferBackupManifest>(
                            File.ReadAllText(manifestPath));

                    if (manifest is null)
                        continue;

                    runs.Add(new TransferBackupRunInfo(
                        BackupRootPath: runFolder,
                        ManifestPath: manifestPath,
                        Manifest: manifest));
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
