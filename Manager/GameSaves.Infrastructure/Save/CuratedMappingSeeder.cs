using GameSaves.Core.Save;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text.Json;

namespace GameSaves.Infrastructure.Save
{
    public sealed class CuratedMappingSeeder : ICuratedMappingSeeder
    {
        public const string CuratedSourceName = "CuratedSeed";
        private const string EmbeddedResourceSuffix = "curated-mappings.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public CuratedSeedResult Seed(string databasePath)
        {
            CuratedMappingSeedDocument document = LoadCuratedSeed();
            return Seed(databasePath, document);
        }

        public CuratedSeedResult Seed(string databasePath, string jsonContent)
        {
            CuratedMappingSeedDocument document = ParseSeedDocument(jsonContent);
            return Seed(databasePath, document);
        }

        public CuratedMappingSeedDocument LoadCuratedSeed()
        {
            Assembly assembly = typeof(CuratedMappingSeeder).Assembly;
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(EmbeddedResourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                throw new InvalidOperationException(
                    $"Embedded resource '{EmbeddedResourceSuffix}' was not found in assembly '{assembly.GetName().Name}'.");
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                throw new InvalidOperationException(
                    $"Could not open manifest resource stream for '{resourceName}'.");
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();

            return ParseSeedDocument(json);
        }

        public CuratedMappingSeedDocument ParseSeedDocument(string jsonContent)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                throw new ArgumentException("JSON content cannot be empty.", nameof(jsonContent));

            CuratedMappingSeedDocument? document = JsonSerializer.Deserialize<CuratedMappingSeedDocument>(
                jsonContent,
                JsonOptions);

            if (document is null)
                throw new InvalidOperationException("Failed to deserialize curated mapping seed document.");

            return document;
        }

        public CuratedSeedResult Seed(string databasePath, CuratedMappingSeedDocument document)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            if (document?.Mappings is null || document.Mappings.Count == 0)
                return new CuratedSeedResult(0, 0, 0, 0, 0);

            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var db = new SavePathDatabase(databasePath);
            db.Initialize();

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            SavePathDatabase.EnsureReviewColumns(connection);

            using var transaction = connection.BeginTransaction();

            int inserted = 0;
            int updated = 0;
            int unchanged = 0;
            int skippedOverrides = 0;

            foreach (CuratedMappingEntry entry in document.Mappings)
            {
                // Ensure game_titles is populated or maintained
                UpsertGameTitle(connection, transaction, entry);

                // Check existing save_path_mapping
                using var selectCmd = connection.CreateCommand();
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = """
                SELECT
                    id,
                    game_name,
                    path_kind,
                    source_name,
                    source_url,
                    source_license,
                    notes,
                    priority,
                    enabled,
                    COALESCE(review_status, 'Pending') AS review_status,
                    review_notes
                FROM save_path_mappings
                WHERE steam_app_id = $steam_app_id
                  AND platform = $platform
                  AND path_template = $path_template;
                """;
                selectCmd.Parameters.AddWithValue("$steam_app_id", entry.SteamAppId);
                selectCmd.Parameters.AddWithValue("$platform", entry.Platform);
                selectCmd.Parameters.AddWithValue("$path_template", entry.PathTemplate);

                using var reader = selectCmd.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();

                    // Row does not exist -> Insert new curated mapping as Approved & Enabled
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = """
                    INSERT INTO save_path_mappings (
                        steam_app_id,
                        game_name,
                        platform,
                        path_template,
                        path_kind,
                        source_name,
                        source_url,
                        source_license,
                        notes,
                        priority,
                        enabled,
                        review_status,
                        review_notes,
                        reviewed_utc,
                        created_utc,
                        updated_utc
                    )
                    VALUES (
                        $steam_app_id,
                        $game_name,
                        $platform,
                        $path_template,
                        $path_kind,
                        $source_name,
                        $source_url,
                        $source_license,
                        $notes,
                        $priority,
                        1,
                        'Approved',
                        'Curated project seed distribution',
                        CURRENT_TIMESTAMP,
                        CURRENT_TIMESTAMP,
                        CURRENT_TIMESTAMP
                    );
                    """;

                    insertCmd.Parameters.AddWithValue("$steam_app_id", entry.SteamAppId);
                    insertCmd.Parameters.AddWithValue("$game_name", ToDbValue(entry.GameName));
                    insertCmd.Parameters.AddWithValue("$platform", entry.Platform);
                    insertCmd.Parameters.AddWithValue("$path_template", entry.PathTemplate);
                    insertCmd.Parameters.AddWithValue("$path_kind", entry.PathKind);
                    insertCmd.Parameters.AddWithValue("$source_name", CuratedSourceName);
                    insertCmd.Parameters.AddWithValue("$source_url", ToDbValue(entry.SourceUrl));
                    insertCmd.Parameters.AddWithValue("$source_license", ToDbValue(entry.SourceLicense));
                    insertCmd.Parameters.AddWithValue("$notes", ToDbValue(entry.Notes));
                    insertCmd.Parameters.AddWithValue("$priority", entry.Priority);

                    insertCmd.ExecuteNonQuery();
                    inserted++;
                    continue;
                }

