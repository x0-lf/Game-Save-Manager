using GameSaves.App.Services;
using GameSaves.Core.Data;
using GameSaves.Core.Platform;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Data;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.Save;
using GameSaves.Infrastructure.Secrets;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameSaves.Tests;

/// <summary>
/// RELEASE-003. The checked-in fixtures under <c>Manager/GameSaves.Tests/Fixtures/</c>
/// are the database and the two settings files as a released version wrote
/// them. Every test here replays them into a temporary folder and proves that
/// the current code opens them without loss, and that a database written by a
/// newer version is refused in words without a single write. Nothing touches
/// <c>%LOCALAPPDATA%\GameSave\</c>; no fixture holds a readable secret.
/// </summary>
public sealed class MigrationFixtureTests : IDisposable
{
    private static readonly string FixtureRoot = Path.Combine(
        DocumentationIntegrityTests.FindRepoRoot(), "Manager", "GameSaves.Tests", "Fixtures");

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid FixtureFolderProfileId = Guid.Parse("7d2f8f0e-5c3a-4b61-9a7e-0f1c2d3e4a5b");
    private static readonly Guid FixtureGoogleProfileId = Guid.Parse("1b9c4e2a-6d7f-4a30-8c15-2e4f6a8b0c1d");
    private static readonly Guid FixtureBackupJobId = Guid.Parse("3a5c7e9b-1d2f-4b6a-9c8e-0f1a2b3c4d5e");
    private static readonly Guid FixtureSyncJobId = Guid.Parse("5e7a9c1b-3d4f-4c6e-8a2b-1c3d5e7f9a0b");

