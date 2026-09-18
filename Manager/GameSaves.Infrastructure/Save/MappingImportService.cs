using GameSaves.Core.Save;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameSaves.Infrastructure.Save
{
    public sealed class MappingImportService : IMappingImportService
    {
        private static readonly HashSet<string> SupportedPlatforms = new(StringComparer.OrdinalIgnoreCase)
        {
            "windows",
            "linux",
            "macos",
            "steamdeck"
        };

        public MappingImportDocument ParseDocument(string jsonContent)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                return new MappingImportDocument(1, Array.Empty<TitleImportEntry>(), Array.Empty<MappingImportEntry>());

            using var doc = JsonDocument.Parse(jsonContent);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var titles = new List<TitleImportEntry>();
                var mappings = new List<MappingImportEntry>();

                foreach (JsonElement element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                        continue;

                    if (HasProperty(element, "path_template", "pathTemplate", "template"))
                    {
                        mappings.Add(ParseMappingEntry(element));
                    }
                    else if (HasProperty(element, "title"))
                    {
                        titles.Add(ParseTitleEntry(element));
                    }
                    else
                    {
                        mappings.Add(ParseMappingEntry(element));
                    }
                }

                return new MappingImportDocument(1, titles, mappings);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                int schemaVersion = 1;
                if (TryGetIntProperty(root, out int ver, "schema_version", "schemaVersion", "version"))
                    schemaVersion = ver;

                var titles = new List<TitleImportEntry>();
                if (TryGetProperty(root, out JsonElement titlesElement, "titles") && titlesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement titleEl in titlesElement.EnumerateArray())
                    {
                        if (titleEl.ValueKind == JsonValueKind.Object)
                            titles.Add(ParseTitleEntry(titleEl));
                    }
                }

                var mappings = new List<MappingImportEntry>();
                if (TryGetProperty(root, out JsonElement mappingsElement, "mappings") && mappingsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement mappingEl in mappingsElement.EnumerateArray())
                    {
                        if (mappingEl.ValueKind == JsonValueKind.Object)
                            mappings.Add(ParseMappingEntry(mappingEl));
                    }
                }

                return new MappingImportDocument(schemaVersion, titles, mappings);
            }

            return new MappingImportDocument(1, Array.Empty<TitleImportEntry>(), Array.Empty<MappingImportEntry>());
        }

        public MappingImportReport ImportFile(string databasePath, string filePath, MappingImportOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));

            if (!File.Exists(filePath))
            {
                return new MappingImportReport(
                    TotalItemsProcessed: 0,
                    MappingsInserted: 0,
                    MappingsUpdated: 0,
                    MappingsUnchanged: 0,
                    MappingsSkippedDuplicate: 0,
                    TitlesInserted: 0,
                    TitlesUpdated: 0,
                    TitlesSkippedDuplicate: 0,
                    Errors: new[] { new MappingImportError(0, null, "File", $"Import file not found: {filePath}") });
            }

            try
            {
                string json = File.ReadAllText(filePath);
                return ImportJson(databasePath, json, options);
            }
            catch (Exception ex)
            {
                return new MappingImportReport(
                    TotalItemsProcessed: 0,
                    MappingsInserted: 0,
                    MappingsUpdated: 0,
                    MappingsUnchanged: 0,
                    MappingsSkippedDuplicate: 0,
                    TitlesInserted: 0,
                    TitlesUpdated: 0,
                    TitlesSkippedDuplicate: 0,
                    Errors: new[] { new MappingImportError(0, null, "IO", $"Failed to read file '{filePath}': {ex.Message}") });
            }
        }

        public MappingImportReport ImportJson(string databasePath, string jsonContent, MappingImportOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                return new MappingImportReport(
                    TotalItemsProcessed: 0,
                    MappingsInserted: 0,
                    MappingsUpdated: 0,
                    MappingsUnchanged: 0,
                    MappingsSkippedDuplicate: 0,
                    TitlesInserted: 0,
                    TitlesUpdated: 0,
                    TitlesSkippedDuplicate: 0,
                    Errors: new[] { new MappingImportError(0, null, "Json", "JSON content is empty or whitespace.") });
            }

            MappingImportDocument document;
            try
            {
                document = ParseDocument(jsonContent);
            }
            catch (JsonException ex)
            {
                return new MappingImportReport(
                    TotalItemsProcessed: 0,
                    MappingsInserted: 0,
                    MappingsUpdated: 0,
                    MappingsUnchanged: 0,
                    MappingsSkippedDuplicate: 0,
                    TitlesInserted: 0,
                    TitlesUpdated: 0,
                    TitlesSkippedDuplicate: 0,
                    Errors: new[] { new MappingImportError(0, null, "Json", $"Invalid JSON format: {ex.Message}") });
            }

            return Import(databasePath, document, options);
        }

        public MappingImportReport Import(string databasePath, MappingImportDocument document, MappingImportOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            options ??= new MappingImportOptions();

            var errors = new List<MappingImportError>();
            var validTitles = new List<TitleImportEntry>();
            var validMappings = new List<MappingImportEntry>();

            // 1. Validate Titles
            int titleIndex = 0;
            if (document.Titles != null)
            {
                foreach (TitleImportEntry title in document.Titles)
                {
                    titleIndex++;
                    if (string.IsNullOrWhiteSpace(title.SteamAppId))
                    {
                        errors.Add(new MappingImportError(titleIndex, null, "SteamAppId", "SteamAppId is required for game title."));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(title.Title))
                    {
                        errors.Add(new MappingImportError(titleIndex, title.SteamAppId, "Title", "Title is required."));
                        continue;
                    }

                    validTitles.Add(title);
                }
            }

            // 2. Validate Mappings
            int mappingIndex = 0;
            if (document.Mappings != null)
            {
                foreach (MappingImportEntry mapping in document.Mappings)
                {
                    mappingIndex++;
                    if (string.IsNullOrWhiteSpace(mapping.SteamAppId))
                    {
                        errors.Add(new MappingImportError(mappingIndex, null, "SteamAppId", "SteamAppId is required for mapping."));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(mapping.Platform))
                    {
                        errors.Add(new MappingImportError(mappingIndex, mapping.SteamAppId, "Platform", "Platform is required."));
                        continue;
                    }

                    string normalizedPlatform = mapping.Platform.Trim().ToLowerInvariant();
                    if (!SupportedPlatforms.Contains(normalizedPlatform))
                    {
                        errors.Add(new MappingImportError(mappingIndex, mapping.SteamAppId, "Platform", $"Unsupported platform '{mapping.Platform}'. Supported: windows, linux, macos, steamdeck."));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(mapping.PathTemplate))
                    {
                        errors.Add(new MappingImportError(mappingIndex, mapping.SteamAppId, "PathTemplate", "PathTemplate is required."));
                        continue;
                    }

                    if (mapping.PathTemplate.Contains('\0'))
                    {
                        errors.Add(new MappingImportError(mappingIndex, mapping.SteamAppId, "PathTemplate", "PathTemplate contains invalid null character."));
                        continue;
                    }

                    string normalizedKind = string.IsNullOrWhiteSpace(mapping.PathKind) ? "Directory" : mapping.PathKind.Trim();
                    if (!normalizedKind.Equals("Directory", StringComparison.OrdinalIgnoreCase) &&
                        !normalizedKind.Equals("File", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new MappingImportError(mappingIndex, mapping.SteamAppId, "PathKind", $"Invalid PathKind '{mapping.PathKind}'. Expected 'Directory' or 'File'."));
                        continue;
                    }

                    if (mapping.Priority < 0 || mapping.Priority > 10000)
                    {
                        errors.Add(new MappingImportError(mappingIndex, mapping.SteamAppId, "Priority", $"Priority '{mapping.Priority}' is out of range [0-10000]."));
                        continue;
                    }

                    validMappings.Add(mapping with
                    {
                        Platform = normalizedPlatform,
                        PathKind = normalizedKind.Equals("File", StringComparison.OrdinalIgnoreCase) ? "File" : "Directory",
                        SourceName = string.IsNullOrWhiteSpace(mapping.SourceName) ? options.DefaultSourceName : mapping.SourceName.Trim()
                    });
                }
            }

            int totalProcessed = (document.Titles?.Count ?? 0) + (document.Mappings?.Count ?? 0);

            // 3. Database Execution
            var db = new SavePathDatabase(databasePath);
            db.Initialize();

            int titlesInserted = 0;
            int titlesUpdated = 0;
            int titlesSkippedDuplicate = 0;

            int mappingsInserted = 0;
            int mappingsUpdated = 0;
            int mappingsUnchanged = 0;
            int mappingsSkippedDuplicate = 0;

            var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                SavePathDatabase.EnsureReviewColumns(connection);

                using var transaction = connection.BeginTransaction();

                // Process Titles
                foreach (TitleImportEntry title in validTitles)
                {
                    using var selectCmd = connection.CreateCommand();
                    selectCmd.Transaction = transaction;
                    selectCmd.CommandText = "SELECT title, source_name FROM game_titles WHERE steam_app_id = $appId;";
                    selectCmd.Parameters.AddWithValue("$appId", title.SteamAppId);

                    using var reader = selectCmd.ExecuteReader();
                    if (!reader.Read())
                    {
                        reader.Close();

                        using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = """
                        INSERT INTO game_titles (
                            steam_app_id,
                            title,
                            platform_hint,
                            pcgw_page_id,
                            pcgw_page_name,
                            source_name,
                            source_url,
                            source_license,
                            notes,
                            first_seen_utc,
                            last_updated_utc
                        )
                        VALUES (
                            $appId,
                            $title,
                            $platformHint,
                            $pageId,
                            $pageName,
                            $sourceName,
                            $sourceUrl,
                            $sourceLicense,
                            $notes,
                            CURRENT_TIMESTAMP,
                            CURRENT_TIMESTAMP
                        );
                        """;
                        insertCmd.Parameters.AddWithValue("$appId", title.SteamAppId);
                        insertCmd.Parameters.AddWithValue("$title", title.Title);
                        insertCmd.Parameters.AddWithValue("$platformHint", ToDbValue(title.PlatformHint));
                        insertCmd.Parameters.AddWithValue("$pageId", title.PcgwPageId.HasValue ? title.PcgwPageId.Value : DBNull.Value);
                        insertCmd.Parameters.AddWithValue("$pageName", ToDbValue(title.PcgwPageName));
                        insertCmd.Parameters.AddWithValue("$sourceName", string.IsNullOrWhiteSpace(title.SourceName) ? options.DefaultSourceName : title.SourceName);
                        insertCmd.Parameters.AddWithValue("$sourceUrl", ToDbValue(title.SourceUrl));
                        insertCmd.Parameters.AddWithValue("$sourceLicense", ToDbValue(title.SourceLicense));
                        insertCmd.Parameters.AddWithValue("$notes", ToDbValue(title.Notes));
                        insertCmd.ExecuteNonQuery();

                        titlesInserted++;
                    }
                    else
                    {
                        string existingTitle = reader.GetString(0);
                        reader.Close();

                        if (string.Equals(existingTitle, title.Title, StringComparison.OrdinalIgnoreCase))
                        {
                            titlesSkippedDuplicate++;
                        }
                        else if (options.UpdateExistingTitles)
                        {
                            using var updateCmd = connection.CreateCommand();
                            updateCmd.Transaction = transaction;
                            updateCmd.CommandText = """
                            UPDATE game_titles
                            SET title = $title,
                                platform_hint = COALESCE($platformHint, platform_hint),
                                source_url = COALESCE($sourceUrl, source_url),
                                last_updated_utc = CURRENT_TIMESTAMP
                            WHERE steam_app_id = $appId;
                            """;
                            updateCmd.Parameters.AddWithValue("$appId", title.SteamAppId);
                            updateCmd.Parameters.AddWithValue("$title", title.Title);
                            updateCmd.Parameters.AddWithValue("$platformHint", ToDbValue(title.PlatformHint));
                            updateCmd.Parameters.AddWithValue("$sourceUrl", ToDbValue(title.SourceUrl));
                            updateCmd.ExecuteNonQuery();

                            titlesUpdated++;
                        }
                        else
                        {
                            // Duplicate title detected with different name, preserving existing title
                            titlesSkippedDuplicate++;
                        }
                    }
                }

                // Process Mappings
                foreach (MappingImportEntry mapping in validMappings)
                {
                    // Synchronize title into game_titles if gameName provided
                    if (!string.IsNullOrWhiteSpace(mapping.GameName))
                    {
                        using var checkTitleCmd = connection.CreateCommand();
                        checkTitleCmd.Transaction = transaction;
                        checkTitleCmd.CommandText = "SELECT COUNT(*) FROM game_titles WHERE steam_app_id = $appId;";
                        checkTitleCmd.Parameters.AddWithValue("$appId", mapping.SteamAppId);

                        if (Convert.ToInt32(checkTitleCmd.ExecuteScalar() ?? 0) == 0)
                        {
                            using var insertTitleCmd = connection.CreateCommand();
                            insertTitleCmd.Transaction = transaction;
                            insertTitleCmd.CommandText = """
                            INSERT INTO game_titles (
                                steam_app_id,
                                title,
                                platform_hint,
                                source_name,
                                first_seen_utc,
                                last_updated_utc
                            )
                            VALUES ($appId, $title, $platform, $sourceName, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                            """;
                            insertTitleCmd.Parameters.AddWithValue("$appId", mapping.SteamAppId);
                            insertTitleCmd.Parameters.AddWithValue("$title", mapping.GameName);
                            insertTitleCmd.Parameters.AddWithValue("$platform", mapping.Platform);
                            insertTitleCmd.Parameters.AddWithValue("$sourceName", mapping.SourceName);
                            insertTitleCmd.ExecuteNonQuery();

                            titlesInserted++;
                        }
                    }

                    // Strict Trust Rule: Defaults to Pending unless explicitly AutoApprove
                    string targetReviewStatus = options.AutoApprove ? "Approved" : "Pending";
                    int targetEnabled = options.AutoApprove ? 1 : 0;

                    using var checkMappingCmd = connection.CreateCommand();
                    checkMappingCmd.Transaction = transaction;
                    checkMappingCmd.CommandText = """
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
                        review_status
                    FROM save_path_mappings
                    WHERE steam_app_id = $appId
                      AND platform = $platform
                      AND path_template = $pathTemplate;
                    """;
                    checkMappingCmd.Parameters.AddWithValue("$appId", mapping.SteamAppId);
                    checkMappingCmd.Parameters.AddWithValue("$platform", mapping.Platform);
                    checkMappingCmd.Parameters.AddWithValue("$pathTemplate", mapping.PathTemplate);

                    using var reader = checkMappingCmd.ExecuteReader();
                    if (!reader.Read())
                    {
                        reader.Close();

                        using var insertMapCmd = connection.CreateCommand();
                        insertMapCmd.Transaction = transaction;
                        insertMapCmd.CommandText = """
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
                            $appId,
                            $gameName,
                            $platform,
                            $pathTemplate,
                            $pathKind,
                            $sourceName,
                            $sourceUrl,
                            $sourceLicense,
                            $notes,
                            $priority,
                            $enabled,
                            $reviewStatus,
                            $reviewNotes,
                            $reviewedUtc,
                            CURRENT_TIMESTAMP,
                            CURRENT_TIMESTAMP
                        );
                        """;
                        insertMapCmd.Parameters.AddWithValue("$appId", mapping.SteamAppId);
                        insertMapCmd.Parameters.AddWithValue("$gameName", ToDbValue(mapping.GameName));
                        insertMapCmd.Parameters.AddWithValue("$platform", mapping.Platform);
                        insertMapCmd.Parameters.AddWithValue("$pathTemplate", mapping.PathTemplate);
                        insertMapCmd.Parameters.AddWithValue("$pathKind", mapping.PathKind);
                        insertMapCmd.Parameters.AddWithValue("$sourceName", mapping.SourceName);
                        insertMapCmd.Parameters.AddWithValue("$sourceUrl", ToDbValue(mapping.SourceUrl));
                        insertMapCmd.Parameters.AddWithValue("$sourceLicense", ToDbValue(mapping.SourceLicense));
                        insertMapCmd.Parameters.AddWithValue("$notes", ToDbValue(mapping.Notes));
                        insertMapCmd.Parameters.AddWithValue("$priority", mapping.Priority);
                        insertMapCmd.Parameters.AddWithValue("$enabled", targetEnabled);
                        insertMapCmd.Parameters.AddWithValue("$reviewStatus", targetReviewStatus);
                        insertMapCmd.Parameters.AddWithValue("$reviewNotes", options.AutoApprove ? "Approved during import" : DBNull.Value);
                        insertMapCmd.Parameters.AddWithValue("$reviewedUtc", options.AutoApprove ? DateTimeOffset.UtcNow.ToString("o") : DBNull.Value);
                        insertMapCmd.ExecuteNonQuery();

                        mappingsInserted++;
                    }
                    else
                    {
                        string? existingGameName = reader.IsDBNull(1) ? null : reader.GetString(1);
                        string existingPathKind = reader.GetString(2);
                        string existingSourceName = reader.GetString(3);
                        string? existingSourceUrl = reader.IsDBNull(4) ? null : reader.GetString(4);
                        string? existingSourceLicense = reader.IsDBNull(5) ? null : reader.GetString(5);
                        string? existingNotes = reader.IsDBNull(6) ? null : reader.GetString(6);
                        int existingPriority = reader.GetInt32(7);
                        int existingEnabled = reader.GetInt32(8);
                        string existingReviewStatus = reader.IsDBNull(9) ? "Pending" : reader.GetString(9);
                        reader.Close();

                        bool isIdentical =
                            string.Equals(existingGameName, mapping.GameName, StringComparison.Ordinal) &&
                            string.Equals(existingPathKind, mapping.PathKind, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existingSourceName, mapping.SourceName, StringComparison.Ordinal) &&
                            string.Equals(existingSourceUrl, mapping.SourceUrl, StringComparison.Ordinal) &&
                            string.Equals(existingSourceLicense, mapping.SourceLicense, StringComparison.Ordinal) &&
                            string.Equals(existingNotes, mapping.Notes, StringComparison.Ordinal) &&
                            existingPriority == mapping.Priority &&
                            (!options.AutoApprove || (existingEnabled == targetEnabled && string.Equals(existingReviewStatus, targetReviewStatus, StringComparison.OrdinalIgnoreCase)));

                        if (isIdentical)
                        {
                            mappingsUnchanged++;
                            continue;
                        }

                        // Determine updated review status & enabled
                        string newReviewStatus;
                        int newEnabled;

                        if (options.AutoApprove)
                        {
                            newReviewStatus = "Approved";
                            newEnabled = 1;
                        }
                        else if (options.ForceReviewStatus)
                        {
                            newReviewStatus = "Pending";
                            newEnabled = 0;
                        }
                        else if (!existingPathKind.Equals(mapping.PathKind, StringComparison.OrdinalIgnoreCase))
                        {
                            // Path kind change invalidates previous review
                            newReviewStatus = "Pending";
                            newEnabled = 0;
                        }
                        else
                        {
                            // Preserve existing review status and enabled flag
                            newReviewStatus = existingReviewStatus;
                            newEnabled = existingEnabled;
                        }

                        using var updateMapCmd = connection.CreateCommand();
                        updateMapCmd.Transaction = transaction;
                        updateMapCmd.CommandText = """
                        UPDATE save_path_mappings
                        SET game_name = COALESCE($gameName, game_name),
                            path_kind = $pathKind,
                            source_name = $sourceName,
                            source_url = COALESCE($sourceUrl, source_url),
                            source_license = COALESCE($sourceLicense, source_license),
                            notes = COALESCE($notes, notes),
                            priority = $priority,
                            enabled = $enabled,
                            review_status = $reviewStatus,
                            updated_utc = CURRENT_TIMESTAMP
                        WHERE steam_app_id = $appId
                          AND platform = $platform
                          AND path_template = $pathTemplate;
                        """;
                        updateMapCmd.Parameters.AddWithValue("$appId", mapping.SteamAppId);
                        updateMapCmd.Parameters.AddWithValue("$gameName", ToDbValue(mapping.GameName));
                        updateMapCmd.Parameters.AddWithValue("$platform", mapping.Platform);
                        updateMapCmd.Parameters.AddWithValue("$pathTemplate", mapping.PathTemplate);
                        updateMapCmd.Parameters.AddWithValue("$pathKind", mapping.PathKind);
                        updateMapCmd.Parameters.AddWithValue("$sourceName", mapping.SourceName);
                        updateMapCmd.Parameters.AddWithValue("$sourceUrl", ToDbValue(mapping.SourceUrl));
                        updateMapCmd.Parameters.AddWithValue("$sourceLicense", ToDbValue(mapping.SourceLicense));
                        updateMapCmd.Parameters.AddWithValue("$notes", ToDbValue(mapping.Notes));
                        updateMapCmd.Parameters.AddWithValue("$priority", mapping.Priority);
                        updateMapCmd.Parameters.AddWithValue("$enabled", newEnabled);
                        updateMapCmd.Parameters.AddWithValue("$reviewStatus", newReviewStatus);
                        updateMapCmd.ExecuteNonQuery();

                        mappingsUpdated++;
                    }
                }

                transaction.Commit();
            }

            return new MappingImportReport(
                TotalItemsProcessed: totalProcessed,
                MappingsInserted: mappingsInserted,
                MappingsUpdated: mappingsUpdated,
                MappingsUnchanged: mappingsUnchanged,
                MappingsSkippedDuplicate: mappingsSkippedDuplicate,
                TitlesInserted: titlesInserted,
                TitlesUpdated: titlesUpdated,
                TitlesSkippedDuplicate: titlesSkippedDuplicate,
                Errors: errors);
        }

        private static TitleImportEntry ParseTitleEntry(JsonElement element)
        {
            string appId = GetStringProperty(element, "steam_app_id", "steamAppId", "appId", "app_id") ?? string.Empty;
            string title = GetStringProperty(element, "title", "game_name", "gameName", "name") ?? string.Empty;
            string? platformHint = GetStringProperty(element, "platform_hint", "platformHint", "platform");
            int? pcgwPageId = TryGetIntProperty(element, out int pageId, "pcgw_page_id", "pcgwPageId", "pageId") ? pageId : null;
            string? pcgwPageName = GetStringProperty(element, "pcgw_page_name", "pcgwPageName", "pageName");
            string? sourceName = GetStringProperty(element, "source_name", "sourceName");
            string? sourceUrl = GetStringProperty(element, "source_url", "sourceUrl");
            string? sourceLicense = GetStringProperty(element, "source_license", "sourceLicense");
            string? notes = GetStringProperty(element, "notes");

            return new TitleImportEntry(
                SteamAppId: appId,
                Title: title,
                PlatformHint: platformHint,
                PcgwPageId: pcgwPageId,
                PcgwPageName: pcgwPageName,
                SourceName: sourceName,
                SourceUrl: sourceUrl,
                SourceLicense: sourceLicense,
                Notes: notes);
        }

        private static MappingImportEntry ParseMappingEntry(JsonElement element)
        {
            string appId = GetStringProperty(element, "steam_app_id", "steamAppId", "appId", "app_id") ?? string.Empty;
            string? gameName = GetStringProperty(element, "game_name", "gameName", "title", "name");
            string platform = GetStringProperty(element, "platform") ?? string.Empty;
            string pathTemplate = GetStringProperty(element, "path_template", "pathTemplate", "template", "path") ?? string.Empty;
            string pathKind = GetStringProperty(element, "path_kind", "pathKind") ?? "Directory";
            string? sourceName = GetStringProperty(element, "source_name", "sourceName");
            string? sourceUrl = GetStringProperty(element, "source_url", "sourceUrl");
            string? sourceLicense = GetStringProperty(element, "source_license", "sourceLicense");
            string? notes = GetStringProperty(element, "notes");
            int priority = TryGetIntProperty(element, out int p, "priority") ? p : 100;
            string? reviewStatus = GetStringProperty(element, "review_status", "reviewStatus");

            return new MappingImportEntry(
                SteamAppId: appId,
                GameName: gameName,
                Platform: platform,
                PathTemplate: pathTemplate,
                PathKind: pathKind,
                SourceName: sourceName,
                SourceUrl: sourceUrl,
                SourceLicense: sourceLicense,
                Notes: notes,
                Priority: priority,
                ReviewStatus: reviewStatus);
        }

        private static bool HasProperty(JsonElement element, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                if (element.TryGetProperty(name, out _))
                    return true;
            }
            return false;
        }

        private static bool TryGetProperty(JsonElement element, out JsonElement result, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                if (element.TryGetProperty(name, out result))
                    return true;
            }

            result = default;
            return false;
        }

        private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                if (element.TryGetProperty(name, out JsonElement prop))
                {
                    if (prop.ValueKind == JsonValueKind.String)
                        return prop.GetString();
                    if (prop.ValueKind == JsonValueKind.Number)
                        return prop.ToString();
                }
            }
            return null;
        }

        private static bool TryGetIntProperty(JsonElement element, out int value, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                if (element.TryGetProperty(name, out JsonElement prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
                        return true;
                    if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value))
                        return true;
                }
            }

            value = 0;
            return false;
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        }
    }
}
