using GameSaves.Core.Save;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Save
{
    internal enum MappingWriteOutcome
    {
        Inserted,
        Updated,
        Unchanged
    }

    // The single write path for save path mappings from every import source:
    // the JSON importer, the PCGamingWiki harvester and AI detection. Two
    // writers with diverging rules used to exist; keep it one.
    internal sealed class SavePathMappingWriter : IDisposable
    {
        private static readonly string[] SupportedPlatforms = ["windows", "linux", "macos", "steamdeck"];

        private static readonly string[] ParameterNames =
        [
            "$steam_app_id", "$game_name", "$platform", "$path_template", "$path_kind",
            "$source_name", "$default_source_name", "$source_url", "$source_license",
            "$notes", "$priority", "$enabled", "$review_status", "$review_notes", "$force_review"
        ];

        private readonly SqliteCommand _insert;
        private readonly SqliteCommand _update;

        /// <param name="reviewStatus">
        /// "Pending" for every untrusted source. Anything else is an explicit
        /// review decision that is applied to existing rows too.
        /// </param>
        public SavePathMappingWriter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string reviewStatus,
            bool enabled,
            string defaultSourceName,
            string? reviewNotes = null)
        {
            string effectiveReviewStatus = string.IsNullOrWhiteSpace(reviewStatus) ? "Pending" : reviewStatus;

            _insert = CreateCommand(connection, transaction, """
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
                COALESCE($source_name, $default_source_name),
                $source_url,
                $source_license,
                $notes,
                $priority,
                $enabled,
                $review_status,
                $review_notes,
                CASE WHEN $force_review = 1 THEN CURRENT_TIMESTAMP END,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            )
            ON CONFLICT (steam_app_id, platform, path_template) DO NOTHING;
            """);

            // Optional fields an import omits keep their stored value, and a row
            // whose values would not change is not written at all, so a re-import
            // reports it unchanged and leaves updated_utc alone.
            _update = CreateCommand(connection, transaction, """
            UPDATE save_path_mappings
            SET game_name = COALESCE($game_name, game_name),
                path_kind = $path_kind,
                source_name = COALESCE($source_name, source_name),
                source_url = COALESCE($source_url, source_url),
                source_license = COALESCE($source_license, source_license),
                notes = COALESCE($notes, notes),
                priority = $priority,
                -- An import that explicitly approves must reach rows that already
                -- exist, or the caller is told the mapping was approved when it
                -- was not. Otherwise the previous review stands, except when the
                -- import changes how the path is used: that invalidates the
                -- review it was granted under, so it returns to Pending.
                review_status = CASE
                    WHEN $force_review = 1 THEN $review_status
                    WHEN path_kind <> $path_kind THEN 'Pending'
                    ELSE review_status
                END,
                enabled = CASE
                    WHEN $force_review = 1 THEN $enabled
                    WHEN path_kind <> $path_kind THEN 0
                    ELSE enabled
                END,
                review_notes = CASE WHEN $force_review = 1 THEN COALESCE($review_notes, review_notes) ELSE review_notes END,
                reviewed_utc = CASE WHEN $force_review = 1 THEN CURRENT_TIMESTAMP ELSE reviewed_utc END,
                updated_utc = CURRENT_TIMESTAMP
            WHERE steam_app_id = $steam_app_id
              AND platform = $platform
              AND path_template = $path_template
              -- An untrusted source may only refine a row that is still awaiting
              -- review. A reviewed row (or a curated one the seeder maintains)
              -- belongs to its reviewer, not to the next harvest.
              AND ($force_review = 1 OR review_status = 'Pending')
              AND (game_name IS NOT COALESCE($game_name, game_name)
                OR path_kind IS NOT $path_kind
                OR source_name IS NOT COALESCE($source_name, source_name)
                OR source_url IS NOT COALESCE($source_url, source_url)
                OR source_license IS NOT COALESCE($source_license, source_license)
                OR notes IS NOT COALESCE($notes, notes)
                OR priority IS NOT $priority
                OR ($force_review = 1 AND (review_status IS NOT $review_status OR enabled IS NOT $enabled)));
            """);

            Set("$default_source_name", defaultSourceName);
            Set("$enabled", enabled ? 1 : 0);
            Set("$review_status", effectiveReviewStatus);
            Set("$review_notes", ToDbValue(reviewNotes));
            Set(
                "$force_review",
                effectiveReviewStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        /// <summary>Writes one mapping already accepted by <see cref="Validate"/>.</summary>
        public MappingWriteOutcome Write(MappingImportEntry mapping)
        {
            Set("$steam_app_id", mapping.SteamAppId);
            Set("$game_name", ToDbValue(mapping.GameName));
            Set("$platform", mapping.Platform);
            Set("$path_template", mapping.PathTemplate);
            Set("$path_kind", mapping.PathKind);
            Set("$source_name", ToDbValue(mapping.SourceName?.Trim()));
            Set("$source_url", ToDbValue(mapping.SourceUrl));
            Set("$source_license", ToDbValue(mapping.SourceLicense));
            Set("$notes", ToDbValue(mapping.Notes));
            Set("$priority", mapping.Priority);

            if (_insert.ExecuteNonQuery() == 1)
                return MappingWriteOutcome.Inserted;

            return _update.ExecuteNonQuery() == 1
                ? MappingWriteOutcome.Updated
                : MappingWriteOutcome.Unchanged;
        }

        public void Dispose()
        {
            _insert.Dispose();
            _update.Dispose();
        }

        /// <summary>
        /// Validates a mapping from any source and returns it with its platform
        /// and path kind in canonical form, or returns the reason it is refused.
        /// </summary>
        public static MappingImportError? Validate(int index, MappingImportEntry mapping, out MappingImportEntry normalized)
        {
            normalized = mapping;
            string appId = mapping.SteamAppId;

            if (string.IsNullOrWhiteSpace(appId))
                return new MappingImportError(index, null, "SteamAppId", "SteamAppId is required for mapping.");

            if (!IsSteamAppId(appId))
                return new MappingImportError(index, appId, "SteamAppId", $"SteamAppId '{appId}' is not a Steam AppID (expected a positive whole number written in digits).");

            if (string.IsNullOrWhiteSpace(mapping.Platform))
                return new MappingImportError(index, appId, "Platform", "Platform is required.");

            string platform = mapping.Platform.Trim().ToLowerInvariant();
            if (!SupportedPlatforms.Contains(platform))
                return new MappingImportError(index, appId, "Platform", $"Unsupported platform '{mapping.Platform}'. Supported: {string.Join(", ", SupportedPlatforms)}.");

            if (string.IsNullOrWhiteSpace(mapping.PathTemplate))
                return new MappingImportError(index, appId, "PathTemplate", "PathTemplate is required.");

            if (mapping.PathTemplate.Contains('\0'))
                return new MappingImportError(index, appId, "PathTemplate", "PathTemplate contains invalid null character.");

            string? pathKind = string.IsNullOrWhiteSpace(mapping.PathKind)
                ? nameof(SavePathKind.Directory)
                : Enum.GetNames<SavePathKind>().FirstOrDefault(
                    name => name.Equals(mapping.PathKind.Trim(), StringComparison.OrdinalIgnoreCase));

            if (pathKind is null)
                return new MappingImportError(index, appId, "PathKind", $"Invalid PathKind '{mapping.PathKind}'. Expected 'Directory', 'File' or 'Glob'.");

            if (mapping.Priority < 0 || mapping.Priority > 10000)
                return new MappingImportError(index, appId, "Priority", $"Priority '{mapping.Priority}' is out of range [0-10000].");

            normalized = mapping with { Platform = platform, PathKind = pathKind };
            return null;
        }

        // Digits only: numbers from JSON arrive as their raw text ("1e3", "-5",
        // "105600.0"), and "0" is what a detector writes when it has no AppID.
        public static bool IsSteamAppId(string? value) =>
            !string.IsNullOrEmpty(value) &&
            value.All(char.IsAsciiDigit) &&
            value.Any(c => c != '0');

        private void Set(string name, object value)
        {
            _insert.Parameters[name].Value = value;
            _update.Parameters[name].Value = value;
        }

        private static SqliteCommand CreateCommand(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            foreach (string name in ParameterNames)
                command.Parameters.AddWithValue(name, DBNull.Value);

            return command;
        }

        private static object ToDbValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }
}
