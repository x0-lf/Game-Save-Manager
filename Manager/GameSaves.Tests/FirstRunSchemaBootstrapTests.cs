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
    }
}
