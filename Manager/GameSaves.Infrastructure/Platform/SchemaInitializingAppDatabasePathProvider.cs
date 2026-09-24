using GameSaves.Core.Data;
using GameSaves.Core.Platform;
using GameSaves.Core.Save;
using GameSaves.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Platform
{
    // The desktop application historically assumed the database schema
    // already existed, because only the CLI called SavePathDatabase
    // .Initialize(). On a machine that has only ever run the desktop app the
    // file was created empty and every query failed with "no such table".
    // This decorator is the single choke point every database consumer in the
    // DI graph resolves the path through, so wrapping it guarantees the
    // schema exists before the first connection, exactly once per distinct
    // path, without any repository having to know about bootstrapping.
    //
    // It runs the versioned schema migrations (DATA-003) and then, when given
    // a seeder, the project-curated mappings (DATA-002). A path counts as
    // initialized only after both succeed, so a failure is retried by the next
    // caller instead of leaving the application on an un-migrated database.
    public sealed class SchemaInitializingAppDatabasePathProvider
        : IAppDatabasePathProvider
    {
        private readonly IAppDatabasePathProvider _inner;
        private readonly ICuratedMappingSeeder? _seeder;
        private readonly ISchemaMigrator _migrator;
        private readonly object _gate = new();
        private readonly HashSet<string> _initializedPaths = new();

        public SchemaInitializingAppDatabasePathProvider(
            IAppDatabasePathProvider inner,
            ICuratedMappingSeeder? seeder = null,
            ISchemaMigrator? migrator = null)
        {
            _inner = inner;
            _seeder = seeder;
            _migrator = migrator ?? new SchemaMigrator();
        }

        public string GetDatabasePath()
        {
            string path = _inner.GetDatabasePath();

            lock (_gate)
            {
                if (!_initializedPaths.Contains(path))
                {
                    MigrationExecutionResult result = _migrator.Migrate(path);
                    if (!result.Success)
                        throw new SqliteException(result.ErrorMessage, 1);

                    _seeder?.Seed(path);
                    _initializedPaths.Add(path);
                }
            }

            return path;
        }
    }
}
