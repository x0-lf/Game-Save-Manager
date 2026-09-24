using GameSaves.Core.Data;
using GameSaves.Infrastructure.Data;
using GameSaves.Infrastructure.Platform;
using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameSaves.Tests
{
    // The desktop application has no bootstrap path of its own: before the
    // schema-initializing decorator, a machine that had only ever run the
    // desktop app got an empty database file and "no such table" from every
    // query. These tests pin both halves: the decorator makes a fresh
    // database usable, and a fresh database really is unusable without it,
    // so the decorator cannot be removed silently.
    public sealed class FirstRunSchemaBootstrapTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }

        [Fact]
        public void AFreshDatabasePath_ResolvedThroughTheDecorator_YieldsAQueryableSchema()
        {
            string databasePath = _temp.GetPath("fresh.db");
            var provider = new SchemaInitializingAppDatabasePathProvider(
                new TestDatabasePathProvider(databasePath));

            string resolved = provider.GetDatabasePath();
            var repository = new SqliteSavePathMappingRepository(resolved);

            Assert.Equal(databasePath, resolved);
            Assert.Equal(0, repository.CountApprovedMappings("windows"));
            Assert.Equal(0, repository.CountPendingMappings("windows"));
        }

        [Fact]
        public void AFreshDatabasePath_WithoutTheDecorator_StillFails_SoTheDecoratorIsLoadBearing()
        {
            string databasePath = _temp.GetPath("raw.db");
            var repository = new SqliteSavePathMappingRepository(databasePath);

            Assert.Throws<SqliteException>(
                () => repository.CountApprovedMappings("windows"));
        }

        [Fact]
        public void TheDecorator_InitializesEachDistinctPathOnce_AndIsRepeatSafe()
        {
            string databasePath = _temp.GetPath("repeat.db");
            var provider = new SchemaInitializingAppDatabasePathProvider(
                new TestDatabasePathProvider(databasePath));

            string first = provider.GetDatabasePath();
            string second = provider.GetDatabasePath();

            Assert.Equal(first, second);
            var repository = new SqliteSavePathMappingRepository(second);
            Assert.Equal(0, repository.CountNeedsFixMappings("windows"));
        }

        [Fact]
        public void AFailedMigration_Throws_AndTheNextCallRetries()
        {
            string databasePath = _temp.GetPath("retry.db");
            var migrator = new FailOnceMigrator();
            var provider = new SchemaInitializingAppDatabasePathProvider(
                new TestDatabasePathProvider(databasePath),
                migrator: migrator);

            SqliteException ex = Assert.Throws<SqliteException>(() => provider.GetDatabasePath());
            Assert.Contains("simulated", ex.Message);

            Assert.Equal(databasePath, provider.GetDatabasePath());
            Assert.Equal(2, migrator.Calls);
            Assert.Equal(0, new SqliteSavePathMappingRepository(databasePath).CountApprovedMappings("windows"));
        }

        private sealed class FailOnceMigrator : ISchemaMigrator
        {
            private readonly SchemaMigrator _real = new();

            public int Calls { get; private set; }

            public MigrationExecutionResult Migrate(string databasePath) =>
                ++Calls == 1
                    ? new MigrationExecutionResult(false, 0, 0, Array.Empty<string>(), null, false, "simulated failure")
                    : _real.Migrate(databasePath);

            public MigrationPlan Plan(string databasePath) => _real.Plan(databasePath);
            public string Backup(string databasePath) => _real.Backup(databasePath);
            public bool VerifyIntegrity(string databasePath, out string message) => _real.VerifyIntegrity(databasePath, out message);
            public IReadOnlyList<SchemaMigrationRecord> GetAppliedMigrations(string databasePath) => _real.GetAppliedMigrations(databasePath);
        }
    }
}
