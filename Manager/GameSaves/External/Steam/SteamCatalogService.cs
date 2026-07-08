using System.Text.Json;
using GameSaves.Core.Steam;

namespace GameSaves.External.Steam
{
    public sealed class SteamCatalogService
    {
        private readonly SteamCatalogFetchOptions _options;
        private readonly SteamCatalogDatabase _database;

        public SteamCatalogService(SteamCatalogFetchOptions options)
        {
            _options = options;
            _database = new SteamCatalogDatabase(options.DatabasePath);
        }

        public async Task<SteamCatalogFetchResult> FetchAsync(
            CancellationToken cancellationToken = default)
        {
            _database.Initialize();

            Directory.CreateDirectory(_options.OutputRoot);

            long runId = _database.StartFetchRun(
                _options.Kind,
                _options.OutputRoot,
                _options.MaxResultsPerPage,
                _options.IfModifiedSinceUnix);

            try
            {
                using var client = new SteamStoreCatalogClient(_options.UserAgent);

                List<SteamCatalogApp> apps = await client.GetAllAppsAsync(
                    _options.SteamWebApiKey,
                    _options.Kind,
                    _options.MaxResultsPerPage,
                    _options.MaxAppsToFetch,
                    _options.IfModifiedSinceUnix,
                    cancellationToken);

                _database.UpsertApps(apps);
                _database.SyncGameTitlesFromCatalog(_options.Kind);

                string kindName = _options.Kind.ToString().ToLowerInvariant();

                string jsonPath = Path.Combine(
                    _options.OutputRoot,
                    $"steam-catalog-{kindName}.json");

                string appIdsPath = Path.Combine(
                    _options.OutputRoot,
                    $"steam-appids-{kindName}.txt");

                await File.WriteAllTextAsync(
                    jsonPath,
                    JsonSerializer.Serialize(
                        apps.OrderBy(app =>
                            int.TryParse(app.SteamAppId, out int parsed)
                                ? parsed
                                : int.MaxValue),
                        new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);

                await File.WriteAllLinesAsync(
                    appIdsPath,
                    apps
                        .Select(app => app.SteamAppId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(appId =>
                            int.TryParse(appId, out int parsed)
                                ? parsed
                                : int.MaxValue),
                    cancellationToken);

                _database.CompleteFetchRun(runId, apps.Count, error: null);

                return new SteamCatalogFetchResult(
                    _options.Kind,
                    apps.Count,
                    jsonPath,
                    appIdsPath);
            }
            catch (Exception ex)
            {
                _database.CompleteFetchRun(runId, 0, ex.Message);
                throw;
            }
        }

        public static async Task<SteamCatalogMissingExportResult> ExportMissingGameAppIdsAsync(
            string databasePath,
            string outputPath,
            int limit,
            bool excludeAlreadyPcgwLinked,
            CancellationToken cancellationToken = default)
        {
            var database = new SteamCatalogDatabase(databasePath);
            database.Initialize();

            List<string> missing = database.GetMissingAppIdsForHarvest(
                SteamCatalogAppKind.Game,
                limit,
                excludeAlreadyPcgwLinked);

            await WriteAppIdsAsync(outputPath, missing, cancellationToken);

            return new SteamCatalogMissingExportResult(
                missing.Count,
                outputPath);
        }

        public static int EnqueueMissingGamesForHarvest(string databasePath)
        {
            var database = new SteamCatalogDatabase(databasePath);
            database.Initialize();

            return database.EnqueueMissingGamesForHarvest();
        }

        public static async Task<SteamCatalogMissingExportResult> ExportNextQueuedGameAppIdsAsync(
            string databasePath,
            string outputPath,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var database = new SteamCatalogDatabase(databasePath);
            database.Initialize();

            List<string> appIds = database.ExportNextQueuedAppIdsForHarvest(limit);

            await WriteAppIdsAsync(outputPath, appIds, cancellationToken);

            return new SteamCatalogMissingExportResult(
                appIds.Count,
                outputPath);
        }

        public static void MarkHarvestQueueStatus(
            string databasePath,
            string steamAppId,
            string status,
            string? error = null)
        {
            var database = new SteamCatalogDatabase(databasePath);
            database.Initialize();

            database.MarkHarvestQueueStatus(steamAppId, status, error);
        }

        private static async Task WriteAppIdsAsync(
            string outputPath,
            IEnumerable<string> appIds,
            CancellationToken cancellationToken)
        {
            string? directory = Path.GetDirectoryName(outputPath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllLinesAsync(outputPath, appIds, cancellationToken);
        }
    }
}