                // Row exists
                long existingId = reader.GetInt64(0);
                string? existingGameName = reader.IsDBNull(1) ? null : reader.GetString(1);
                string existingPathKind = reader.GetString(2);
                string existingSourceName = reader.GetString(3);
                string? existingSourceUrl = reader.IsDBNull(4) ? null : reader.GetString(4);
                string? existingSourceLicense = reader.IsDBNull(5) ? null : reader.GetString(5);
                string? existingNotes = reader.IsDBNull(6) ? null : reader.GetString(6);
                int existingPriority = reader.GetInt32(7);
                int existingEnabled = reader.GetInt32(8);
                string existingReviewStatus = reader.GetString(9);
                reader.Close();

                // Merge Precedence Rules:
                // Rule 1: User-created or external mapping (source_name != CuratedSeed) is never touched or overridden.
                if (!string.Equals(existingSourceName, CuratedSourceName, StringComparison.OrdinalIgnoreCase))
                {
                    skippedOverrides++;
                    continue;
                }

                // Rule 2: User explicit local modifications (disabled or altered review status) take precedence.
                bool userExplicitOverride = existingEnabled == 0 ||
                    !string.Equals(existingReviewStatus, "Approved", StringComparison.OrdinalIgnoreCase);

                if (userExplicitOverride)
                {
                    skippedOverrides++;
                    continue;
                }

                // Rule 3: Existing curated mapping with Approved & Enabled status -> update canonical metadata if changed.
                bool metadataChanged =
                    !string.Equals(existingGameName, entry.GameName, StringComparison.Ordinal) ||
                    !string.Equals(existingPathKind, entry.PathKind, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existingSourceUrl, entry.SourceUrl, StringComparison.Ordinal) ||
                    !string.Equals(existingSourceLicense, entry.SourceLicense, StringComparison.Ordinal) ||
                    !string.Equals(existingNotes, entry.Notes, StringComparison.Ordinal) ||
                    existingPriority != entry.Priority;

                if (metadataChanged)
                {
                    using var updateCmd = connection.CreateCommand();
                    updateCmd.Transaction = transaction;
                    updateCmd.CommandText = """
                    UPDATE save_path_mappings
                    SET game_name = $game_name,
                        path_kind = $path_kind,
                        source_url = $source_url,
                        source_license = $source_license,
                        notes = $notes,
                        priority = $priority,
                        updated_utc = CURRENT_TIMESTAMP
                    WHERE id = $id;
                    """;

                    updateCmd.Parameters.AddWithValue("$id", existingId);
                    updateCmd.Parameters.AddWithValue("$game_name", ToDbValue(entry.GameName));
                    updateCmd.Parameters.AddWithValue("$path_kind", entry.PathKind);
                    updateCmd.Parameters.AddWithValue("$source_url", ToDbValue(entry.SourceUrl));
                    updateCmd.Parameters.AddWithValue("$source_license", ToDbValue(entry.SourceLicense));
                    updateCmd.Parameters.AddWithValue("$notes", ToDbValue(entry.Notes));
                    updateCmd.Parameters.AddWithValue("$priority", entry.Priority);

                    updateCmd.ExecuteNonQuery();
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }

            transaction.Commit();

            return new CuratedSeedResult(
                document.Mappings.Count,
                inserted,
                updated,
                unchanged,
                skippedOverrides);
        }

        private static void UpsertGameTitle(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CuratedMappingEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.GameName))
                return;

            using var titleCmd = connection.CreateCommand();
            titleCmd.Transaction = transaction;
            titleCmd.CommandText = """
            INSERT INTO game_titles (
                steam_app_id,
                title,
                platform_hint,
                source_name,
                source_url,
                source_license,
                notes,
                first_seen_utc,
                last_updated_utc
            )
            VALUES (
                $steam_app_id,
                $title,
                $platform,
                $source_name,
                $source_url,
                $source_license,
                'Curated project seed game title',
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT (steam_app_id) DO UPDATE SET
                title = excluded.title,
                last_updated_utc = CURRENT_TIMESTAMP
            WHERE game_titles.source_name = $source_name;
            """;

            titleCmd.Parameters.AddWithValue("$steam_app_id", entry.SteamAppId);
            titleCmd.Parameters.AddWithValue("$title", entry.GameName);
            titleCmd.Parameters.AddWithValue("$platform", entry.Platform);
            titleCmd.Parameters.AddWithValue("$source_name", CuratedSourceName);
            titleCmd.Parameters.AddWithValue("$source_url", ToDbValue(entry.SourceUrl));
            titleCmd.Parameters.AddWithValue("$source_license", ToDbValue(entry.SourceLicense));

            titleCmd.ExecuteNonQuery();
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value;
        }
    }
}
