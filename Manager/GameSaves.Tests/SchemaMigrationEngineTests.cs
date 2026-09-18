using GameSaves.Core.Data;
using GameSaves.Core.Platform;
using GameSaves.Infrastructure.Data;
using GameSaves.Infrastructure.Data.Migrations;
using GameSaves.Infrastructure.Platform;
using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class SchemaMigrationEngineTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }

        [Fact]
        public void Migrate_OnNewDatabase_AppliesAllDefaultMigrationsSequentially()
        {
            string dbPath = _temp.GetPath("new_db.db");
            var migrator = new SchemaMigrator();

            MigrationExecutionResult result = migrator.Migrate(dbPath);

            Assert.True(result.Success);
            Assert.Equal(0, result.PreviousVersion);
            Assert.Equal(4, result.CurrentVersion);
            Assert.Equal(4, result.AppliedMigrations.Count);
            Assert.False(result.RolledBack);
            Assert.Null(result.ErrorMessage);

            // Verify tables and indexes exist
            Assert.True(TableExists(dbPath, "schema_migrations"));
            Assert.True(TableExists(dbPath, "save_path_mappings"));
            Assert.True(TableExists(dbPath, "verification_results"));
            Assert.True(TableExists(dbPath, "backup_runs"));
            Assert.True(TableExists(dbPath, "backup_items"));
            Assert.True(TableExists(dbPath, "game_titles"));
            Assert.True(TableExists(dbPath, "external_pcgamingwiki_pages"));
            Assert.True(TableExists(dbPath, "external_harvest_runs"));
            Assert.True(TableExists(dbPath, "sync_remote_profiles"));
            Assert.True(TableExists(dbPath, "protected_sync_secrets"));
            Assert.True(TableExists(dbPath, "transfer_runs"));
            Assert.True(TableExists(dbPath, "transfer_items"));
            Assert.True(TableExists(dbPath, "manual_backup_presets"));

            Assert.True(IndexExists(dbPath, "idx_game_titles_source"));
            Assert.True(IndexExists(dbPath, "idx_save_path_mappings_source_enabled"));
            Assert.True(IndexExists(dbPath, "idx_save_path_mappings_review_status"));

            // Verify schema_migrations rows
            IReadOnlyList<SchemaMigrationRecord> applied = migrator.GetAppliedMigrations(dbPath);
            Assert.Equal(4, applied.Count);
            Assert.Equal(1, applied[0].Id);
            Assert.Equal("V001__BaselineSchema", applied[0].Name);
            Assert.Equal(2, applied[1].Id);
            Assert.Equal("V002__ReviewColumnsAndProvenance", applied[1].Name);
            Assert.Equal(3, applied[2].Id);
            Assert.Equal("V003__SyncAndSecretStorage", applied[2].Name);
            Assert.Equal(4, applied[3].Id);
            Assert.Equal("V004__CatalogAndMappingIndexes", applied[3].Name);
        }

        [Fact]
        public void Migrate_WhenAlreadyUpToDate_IsIdempotentAndCreatesNoBackups()
        {
            string dbPath = _temp.GetPath("idempotent.db");
            var migrator = new SchemaMigrator();

            MigrationExecutionResult firstRun = migrator.Migrate(dbPath);
            Assert.True(firstRun.Success);
            Assert.Equal(4, firstRun.CurrentVersion);

            MigrationExecutionResult secondRun = migrator.Migrate(dbPath);
            Assert.True(secondRun.Success);
            Assert.Equal(4, secondRun.PreviousVersion);
            Assert.Equal(4, secondRun.CurrentVersion);
            Assert.Empty(secondRun.AppliedMigrations);
            Assert.Null(secondRun.PreMigrationBackupPath);
            Assert.False(secondRun.RolledBack);

            string backupDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
            if (Directory.Exists(backupDir))
            {
                Assert.Empty(Directory.GetFiles(backupDir, "*.db"));
            }
        }

        [Fact]
        public void Migrate_WhenPendingMigrationsExist_CreatesPreMigrationBackup()
        {
            string dbPath = _temp.GetPath("pending_backup.db");

            // Initial migration with only V001
            var initialMigrator = new SchemaMigrator(new ISchemaMigration[]
            {
                new V001__BaselineSchema()
            });

            MigrationExecutionResult initialResult = initialMigrator.Migrate(dbPath);
            Assert.True(initialResult.Success);
            Assert.Equal(1, initialResult.CurrentVersion);

            // Second migration with full suite (V001 - V004)
            var fullMigrator = new SchemaMigrator();
            MigrationExecutionResult updateResult = fullMigrator.Migrate(dbPath);

            Assert.True(updateResult.Success);
            Assert.Equal(1, updateResult.PreviousVersion);
            Assert.Equal(4, updateResult.CurrentVersion);
            Assert.Equal(3, updateResult.AppliedMigrations.Count);
            Assert.NotNull(updateResult.PreMigrationBackupPath);
            Assert.True(File.Exists(updateResult.PreMigrationBackupPath));
        }

        [Fact]
        public void Plan_ReportsAccurateCurrentTargetAndPendingMigrations()
        {
            string dbPath = _temp.GetPath("plan_test.db");

            // Run with V001 only
            var partialMigrator = new SchemaMigrator(new ISchemaMigration[]
            {
                new V001__BaselineSchema()
            });
            partialMigrator.Migrate(dbPath);

            // Plan with full migrator
            var fullMigrator = new SchemaMigrator();
            MigrationPlan plan = fullMigrator.Plan(dbPath);

            Assert.Equal(dbPath, plan.DatabasePath);
            Assert.Equal(1, plan.CurrentVersion);
            Assert.Equal(4, plan.TargetVersion);
            Assert.True(plan.IntegrityCheckPassed);
            Assert.Equal("ok", plan.IntegrityMessage);
            Assert.Equal(3, plan.PendingMigrations.Count);
            Assert.Equal("V002__ReviewColumnsAndProvenance", plan.PendingMigrations[0].Name);
            Assert.Equal("V003__SyncAndSecretStorage", plan.PendingMigrations[1].Name);
            Assert.Equal("V004__CatalogAndMappingIndexes", plan.PendingMigrations[2].Name);
            Assert.NotNull(plan.PlannedBackupPath);
        }

        [Fact]
        public void Plan_DoesNotMutateDatabase()
        {
            string dbPath = _temp.GetPath("dry_run.db");

            var partialMigrator = new SchemaMigrator(new ISchemaMigration[]
            {
                new V001__BaselineSchema()
            });
            partialMigrator.Migrate(dbPath);

            var fullMigrator = new SchemaMigrator();
            MigrationPlan plan = fullMigrator.Plan(dbPath);

            Assert.Equal(3, plan.PendingMigrations.Count);

            // Verify database was NOT changed
            IReadOnlyList<SchemaMigrationRecord> applied = fullMigrator.GetAppliedMigrations(dbPath);
            Assert.Single(applied);
            Assert.Equal(1, applied[0].Id);
            Assert.False(TableExists(dbPath, "sync_remote_profiles"));
        }

        [Fact]
        public void Migrate_WhenMigrationFails_RollsBackTransactionAndRestoresPreMigrationSnapshot()
        {
            string dbPath = _temp.GetPath("rollback_test.db");

            // 1. Initial good migration
            var initialMigrator = new SchemaMigrator(new ISchemaMigration[]
            {
                new V001__BaselineSchema()
            });
            MigrationExecutionResult initialResult = initialMigrator.Migrate(dbPath);
            Assert.True(initialResult.Success);

            // Insert canary record into save_path_mappings to verify data is not corrupted or lost
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                INSERT INTO save_path_mappings (steam_app_id, platform, path_template, source_name)
                VALUES ('400', 'windows', '%USERPROFILE%/Portal', 'ManualTest');
                """;
                cmd.ExecuteNonQuery();
            }

            Assert.Equal(1, GetRowCount(dbPath, "save_path_mappings"));

            // 2. Prepare migrator with a failing migration V2
            var failingMigration = new TestFailingMigration(2, "V002__IntentionalFailure", "Fails intentionally to test rollback.");
            var failingMigrator = new SchemaMigrator(new ISchemaMigration[]
            {
                new V001__BaselineSchema(),
                failingMigration
            });

            MigrationExecutionResult failResult = failingMigrator.Migrate(dbPath);

            Assert.False(failResult.Success);
            Assert.True(failResult.RolledBack);
            Assert.NotNull(failResult.ErrorMessage);
            Assert.Contains("Intentional migration error", failResult.ErrorMessage);
            Assert.NotNull(failResult.PreMigrationBackupPath);
            Assert.True(File.Exists(failResult.PreMigrationBackupPath));

            // 3. Verify canary record is intact and database restored to version 1
            Assert.Equal(1, GetRowCount(dbPath, "save_path_mappings"));
            IReadOnlyList<SchemaMigrationRecord> applied = failingMigrator.GetAppliedMigrations(dbPath);
            Assert.Single(applied);
            Assert.Equal(1, applied[0].Id);

            // Verify failing migration table artifact does not exist
            Assert.False(TableExists(dbPath, "test_failing_table"));
        }

        [Fact]
        public void VerifyIntegrity_DetectsCorruptedDatabase_AndPreventsMigration()
        {
            string dbPath = _temp.GetPath("corrupted.db");
            File.WriteAllBytes(dbPath, new byte[] { 0x47, 0x41, 0x52, 0x42, 0x41, 0x47, 0x45, 0x00, 0x11, 0x22 });

            var migrator = new SchemaMigrator();
            bool passed = migrator.VerifyIntegrity(dbPath, out string message);

            Assert.False(passed);
            Assert.False(string.IsNullOrWhiteSpace(message));

            MigrationExecutionResult result = migrator.Migrate(dbPath);
            Assert.False(result.Success);
            Assert.Contains("integrity check failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void VerifyIntegrity_OnNonExistentDatabase_ReturnsOk()
        {
            string dbPath = _temp.GetPath("does_not_exist.db");
            var migrator = new SchemaMigrator();

            bool passed = migrator.VerifyIntegrity(dbPath, out string message);
            Assert.True(passed);
            Assert.Contains("clean state", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Backup_PrunesSnapshots_WhenExceedingRetentionLimit()
        {
            string dbPath = _temp.GetPath("prune_test.db");
            var migrator = new SchemaMigrator();
            migrator.Migrate(dbPath);

            string backupDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");

            // Generate 12 backups
            for (int i = 0; i < 12; i++)
            {
                migrator.Backup(dbPath);
                Thread.Sleep(10); // Ensure timestamp divergence
            }

            // Force migration/prune preserves at most DefaultBackupRetentionCount
            migrator.Migrate(dbPath, forceBackup: true);

            var prunedFiles = Directory.GetFiles(backupDir, "gamesave-pre-migration-*.db");
            Assert.True(prunedFiles.Length <= SchemaMigrator.DefaultBackupRetentionCount,
                $"Expected at most {SchemaMigrator.DefaultBackupRetentionCount} backups, but found {prunedFiles.Length}.");
        }

        [Fact]
        public void SchemaInitializingAppDatabasePathProvider_RunsMigrationsAutomatically()
        {
            string dbPath = _temp.GetPath("provider_migrated.db");
            var inner = new CustomPathProvider(dbPath);
            var migrator = new SchemaMigrator();
            var seeder = new CuratedMappingSeeder();

            var provider = new SchemaInitializingAppDatabasePathProvider(inner, seeder, migrator);

            string resolvedPath = provider.GetDatabasePath();
            Assert.Equal(dbPath, resolvedPath);

            // Database should be fully migrated and seeded
            IReadOnlyList<SchemaMigrationRecord> applied = migrator.GetAppliedMigrations(dbPath);
            Assert.Equal(4, applied.Count);

            var repository = new SqliteSavePathMappingRepository(dbPath);
            Assert.True(repository.CountApprovedMappings("windows") > 0);
        }

        [Fact]
        public void SavePathDatabase_Initialize_ExecutesSchemaMigrations()
        {
            string dbPath = _temp.GetPath("savepath_init.db");
            var database = new SavePathDatabase(dbPath);

            database.Initialize();

            var migrator = new SchemaMigrator();
            IReadOnlyList<SchemaMigrationRecord> applied = migrator.GetAppliedMigrations(dbPath);
            Assert.Equal(4, applied.Count);
            Assert.True(TableExists(dbPath, "schema_migrations"));
            Assert.True(TableExists(dbPath, "sync_remote_profiles"));
        }

        private static bool TableExists(string dbPath, string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table;";
            command.Parameters.AddWithValue("$table", tableName);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
        }

        private static bool IndexExists(string dbPath, string indexName)
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name;";
            command.Parameters.AddWithValue("$name", indexName);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
        }

        private static int GetRowCount(string dbPath, string tableName)
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        private sealed class CustomPathProvider : IAppDatabasePathProvider
        {
            private readonly string _path;
            public CustomPathProvider(string path) => _path = path;
            public string GetDatabasePath() => _path;
        }

        private sealed class TestFailingMigration : ISchemaMigration
        {
            public TestFailingMigration(int version, string name, string description)
            {
                Version = version;
                Name = name;
                Description = description;
            }

            public int Version { get; }
            public string Name { get; }
            public string Description { get; }

            public void Up(DbConnection connection, DbTransaction transaction)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE test_failing_table (id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();

                throw new InvalidOperationException("Intentional migration error for rollback test.");
            }
        }
    }
}
