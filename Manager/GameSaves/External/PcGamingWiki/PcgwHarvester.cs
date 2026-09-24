using GameSaves.Core.Save;
using GameSaves.External.Http;
using GameSaves.Infrastructure.Save;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSaves.External
{
    public sealed class PcgwHarvester
    {
        private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

        private static readonly char[] AppIdSeparators = { ',', ';', ' ', '\t' };

        private readonly PcgwHarvestOptions _options;
        private readonly ExternalHarvestDatabase _externalDatabase;
        private readonly SavePathDatabase _savePathDatabase;
        private readonly IPcgwApiClient? _injectedApiClient;
        private readonly PcgwSavePathExtractor _extractor = new();

        public PcgwHarvester(PcgwHarvestOptions options)
            : this(options, null, null, null)
        {
        }

        public PcgwHarvester(
            PcgwHarvestOptions options,
            IPcgwApiClient? apiClient = null,
            ExternalHarvestDatabase? externalDatabase = null,
            SavePathDatabase? savePathDatabase = null)
        {
            _options = options;
            _injectedApiClient = apiClient;
            _externalDatabase = externalDatabase ?? new ExternalHarvestDatabase(options.DatabasePath);
            _savePathDatabase = savePathDatabase ?? new SavePathDatabase(options.DatabasePath);
        }

        public async Task<PcgwHarvestResult> HarvestAsync(
            CancellationToken cancellationToken = default)
        {
            // The schema migrations create game_titles and the external_* harvest tables.
            _savePathDatabase.Initialize();

            Directory.CreateDirectory(_options.OutputRoot);
            Directory.CreateDirectory(Path.Combine(_options.OutputRoot, "index"));

            if (_options.SteamAppIds.Count == 0)
                throw new InvalidOperationException("No Steam AppIDs were provided to the PCGamingWiki harvester.");

            List<string> appIds = ResolveHarvestTargets(_options);

            if (appIds.Count == 0)
            {
                Console.WriteLine("No candidate Steam AppIDs remain to harvest (all may be covered or filtered).");
                return new PcgwHarvestResult(0, 0, 0, 0);
            }

            long runId = _externalDatabase.StartHarvestRun(
                _options.OutputRoot,
                _options.RequestsPerMinute);

            int titlesProcessed = 0;
            int titlesFailed = 0;
            int mappingsExtracted = 0;
            string stoppedReason = "Completed";

            try
            {
                using PoliteHttpClient? politeHttp = _injectedApiClient is null
                    ? new PoliteHttpClient(_options.UserAgent, _options.ToRateLimitOptions())
                    : null;

                IPcgwApiClient apiClient = _injectedApiClient ?? new PcgwApiClient(politeHttp!);

                await WriteAppIdInputIndexAsync(appIds, cancellationToken);

                foreach (string appId in appIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        PcgwTitle? title = await apiClient.ResolveSteamAppIdAsync(
                            appId,
                            cancellationToken);

                        if (title is null)
                        {
                            titlesFailed++;
                            Console.WriteLine($"[{titlesProcessed + titlesFailed}/{appIds.Count}] AppID {appId}: no PCGamingWiki page found.");
                            continue;
                        }

                        _externalDatabase.UpsertPcgwTitle(title);

                        int extractedForTitle = await HarvestOneTitleAsync(
                            apiClient,
                            title,
                            requestedSteamAppId: appId,
                            cancellationToken);

                        titlesProcessed++;
                        mappingsExtracted += extractedForTitle;

                        Console.WriteLine(
                            $"[{titlesProcessed + titlesFailed}/{appIds.Count}] {title.PageName} ({appId}): {extractedForTitle} mapping(s)");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        stoppedReason = "Cancelled";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // An HttpClient timeout surfaces as TaskCanceledException without our token
                        // being cancelled: it is a failure of this title, not a request to stop.
                        titlesFailed++;
                        Console.WriteLine($"FAILED AppID {appId}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stoppedReason = "Cancelled";
            }
            finally
            {
                _externalDatabase.CompleteHarvestRun(
                    runId,
                    appIds.Count,
                    titlesProcessed,
                    titlesFailed,
                    mappingsExtracted,
                    stoppedReason);
            }

            return new PcgwHarvestResult(
                appIds.Count,
                titlesProcessed,
                titlesFailed,
                mappingsExtracted);
        }

        public static List<string> ResolveHarvestTargets(PcgwHarvestOptions options)
        {
            IEnumerable<string> appIds = options.SteamAppIds
                .Select(id => id?.Trim() ?? string.Empty)
                .Where(IsAppId)
                .Distinct(StringComparer.Ordinal);

            return options.MaxTitlesToProcess > 0
                ? appIds.Take(options.MaxTitlesToProcess).ToList()
                : appIds.ToList();
        }

        public static bool IsAppId([NotNullWhen(true)] string? value) =>
            !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit);

        /// <summary>
        /// Reads Steam AppIDs from a tracklist export (JSON or CSV with a SteamAppId column)
        /// or from plain text holding one or more AppIDs per line.
        /// </summary>
        /// <exception cref="InvalidDataException">The content looks like JSON but is not a readable AppID list.</exception>
        public static List<string> ReadAppIds(string content)
        {
            var appIds = new List<string>();
            string trimmed = content.TrimStart();

            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                // Never fall back to scanning a JSON document for digits: that harvests every
                // bare number in it ("totalReconciled": 220) while skipping the quoted AppIDs.
                ReadJsonAppIds(content, appIds);
                return appIds.Distinct(StringComparer.Ordinal).ToList();
            }

            string[] lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);

            int appIdColumn = lines.Length == 0
                ? -1
                : Array.FindIndex(
                    lines[0].Split(','),
                    header => header.Trim().Trim('"').Equals("SteamAppId", StringComparison.OrdinalIgnoreCase));

            if (appIdColumn >= 0)
            {
                foreach (string line in lines.Skip(1))
                {
                    string[] columns = line.Split(',');
                    if (columns.Length > appIdColumn)
                    {
                        string value = columns[appIdColumn].Trim().Trim('"');
                        if (IsAppId(value))
                            appIds.Add(value);
                    }
                }
            }
            else
            {
                foreach (string line in lines)
                {
                    string[] tokens = line.Split('#')[0].Split(
                        AppIdSeparators,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    // "413150, Portal 2" is an AppID followed by a title: only the first token
                    // counts, so the 2 in the title is not harvested as an AppID.
                    if (tokens.All(IsAppId))
                        appIds.AddRange(tokens);
                    else if (tokens.Length > 0 && IsAppId(tokens[0]))
                        appIds.Add(tokens[0]);
                }
            }

            return appIds.Distinct(StringComparer.Ordinal).ToList();
        }

        private static void ReadJsonAppIds(string content, List<string> appIds)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    content,
                    new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                JsonElement items = document.RootElement.ValueKind == JsonValueKind.Object
                    ? GetPropertyIgnoreCase(document.RootElement, "items")
                    : document.RootElement;

                if (items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Expected a JSON array of AppIDs or an object with an \"items\" array.");

                foreach (JsonElement item in items.EnumerateArray())
                {
                    JsonElement value = item.ValueKind == JsonValueKind.Object
                        ? GetPropertyIgnoreCase(item, "steamAppId")
                        : item;

                    string? text = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString()?.Trim(),
                        JsonValueKind.Number => value.GetRawText(),
                        _ => null
                    };

                    if (IsAppId(text))
                        appIds.Add(text);
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The AppID list looks like JSON but could not be parsed: {ex.Message}", ex);
            }
        }

        private static JsonElement GetPropertyIgnoreCase(JsonElement element, string name)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }

            return default;
        }

        private async Task WriteAppIdInputIndexAsync(
            List<string> appIds,
            CancellationToken cancellationToken)
        {
            string indexPath = Path.Combine(
                _options.OutputRoot,
                "index",
                "steam-appids.input.json");

            string json = JsonSerializer.Serialize(appIds, IndentedJson);

            await File.WriteAllTextAsync(indexPath, json, cancellationToken);
        }

        private async Task<int> HarvestOneTitleAsync(
            IPcgwApiClient apiClient,
            PcgwTitle title,
            string requestedSteamAppId,
            CancellationToken cancellationToken)
        {
            string titleDirectory = GetTitleDirectory(title);
            Directory.CreateDirectory(titleDirectory);

            string metadataPath = Path.Combine(titleDirectory, "metadata.json");
            string rawPath = Path.Combine(titleDirectory, "raw.wikitext");
            string extractedJsonPath = Path.Combine(titleDirectory, "savepaths.extracted.json");

            string wikitext = await apiClient.GetWikitextByPageIdAsync(
                title.PageId,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(wikitext))
                throw new InvalidOperationException("Empty PCGamingWiki wikitext response.");

            await File.WriteAllTextAsync(rawPath, wikitext, cancellationToken);

            string rawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wikitext)));

            string effectiveGameName = title.DisplayTitle
                ?? title.PageName.Replace('_', ' ');

            var extractionTitle = new PcgwTitle(
                title.PageId,
                title.PageName,
                effectiveGameName,
                new List<string> { requestedSteamAppId },
                title.SourceUrl);

            List<SavePathImportItem> extracted = _extractor.ExtractCandidates(
                extractionTitle,
                wikitext);

            string extractedJson = JsonSerializer.Serialize(extracted, IndentedJson);

            await File.WriteAllTextAsync(extractedJsonPath, extractedJson, cancellationToken);

            var metadata = new
            {
                title.PageId,
                title.PageName,
                DisplayTitle = effectiveGameName,
                RequestedSteamAppId = requestedSteamAppId,
                AllPcgwSteamAppIds = title.SteamAppIds,
                title.SourceUrl,
                RawWikitextPath = rawPath,
                ExtractedJsonPath = extractedJsonPath,
                RawSha256 = rawSha256,
                ExtractedCount = extracted.Count,
                SourceLicense = "CC-BY-NC-SA unless otherwise noted",
                HarvestedUtc = DateTime.UtcNow
            };

            string metadataJson = JsonSerializer.Serialize(metadata, IndentedJson);

            await File.WriteAllTextAsync(metadataPath, metadataJson, cancellationToken);

            _externalDatabase.MarkPcgwTitleHarvested(
                title,
                rawPath,
                extractedJsonPath,
                rawSha256);

            if (extracted.Count > 0)
            {
                // Strict Trust Model Invariant: All harvested mappings are inserted as Pending and disabled (enabled = 0).
                _savePathDatabase.ImportMappings(
                    extracted,
                    enabled: false,
                    reviewStatus: "Pending");
            }

            return extracted.Count;
        }

        private string GetTitleDirectory(PcgwTitle title)
        {
            string safeName = MakeSafePathPart(title.PageName);

            return Path.Combine(
                _options.OutputRoot,
                $"{title.PageId}-{safeName}");
        }

        private static string MakeSafePathPart(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();

            string cleaned = new string(
                value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            return string.IsNullOrWhiteSpace(cleaned)
                ? "Unknown"
                : cleaned;
        }
    }
}
