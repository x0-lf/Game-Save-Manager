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
            string dbPath = MigratedDatabase.Create(_temp, "import_pending.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "import_approved.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "import_doc.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "import_dupes.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "title_dupes.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "malformed.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "missing_file.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "validation.db");
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
        public void Import_WithoutApproval_NeverRewritesAReviewedMapping()
        {
            string dbPath = MigratedDatabase.Create(_temp, "preserve_approved.db");
            var service = new MappingImportService();

            service.ImportJson(dbPath, Mapping("Directory", "\"priority\": 50"), new MappingImportOptions { AutoApprove = true });
            var repo = new SqliteSavePathMappingRepository(dbPath);
            SavePathMapping approved = Assert.Single(repo.GetApprovedMappingsForApp("400", "windows"));

            // Neither new notes nor a different path kind from an unreviewed
            // import may touch the approved row: its review stands as granted.
            MappingImportReport notes = service.ImportJson(dbPath, Mapping("Directory", "\"notes\": \"Updated notes from community\""));
            MappingImportReport kind = service.ImportJson(dbPath, Mapping("File", "\"priority\": 50"));

            Assert.Equal(1, notes.MappingsUnchanged);
            Assert.Equal(1, kind.MappingsUnchanged);
            Assert.Equal(approved, Assert.Single(repo.GetApprovedMappingsForApp("400", "windows")));

            // An explicit approval still reaches the existing row.
            MappingImportReport reapproved = service.ImportJson(
                dbPath,
                Mapping("File", "\"notes\": \"Reviewed as a file\""),
                new MappingImportOptions { AutoApprove = true });

            Assert.Equal(1, reapproved.MappingsUpdated);
            SavePathMapping updated = Assert.Single(repo.GetApprovedMappingsForApp("400", "windows"));
            Assert.Equal(SavePathKind.File, updated.PathKind);
            Assert.Equal("Reviewed as a file", updated.Notes);
        }

        [Fact]
        public void Import_PathKindChangeOnAPendingMapping_KeepsItPendingAndDisabled()
        {
            string dbPath = MigratedDatabase.Create(_temp, "pending_kind_change.db");
            var service = new MappingImportService();

            service.ImportJson(dbPath, Mapping("Directory", "\"priority\": 50"));
            MappingImportReport report = service.ImportJson(dbPath, Mapping("File", "\"priority\": 50"));

            Assert.Equal(1, report.MappingsUpdated);
            SavePathMapping mapping = Assert.Single(
                new SqliteSavePathMappingRepository(dbPath).GetMappingsForApp("400", "windows", includeDisabled: true));
            Assert.Equal(SavePathKind.File, mapping.PathKind);
            Assert.Equal("Pending", mapping.ReviewStatus);
            Assert.False(mapping.Enabled);
        }

        [Fact]
        public void Reimport_OmittingOptionalFields_ReportsUnchanged_AndKeepsTheStoredValues()
        {
            string dbPath = MigratedDatabase.Create(_temp, "omitted_fields.db");
            var service = new MappingImportService();

            service.ImportJson(dbPath, Mapping("Directory", "\"notes\": \"n\", \"sourceUrl\": \"https://example.com\", \"sourceName\": \"Community\""));
            MigratedDatabase.Execute(dbPath, "UPDATE save_path_mappings SET updated_utc = '2000-01-01 00:00:00';");

            MappingImportReport report = service.ImportJson(dbPath, Mapping("Directory", "\"priority\": 100"));

            Assert.Equal(1, report.MappingsUnchanged);
            Assert.Equal(0, report.MappingsUpdated);
            SavePathMapping mapping = Assert.Single(
                new SqliteSavePathMappingRepository(dbPath).GetMappingsForApp("400", "windows", includeDisabled: true));
            Assert.Equal("n", mapping.Notes);
            Assert.Equal("https://example.com", mapping.SourceUrl);
            Assert.Equal("Community", mapping.SourceName);
            Assert.Equal(1, MigratedDatabase.Scalar(dbPath, "SELECT COUNT(*) FROM save_path_mappings WHERE updated_utc = '2000-01-01 00:00:00';"));
        }

        [Fact]
        public void ApprovedImport_OmittingSourceName_DoesNotReattributeACuratedMapping()
        {
            string dbPath = MigratedDatabase.Create(_temp, "keep_curated_source.db");
            new CuratedMappingSeeder().Seed(dbPath);
            var repo = new SqliteSavePathMappingRepository(dbPath);
            SavePathMapping curated = Assert.Single(repo.GetApprovedMappingsForApp("220", "windows"));

            string json = $$"""
            [ { "steamAppId": "220", "platform": "windows", "pathTemplate": {{System.Text.Json.JsonSerializer.Serialize(curated.PathTemplate)}}, "notes": "Maintainer note" } ]
            """;
            MappingImportReport report = new MappingImportService().ImportJson(dbPath, json, new MappingImportOptions { AutoApprove = true });

            Assert.Equal(1, report.MappingsUpdated);
            SavePathMapping after = Assert.Single(repo.GetApprovedMappingsForApp("220", "windows"));
            Assert.Equal(CuratedMappingSeeder.CuratedSourceName, after.SourceName);
            Assert.Equal("Maintainer note", after.Notes);
        }

        [Fact]
        public void Import_PascalCaseDocument_AsWrittenByAiDetectOutput_IsImported()
        {
            string dbPath = MigratedDatabase.Create(_temp, "pascal_case.db");

            string json = """
            {
              "SchemaVersion": 1,
              "Titles": [ { "SteamAppId": "105600", "Title": "Terraria" } ],
              "Mappings": [
                { "SteamAppId": "105600", "GameName": "Terraria", "Platform": "windows", "PathTemplate": "{Documents}/My Games/Terraria/*.plr", "PathKind": "Glob", "ReviewStatus": "Approved" }
              ]
            }
            """;

            MappingImportReport report = new MappingImportService().ImportJson(dbPath, json);

            Assert.True(report.Success);
            Assert.Equal(1, report.TitlesInserted);
            Assert.Equal(1, report.MappingsInserted);
            SavePathMapping mapping = Assert.Single(
                new SqliteSavePathMappingRepository(dbPath).GetMappingsForApp("105600", "windows", includeDisabled: true));
            Assert.Equal(SavePathKind.Glob, mapping.PathKind);
            // A reviewStatus inside the file is never trusted.
            Assert.Equal("Pending", mapping.ReviewStatus);
            Assert.False(mapping.Enabled);
        }

        [Fact]
        public void Import_FlatArrayItemWithPathAlias_IsAMapping_NotATitle()
        {
            string dbPath = MigratedDatabase.Create(_temp, "path_alias.db");

            string json = """
            [ { "appId": "400", "title": "Portal", "platform": "windows", "path": "%APPDATA%/Portal" } ]
            """;

            MappingImportReport report = new MappingImportService().ImportJson(dbPath, json);

            Assert.True(report.Success);
            Assert.Equal(1, report.MappingsInserted);
            Assert.Equal("%APPDATA%/Portal", Assert.Single(
                new SqliteSavePathMappingRepository(dbPath).GetMappingsForApp("400", "windows", includeDisabled: true)).PathTemplate);
        }

        [Theory]
        [InlineData("1e3")]
        [InlineData("-5")]
        [InlineData("105600.0")]
        [InlineData("\"My Game\"")]
        [InlineData("0")]
        public void Import_RejectsAppIdsThatAreNotSteamAppIds(string appIdJson)
        {
            string dbPath = MigratedDatabase.Create(_temp, "bad_appid.db");

            string json = $$"""
            [ { "steamAppId": {{appIdJson}}, "platform": "windows", "pathTemplate": "%APPDATA%/X" } ]
            """;

            MappingImportReport report = new MappingImportService().ImportJson(dbPath, json);

            Assert.False(report.Success);
            Assert.Equal("SteamAppId", Assert.Single(report.Errors).Property);
            Assert.Equal(0, MigratedDatabase.Scalar(dbPath, "SELECT COUNT(*) FROM save_path_mappings;"));
        }

        [Theory]
        [InlineData("""{ "savePaths": [] }""")]
        [InlineData("""[ 42 ]""")]
        [InlineData("""{ "mappings": [ "not an object" ] }""")]
        public void Import_DocumentsWithTheWrongShape_AreReportedAsErrors(string json)
        {
            string dbPath = MigratedDatabase.Create(_temp, "wrong_shape.db");

            MappingImportReport report = new MappingImportService().ImportJson(dbPath, json);

            Assert.False(report.Success);
            Assert.Equal("Json", Assert.Single(report.Errors).Property);
        }

        [Fact]
        public void ImportFile_DatabaseErrors_AreNotReportedAsFileReadErrors()
        {
            string dbPath = _temp.GetPath("not_migrated.db");
            string jsonFile = _temp.GetPath("input.json");
            File.WriteAllText(jsonFile, Mapping("Directory", "\"priority\": 100"));

            Assert.Throws<SqliteException>(() => new MappingImportService().ImportFile(dbPath, jsonFile));
        }

        private static string Mapping(string pathKind, string extra) => $$"""
            [ { "steamAppId": "400", "gameName": "Portal", "platform": "windows", "pathTemplate": "%LOCALAPPDATA%/Portal/Saves", "pathKind": "{{pathKind}}", {{extra}} } ]
            """;

        [Fact]
        public void Import_SupportsBothSnakeCaseAndCamelCase()
        {
            string dbPath = MigratedDatabase.Create(_temp, "snake_case.db");
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
            string dbPath = MigratedDatabase.Create(_temp, "numeric_appid.db");
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
        public void ImportFile_ReadsTheFileAndReportsASummary()
        {
            string dbPath = MigratedDatabase.Create(_temp, "db_bridge.db");

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

            MappingImportReport report = new MappingImportService().ImportFile(dbPath, jsonFile);
            Assert.True(report.Success);
            Assert.Equal(1, report.MappingsInserted);

            string summary = report.FormatSummary();
            Assert.Contains("Import Summary", summary);
            Assert.Contains("1 inserted", summary);
        }
    }
}
