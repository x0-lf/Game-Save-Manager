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
    //
    // When given a seed database (the sanitized catalog the package ships
    // beside the program, see SeedDatabaseExporter) and the path does not
    // exist yet, the seed is copied there first and then migrated and seeded
    // like any other database. An existing database is never replaced.
    public sealed class SchemaInitializingAppDatabasePathProvider
        : IAppDatabasePathProvider
    {
        /// <summary>Where build/publish-release.ps1 puts the seed database relative to the program.</summary>
        public static string ShippedSeedDatabasePath =>
            Path.Combine(AppContext.BaseDirectory, "seed", "gamesave.db");

        private readonly IAppDatabasePathProvider _inner;
        private readonly ICuratedMappingSeeder? _seeder;
        private readonly ISchemaMigrator _migrator;
        private readonly string? _seedDatabasePath;
        private readonly object _gate = new();
        private readonly HashSet<string> _initializedPaths = new();

        public SchemaInitializingAppDatabasePathProvider(
            IAppDatabasePathProvider inner,
            ICuratedMappingSeeder? seeder = null,
            ISchemaMigrator? migrator = null,
            string? seedDatabasePath = null)
        {
            _inner = inner;
            _seeder = seeder;
            _migrator = migrator ?? new SchemaMigrator();
            _seedDatabasePath = seedDatabasePath;
        }

        public string GetDatabasePath()
        {
            string path = _inner.GetDatabasePath();

            lock (_gate)
            {
                if (!_initializedPaths.Contains(path))
                {
                    if (_seedDatabasePath is not null && !File.Exists(path) && File.Exists(_seedDatabasePath))
                        CopySeed(_seedDatabasePath, path);

                    MigrationExecutionResult result = _migrator.Migrate(path);
                    if (!result.Success)
                        throw new SqliteException(result.ErrorMessage, 1);

                    _seeder?.Seed(path);
                    _initializedPaths.Add(path);
                }
            }

            return path;
        }

        // Copied under a temporary name and renamed into place, so a crash
        // mid-copy never leaves a truncated file that the next start would
        // take for the database.
        private static void CopySeed(string seedPath, string databasePath)
        {
            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporary = databasePath + ".seed-copy";
            File.Copy(seedPath, temporary, overwrite: true);
            File.Move(temporary, databasePath);
        }
    }
}
