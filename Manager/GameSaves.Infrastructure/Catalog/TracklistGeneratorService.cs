using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameSaves.Core.Catalog;
using GameSaves.Core.Steam;
using GameSaves.Infrastructure.Save;
using GameSaves.Infrastructure.Steam;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Catalog
{
    /// <summary>
    /// Reconciles game catalog titles against existing save path mappings and generates
    /// prioritized, privacy-scrubbed tracklists of missing titles for research and harvesting.
    /// </summary>
    public sealed class TracklistGeneratorService : ITracklistGeneratorService
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;

        public TracklistGeneratorService(ISteamDiscoveryService? steamDiscoveryService = null)
        {
            _steamDiscoveryService = steamDiscoveryService ?? new SteamDiscoveryService();
        }

        public MissingTitlesTracklist GenerateTracklist(
            string databasePath,
            IEnumerable<MissingTitleCandidate>? candidates = null,
            TracklistOptions? options = null)
        {
            options ??= new TracklistOptions();

            // 1. Gather candidates from inputs, installed games, and/or database
            var candidateMap = new Dictionary<string, MissingTitleCandidate>(StringComparer.OrdinalIgnoreCase);

            if (candidates != null)
            {
                foreach (MissingTitleCandidate c in candidates)
                {
                    AddOrMergeCandidate(candidateMap, c);
                }
            }

            // If not restricted to explicit candidates, gather from installed and/or catalog
            if (candidates == null)
            {
                // Discovered Steam games
                try
                {
                    SteamDiscoveryResult discovery = _steamDiscoveryService.Discover(new SteamDiscoveryOptions
                    {
                        FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                        FallbackTimeout = TimeSpan.FromSeconds(15),
                        FallbackMaxDepth = 4
                    });

                    if (discovery?.Games != null)
                    {
                        foreach (SteamGame game in discovery.Games)
                        {
                            if (string.IsNullOrWhiteSpace(game.AppId))
                                continue;

                            AddOrMergeCandidate(candidateMap, new MissingTitleCandidate(
                                SteamAppId: game.AppId.Trim(),
                                Title: string.IsNullOrWhiteSpace(game.Name) ? $"App {game.AppId}" : game.Name.Trim(),
                                IsInstalled: true,
                                Priority: "High",
                                Source: "SteamInstalled"));
                        }
                    }
                }
                catch
                {
                    // Discovery failure should not crash tracklist generation
                }

                // If not restricted to installed only, query game_titles from database
                if (!options.IncludeInstalledOnly && File.Exists(databasePath))
                {
                    LoadCatalogTitlesFromDatabase(databasePath, candidateMap);
                }
            }

            // 2. Query save_path_mappings and game_titles notes from database
            var mappingStats = new Dictionary<string, MappingStats>(StringComparer.OrdinalIgnoreCase);
            var titleNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(databasePath))
            {
                LoadMappingStatsFromDatabase(databasePath, options.Platform, mappingStats);
                LoadTitleNotesFromDatabase(databasePath, titleNotes);
            }

            // 3. Reconcile candidates against mapping stats
            int totalReconciled = 0;
            int totalCovered = 0;
            int totalMissing = 0;
            int unresearchedCount = 0;
            int inReviewCount = 0;
            int noSaveLocationCount = 0;

            var missingItems = new List<MissingTitleEntry>();

            foreach (MissingTitleCandidate candidate in candidateMap.Values)
            {
                // Validate AppID: must be numeric
                if (!candidate.SteamAppId.All(char.IsDigit))
                    continue;

                totalReconciled++;

                mappingStats.TryGetValue(candidate.SteamAppId, out MappingStats stats);

                // If at least one approved mapping exists, the title has save path coverage
                if (stats.ApprovedCount > 0)
                {
                    totalCovered++;
                    continue;
                }

                totalMissing++;

                // Determine research status
                string? effectiveNotes = !string.IsNullOrWhiteSpace(candidate.Notes)
                    ? candidate.Notes
                    : (titleNotes.TryGetValue(candidate.SteamAppId, out string? dbNotes) ? dbNotes : null);

                MissingTitleResearchStatus researchStatus;

                if (IsKnownNoSaveLocation(effectiveNotes))
                {
                    researchStatus = MissingTitleResearchStatus.NoSaveLocation;
                    noSaveLocationCount++;
                }
                else if (stats.TotalCount > 0)
                {
                    researchStatus = MissingTitleResearchStatus.InReview;
                    inReviewCount++;
                }
                else
                {
                    researchStatus = MissingTitleResearchStatus.Unresearched;
                    unresearchedCount++;
                }

                string priority = !string.IsNullOrWhiteSpace(candidate.Priority)
                    ? candidate.Priority
                    : (candidate.IsInstalled ? "High" : "Normal");

                missingItems.Add(new MissingTitleEntry(
                    SteamAppId: candidate.SteamAppId,
                    Title: candidate.Title,
                    StoreUrl: $"https://store.steampowered.com/app/{candidate.SteamAppId}",
                    ResearchStatus: researchStatus,
                    Priority: priority,
                    IsInstalled: candidate.IsInstalled,
                    ExistingCandidateCount: stats.TotalCount,
                    DiscoveredUtc: DateTimeOffset.UtcNow,
                    Notes: effectiveNotes));
            }

            // 4. Apply filtering
            IEnumerable<MissingTitleEntry> filtered = missingItems;

            if (options.StatusFilter.HasValue)
            {
                filtered = filtered.Where(i => i.ResearchStatus == options.StatusFilter.Value);
            }

            if (!string.IsNullOrWhiteSpace(options.MinPriority))
            {
                int minRank = PriorityToRank(options.MinPriority);
                filtered = filtered.Where(i => PriorityToRank(i.Priority) <= minRank);
            }

            // 5. Sort: Priority (High > Normal > Low), then ResearchStatus, then numeric AppId
            var sorted = filtered
                .OrderBy(i => PriorityToRank(i.Priority))
                .ThenBy(i => i.ResearchStatus)
                .ThenBy(i => int.TryParse(i.SteamAppId, out int id) ? id : int.MaxValue)
                .ToList();

            if (options.Limit.HasValue && options.Limit.Value > 0)
            {
                sorted = sorted.Take(options.Limit.Value).ToList();
            }

            return new MissingTitlesTracklist(
                GeneratedUtc: DateTimeOffset.UtcNow,
                TotalReconciled: totalReconciled,
                TotalCovered: totalCovered,
                TotalMissing: totalMissing,
                UnresearchedCount: unresearchedCount,
                InReviewCount: inReviewCount,
                NoSaveLocationCount: noSaveLocationCount,
                Items: sorted);
        }

        public MissingTitlesTracklist GenerateTracklistFromInstalled(
            string databasePath,
            TracklistOptions? options = null)
        {
            options ??= new TracklistOptions();
            TracklistOptions installedOptions = options with { IncludeInstalledOnly = true };
            return GenerateTracklist(databasePath, candidates: null, installedOptions);
        }

        public string ExportJson(MissingTitlesTracklist tracklist, bool indented = true)
        {
            var serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            return JsonSerializer.Serialize(tracklist, serializerOptions);
        }

        public string ExportCsv(MissingTitlesTracklist tracklist)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SteamAppId,Title,StoreUrl,ResearchStatus,Priority,IsInstalled,ExistingCandidateCount,DiscoveredUtc,Notes");

            foreach (MissingTitleEntry item in tracklist.Items)
            {
                sb.Append(EscapeCsv(item.SteamAppId)).Append(',');
                sb.Append(EscapeCsv(item.Title)).Append(',');
                sb.Append(EscapeCsv(item.StoreUrl)).Append(',');
                sb.Append(item.ResearchStatus.ToString()).Append(',');
                sb.Append(EscapeCsv(item.Priority)).Append(',');
                sb.Append(item.IsInstalled ? "true" : "false").Append(',');
                sb.Append(item.ExistingCandidateCount).Append(',');
                sb.Append(item.DiscoveredUtc.ToString("o")).Append(',');
                sb.Append(EscapeCsv(item.Notes ?? string.Empty));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public void ExportToFile(MissingTitlesTracklist tracklist, string outputPath, TracklistExportFormat format)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string content = format switch
            {
                TracklistExportFormat.Csv => ExportCsv(tracklist),
                _ => ExportJson(tracklist, indented: true)
            };

            File.WriteAllText(outputPath, content, Encoding.UTF8);
        }

        private static void AddOrMergeCandidate(
            Dictionary<string, MissingTitleCandidate> candidateMap,
            MissingTitleCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.SteamAppId))
                return;

            string appId = candidate.SteamAppId.Trim();

            if (candidateMap.TryGetValue(appId, out MissingTitleCandidate? existing))
            {
                // Merge: installed presence takes precedence
                bool isInstalled = existing.IsInstalled || candidate.IsInstalled;
                string priority = (existing.IsInstalled || candidate.IsInstalled)
                    ? "High"
                    : (candidate.Priority ?? existing.Priority ?? "Normal");
                string title = !string.IsNullOrWhiteSpace(candidate.Title) ? candidate.Title : existing.Title;
                string? notes = !string.IsNullOrWhiteSpace(candidate.Notes) ? candidate.Notes : existing.Notes;

                candidateMap[appId] = new MissingTitleCandidate(
                    SteamAppId: appId,
                    Title: title,
                    IsInstalled: isInstalled,
                    Priority: priority,
                    Source: candidate.Source ?? existing.Source,
                    Notes: notes);
            }
            else
            {
                candidateMap[appId] = candidate with { SteamAppId = appId };
            }
        }

        private static void LoadCatalogTitlesFromDatabase(
            string databasePath,
            Dictionary<string, MissingTitleCandidate> candidateMap)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = """
                SELECT steam_app_id, title, notes
                FROM game_titles
                WHERE steam_app_id IS NOT NULL AND length(trim(steam_app_id)) > 0;
                """;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string appId = reader.GetString(0).Trim();
                    string title = reader.IsDBNull(1) ? $"App {appId}" : reader.GetString(1);
                    string? notes = reader.IsDBNull(2) ? null : reader.GetString(2);

                    AddOrMergeCandidate(candidateMap, new MissingTitleCandidate(
                        SteamAppId: appId,
                        Title: title,
                        IsInstalled: false,
                        Priority: "Normal",
                        Source: "Catalog",
                        Notes: notes));
                }
            }
            catch
            {
                // Table might not exist yet
            }
        }

        private static void LoadMappingStatsFromDatabase(
            string databasePath,
            string platform,
            Dictionary<string, MappingStats> statsMap)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();

                SavePathDatabase.EnsureReviewColumns(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                SELECT
                    steam_app_id,
                    COUNT(*) AS total_count,
                    SUM(CASE WHEN enabled = 1 AND COALESCE(review_status, '') = 'Approved' THEN 1 ELSE 0 END) AS approved_count,
                    SUM(CASE WHEN COALESCE(review_status, 'Pending') = 'Pending' THEN 1 ELSE 0 END) AS pending_count
                FROM save_path_mappings
                WHERE platform = $platform
                GROUP BY steam_app_id;
                """;

                command.Parameters.AddWithValue("$platform", platform);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string appId = reader.GetString(0).Trim();
                    int total = reader.GetInt32(1);
                    int approved = reader.GetInt32(2);
                    int pending = reader.GetInt32(3);

                    statsMap[appId] = new MappingStats(total, approved, pending);
                }
            }
            catch
            {
                // If tables missing, stats remain empty
            }
        }

        private static void LoadTitleNotesFromDatabase(
            string databasePath,
            Dictionary<string, string> notesMap)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = """
                SELECT steam_app_id, notes
                FROM game_titles
                WHERE steam_app_id IS NOT NULL AND notes IS NOT NULL;
                """;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string appId = reader.GetString(0).Trim();
                    string notes = reader.GetString(1);
                    notesMap[appId] = notes;
                }
            }
            catch
            {
                // Ignore if game_titles table absent
            }
        }

        private static bool IsKnownNoSaveLocation(string? notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return false;

            return notes.Contains("no save", StringComparison.OrdinalIgnoreCase) ||
                   notes.Contains("no local save", StringComparison.OrdinalIgnoreCase) ||
                   notes.Contains("server-side", StringComparison.OrdinalIgnoreCase) ||
                   notes.Contains("cloud only", StringComparison.OrdinalIgnoreCase) ||
                   notes.Contains("no save location", StringComparison.OrdinalIgnoreCase);
        }

        private static int PriorityToRank(string? priority)
        {
            if (string.Equals(priority, "High", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(priority, "Normal", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(priority, "Low", StringComparison.OrdinalIgnoreCase))
                return 3;
            return 4;
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        private readonly struct MappingStats
        {
            public int TotalCount { get; }
            public int ApprovedCount { get; }
            public int PendingCount { get; }

            public MappingStats(int total, int approved, int pending)
            {
                TotalCount = total;
                ApprovedCount = approved;
                PendingCount = pending;
            }
        }
    }
}
