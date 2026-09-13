using GameSaves.Core.Save;
using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace GameSaves.Tests;

public sealed class MappingApprovalTrustTests : IDisposable
{
    private readonly TemporaryDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void ImportMappingsFromJson_DefaultsToPendingAndDisabled()
    {
        string dbPath = _temp.GetPath("import_test.db");
        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        string jsonPath = _temp.GetPath("candidates.json");
        var candidates = new List<SavePathImportItem>
        {
            new(
                SteamAppId: "12345",
                GameName: "Test Unreviewed Game",
                Platform: "windows",
                PathTemplate: "%APPDATA%/TestGame/Saves",
                PathKind: "Directory",
                SourceName: "CandidateHarvester",
                SourceUrl: "https://example.com/save",
                SourceLicense: "CC-BY",
                Notes: "Harvested automatically",
                Priority: 100)
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(candidates));

        // Act
        database.ImportMappingsFromJson(jsonPath);

        // Assert
        var repo = new SqliteSavePathMappingRepository(dbPath);
        IReadOnlyList<SavePathMapping> allMappings = repo.GetMappingsForApp("12345", "windows", includeDisabled: true);
        Assert.Single(allMappings);

        SavePathMapping imported = allMappings[0];
        Assert.False(imported.Enabled, "Imported unreviewed candidates must be disabled by default.");
        Assert.Equal("Pending", imported.ReviewStatus);

        // Crucial invariant: never returned by GetApprovedMappingsForApp
        IReadOnlyList<SavePathMapping> approvedRepo = repo.GetApprovedMappingsForApp("12345", "windows");
        Assert.Empty(approvedRepo);

        List<SavePathMapping> approvedDb = database.GetApprovedMappingsForApp("12345", "windows");
        Assert.Empty(approvedDb);

        Assert.Equal(0, repo.CountApprovedMappings("windows"));
        Assert.Equal(1, repo.CountPendingMappings("windows"));
    }

    [Fact]
    public void ImportMappings_WithExplicitApproval_SetsApprovedAndEnabled()
    {
        string dbPath = _temp.GetPath("import_approved.db");
        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        var items = new List<SavePathImportItem>
        {
            new(
                SteamAppId: "99999",
                GameName: "Curated Game",
                Platform: "windows",
                PathTemplate: "%USERPROFILE%/Saved Games/Curated",
                PathKind: "Directory",
                SourceName: "CuratedCatalog",
                SourceUrl: null,
                SourceLicense: "MIT",
                Notes: "Curated by maintainer",
                Priority: 50)
        };

        // Act - explicit approval during import
        database.ImportMappings(items, enabled: true, reviewStatus: "Approved");

        // Assert
        var repo = new SqliteSavePathMappingRepository(dbPath);
        IReadOnlyList<SavePathMapping> approved = repo.GetApprovedMappingsForApp("99999", "windows");
        Assert.Single(approved);
        Assert.True(approved[0].Enabled);
        Assert.Equal("Approved", approved[0].ReviewStatus);
        Assert.Equal(1, repo.CountApprovedMappings("windows"));
    }

