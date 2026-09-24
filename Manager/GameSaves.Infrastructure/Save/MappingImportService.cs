using GameSaves.Core.Save;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameSaves.Infrastructure.Save
{
    // Expects a migrated database: every production caller (the CLI import
    // command) initializes it first.
    public sealed class MappingImportService : IMappingImportService
    {
        private static readonly string[] AppIdNames = ["steam_app_id", "steamAppId", "appId", "app_id"];
        private static readonly string[] PathTemplateNames = ["path_template", "pathTemplate", "template", "path"];

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

                int index = 0;
                foreach (JsonElement element in root.EnumerateArray())
                {
                    index++;
                    RequireObject(element, index);

                    if (!HasProperty(element, PathTemplateNames) && HasProperty(element, "title"))
                        titles.Add(ParseTitleEntry(element));
                    else
                        mappings.Add(ParseMappingEntry(element));
                }

                return new MappingImportDocument(1, titles, mappings);
            }

            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The document must be a JSON array of mappings or an object with \"titles\" and/or \"mappings\".");

            bool hasTitles = TryGetProperty(root, out JsonElement titlesElement, "titles");
            bool hasMappings = TryGetProperty(root, out JsonElement mappingsElement, "mappings");

            // An object with neither list is almost certainly the wrong file;
            // importing nothing and calling it success would hide that.
            if (!hasTitles && !hasMappings)
                throw new JsonException("The document object has neither a \"titles\" nor a \"mappings\" array.");

            int schemaVersion = TryGetIntProperty(root, out int ver, "schema_version", "schemaVersion", "version") ? ver : 1;

            return new MappingImportDocument(
                schemaVersion,
                hasTitles ? ParseArray(titlesElement, "titles", ParseTitleEntry) : Array.Empty<TitleImportEntry>(),
                hasMappings ? ParseArray(mappingsElement, "mappings", ParseMappingEntry) : Array.Empty<MappingImportEntry>());
        }

        public MappingImportReport ImportFile(string databasePath, string filePath, MappingImportOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));

            if (!File.Exists(filePath))
                return Failed("File", $"Import file not found: {filePath}");

            string json;
            try
            {
                json = File.ReadAllText(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed("IO", $"Failed to read file '{filePath}': {ex.Message}");
            }

            return ImportJson(databasePath, json, options);
        }

        public MappingImportReport ImportJson(string databasePath, string jsonContent, MappingImportOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                return Failed("Json", "JSON content is empty or whitespace.");

            MappingImportDocument document;
            try
            {
                document = ParseDocument(jsonContent);
            }
            catch (JsonException ex)
            {
                return Failed("Json", $"Invalid JSON format: {ex.Message}");
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

            int titleIndex = 0;
            foreach (TitleImportEntry title in document.Titles ?? Array.Empty<TitleImportEntry>())
            {
                titleIndex++;
                if (string.IsNullOrWhiteSpace(title.SteamAppId))
                    errors.Add(new MappingImportError(titleIndex, null, "SteamAppId", "SteamAppId is required for game title."));
                else if (!SavePathMappingWriter.IsSteamAppId(title.SteamAppId))
                    errors.Add(new MappingImportError(titleIndex, title.SteamAppId, "SteamAppId", $"SteamAppId '{title.SteamAppId}' is not a Steam AppID (expected a positive whole number written in digits)."));
                else if (string.IsNullOrWhiteSpace(title.Title))
                    errors.Add(new MappingImportError(titleIndex, title.SteamAppId, "Title", "Title is required."));
                else
                    validTitles.Add(title);
            }

            int mappingIndex = 0;
            foreach (MappingImportEntry mapping in document.Mappings ?? Array.Empty<MappingImportEntry>())
            {
                mappingIndex++;
                MappingImportError? error = SavePathMappingWriter.Validate(mappingIndex, mapping, out MappingImportEntry valid);

                if (error is null)
                    validMappings.Add(valid);
                else
                    errors.Add(error);
            }

            int totalProcessed = (document.Titles?.Count ?? 0) + (document.Mappings?.Count ?? 0);

            int titlesInserted = 0;
            int titlesSkippedDuplicate = 0;
            int mappingsInserted = 0;
            int mappingsUpdated = 0;
            int mappingsUnchanged = 0;

            var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();

                // An existing title is never overwritten by an import.
                using var insertTitle = connection.CreateCommand();
                insertTitle.Transaction = transaction;
                insertTitle.CommandText = """
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
                )
                ON CONFLICT (steam_app_id) DO NOTHING;
                """;
                foreach (string name in new[] { "$appId", "$title", "$platformHint", "$pageId", "$pageName", "$sourceName", "$sourceUrl", "$sourceLicense", "$notes" })
                    insertTitle.Parameters.AddWithValue(name, DBNull.Value);

                bool InsertTitle(string appId, string title, string? platformHint, int? pageId, string? pageName, string? sourceName, string? sourceUrl, string? sourceLicense, string? notes)
                {
                    insertTitle.Parameters["$appId"].Value = appId;
                    insertTitle.Parameters["$title"].Value = title;
                    insertTitle.Parameters["$platformHint"].Value = ToDbValue(platformHint);
                    insertTitle.Parameters["$pageId"].Value = pageId.HasValue ? pageId.Value : DBNull.Value;
                    insertTitle.Parameters["$pageName"].Value = ToDbValue(pageName);
                    insertTitle.Parameters["$sourceName"].Value = string.IsNullOrWhiteSpace(sourceName) ? options.DefaultSourceName : sourceName.Trim();
                    insertTitle.Parameters["$sourceUrl"].Value = ToDbValue(sourceUrl);
                    insertTitle.Parameters["$sourceLicense"].Value = ToDbValue(sourceLicense);
                    insertTitle.Parameters["$notes"].Value = ToDbValue(notes);
                    return insertTitle.ExecuteNonQuery() == 1;
                }

                foreach (TitleImportEntry title in validTitles)
                {
                    if (InsertTitle(title.SteamAppId, title.Title, title.PlatformHint, title.PcgwPageId, title.PcgwPageName, title.SourceName, title.SourceUrl, title.SourceLicense, title.Notes))
                        titlesInserted++;
                    else
                        titlesSkippedDuplicate++;
                }

                // Strict trust rule: Pending and disabled unless the caller
                // explicitly approves the import.
                using var writer = new SavePathMappingWriter(
                    connection,
                    transaction,
                    options.AutoApprove ? "Approved" : "Pending",
                    enabled: options.AutoApprove,
                    options.DefaultSourceName,
                    options.AutoApprove ? "Approved during import" : null);

                foreach (MappingImportEntry mapping in validMappings)
                {
                    if (!string.IsNullOrWhiteSpace(mapping.GameName) &&
                        InsertTitle(mapping.SteamAppId, mapping.GameName, mapping.Platform, null, null, mapping.SourceName, null, null, null))
                    {
                        titlesInserted++;
                    }

                    switch (writer.Write(mapping))
                    {
                        case MappingWriteOutcome.Inserted:
                            mappingsInserted++;
                            break;
                        case MappingWriteOutcome.Updated:
                            mappingsUpdated++;
                            break;
                        default:
                            mappingsUnchanged++;
                            break;
                    }
                }

                transaction.Commit();
            }

            return new MappingImportReport(
                TotalItemsProcessed: totalProcessed,
                MappingsInserted: mappingsInserted,
                MappingsUpdated: mappingsUpdated,
                MappingsUnchanged: mappingsUnchanged,
                TitlesInserted: titlesInserted,
                TitlesSkippedDuplicate: titlesSkippedDuplicate,
                Errors: errors);
        }

        private static MappingImportReport Failed(string property, string message) =>
            new(
                TotalItemsProcessed: 0,
                MappingsInserted: 0,
                MappingsUpdated: 0,
                MappingsUnchanged: 0,
                TitlesInserted: 0,
                TitlesSkippedDuplicate: 0,
                Errors: new[] { new MappingImportError(0, null, property, message) });

        private static void RequireObject(JsonElement element, int index)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Item #{index} is a JSON {element.ValueKind}, not an object.");
        }

        private static List<T> ParseArray<T>(JsonElement array, string name, Func<JsonElement, T> parse)
        {
            if (array.ValueKind != JsonValueKind.Array)
                throw new JsonException($"\"{name}\" must be an array.");

            var items = new List<T>();
            int index = 0;
            foreach (JsonElement element in array.EnumerateArray())
            {
                index++;
                RequireObject(element, index);
                items.Add(parse(element));
            }

            return items;
        }

        private static TitleImportEntry ParseTitleEntry(JsonElement element)
        {
            string appId = GetStringProperty(element, AppIdNames) ?? string.Empty;
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
            string appId = GetStringProperty(element, AppIdNames) ?? string.Empty;
            string? gameName = GetStringProperty(element, "game_name", "gameName", "title", "name");
            string platform = GetStringProperty(element, "platform") ?? string.Empty;
            string pathTemplate = GetStringProperty(element, PathTemplateNames) ?? string.Empty;
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

        private static bool HasProperty(JsonElement element, params string[] propertyNames) =>
            TryGetProperty(element, out _, propertyNames);

        // Case-insensitive: the producers of these files (ai-detect --output,
        // the harvester's savepaths.extracted.json) write PascalCase names.
        private static bool TryGetProperty(JsonElement element, out JsonElement result, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        result = property.Value;
                        return true;
                    }
                }
            }

            result = default;
            return false;
        }

        private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
        {
            if (!TryGetProperty(element, out JsonElement prop, propertyNames))
                return null;

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.ToString(),
                _ => null
            };
        }

        private static bool TryGetIntProperty(JsonElement element, out int value, params string[] propertyNames)
        {
            value = 0;

            if (!TryGetProperty(element, out JsonElement prop, propertyNames))
                return false;

            return (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value)) ||
                   (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value));
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        }
    }
}
