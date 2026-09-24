using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameSaves.Core.Catalog;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Infrastructure.Catalog;
using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class TracklistGeneratorServiceTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }

        [Fact]
        public void GenerateTracklist_WithCandidates_ReconcilesCoveredAndMissingCategories()
        {
            string dbPath = _temp.GetPath("reconcile_test.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            // Insert 1 approved mapping for AppID 105600
            InsertMapping(dbPath, "105600", "Terraria", "windows", "%USERPROFILE%/Documents/My Games/Terraria", "Approved", enabled: true);

            // Insert 1 pending mapping for AppID 200
            InsertMapping(dbPath, "200", "Half-Life 2", "windows", "%PROGRAMFILES%/Steam/steamapps/common/Half-Life 2", "Pending", enabled: false);

            var candidates = new List<MissingTitleCandidate>
            {
                new("105600", "Terraria", IsInstalled: true),
                new("200", "Half-Life 2", IsInstalled: true),
                new("300", "Unknown Game", IsInstalled: false),
                new("400", "Online Arena", IsInstalled: false, Notes: "Server-side multiplayer only; no local saves")
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            Assert.Equal(4, tracklist.TotalReconciled);
            Assert.Equal(1, tracklist.TotalCovered);
            Assert.Equal(3, tracklist.TotalMissing);
            Assert.Equal(1, tracklist.UnresearchedCount);
            Assert.Equal(1, tracklist.InReviewCount);
            Assert.Equal(1, tracklist.NoSaveLocationCount);
            Assert.Equal(3, tracklist.Items.Count);

            MissingTitleEntry hl2 = tracklist.Items.First(i => i.SteamAppId == "200");
            Assert.Equal(MissingTitleResearchStatus.InReview, hl2.ResearchStatus);
            Assert.Equal(1, hl2.ExistingCandidateCount);
            Assert.True(hl2.IsInstalled);
            Assert.Equal("High", hl2.Priority);

            MissingTitleEntry unknown = tracklist.Items.First(i => i.SteamAppId == "300");
            Assert.Equal(MissingTitleResearchStatus.Unresearched, unknown.ResearchStatus);
            Assert.Equal(0, unknown.ExistingCandidateCount);
            Assert.False(unknown.IsInstalled);

            MissingTitleEntry online = tracklist.Items.First(i => i.SteamAppId == "400");
            Assert.Equal(MissingTitleResearchStatus.NoSaveLocation, online.ResearchStatus);
            Assert.Contains("Server-side", online.Notes);
        }

        [Fact]
        public void GenerateTracklist_PrioritySorting_PutsHighAboveNormalAndLow()
        {
            string dbPath = _temp.GetPath("priority_sort.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("300", "Low Priority Game", Priority: "Low"),
                new("100", "Installed High Priority Game", IsInstalled: true),
                new("200", "Normal Priority Game", Priority: "Normal")
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            Assert.Equal(3, tracklist.Items.Count);
            Assert.Equal("100", tracklist.Items[0].SteamAppId); // High
            Assert.Equal("200", tracklist.Items[1].SteamAppId); // Normal
            Assert.Equal("300", tracklist.Items[2].SteamAppId); // Low
        }

        [Fact]
        public void GenerateTracklist_Deduplication_MergesInstalledStatusAndAppIds()
        {
            string dbPath = _temp.GetPath("dedup.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("500", "Game from Catalog", IsInstalled: false, Priority: "Normal"),
                new("500", "Game from Library", IsInstalled: true, Priority: "High")
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            Assert.Equal(1, tracklist.TotalReconciled);
            Assert.Single(tracklist.Items);
            MissingTitleEntry item = tracklist.Items[0];
            Assert.Equal("500", item.SteamAppId);
            Assert.True(item.IsInstalled);
            Assert.Equal("High", item.Priority);
        }

        [Fact]
        public void GenerateTracklist_StatusFilter_FiltersAccurately()
        {
            string dbPath = _temp.GetPath("status_filter.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            InsertMapping(dbPath, "200", "In Review Game", "windows", "%APPDATA%/Save", "Pending", enabled: false);

            var candidates = new List<MissingTitleCandidate>
            {
                new("100", "Unresearched Game"),
                new("200", "In Review Game"),
                new("300", "No Save Game", Notes: "No save location cloud only")
            };

            var service = new TracklistGeneratorService();
            var options = new TracklistOptions(StatusFilter: MissingTitleResearchStatus.InReview);
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates, options);

            Assert.Equal(3, tracklist.TotalMissing);
            Assert.Single(tracklist.Items);
            Assert.Equal("200", tracklist.Items[0].SteamAppId);
            Assert.Equal(MissingTitleResearchStatus.InReview, tracklist.Items[0].ResearchStatus);
        }

        [Fact]
        public void GenerateTracklist_MinPriorityFilter_FiltersOutLowerPriorities()
        {
            string dbPath = _temp.GetPath("min_priority.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("100", "High Game", Priority: "High"),
                new("200", "Normal Game", Priority: "Normal"),
                new("300", "Low Game", Priority: "Low")
            };

            var service = new TracklistGeneratorService();
            var options = new TracklistOptions(MinPriority: "High");
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates, options);

            Assert.Single(tracklist.Items);
            Assert.Equal("100", tracklist.Items[0].SteamAppId);
        }

        [Fact]
        public void GenerateTracklist_Limit_RestrictsReturnedItems()
        {
            string dbPath = _temp.GetPath("limit.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = Enumerable.Range(1, 10)
                .Select(i => new MissingTitleCandidate(i.ToString(), $"Game {i}"))
                .ToList();

            var service = new TracklistGeneratorService();
            var options = new TracklistOptions(Limit: 3);
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates, options);

            Assert.Equal(10, tracklist.TotalMissing);
            Assert.Equal(3, tracklist.Items.Count);
        }

        [Fact]
        public void GenerateTracklist_InvalidAppIds_AreScrubbed()
        {
            string dbPath = _temp.GetPath("scrub.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("abc", "Invalid Non-numeric"),
                new("", "Invalid Empty"),
                new("   ", "Invalid Whitespace"),
                new("12345", "Valid Numeric")
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            Assert.Equal(1, tracklist.TotalReconciled);
            Assert.Single(tracklist.Items);
            Assert.Equal("12345", tracklist.Items[0].SteamAppId);
        }

        [Fact]
        public void ExportJson_GeneratesValidJsonWithZeroPrivatePaths()
        {
            string dbPath = _temp.GetPath("export_json.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("105600", "Terraria", IsInstalled: true),
                new("200", "Half-Life 2", IsInstalled: false)
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            string json = service.ExportJson(tracklist, indented: true);

            // Assert JSON parses correctly
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            Assert.True(root.TryGetProperty("totalReconciled", out _));
            Assert.True(root.TryGetProperty("items", out JsonElement items));
            Assert.Equal(2, items.GetArrayLength());

            // Assert strict privacy invariant: no local filesystem paths or usernames
            Assert.DoesNotContain(@"C:\", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AppData", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("steamapps", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExportCsv_GeneratesRfc4180CompliantCsvWithProperEscaping()
        {
            string dbPath = _temp.GetPath("export_csv.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("500", "Game, With \"Quotes\" and Commas", IsInstalled: true)
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            string csv = service.ExportCsv(tracklist);

            Assert.Contains("SteamAppId,Title,StoreUrl,ResearchStatus,Priority,IsInstalled,ExistingCandidateCount,DiscoveredUtc,Notes", csv);
            Assert.Contains("\"Game, With \"\"Quotes\"\" and Commas\"", csv);
            Assert.Contains("https://store.steampowered.com/app/500", csv);
        }

        [Fact]
        public void ExportToFile_WritesJsonAndCsvFilesCorrectly()
        {
            string dbPath = _temp.GetPath("export_files.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("100", "Test Game")
            };

            var service = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates);

            string jsonPath = _temp.GetPath("out", "tracklist.json");
            string csvPath = _temp.GetPath("out", "tracklist.csv");

            service.ExportToFile(tracklist, jsonPath, TracklistExportFormat.Json);
            service.ExportToFile(tracklist, csvPath, TracklistExportFormat.Csv);

            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(csvPath));

            string jsonContent = File.ReadAllText(jsonPath);
            Assert.Contains("\"steamAppId\": \"100\"", jsonContent);

            string csvContent = File.ReadAllText(csvPath);
            Assert.Contains("100,Test Game", csvContent);
        }

        [Fact]
        public void GenerateTracklistFromInstalled_UsesInjectedDiscoveryService()
        {
            string dbPath = _temp.GetPath("installed_discovery.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var fakeDiscovery = new FakeSteamDiscoveryService(new List<SteamGame>
            {
                new("730", "Counter-Strike 2", @"C:\Steam\cs2", @"C:\Steam", @"C:\Steam\manifest.vdf", @"C:\Steam\cs2", true, SteamDiscoveryConfidence.High)
            });

            var service = new TracklistGeneratorService(fakeDiscovery);
            MissingTitlesTracklist tracklist = service.GenerateTracklistFromInstalled(dbPath);

            Assert.Equal(1, tracklist.TotalReconciled);
            Assert.Single(tracklist.Items);
            MissingTitleEntry item = tracklist.Items[0];
            Assert.Equal("730", item.SteamAppId);
            Assert.Equal("Counter-Strike 2", item.Title);
            Assert.True(item.IsInstalled);
            Assert.Equal("High", item.Priority);
        }

        [Fact]
        public void SavePathDatabase_GenerateTracklist_BridgesSuccessfully()
        {
            string dbPath = _temp.GetPath("db_bridge.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var candidates = new List<MissingTitleCandidate>
            {
                new("999", "Bridge Game")
            };

            MissingTitlesTracklist tracklist = database.GenerateTracklist(candidates);

            Assert.Equal(1, tracklist.TotalReconciled);
            Assert.Single(tracklist.Items);
            Assert.Equal("999", tracklist.Items[0].SteamAppId);
        }

        [Fact]
        public void GenerateTracklist_DatabaseGameTitles_PopulatesCandidatesAutomatically()
        {
            string dbPath = _temp.GetPath("db_catalog.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            // Insert a title into game_titles
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                INSERT INTO game_titles (steam_app_id, title, source_name, notes)
                VALUES ('888', 'Catalog Title', 'PCGW', 'Some notes');
                """;
                cmd.ExecuteNonQuery();
            }

            var fakeDiscovery = new FakeSteamDiscoveryService(new List<SteamGame>());
            var service = new TracklistGeneratorService(fakeDiscovery);

            // Pass null candidates to trigger database query
            MissingTitlesTracklist tracklist = service.GenerateTracklist(dbPath, candidates: null);

            Assert.True(tracklist.TotalReconciled >= 1);
            MissingTitleEntry? item = tracklist.Items.FirstOrDefault(i => i.SteamAppId == "888");
            Assert.NotNull(item);
            Assert.Equal("Catalog Title", item.Title);
            Assert.Equal("Some notes", item.Notes);
            Assert.False(item.IsInstalled);
        }

        [Fact]
        public void GenerateTracklist_TitleWithOnlyRejectedOrDisabledMappings_IsUnresearched()
        {
            string dbPath = _temp.GetPath("rejected_only.db");
            new SavePathDatabase(dbPath).Initialize();

            InsertMapping(dbPath, "100", "Rejected Game", "windows", "%APPDATA%/Wrong", "Rejected", enabled: false);
            InsertMapping(dbPath, "200", "Disabled Game", "windows", "%APPDATA%/Off", "Approved", enabled: false);

            MissingTitlesTracklist tracklist = new TracklistGeneratorService().GenerateTracklist(
                dbPath,
                new List<MissingTitleCandidate> { new("100", "Rejected Game"), new("200", "Disabled Game") });

            Assert.Equal(0, tracklist.InReviewCount);
            Assert.Equal(2, tracklist.UnresearchedCount);
            Assert.All(tracklist.Items, item => Assert.Equal(MissingTitleResearchStatus.Unresearched, item.ResearchStatus));
        }

        [Fact]
        public void GenerateTracklist_CandidateWithoutTitle_UsesTheCatalogTitle()
        {
            string dbPath = _temp.GetPath("catalog_title.db");
            new SavePathDatabase(dbPath).Initialize();

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO game_titles (steam_app_id, title, source_name) VALUES ('777', 'Catalog Name', 'Steam');";
                cmd.ExecuteNonQuery();
            }

            MissingTitlesTracklist tracklist = new TracklistGeneratorService().GenerateTracklist(
                dbPath,
                new List<MissingTitleCandidate> { new("777", string.Empty), new("778", string.Empty) });

            Assert.Equal("Catalog Name", tracklist.Items.Single(i => i.SteamAppId == "777").Title);
            Assert.Equal("App 778", tracklist.Items.Single(i => i.SteamAppId == "778").Title);
        }

        [Fact]
        public void GenerateTracklist_UnreadableDatabase_ThrowsInsteadOfReportingEveryTitleMissing()
        {
            string dbPath = _temp.GetPath("corrupt.db");
            File.WriteAllText(dbPath, "this is not a SQLite database, just some text that is long enough");

            Assert.Throws<SqliteException>(() => new TracklistGeneratorService().GenerateTracklist(
                dbPath,
                new List<MissingTitleCandidate> { new("100", "Any Game") }));
        }

        [Fact]
        public void ExportCsv_NeutralizesSpreadsheetFormulasAndUsesCrlf()
        {
            var tracklist = new MissingTitlesTracklist(
                GeneratedUtc: DateTimeOffset.UtcNow,
                TotalReconciled: 2,
                TotalCovered: 0,
                TotalMissing: 2,
                UnresearchedCount: 2,
                InReviewCount: 0,
                NoSaveLocationCount: 0,
                Items: new List<MissingTitleEntry>
                {
                    new("100", "=HYPERLINK(\"http://evil.example\",\"Click\")", "https://store.steampowered.com/app/100", MissingTitleResearchStatus.Unresearched, "Normal", false, 0, DateTimeOffset.UtcNow),
                    new("200", "@SUM(A1)", "https://store.steampowered.com/app/200", MissingTitleResearchStatus.Unresearched, "Normal", false, 0, DateTimeOffset.UtcNow, Notes: "-2+3")
                });

            string csv = new TracklistGeneratorService().ExportCsv(tracklist);

            Assert.Contains("100,\"'=HYPERLINK(\"\"http://evil.example\"\",\"\"Click\"\")\",", csv);
            Assert.Contains("200,'@SUM(A1),", csv);
            Assert.EndsWith(",'-2+3\r\n", csv);
            Assert.DoesNotContain("\n", csv.Replace("\r\n", string.Empty));
        }

        private static void InsertMapping(
            string dbPath,
            string appId,
            string name,
            string platform,
            string pathTemplate,
            string reviewStatus,
            bool enabled)
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            SavePathDatabase.EnsureReviewColumns(connection);

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO save_path_mappings (
                steam_app_id, game_name, platform, path_template, path_kind,
                source_name, enabled, review_status, updated_utc
            )
            VALUES (
                $appId, $name, $platform, $pathTemplate, 'Directory',
                'Test', $enabled, $reviewStatus, CURRENT_TIMESTAMP
            );
            """;
            command.Parameters.AddWithValue("$appId", appId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$pathTemplate", pathTemplate);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$reviewStatus", reviewStatus);
            command.ExecuteNonQuery();
        }

        private sealed class FakeSteamDiscoveryService : ISteamDiscoveryService
        {
            private readonly List<SteamGame> _games;

            public FakeSteamDiscoveryService(List<SteamGame> games)
            {
                _games = games;
            }

            public SteamDiscoveryResult Discover(
                SteamDiscoveryOptions? options = null,
                IProgress<SteamFallbackScanProgress>? fallbackProgress = null,
                System.Threading.CancellationToken cancellationToken = default)
            {
                var result = new SteamDiscoveryResult
                {
                    SteamRoot = @"C:\Steam"
                };
                result.Games.AddRange(_games);
                return result;
            }
        }
    }
}