    [Fact]
    public void GetApprovedMappingsForApp_StrictTrustBoundary_OnlyReturnsApprovedAndEnabled()
    {
        string dbPath = _temp.GetPath("trust_boundary.db");
        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO save_path_mappings (
                steam_app_id, game_name, platform, path_template, path_kind, source_name, priority, enabled, review_status
            ) VALUES
                ('400', 'Portal', 'windows', '%APPDATA%/Portal/ApprovedEnabled', 'Directory', 'Test', 10, 1, 'Approved'),
                ('400', 'Portal', 'windows', '%APPDATA%/Portal/PendingEnabled', 'Directory', 'Test', 20, 1, 'Pending'),
                ('400', 'Portal', 'windows', '%APPDATA%/Portal/NeedsFixEnabled', 'Directory', 'Test', 30, 1, 'NeedsFix'),
                ('400', 'Portal', 'windows', '%APPDATA%/Portal/RejectedEnabled', 'Directory', 'Test', 40, 1, 'Rejected'),
                ('400', 'Portal', 'windows', '%APPDATA%/Portal/ApprovedDisabled', 'Directory', 'Test', 50, 0, 'Approved');
            """;
            command.ExecuteNonQuery();
        }

        var repo = new SqliteSavePathMappingRepository(dbPath);

        // 1. Repo GetApprovedMappingsForApp
        IReadOnlyList<SavePathMapping> approvedRepo = repo.GetApprovedMappingsForApp("400", "windows");
        Assert.Single(approvedRepo);
        Assert.Equal("%APPDATA%/Portal/ApprovedEnabled", approvedRepo[0].PathTemplate);
        Assert.True(approvedRepo[0].Enabled);
        Assert.Equal("Approved", approvedRepo[0].ReviewStatus);

        // 2. Database GetApprovedMappingsForApp
        List<SavePathMapping> approvedDb = database.GetApprovedMappingsForApp("400", "windows");
        Assert.Single(approvedDb);
        Assert.Equal("%APPDATA%/Portal/ApprovedEnabled", approvedDb[0].PathTemplate);

        // 3. Include disabled returns all 5
        IReadOnlyList<SavePathMapping> all = repo.GetMappingsForApp("400", "windows", includeDisabled: true);
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public void MappingCountsAndStatuses_ReflectExactReviewAndEnabledStates()
    {
        string dbPath = _temp.GetPath("counts_test.db");
        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO save_path_mappings (
                steam_app_id, game_name, platform, path_template, path_kind, source_name, priority, enabled, review_status
            ) VALUES
                ('730', 'CS2', 'windows', '%APPDATA%/CS2/Path1', 'Directory', 'Test', 10, 1, 'Approved'),
                ('730', 'CS2', 'windows', '%APPDATA%/CS2/Path2', 'Directory', 'Test', 20, 1, 'Pending'),
                ('730', 'CS2', 'windows', '%APPDATA%/CS2/Path3', 'Directory', 'Test', 30, 0, 'NeedsFix'),
                ('730', 'CS2', 'windows', '%APPDATA%/CS2/Path4', 'Directory', 'Test', 40, 1, 'Rejected'),
                ('730', 'CS2', 'windows', '%APPDATA%/CS2/Path5', 'Directory', 'Test', 50, 0, 'Approved');
            """;
            command.ExecuteNonQuery();
        }

        var repo = new SqliteSavePathMappingRepository(dbPath);

        // CountApprovedMappings must strictly require BOTH enabled = 1 and review_status = 'Approved'
        Assert.Equal(1, repo.CountApprovedMappings("windows"));
        Assert.Equal(1, repo.CountPendingMappings("windows"));
        Assert.Equal(1, repo.CountNeedsFixMappings("windows"));

        var statuses = repo.GetMappingStatusesForApps(new[] { "730" }, "windows");
        Assert.True(statuses.ContainsKey("730"));
        SavePathMappingStatus status = statuses["730"];

