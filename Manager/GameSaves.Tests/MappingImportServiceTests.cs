using GameSaves.Core.Save;
using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class MappingImportServiceTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }

        [Fact]
        public void Import_ValidMappingArray_DefaultsToPendingAndDisabled()
        {
            string dbPath = _temp.GetPath("import_pending.db");
            var service = new MappingImportService();

            string json = """
            [
              {
                "steamAppId": "400",
                "gameName": "Portal",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal/Saves",
                "pathKind": "Directory",
                "sourceName": "CommunitySource",
                "priority": 100
              }
            ]
            """;

            MappingImportReport report = service.ImportJson(dbPath, json);

            Assert.True(report.Success);
            Assert.Equal(1, report.TotalItemsProcessed);
            Assert.Equal(1, report.MappingsInserted);
            Assert.Equal(0, report.MappingsUpdated);
            Assert.Equal(0, report.MappingsUnchanged);
            Assert.Empty(report.Errors);

            var repo = new SqliteSavePathMappingRepository(dbPath);
            IReadOnlyList<SavePathMapping> all = repo.GetMappingsForApp("400", "windows", includeDisabled: true);
            Assert.Single(all);

            SavePathMapping mapping = all[0];
            Assert.False(mapping.Enabled, "Imported unreviewed candidate must be disabled by default.");
            Assert.Equal("Pending", mapping.ReviewStatus);

            // Trust boundary: never returned in approved mappings
            Assert.Empty(repo.GetApprovedMappingsForApp("400", "windows"));
        }

        [Fact]
        public void Import_WithAutoApprove_SetsApprovedAndEnabled()
        {
            string dbPath = _temp.GetPath("import_approved.db");
            var service = new MappingImportService();

            string json = """
            [
              {
                "steamAppId": "620",
                "gameName": "Portal 2",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal2/Saves",
                "pathKind": "Directory"
              }
            ]
            """;

            var options = new MappingImportOptions { AutoApprove = true };
            MappingImportReport report = service.ImportJson(dbPath, json, options);

            Assert.True(report.Success);
            Assert.Equal(1, report.MappingsInserted);

            var repo = new SqliteSavePathMappingRepository(dbPath);
            IReadOnlyList<SavePathMapping> approved = repo.GetApprovedMappingsForApp("620", "windows");
            Assert.Single(approved);
            Assert.True(approved[0].Enabled);
            Assert.Equal("Approved", approved[0].ReviewStatus);
        }

        [Fact]
        public void Import_DocumentWithTitlesAndMappings_ImportsBothAndReconcilesTitles()
        {
            string dbPath = _temp.GetPath("import_doc.db");
            var service = new MappingImportService();

            string json = """
            {
              "schemaVersion": 1,
              "titles": [
                {
                  "steamAppId": "105600",
                  "title": "Terraria",
                  "platformHint": "windows",
                  "sourceName": "CatalogExport"
                }
              ],
              "mappings": [
                {
                  "steamAppId": "105600",
                  "gameName": "Terraria",
                  "platform": "windows",
                  "pathTemplate": "{Documents}/My Games/Terraria",
                  "pathKind": "Directory"
                }
              ]
            }
            """;

            MappingImportReport report = service.ImportJson(dbPath, json);

            Assert.True(report.Success);
            Assert.Equal(2, report.TotalItemsProcessed);
            Assert.Equal(1, report.TitlesInserted);
            Assert.Equal(1, report.MappingsInserted);

            // Verify game_titles record
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT title FROM game_titles WHERE steam_app_id = '105600';";
            string? title = cmd.ExecuteScalar()?.ToString();
            Assert.Equal("Terraria", title);
        }

        [Fact]
        public void Import_DuplicateMappings_DetectsDuplicatesAndLeavesUnchanged()
        {
            string dbPath = _temp.GetPath("import_dupes.db");
            var service = new MappingImportService();

            string json = """
            [
              {
                "steamAppId": "400",
                "gameName": "Portal",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal/Saves",
                "pathKind": "Directory",
                "sourceName": "SourceA",
                "priority": 100
              }
            ]
            """;

            // First run
            MappingImportReport first = service.ImportJson(dbPath, json);
            Assert.Equal(1, first.MappingsInserted);

            // Second run with identical content
            MappingImportReport second = service.ImportJson(dbPath, json);
            Assert.Equal(0, second.MappingsInserted);
            Assert.Equal(0, second.MappingsUpdated);
            Assert.Equal(1, second.MappingsUnchanged);
        }

        [Fact]
        public void Import_DuplicateTitles_DetectsDuplicates()
        {
            string dbPath = _temp.GetPath("title_dupes.db");
            var service = new MappingImportService();

            string json = """
            {
              "titles": [
                { "steamAppId": "400", "title": "Portal" }
              ]
            }
            """;

            MappingImportReport first = service.ImportJson(dbPath, json);
            Assert.Equal(1, first.TitlesInserted);

            MappingImportReport second = service.ImportJson(dbPath, json);
            Assert.Equal(0, second.TitlesInserted);
            Assert.Equal(1, second.TitlesSkippedDuplicate);
        }

        [Fact]
        public void Import_MalformedJson_ReturnsErrorReportWithoutThrowing()
        {
            string dbPath = _temp.GetPath("malformed.db");
            var service = new MappingImportService();

            string badJson = "{ not valid json at all ... ";
            MappingImportReport report = service.ImportJson(dbPath, badJson);

            Assert.False(report.Success);
            Assert.NotEmpty(report.Errors);
            Assert.Equal("Json", report.Errors[0].Property);
        }

        [Fact]
        public void Import_NonExistentFile_ReturnsFileErrorReport()
        {
            string dbPath = _temp.GetPath("missing_file.db");
            var service = new MappingImportService();

            string missingPath = _temp.GetPath("does_not_exist.json");
            MappingImportReport report = service.ImportFile(dbPath, missingPath);

            Assert.False(report.Success);
            Assert.NotEmpty(report.Errors);
            Assert.Equal("File", report.Errors[0].Property);
            Assert.Contains("not found", report.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Import_ValidationDiagnostics_CollectsItemLevelErrors()
        {
            string dbPath = _temp.GetPath("validation.db");
            var service = new MappingImportService();

            string json = """
            [
              {
                "steamAppId": "",
                "platform": "windows",
                "pathTemplate": "some/path"
              },
              {
                "steamAppId": "123",
                "platform": "gameboy_color",
                "pathTemplate": "some/path"
              },
              {
                "steamAppId": "456",
                "platform": "windows",
                "pathTemplate": ""
              },
              {
                "steamAppId": "789",
                "platform": "windows",
                "pathTemplate": "valid/path",
                "pathKind": "Executable"
              }
            ]
            """;

            MappingImportReport report = service.ImportJson(dbPath, json);

            Assert.False(report.Success);
            Assert.Equal(4, report.Errors.Count);

            Assert.Contains(report.Errors, e => e.Property == "SteamAppId");
            Assert.Contains(report.Errors, e => e.Property == "Platform" && e.Message.Contains("Unsupported platform"));
            Assert.Contains(report.Errors, e => e.Property == "PathTemplate");
            Assert.Contains(report.Errors, e => e.Property == "PathKind");
        }

        [Fact]
        public void Import_ExistingApprovedMapping_IsNotDowngradedToPending_UnlessPathKindChanged()
        {
            string dbPath = _temp.GetPath("preserve_approved.db");
            var service = new MappingImportService();

            // 1. Initial import with autoApprove = true
            string initialJson = """
            [
              {
                "steamAppId": "400",
                "gameName": "Portal",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal/Saves",
                "pathKind": "Directory",
                "priority": 50
              }
            ]
            """;
            service.ImportJson(dbPath, initialJson, new MappingImportOptions { AutoApprove = true });

            var repo = new SqliteSavePathMappingRepository(dbPath);
            Assert.Equal(1, repo.CountApprovedMappings("windows"));

            // 2. Re-import with default options (AutoApprove = false) but updating notes
            string updateNotesJson = """
            [
              {
                "steamAppId": "400",
                "gameName": "Portal",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal/Saves",
                "pathKind": "Directory",
                "notes": "Updated notes from community"
              }
            ]
            """;
            MappingImportReport report2 = service.ImportJson(dbPath, updateNotesJson, new MappingImportOptions { AutoApprove = false });
            Assert.Equal(1, report2.MappingsUpdated);

            // Approved status must still be preserved!
            Assert.Equal(1, repo.CountApprovedMappings("windows"));
            IReadOnlyList<SavePathMapping> mappings = repo.GetApprovedMappingsForApp("400", "windows");
            Assert.Single(mappings);
            Assert.Equal("Updated notes from community", mappings[0].Notes);

            // 3. Re-import changing pathKind to "File" -> must invalidate approval!
            string changeKindJson = """
            [
              {
                "steamAppId": "400",
                "gameName": "Portal",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal/Saves",
                "pathKind": "File"
              }
            ]
            """;
            MappingImportReport report3 = service.ImportJson(dbPath, changeKindJson, new MappingImportOptions { AutoApprove = false });
            Assert.Equal(1, report3.MappingsUpdated);

            // Now it must be downgraded to Pending
            Assert.Equal(0, repo.CountApprovedMappings("windows"));
            Assert.Equal(1, repo.CountPendingMappings("windows"));
        }

        [Fact]
        public void Import_SupportsBothSnakeCaseAndCamelCase()
        {
            string dbPath = _temp.GetPath("snake_case.db");
            var service = new MappingImportService();

            string json = """
            [
              {
                "steam_app_id": "500",
                "game_name": "Left 4 Dead",
                "platform": "WINDOWS",
                "path_template": "%LOCALAPPDATA%/L4D",
                "path_kind": "Directory",
                "source_name": "SnakeSource",
                "source_url": "https://example.com/l4d",
                "source_license": "MIT",
                "priority": 80
              }
            ]
            """;

            MappingImportReport report = service.ImportJson(dbPath, json);
            Assert.True(report.Success);
            Assert.Equal(1, report.MappingsInserted);

            var repo = new SqliteSavePathMappingRepository(dbPath);
            IReadOnlyList<SavePathMapping> all = repo.GetMappingsForApp("500", "windows", includeDisabled: true);
            Assert.Single(all);
            Assert.Equal("Left 4 Dead", all[0].GameName);
            Assert.Equal("windows", all[0].Platform);
            Assert.Equal("SnakeSource", all[0].SourceName);
            Assert.Equal(80, all[0].Priority);
        }

        [Fact]
        public void Import_NumericAppId_ParsesSuccessfully()
        {
            string dbPath = _temp.GetPath("numeric_appid.db");
            var service = new MappingImportService();

            string json = """
            [
              {
                "steamAppId": 105600,
                "gameName": "Terraria",
                "platform": "windows",
                "pathTemplate": "{Documents}/Terraria"
              }
            ]
            """;

            MappingImportReport report = service.ImportJson(dbPath, json);
            Assert.True(report.Success);
            Assert.Equal(1, report.MappingsInserted);

            var repo = new SqliteSavePathMappingRepository(dbPath);
            IReadOnlyList<SavePathMapping> all = repo.GetMappingsForApp("105600", "windows", includeDisabled: true);
            Assert.Single(all);
            Assert.Equal("105600", all[0].SteamAppId);
        }

        [Fact]
        public void SavePathDatabase_ImportWithReport_BridgesToService()
        {
            string dbPath = _temp.GetPath("db_bridge.db");
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            string jsonFile = _temp.GetPath("bridge_input.json");
            File.WriteAllText(jsonFile, """
            [
              {
                "steamAppId": "400",
                "gameName": "Portal",
                "platform": "windows",
                "pathTemplate": "%LOCALAPPDATA%/Portal"
              }
            ]
            """);

            MappingImportReport report = database.ImportWithReport(jsonFile);
            Assert.True(report.Success);
            Assert.Equal(1, report.MappingsInserted);

            string summary = report.FormatSummary();
            Assert.Contains("Import Summary", summary);
            Assert.Contains("1 inserted", summary);
        }
    }
}