    private readonly TemporaryDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static string[] DatabaseFixtureNames() =>
        Directory.GetFiles(Path.Combine(FixtureRoot, "database"), "v*.sql")
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] SettingsFixtureNames(string pattern) =>
        Directory.GetFiles(Path.Combine(FixtureRoot, "settings"), pattern)
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => FixtureVersion(name))
            .ToArray();

    public static TheoryData<string> DatabaseFixtures() => [.. DatabaseFixtureNames()];

    public static TheoryData<string> UiSettingsFixtures() => [.. SettingsFixtureNames("ui-settings-v*.json")];

    public static TheoryData<string> SyncSettingsFixtures() => [.. SettingsFixtureNames("sync-settings-v*.json")];

    private static int FixtureVersion(string fileName) =>
        int.Parse(Regex.Match(fileName, @"-v(\d+)\.json$").Groups[1].Value, CultureInfo.InvariantCulture);

    // ---- the database fixtures ----

    [Theory]
    [MemberData(nameof(DatabaseFixtures))]
    public void EveryDatabaseFixture_MigratesToTheCurrentSchema_WithEveryRowIntact(string fixtureName)
    {
        string script = File.ReadAllText(Path.Combine(FixtureRoot, "database", fixtureName));
        int declaredSchema = SqliteScript.HeaderNumber(script, "schema-version");
        string database = _temp.GetPath(fixtureName + ".db");
        SqliteScript.Restore(script, database);

        Assert.Equal(0, MigratedDatabase.Scalar(database, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal((long)declaredSchema, MigratedDatabase.Scalar(database, "SELECT MAX(id) FROM schema_migrations;"));

        var migrator = new SchemaMigrator();
        Dictionary<string, (string[] Columns, string[] Rows)> before = SqliteScript.Rows(database);

        MigrationExecutionResult result = migrator.Migrate(database);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(declaredSchema, result.PreviousVersion);
        Assert.Equal(migrator.LatestKnownVersion, result.CurrentVersion);
        Assert.Equal(
            SqliteScript.Schema(MigratedDatabase.Create(_temp, "fresh-" + fixtureName + ".db")),
            SqliteScript.Schema(database));

        foreach ((string table, (string[] columns, string[] rows)) in before)
        {
            // Every column the released version wrote must still read back the
            // same values; a migration that drops or rewrites one fails here
            // and has to be acknowledged in this test, not discovered by a user.
            Assert.Equal(rows, SqliteScript.Rows(database, table, columns));
        }
    }

    [Fact]
    public void TheFixtureSet_CoversTheCurrentVersion_AndClaimsNoSchemaTheMigratorDoesNotKnow()
    {
        string version = ReleasePackagingTests.ProjectVersion();
        int latestKnown = new SchemaMigrator().LatestKnownVersion;

        Assert.True(
            File.Exists(Path.Combine(FixtureRoot, "database", $"v{version}.sql")),
            $"No database fixture for version {version}. A release needs its fixture set: run " +
            "Manager/.artifacts/bin/GameSaves.Tests/release/GameSaves.Tests.exe -explicit on -method " +
            "\"GameSaves.Tests.MigrationFixtureTests.RegenerateFixturesForTheCurrentVersion\" and check in the result.");

        foreach (string fixture in DatabaseFixtureNames())
        {
            string script = File.ReadAllText(Path.Combine(FixtureRoot, "database", fixture));
            Assert.Equal($"v{SqliteScript.HeaderText(script, "app-version")}.sql", fixture);
            Assert.True(Version.TryParse(SqliteScript.HeaderText(script, "app-version"), out _));
            Assert.InRange(SqliteScript.HeaderNumber(script, "schema-version"), 1, latestKnown);
            Assert.InRange(new FileInfo(Path.Combine(FixtureRoot, "database", fixture)).Length, 1, 1024 * 1024);
        }

        Assert.True(File.Exists(Path.Combine(FixtureRoot, "settings", $"ui-settings-v{AppUiSettings.CurrentSchemaVersion}.json")));
        Assert.True(File.Exists(Path.Combine(FixtureRoot, "settings", $"sync-settings-v{SyncUiSettings.CurrentSchemaVersion}.json")));
    }

    [Fact]
    public async Task TheFixturesSecretPlaceholder_IsReportedUnreadable_NeverThrownOrDecrypted()
    {
        string database = RestoreCurrentFixture();
        var store = new WindowsDpapiSecretStore(database, new FixedUtcClock(Now));

        SecretReadResult read = await store.ReadAsync(new SecretKey(FixtureGoogleProfileId, SecretNames.OAuthTokenData));

        Assert.True(
            read.Status is SecretReadStatus.Corrupted or SecretReadStatus.Unavailable,
            $"a DPAPI blob from another machine must read as corrupted or unavailable, not {read.Status}");
        Assert.Null(read.Value);
    }

    // ---- a database from a newer version ----

    [Fact]
    public void ADatabaseFromANewerVersion_IsRefusedInWords_WithoutAWrite()
    {
        string database = NewerDatabase();
        string before = Sha256(database);
        var migrator = new SchemaMigrator();

        MigrationPlan plan = migrator.Plan(database);
        MigrationExecutionResult result = migrator.Migrate(database);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("newer version of Game Save Manager", plan.Refusal, StringComparison.Ordinal);
        Assert.False(result.Success);
        Assert.Equal(plan.Refusal, result.ErrorMessage);
        Assert.Contains("schema version 99", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains($"up to {migrator.LatestKnownVersion}", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.PreMigrationBackupPath);
        Assert.False(result.RolledBack);
        Assert.Equal(99, result.PreviousVersion);
        Assert.Equal(before, Sha256(database));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(database)!, "backups")));
    }

    [Fact]
    public void TheWindowsStartupPath_RefusesANewerDatabase_BeforeAnyRepositoryOpensIt()
    {
        string database = NewerDatabase();
        string before = Sha256(database);

        // AppServices.Build is what App.OnFrameworkInitializationCompleted
        // runs; resolving the shell is what opens the database. The exception
        // that comes out is the one the startup-failure window shows.
        using ServiceProvider services = GameSaves.App.AppServices.Build(collection =>
        {
            collection.AddSingleton<IAppDatabasePathProvider>(provider =>
                ServiceCollectionExtensions.CreateDatabasePathProvider(provider, new TestDatabasePathProvider(database)));
            collection.AddSingleton<IUiSettingsStore>(new UiSettingsStore(_temp.GetPath("ui-settings.json")));
            collection.AddSingleton<ISyncSettingsStore>(new SyncSettingsStore(_temp.GetPath("sync-settings.json")));
        });

        SqliteException refusal = Assert.Throws<SqliteException>(
            () => services.GetRequiredService<GameSaves.App.ViewModels.MainWindowViewModel>());

        Assert.Contains("newer version of Game Save Manager", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, Sha256(database));
    }

    [Fact]
    public void TheHeadlessHost_RefusesANewerDatabase_WithTheFailedOutcome_AndWritesNothing()
    {
        string database = NewerDatabase();
        string before = Sha256(database);

        int? exitCode = GameSaves.App.Program.RunScheduledJob(
            ["--run-job", Guid.NewGuid().ToString()],
            collection => collection.AddSingleton<IAppDatabasePathProvider>(provider =>
                ServiceCollectionExtensions.CreateDatabasePathProvider(provider, new TestDatabasePathProvider(database))));

        Assert.Equal((int)ScheduledBackupOutcome.Failed, exitCode);
        Assert.Equal(before, Sha256(database));
        Assert.Equal(8L, MigratedDatabase.Scalar(database, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    // ---- the settings fixtures ----

    [Theory]
    [MemberData(nameof(UiSettingsFixtures))]
    public void EveryUiSettingsFixture_LoadsToTheCurrentRecord_WithTheDocumentedDefaults(string fixtureName)
    {
        int version = FixtureVersion(fixtureName);
        string path = CopyFixture("settings", fixtureName);
        Assert.Equal(version, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("SchemaVersion").GetInt32());

        AppUiSettings loaded = new UiSettingsStore(path).Load();

        Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
        Assert.Equal(9, loaded.RailLayout.TabOrder.Count);

        if (version < 11)
        {
            Assert.False(loaded.CheckForUpdatesOnStartup);
            Assert.Null(loaded.LastUpdateCheckUtc);
        }

        if (version < 10)
            Assert.Equal(AppUiSettings.BackupFormatZip, loaded.NewBackupFormat);

        if (version < 8)
            Assert.Equal(UiRailLayoutSettings.TabDashboard, loaded.StartupTabKey);
    }

    [Fact]
    public void TheUiSettingsFixtures_CoverEveryVersionFromTheFirstSchemaToTheCurrentOne()
    {
        int[] versions = SettingsFixtureNames("ui-settings-v*.json").Select(FixtureVersion).ToArray();

        Assert.Equal(Enumerable.Range(2, AppUiSettings.CurrentSchemaVersion - 1), versions);
    }

    [Theory]
    [MemberData(nameof(SyncSettingsFixtures))]
    public void EverySyncSettingsFixture_LoadsAndMigrates_WithoutLoss(string fixtureName)
    {
        int version = FixtureVersion(fixtureName);
        string path = CopyFixture("settings", fixtureName);
        var store = new SyncSettingsStore(path);

        SyncUiSettings loaded = store.Load();

        Assert.Equal(version, loaded.SchemaVersion);
        Assert.Equal(version >= SyncUiSettings.CurrentSchemaVersion, loaded.LegacyProfileMigrationCompleted);
        Assert.NotEqual(SyncProviderKind.Unknown, loaded.SelectedProviderKind);

        var profiles = new InMemorySyncRemoteProfileRepository();
        SyncUiSettings migrated = new SyncRemoteProfileMigrationService(store, profiles, new FixedUtcClock(Now)).LoadAndMigrate();

        Assert.Equal(SyncUiSettings.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.True(migrated.LegacyProfileMigrationCompleted);
        Assert.Equal(loaded.SelectedProviderKind, migrated.SelectedProviderKind);

        // A pre-profile file that named a remote becomes exactly one profile;
        // a current file is left alone.
        if (version < SyncUiSettings.CurrentSchemaVersion)
        {
            SyncRemoteProfile profile = Assert.Single(profiles.GetAll());
            Assert.Equal(loaded.SelectedProviderKind, profile.ProviderKind);
            Assert.Equal(profile.Id, migrated.SelectedRemoteProfileId);
        }
        else
        {
            Assert.Empty(profiles.GetAll());
            Assert.Equal(loaded, migrated);
        }
    }

    // ---- regeneration ----

    /// <summary>
    /// Writes the current version's fixture set: the database as the real
    /// migrator, seeder and repositories produce it after representative use,
    /// plus the UI and sync settings files as their stores save them. Run it
    /// when cutting a release (see docs/development.md), never against real
    /// data: everything is built in a temporary folder.
    /// </summary>
    [Fact(Explicit = true)]
    public void RegenerateFixturesForTheCurrentVersion()
    {
        string version = ReleasePackagingTests.ProjectVersion();
        string database = MigratedDatabase.Create(_temp, "regenerate.db");

        // What a first run writes: the bundled curated mappings, approved.
        new CuratedMappingSeeder().Seed(database);

        // One pending mapping with a verification result, as a review leaves it.
        MigratedDatabase.Execute(database, """
            INSERT INTO save_path_mappings
                (steam_app_id, game_name, platform, path_template, path_kind, source_name, source_url, priority, enabled, review_status, created_utc, updated_utc)
            VALUES
                ('440', 'Fixture Pending Game', 'windows', '%USERPROFILE%\Documents\Fixture\Saves', 'Directory', 'fixture', 'https://example.invalid/fixture', 100, 0, 'Pending', '2026-10-21 20:00:00', '2026-10-21 20:00:00');
            INSERT INTO verification_results
                (mapping_id, steam_app_id, expanded_path, normalized_path, exists_flag, is_directory, file_count, total_bytes, confidence, last_verified_utc)
            VALUES
                ((SELECT id FROM save_path_mappings WHERE steam_app_id = '440' AND platform = 'windows'), '440', 'C:\Users\fixture\Documents\Fixture\Saves', 'c:\users\fixture\documents\fixture\saves', 1, 1, 3, 4096, 2, '2026-10-21 20:00:00');
            INSERT INTO game_titles (steam_app_id, title, platform_hint, source_name, first_seen_utc, last_updated_utc)
            VALUES ('440', 'Fixture Pending Game', 'windows', 'fixture', '2026-10-21 20:00:00', '2026-10-21 20:00:00');
            INSERT INTO external_harvest_runs (source_name, started_utc, completed_utc, output_root, requested_per_minute, titles_indexed, titles_processed, titles_failed, mappings_extracted)
            VALUES ('pcgamingwiki', '2026-10-21 19:00:00', '2026-10-21 19:05:00', 'D:\Harvest', 10, 1, 1, 0, 1);
            INSERT INTO external_pcgamingwiki_pages (page_id, page_name, display_title, steam_app_ids, source_url, harvest_status, last_fetched_utc)
            VALUES (4242, 'Fixture_Pending_Game', 'Fixture Pending Game', '440', 'https://www.pcgamingwiki.com/wiki/Fixture_Pending_Game', 'Extracted', '2026-10-21 19:01:00');
            INSERT INTO backup_runs (started_utc, completed_utc, destination_root, dry_run, item_count, total_bytes)
            VALUES ('2026-10-21 18:00:00', '2026-10-21 18:00:05', 'D:\Backups', 0, 1, 4096);
            INSERT INTO backup_items (backup_run_id, steam_app_id, game_name, source_path, destination_path, copied, bytes, sha256)
            VALUES (1, '440', 'Fixture Pending Game', 'C:\Users\fixture\Documents\Fixture\Saves\slot1.sav', 'D:\Backups\440\slot1.sav', 1, 4096, '0000000000000000000000000000000000000000000000000000000000000000');
            """);

        // History, presets and scheduled jobs through the real repositories.
        var history = new SqliteTransferHistoryRepository(database);
        history.RecordRun(new TransferRunRecord(
            Kind: TransferRunKind.ManualBackup, GameName: "Fixture Pending Game", SteamAppId: "440",
            SourceAccountId: "111", TargetAccountId: "-", DryRun: false, OverwriteEnabled: false, BackupEnabled: false,
            FilesConsidered: 1, FilesCopied: 1, FilesSkipped: 0, FilesFailed: 0, BytesCopied: 4096, FilesBackedUp: 0,
            BackupRootPath: @"D:\Backups", BlockedReason: null, StartedUtc: Now.AddHours(-2), CompletedUtc: Now.AddHours(-2).AddSeconds(5),
            Items: [new TransferRunItemRecord(@"C:\Users\fixture\Documents\Fixture\Saves\slot1.sav", @"D:\Backups\440\slot1.sav", 4096, true, "Copied", null, null)]));
        history.RecordRun(new TransferRunRecord(
            Kind: TransferRunKind.Sync, GameName: "(backup sync)", SteamAppId: "-",
            SourceAccountId: "device", TargetAccountId: "remote", DryRun: false, OverwriteEnabled: false, BackupEnabled: false,
            FilesConsidered: 1, FilesCopied: 1, FilesSkipped: 0, FilesFailed: 0, BytesCopied: 4096, FilesBackedUp: 0,
            BackupRootPath: null, BlockedReason: null, StartedUtc: Now.AddHours(-1), CompletedUtc: Now.AddHours(-1).AddSeconds(3),
            Items: [new TransferRunItemRecord("run-20261021-180000", "remote/run-20261021-180000", 4096, true, "Uploaded", null, null)]));

        new SqliteManualBackupPresetRepository(database).Save(
            new ManualBackupPreset(0, "Fixture preset", @"D:\Backups", true, true, Now.AddDays(-1), Now.AddHours(-2)));

        var profiles = new SqliteSyncRemoteProfileRepository(database, new SyncRemoteProfileSettingsSerializer());
        profiles.Create(new SyncRemoteProfile(
            FixtureFolderProfileId, "Fixture folder", SyncProviderKind.LocalFolder, null, @"D:\GameSaveSync",
            new LocalFolderSyncRemoteSettings(@"D:\GameSaveSync"), Now.AddDays(-3), Now.AddDays(-3), Now.AddHours(-1), Now.AddHours(-1), null));
        profiles.Create(new SyncRemoteProfile(
            FixtureGoogleProfileId, "Fixture Google Drive", SyncProviderKind.GoogleDrive, "fixture@example.invalid", "GameSaveManager",
            new GoogleDriveSyncRemoteSettings("fixture@example.invalid", GoogleDriveAuthorizationScopes.DriveFile),
            Now.AddDays(-2), Now.AddDays(-2), null, null, "fixture-folder-id"));

        // A DPAPI blob is bound to the machine and user that wrote it, so a
        // fixture carries a placeholder the secret store reports as unreadable.
        MigratedDatabase.Execute(database, $"""
            INSERT INTO protected_sync_secrets (owner_id, secret_name, protection_scheme, format_version, protected_payload, created_utc, updated_utc)
            VALUES ('{FixtureGoogleProfileId:D}', '{SecretNames.OAuthTokenData}', '{WindowsDpapiSecretStore.ProtectionScheme}', {WindowsDpapiSecretStore.FormatVersion}, X'00112233445566778899AABBCCDDEEFF', '2026-10-19T20:00:00.0000000+00:00', '2026-10-19T20:00:00.0000000+00:00');
            """);

        var backupJobs = new SqliteScheduledBackupJobRepository(database);
        backupJobs.Add(new ScheduledBackupJob(FixtureBackupJobId, "440", "Fixture Pending Game", "111", @"D:\Backups", true, false, Now.AddDays(-1)));
        backupJobs.RecordOutcome(FixtureBackupJobId, Now.AddHours(-2), "Completed: Backed up 1 file(s).");
        var syncJobs = new SqliteScheduledSyncJobRepository(database);
        syncJobs.Add(new ScheduledSyncJob(FixtureSyncJobId, FixtureFolderProfileId, "Fixture folder", true, false, true, Now.AddDays(-1)));
        syncJobs.RecordOutcome(FixtureSyncJobId, Now.AddHours(-1), "Completed: Uploaded 1 run(s).");

        SqliteConnection.ClearAllPools();
        string databaseFixture = Path.Combine(FixtureRoot, "database", $"v{version}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFixture)!);
        File.WriteAllText(
            databaseFixture,
            SqliteScript.Dump(database, version, new SchemaMigrator().LatestKnownVersion),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string uiPath = _temp.GetPath("ui-settings.json");
        new UiSettingsStore(uiPath).Save(AppUiSettings.Default with
        {
            ThemeChoice = AppUiSettings.ThemeDark,
            AccentTheme = AppUiSettings.AccentTeal,
            CheckForUpdatesOnStartup = true,
            LastUpdateCheckUtc = Now,
        });
        File.Copy(uiPath, Path.Combine(FixtureRoot, "settings", $"ui-settings-v{AppUiSettings.CurrentSchemaVersion}.json"), overwrite: true);

        string syncPath = _temp.GetPath("sync-settings.json");
        new SyncSettingsStore(syncPath).Save(SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.LocalFolder,
            LocalFolderPath = @"D:\GameSaveSync",
            SelectedRemoteProfileId = FixtureFolderProfileId,
        });
        File.Copy(syncPath, Path.Combine(FixtureRoot, "settings", $"sync-settings-v{SyncUiSettings.CurrentSchemaVersion}.json"), overwrite: true);

        Assert.True(File.Exists(databaseFixture));
    }

    // ---- helpers ----

    private string RestoreCurrentFixture()
    {
        string version = ReleasePackagingTests.ProjectVersion();
        string database = _temp.GetPath($"v{version}.db");
        SqliteScript.Restore(File.ReadAllText(Path.Combine(FixtureRoot, "database", $"v{version}.sql")), database);
        return database;
    }

    // A database the current migrator has fully migrated, plus one migration
    // row from a later version: exactly what an older install sees after the
    // user downgrades, or after a newer copy on another machine synced it.
    private string NewerDatabase()
    {
        string database = MigratedDatabase.Create(_temp, "future.db");
        MigratedDatabase.Execute(
            database,
            "INSERT INTO schema_migrations (id, name, applied_utc) VALUES (99, 'V099__FromTheFuture', '2027-01-01 00:00:00');");
        SqliteConnection.ClearAllPools();
        return database;
    }

    private string CopyFixture(string folder, string fileName)
    {
        string path = _temp.GetPath(fileName);
        File.Copy(Path.Combine(FixtureRoot, folder, fileName), path);
        return path;
    }

    // Pooled SQLite handles keep the file open for writing; read beside them.
    private static string Sha256(string path)
    {
        SqliteConnection.ClearAllPools();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// A plain-SQL dump of a SQLite database (schema objects, then one INSERT per
/// row) and its replay. Text rather than a binary <c>.db</c> so a fixture is
/// readable and diffable in a review and small enough to live in the repo.
/// </summary>
internal static class SqliteScript
{
    public static string Dump(string databasePath, string appVersion, int schemaVersion)
    {
        var script = new StringBuilder();
        script.Append("-- Game Save Manager database fixture\n");
        script.Append(CultureInfo.InvariantCulture, $"-- app-version: {appVersion}\n");
        script.Append(CultureInfo.InvariantCulture, $"-- schema-version: {schemaVersion}\n");
        script.Append("-- generated-by: GameSaves.Tests MigrationFixtureTests.RegenerateFixturesForTheCurrentVersion\n");
        script.Append("-- Replayed by MigrationFixtureTests into a temporary file; never run it against a real database.\n");
        script.Append("PRAGMA foreign_keys = OFF;\nBEGIN;\n");

        using var connection = Open(databasePath);
        List<object?[]> objects = Query(
            connection,
            "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' " +
            "ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 WHEN 'view' THEN 2 ELSE 3 END, rowid;");

        foreach (object?[] row in objects)
            script.Append(((string)row[2]!).Trim()).Append(";\n");

        foreach (string table in objects.Where(row => (string)row[0]! == "table").Select(row => (string)row[1]!))
        {
            string[] columns = Columns(connection, table);
            string columnList = string.Join(", ", columns.Select(Quote));

            foreach (object?[] row in Query(connection, $"SELECT {columnList} FROM {Quote(table)} ORDER BY rowid;"))
            {
                script.Append("INSERT INTO ").Append(Quote(table)).Append(" (").Append(columnList).Append(") VALUES (")
                    .Append(string.Join(", ", row.Select(Literal))).Append(");\n");
            }
        }

        script.Append("COMMIT;\n");
        return script.ToString();
    }

    public static void Restore(string script, string databasePath)
    {
        using var connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    /// <summary>Every schema object with its statement, whitespace-normalised, in a stable order.</summary>
    public static string[] Schema(string databasePath)
    {
        using var connection = Open(databasePath);
        return Query(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;")
            .Select(row => $"{row[0]} {row[1]} on {row[2]}: {Regex.Replace((string?)row[3] ?? string.Empty, @"\s+", " ").Trim()}")
            .ToArray();
    }

    public static Dictionary<string, (string[] Columns, string[] Rows)> Rows(string databasePath)
    {
        using var connection = Open(databasePath);
        var result = new Dictionary<string, (string[], string[])>(StringComparer.Ordinal);

        foreach (string table in Query(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")
                     .Select(row => (string)row[0]!))
        {
            string[] columns = Columns(connection, table);
            result[table] = (columns, Rows(connection, table, columns));
        }

        return result;
    }

    public static string[] Rows(string databasePath, string table, string[] columns)
    {
        using var connection = Open(databasePath);
        return Rows(connection, table, columns);
    }

    public static int HeaderNumber(string script, string key) =>
        int.Parse(HeaderText(script, key), CultureInfo.InvariantCulture);

    public static string HeaderText(string script, string key)
    {
        Match match = Regex.Match(script, $@"^-- {Regex.Escape(key)}: (?<value>\S+)\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"fixture header '-- {key}:' missing");
        return match.Groups["value"].Value;
    }

    public static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        _ => "'" + value.ToString()!.Replace("'", "''", StringComparison.Ordinal) + "'",
    };

    private static string[] Rows(SqliteConnection connection, string table, string[] columns) =>
        Query(connection, $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)} ORDER BY rowid;")
            .Select(row => string.Join(" | ", row.Select(Literal)))
            .ToArray();

    private static string[] Columns(SqliteConnection connection, string table) =>
        Query(connection, $"PRAGMA table_info({Quote(table)});").Select(row => (string)row[1]!).ToArray();

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static List<object?[]> Query(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<object?[]>();

        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row);
        }

        return rows;
    }
}