        Assert.Equal(5, status.TotalMappings);
        Assert.Equal(3, status.EnabledMappings);
        Assert.Equal(1, status.ApprovedMappings);
        Assert.Equal(1, status.PendingMappings);
        Assert.Equal(1, status.NeedsFixMappings);
        Assert.Equal(1, status.RejectedMappings);
    }

    [Fact]
    public void LegacyDatabaseWithoutReviewColumns_MigratesNullReviewStatusToPending()
    {
        string dbPath = _temp.GetPath("legacy_raw.db");

        // Simulate legacy database without review_status column
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE save_path_mappings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_app_id TEXT NOT NULL,
                game_name TEXT NULL,
                platform TEXT NOT NULL,
                path_template TEXT NOT NULL,
                path_kind TEXT NOT NULL DEFAULT 'Directory',
                source_name TEXT NOT NULL,
                source_url TEXT NULL,
                source_license TEXT NULL,
                notes TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE (steam_app_id, platform, path_template)
            );

            INSERT INTO save_path_mappings (steam_app_id, game_name, platform, path_template, source_name, enabled)
            VALUES ('570', 'Dota 2', 'windows', '%APPDATA%/Dota2', 'Legacy', 1);
            """;
            command.ExecuteNonQuery();
        }

        // Opening through repository prepares review columns and migrates nulls to 'Pending'
        var repo = new SqliteSavePathMappingRepository(dbPath);

        // Legacy enabled row with no review status must NOT be treated as approved!
        IReadOnlyList<SavePathMapping> approved = repo.GetApprovedMappingsForApp("570", "windows");
        Assert.Empty(approved);

        IReadOnlyList<SavePathMapping> all = repo.GetMappingsForApp("570", "windows", includeDisabled: true);
        Assert.Single(all);
        Assert.Equal("Pending", all[0].ReviewStatus);
    }

    [Fact]
    public void MigrateLegacyMappings_WithTrustOptIn_PromotesLegacyEnabledRowsToApproved()
    {
        string dbPath = _temp.GetPath("legacy_migration.db");

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE save_path_mappings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_app_id TEXT NOT NULL,
                game_name TEXT NULL,
                platform TEXT NOT NULL,
                path_template TEXT NOT NULL,
                path_kind TEXT NOT NULL DEFAULT 'Directory',
                source_name TEXT NOT NULL,
                source_url TEXT NULL,
                source_license TEXT NULL,
                notes TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE (steam_app_id, platform, path_template)
            );

            INSERT INTO save_path_mappings (steam_app_id, game_name, platform, path_template, source_name, enabled)
            VALUES
                ('10', 'Counter-Strike', 'windows', '%APPDATA%/CS', 'Legacy', 1),
                ('20', 'TF Classic', 'windows', '%APPDATA%/TFC', 'Legacy', 0);
            """;
            command.ExecuteNonQuery();
        }

        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        // Act - explicit migration with opt-in to trust legacy enabled rows
        int updated = database.MigrateLegacyMappings(trustLegacyEnabledAsApproved: true);
        Assert.True(updated >= 1);

        var repo = new SqliteSavePathMappingRepository(dbPath);

        // App '10' was enabled, so it was promoted to Approved
        IReadOnlyList<SavePathMapping> approved10 = repo.GetApprovedMappingsForApp("10", "windows");
        Assert.Single(approved10);
        Assert.Equal("Approved", approved10[0].ReviewStatus);
        Assert.NotNull(approved10[0].ReviewedUtc);

        // App '20' was disabled, so it remained Pending
        IReadOnlyList<SavePathMapping> approved20 = repo.GetApprovedMappingsForApp("20", "windows");
        Assert.Empty(approved20);
    }

    [Fact]
    public void ApproveMapping_TransitionsPendingCandidateToApprovedAndEnabled()
    {
        string dbPath = _temp.GetPath("approve_actions.db");
        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        var candidates = new List<SavePathImportItem>
        {
            new(
                SteamAppId: "500",
                GameName: "Left 4 Dead",
                Platform: "windows",
                PathTemplate: "%APPDATA%/L4D",
                PathKind: "Directory",
                SourceName: "Community",
                SourceUrl: null,
                SourceLicense: null,
                Notes: null)
        };
        database.ImportMappings(candidates, enabled: false, reviewStatus: "Pending");

        var repo = new SqliteSavePathMappingRepository(dbPath);
        IReadOnlyList<SavePathMapping> all = repo.GetMappingsForApp("500", "windows", includeDisabled: true);
        Assert.Single(all);
        long mappingId = all[0].Id;

        Assert.Empty(repo.GetApprovedMappingsForApp("500", "windows"));

        // Act - approve by ID
        database.ApproveMapping(mappingId, "Verified by maintainer");

        // Assert
        IReadOnlyList<SavePathMapping> approved = repo.GetApprovedMappingsForApp("500", "windows");
        Assert.Single(approved);
        Assert.True(approved[0].Enabled);
        Assert.Equal("Approved", approved[0].ReviewStatus);
        Assert.Equal("Verified by maintainer", approved[0].ReviewNotes);
        Assert.NotNull(approved[0].ReviewedUtc);
    }

    [Fact]
    public void ApproveMappingsForApp_ApprovesAllMappingsForGivenSteamAppId()
    {
        string dbPath = _temp.GetPath("approve_app.db");
        var database = new SavePathDatabase(dbPath);
        database.Initialize();

        var candidates = new List<SavePathImportItem>
        {
            new("620", "Portal 2", "windows", "%USERPROFILE%/Saved Games/Portal2_1", "Directory", "Src", null, null, null, 10),
            new("620", "Portal 2", "windows", "%USERPROFILE%/Saved Games/Portal2_2", "Directory", "Src", null, null, null, 20)
        };
        database.ImportMappings(candidates, enabled: false, reviewStatus: "Pending");

        var repo = new SqliteSavePathMappingRepository(dbPath);
        Assert.Empty(repo.GetApprovedMappingsForApp("620", "windows"));

        // Act - approve all for app
        database.ApproveMappingsForApp("620", "Batch approved");

        // Assert
        IReadOnlyList<SavePathMapping> approved = repo.GetApprovedMappingsForApp("620", "windows");
        Assert.Equal(2, approved.Count);
        Assert.All(approved, m =>
        {
            Assert.True(m.Enabled);
            Assert.Equal("Approved", m.ReviewStatus);
            Assert.Equal("Batch approved", m.ReviewNotes);
        });
    }
}
