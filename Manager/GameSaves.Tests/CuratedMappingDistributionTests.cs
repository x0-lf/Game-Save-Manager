using GameSaves.Core.Save;
using GameSaves.Infrastructure.Platform;
using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class CuratedMappingDistributionTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }

        [Fact]
        public void LoadCuratedSeed_FromEmbeddedResource_LoadsValidDataset()
        {
            var seeder = new CuratedMappingSeeder();
            CuratedMappingSeedDocument document = seeder.LoadCuratedSeed();

            Assert.NotNull(document);
            Assert.NotNull(document.Mappings);
            Assert.True(document.Mappings.Count >= 20, $"Expected at least 20 curated mappings, but found {document.Mappings.Count}.");

            foreach (CuratedMappingEntry mapping in document.Mappings)
            {
                Assert.False(string.IsNullOrWhiteSpace(mapping.SteamAppId), "SteamAppId must not be empty.");
                Assert.False(string.IsNullOrWhiteSpace(mapping.GameName), "GameName must not be empty.");
                Assert.Equal("windows", mapping.Platform);
                Assert.False(string.IsNullOrWhiteSpace(mapping.PathTemplate), "PathTemplate must not be empty.");
                Assert.True(mapping.Priority > 0, "Priority must be positive.");
            }
        }

        [Fact]
        public void Seed_IntoEmptyDatabase_PopulatesApprovedMappingsAndGameTitles()
        {
            string dbPath = MigratedDatabase.Create(_temp, "empty_seed.db");
            var seeder = new CuratedMappingSeeder();
            CuratedMappingSeedDocument seedDoc = seeder.LoadCuratedSeed();

            CuratedSeedResult result = seeder.Seed(dbPath);

            Assert.Equal(seedDoc.Mappings.Count, result.TotalProcessed);
            Assert.Equal(seedDoc.Mappings.Count, result.Inserted);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Unchanged);
            Assert.Equal(0, result.SkippedUserOverrides);

            var repository = new SqliteSavePathMappingRepository(dbPath);
            Assert.Equal(seedDoc.Mappings.Count, repository.CountApprovedMappings("windows"));
            Assert.Equal(seedDoc.Mappings.Count, CountCurated(dbPath));
            Assert.Equal(0, repository.CountPendingMappings("windows"));
            Assert.Equal(0, repository.CountNeedsFixMappings("windows"));

            // Verify specific well-known games exist in approved mappings
            IReadOnlyList<SavePathMapping> hl2 = repository.GetApprovedMappingsForApp("220", "windows");
            Assert.Single(hl2);
            Assert.Equal("Half-Life 2", hl2[0].GameName);
            Assert.Equal(CuratedMappingSeeder.CuratedSourceName, hl2[0].SourceName);
            Assert.Equal("Approved", hl2[0].ReviewStatus);
            Assert.True(hl2[0].Enabled);

            IReadOnlyList<SavePathMapping> cyberpunk = repository.GetApprovedMappingsForApp("1091500", "windows");
            Assert.Single(cyberpunk);
            Assert.Equal("Cyberpunk 2077", cyberpunk[0].GameName);

            // Verify game_titles table has entries
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            connection.Open();
            using var titleCmd = connection.CreateCommand();
            titleCmd.CommandText = "SELECT COUNT(*) FROM game_titles WHERE source_name = $source;";
            titleCmd.Parameters.AddWithValue("$source", CuratedMappingSeeder.CuratedSourceName);
            long titleCount = (long)(titleCmd.ExecuteScalar() ?? 0L);
            Assert.True(titleCount >= 20, "Expected at least 20 game title records in game_titles table.");
        }

        [Fact]
        public void Seed_IdempotentOnSubsequentRuns_DoesNotDuplicateOrAlterData()
        {
            string dbPath = MigratedDatabase.Create(_temp, "idempotent.db");
            var seeder = new CuratedMappingSeeder();
            CuratedMappingSeedDocument seedDoc = seeder.LoadCuratedSeed();

            CuratedSeedResult firstRun = seeder.Seed(dbPath);
            Assert.Equal(seedDoc.Mappings.Count, firstRun.Inserted);

            // Sentinel timestamps: any write by the second run would replace them.
            const string Sentinel = "2000-01-01 00:00:00";
            MigratedDatabase.Execute(dbPath, $"""
                UPDATE game_titles SET last_updated_utc = '{Sentinel}';
                UPDATE save_path_mappings SET updated_utc = '{Sentinel}';
                """);

            CuratedSeedResult secondRun = seeder.Seed(dbPath);
            Assert.Equal(seedDoc.Mappings.Count, secondRun.TotalProcessed);
            Assert.Equal(0, secondRun.Inserted);
            Assert.Equal(0, secondRun.Updated);
            Assert.Equal(seedDoc.Mappings.Count, secondRun.Unchanged);
            Assert.Equal(0, secondRun.SkippedUserOverrides);

            var repository = new SqliteSavePathMappingRepository(dbPath);
            Assert.Equal(seedDoc.Mappings.Count, repository.CountApprovedMappings("windows"));
            Assert.Equal(seedDoc.Mappings.Count, CountCurated(dbPath));

            Assert.Equal(0, MigratedDatabase.Scalar(dbPath, $"SELECT COUNT(*) FROM game_titles WHERE last_updated_utc <> '{Sentinel}';"));
            Assert.Equal(0, MigratedDatabase.Scalar(dbPath, $"SELECT COUNT(*) FROM save_path_mappings WHERE updated_utc <> '{Sentinel}';"));
        }

        [Fact]
        public void Seed_PreservesUserModifiedCuratedMapping_WhenUserDisablesIt()
        {
            string dbPath = MigratedDatabase.Create(_temp, "user_disabled.db");
            var seeder = new CuratedMappingSeeder();
            seeder.Seed(dbPath);

            var repository = new SqliteSavePathMappingRepository(dbPath);
            Assert.True(repository.GetApprovedMappingsForApp("220", "windows")[0].Enabled);

            // Simulate user disabling Half-Life 2 mapping
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE save_path_mappings SET enabled = 0, updated_utc = CURRENT_TIMESTAMP WHERE steam_app_id = '220';";
                cmd.ExecuteNonQuery();
            }

            // Verify it is disabled
            IReadOnlyList<SavePathMapping> beforeReseed = repository.GetMappingsForApp("220", "windows", includeDisabled: true);
            Assert.Single(beforeReseed);
            Assert.False(beforeReseed[0].Enabled);
            Assert.Empty(repository.GetApprovedMappingsForApp("220", "windows"));

            // Act: Re-apply seed
            CuratedSeedResult reseedResult = seeder.Seed(dbPath);

            // Assert: Seeder respected user override and skipped re-enabling
            Assert.True(reseedResult.SkippedUserOverrides >= 1);

            IReadOnlyList<SavePathMapping> afterReseed = repository.GetMappingsForApp("220", "windows", includeDisabled: true);
            Assert.Single(afterReseed);
            Assert.False(afterReseed[0].Enabled, "User disablement must not be overwritten by seed updates.");
            Assert.Empty(repository.GetApprovedMappingsForApp("220", "windows"));
        }

        [Fact]
        public void Seed_PreservesUserModifiedReviewStatus_WhenReviewStatusChanged()
        {
            string dbPath = MigratedDatabase.Create(_temp, "user_status_override.db");
            var seeder = new CuratedMappingSeeder();
            seeder.Seed(dbPath);

            // Simulate user / reviewer setting review_status to NeedsFix
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE save_path_mappings SET review_status = 'NeedsFix', updated_utc = CURRENT_TIMESTAMP WHERE steam_app_id = '220';";
                cmd.ExecuteNonQuery();
            }

            // Act: Re-apply seed
            CuratedSeedResult reseedResult = seeder.Seed(dbPath);

            // Assert
            Assert.True(reseedResult.SkippedUserOverrides >= 1);
            var repository = new SqliteSavePathMappingRepository(dbPath);
            IReadOnlyList<SavePathMapping> hl2 = repository.GetMappingsForApp("220", "windows", includeDisabled: true);
            Assert.Single(hl2);
            Assert.Equal("NeedsFix", hl2[0].ReviewStatus);
            Assert.Empty(repository.GetApprovedMappingsForApp("220", "windows"));
        }

        [Fact]
        public void Seed_PreservesUserCustomMappings_WhenCustomMappingAdded()
        {
            string dbPath = MigratedDatabase.Create(_temp, "custom_mapping.db");
            var seeder = new CuratedMappingSeeder();
            seeder.Seed(dbPath);

            // Add user custom mapping
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                INSERT INTO save_path_mappings (
                    steam_app_id,
                    game_name,
                    platform,
                    path_template,
                    path_kind,
                    source_name,
                    enabled,
                    review_status
                )
                VALUES (
                    '999999',
                    'My Indie Custom Game',
                    'windows',
                    '%APPDATA%/MyIndieGame/Saves',
                    'Directory',
                    'CustomUserMapping',
                    1,
                    'Approved'
                );
                """;
                cmd.ExecuteNonQuery();
            }

            // Act: Re-apply seed
            seeder.Seed(dbPath);

            // Assert: Custom mapping is intact
            var repository = new SqliteSavePathMappingRepository(dbPath);
            IReadOnlyList<SavePathMapping> custom = repository.GetApprovedMappingsForApp("999999", "windows");
            Assert.Single(custom);
            Assert.Equal("My Indie Custom Game", custom[0].GameName);
            Assert.Equal("CustomUserMapping", custom[0].SourceName);
        }

        [Fact]
        public void Seed_UpdatesMetadata_WhenCuratedSeedHasUpdatedMetadata()
        {
            string dbPath = MigratedDatabase.Create(_temp, "update_metadata.db");
            var seeder = new CuratedMappingSeeder();
            CuratedMappingSeedDocument originalDoc = seeder.LoadCuratedSeed();

            seeder.Seed(dbPath, originalDoc);

            // Create updated document where notes and sourceUrl changed for Half-Life 2
            var updatedMappings = originalDoc.Mappings.Select(m =>
            {
                if (m.SteamAppId == "220")
                {
                    return m with
                    {
                        Notes = "Updated curated notes v2",
                        SourceUrl = "https://updated.example.com/hl2"
                    };
                }
                return m;
            }).ToList();

            var updatedDoc = originalDoc with { Mappings = updatedMappings };

            // Act
            CuratedSeedResult updateResult = seeder.Seed(dbPath, updatedDoc);

            // Assert
            Assert.Equal(1, updateResult.Updated);
            Assert.Equal(originalDoc.Mappings.Count - 1, updateResult.Unchanged);
            Assert.Equal(0, updateResult.Inserted);

            var repository = new SqliteSavePathMappingRepository(dbPath);
            SavePathMapping hl2 = repository.GetApprovedMappingsForApp("220", "windows")[0];
            Assert.Equal("Updated curated notes v2", hl2.Notes);
            Assert.Equal("https://updated.example.com/hl2", hl2.SourceUrl);
            Assert.True(hl2.Enabled);
            Assert.Equal("Approved", hl2.ReviewStatus);
        }

        [Fact]
        public void Seed_ThroughSchemaInitializingAppDatabasePathProvider_SeedsAutomatically()
        {
            string dbPath = _temp.GetPath("provider_auto_seeded.db");
            var seeder = new CuratedMappingSeeder();
            var provider = new SchemaInitializingAppDatabasePathProvider(
                new TestDatabasePathProvider(dbPath),
                seeder);

            string resolvedPath = provider.GetDatabasePath();
            Assert.Equal(dbPath, resolvedPath);

            var repository = new SqliteSavePathMappingRepository(resolvedPath);
            Assert.True(repository.CountApprovedMappings("windows") >= 20);
            Assert.True(CountCurated(dbPath) >= 20);
        }

        private static long CountCurated(string dbPath) =>
            MigratedDatabase.Scalar(dbPath, $"SELECT COUNT(*) FROM save_path_mappings WHERE platform = 'windows' AND source_name = '{CuratedMappingSeeder.CuratedSourceName}';");
    }
}
