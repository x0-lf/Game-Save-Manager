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

        // Expects a migrated database: the App's database path decorator and the
        // CLI commands migrate before they seed.
        public CuratedSeedResult Seed(string databasePath, CuratedMappingSeedDocument document)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            if (document?.Mappings is null || document.Mappings.Count == 0)
                return new CuratedSeedResult(0, 0, 0, 0, 0);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            // This runs on every application start, where normally nothing has
            // changed. A read-only pass decides that without taking the write
            // lock, which another process may hold for a long time.
            (CuratedSeedResult result, bool writesNeeded) = Apply(connection, transaction: null, document);

            if (!writesNeeded)
                return result;

            using var transaction = connection.BeginTransaction();
            (result, _) = Apply(connection, transaction, document);
            transaction.Commit();

            return result;
        }

        // With no transaction nothing is written: the pass only reports what a
        // write pass would do.
        private static (CuratedSeedResult Result, bool WritesNeeded) Apply(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CuratedMappingSeedDocument document)
        {
            bool write = transaction is not null;

            using var selectMapping = CreateCommand(connection, transaction, """
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
                COALESCE(review_status, 'Pending') AS review_status
            FROM save_path_mappings
            WHERE steam_app_id = $steam_app_id
              AND platform = $platform
              AND path_template = $path_template;
            """, "$steam_app_id", "$platform", "$path_template");

            using var insertMapping = CreateCommand(connection, transaction, """
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
            """, "$steam_app_id", "$game_name", "$platform", "$path_template", "$path_kind", "$source_name", "$source_url", "$source_license", "$notes", "$priority");

            using var updateMapping = CreateCommand(connection, transaction, """
            UPDATE save_path_mappings
            SET game_name = $game_name,
                path_kind = $path_kind,
                source_url = $source_url,
                source_license = $source_license,
                notes = $notes,
                priority = $priority,
                updated_utc = CURRENT_TIMESTAMP
            WHERE id = $id;
            """, "$id", "$game_name", "$path_kind", "$source_url", "$source_license", "$notes", "$priority");

            // A title another source owns is left alone, and an unchanged
            // curated title is not rewritten.
            using var upsertTitle = CreateCommand(connection, transaction, """
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
            WHERE game_titles.source_name = $source_name
              AND game_titles.title IS NOT excluded.title;
            """, "$steam_app_id", "$title", "$platform", "$source_name", "$source_url", "$source_license");

            using var titleIsCurrent = CreateCommand(connection, transaction, """
            SELECT COUNT(*)
            FROM game_titles
            WHERE steam_app_id = $steam_app_id
              AND (source_name IS NOT $source_name OR title IS $title);
            """, "$steam_app_id", "$title", "$source_name");

            int inserted = 0;
            int updated = 0;
            int unchanged = 0;
            int skippedOverrides = 0;
            bool titleWritesNeeded = false;

            foreach (CuratedMappingEntry entry in document.Mappings)
            {
                object sourceUrl = ToDbValue(entry.SourceUrl);
                object sourceLicense = ToDbValue(entry.SourceLicense);

                if (!string.IsNullOrWhiteSpace(entry.GameName))
                {
                    if (write)
                    {
                        Bind(upsertTitle, entry.SteamAppId, entry.GameName, entry.Platform, CuratedSourceName, sourceUrl, sourceLicense);
                        upsertTitle.ExecuteNonQuery();
                    }
                    else
                    {
                        Bind(titleIsCurrent, entry.SteamAppId, entry.GameName, CuratedSourceName);
                        titleWritesNeeded |= Convert.ToInt64(titleIsCurrent.ExecuteScalar()) == 0;
                    }
                }

                Bind(selectMapping, entry.SteamAppId, entry.Platform, entry.PathTemplate);
                using var reader = selectMapping.ExecuteReader();

                if (!reader.Read())
                {
                    reader.Close();

                    // Row does not exist -> insert the curated mapping as Approved & Enabled.
                    if (write)
                    {
                        Bind(insertMapping, entry.SteamAppId, ToDbValue(entry.GameName), entry.Platform, entry.PathTemplate, entry.PathKind, CuratedSourceName, sourceUrl, sourceLicense, ToDbValue(entry.Notes), entry.Priority);
                        insertMapping.ExecuteNonQuery();
                    }

                    inserted++;
                    continue;
                }

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

                if (!metadataChanged)
                {
                    unchanged++;
                    continue;
                }

                if (write)
                {
                    Bind(updateMapping, existingId, ToDbValue(entry.GameName), entry.PathKind, sourceUrl, sourceLicense, ToDbValue(entry.Notes), entry.Priority);
                    updateMapping.ExecuteNonQuery();
                }

                updated++;
            }

            var result = new CuratedSeedResult(
                document.Mappings.Count,
                inserted,
                updated,
                unchanged,
                skippedOverrides);

            return (result, titleWritesNeeded || inserted > 0 || updated > 0);
        }

        // Commands are prepared once per pass and re-bound for every entry.
        private static SqliteCommand CreateCommand(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string sql,
            params string[] parameterNames)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            foreach (string name in parameterNames)
                command.Parameters.AddWithValue(name, DBNull.Value);

            return command;
        }

        private static void Bind(SqliteCommand command, params object[] values)
        {
            for (int i = 0; i < values.Length; i++)
                command.Parameters[i].Value = values[i];
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value;
        }
    }
}
