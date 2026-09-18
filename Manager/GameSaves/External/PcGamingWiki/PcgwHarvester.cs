using GameSaves.Core.Catalog;
using GameSaves.Core.Save;
using GameSaves.External.Http;
using GameSaves.Infrastructure.Save;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSaves.External
{
    public sealed class PcgwHarvester
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

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
            _externalDatabase.Initialize();
            _savePathDatabase.Initialize();

            Directory.CreateDirectory(_options.OutputRoot);
            Directory.CreateDirectory(Path.Combine(_options.OutputRoot, "index"));

            var (appIds, knownTitles) = ResolveHarvestTargets(_options, _savePathDatabase);

            bool hadAnyInputs = (_options.Tracklist is not null) ||
                                !string.IsNullOrWhiteSpace(_options.TracklistPath) ||
                                (_options.SteamAppIds is not null && _options.SteamAppIds.Count > 0);

            if (!hadAnyInputs)
                throw new InvalidOperationException("No Steam AppIDs or tracklist were provided to the PCGamingWiki harvester.");

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

                        string? knownTitle = knownTitles.GetValueOrDefault(appId);

                        int extractedForTitle = await HarvestOneTitleAsync(
                            apiClient,
                            title,
                            requestedSteamAppId: appId,
                            fallbackTitle: knownTitle,
                            cancellationToken);

                        titlesProcessed++;
                        mappingsExtracted += extractedForTitle;

                        Console.WriteLine(
                            $"[{titlesProcessed + titlesFailed}/{appIds.Count}] {title.PageName} ({appId}): {extractedForTitle} mapping(s)");
                    }
                    catch (OperationCanceledException)
                    {
                        stoppedReason = "Cancelled";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        titlesFailed++;
                        Console.WriteLine($"FAILED AppID {appId}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
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

        public static (List<string> AppIds, Dictionary<string, string> KnownTitles) ResolveHarvestTargets(
            PcgwHarvestOptions options,
            SavePathDatabase? savePathDatabase = null)
        {
            var appIds = new List<string>();
            var knownTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Direct Tracklist instance
            if (options.Tracklist is not null)
            {
                foreach (MissingTitleEntry entry in options.Tracklist.Items)
                {
                    if (string.IsNullOrWhiteSpace(entry.SteamAppId))
                        continue;

                    string cleaned = entry.SteamAppId.Trim();
                    if (!cleaned.All(char.IsDigit))
                        continue;

                    if (seen.Add(cleaned))
                    {
                        appIds.Add(cleaned);
                        if (!string.IsNullOrWhiteSpace(entry.Title))
                            knownTitles[cleaned] = entry.Title;
                    }
                }
            }

            // 2. Tracklist file path
            if (!string.IsNullOrWhiteSpace(options.TracklistPath))
            {
                if (!File.Exists(options.TracklistPath))
                    throw new FileNotFoundException($"Tracklist file not found: {options.TracklistPath}", options.TracklistPath);

                string content = File.ReadAllText(options.TracklistPath);
                bool parsedJson = false;

                string trimmed = content.TrimStart();
                if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("items", out JsonElement itemsElement) ||
                                doc.RootElement.TryGetProperty("Items", out itemsElement))
                            {
                                var items = JsonSerializer.Deserialize<List<MissingTitleEntry>>(itemsElement.GetRawText(), JsonOptions);
                                if (items is not null)
                                {
                                    foreach (var entry in items)
                                    {
                                        string cleaned = entry.SteamAppId?.Trim() ?? string.Empty;
                                        if (cleaned.Length > 0 && cleaned.All(char.IsDigit) && seen.Add(cleaned))
                                        {
                                            appIds.Add(cleaned);
                                            if (!string.IsNullOrWhiteSpace(entry.Title))
                                                knownTitles[cleaned] = entry.Title;
                                        }
                                    }
                                    parsedJson = true;
                                }
                            }
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            try
                            {
                                var items = JsonSerializer.Deserialize<List<MissingTitleEntry>>(content, JsonOptions);
                                if (items is not null && items.Count > 0 && !string.IsNullOrWhiteSpace(items[0].SteamAppId))
                                {
                                    foreach (var entry in items)
                                    {
                                        string cleaned = entry.SteamAppId?.Trim() ?? string.Empty;
                                        if (cleaned.Length > 0 && cleaned.All(char.IsDigit) && seen.Add(cleaned))
                                        {
                                            appIds.Add(cleaned);
                                            if (!string.IsNullOrWhiteSpace(entry.Title))
                                                knownTitles[cleaned] = entry.Title;
                                        }
                                    }
                                    parsedJson = true;
                                }
                            }
                            catch
                            {
                                // Try array of string appids
                            }

                            if (!parsedJson)
                            {
                                var strings = JsonSerializer.Deserialize<List<string>>(content, JsonOptions);
                                if (strings is not null)
                                {
                                    foreach (var str in strings)
                                    {
                                        string cleaned = str?.Trim() ?? string.Empty;
                                        if (cleaned.Length > 0 && cleaned.All(char.IsDigit) && seen.Add(cleaned))
                                        {
                                            appIds.Add(cleaned);
                                        }
                                    }
                                    parsedJson = true;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Fall back to CSV/plain text parsing
                    }
                }

                if (!parsedJson)
                {
                    AddAppIdsFromTextOrCsv(content, appIds, knownTitles, seen);
                }
            }

            // 3. Raw SteamAppIds in options
            if (options.SteamAppIds is not null)
            {
                foreach (string rawId in options.SteamAppIds)
                {
                    if (string.IsNullOrWhiteSpace(rawId))
                        continue;

                    string cleaned = rawId.Trim();
                    if (cleaned.All(char.IsDigit) && seen.Add(cleaned))
                    {
                        appIds.Add(cleaned);
                    }
                }
            }

            // 4. Filter existing in database if requested
            if (options.SkipExistingInDatabase && savePathDatabase is not null)
            {
                var filtered = new List<string>();
                foreach (string appId in appIds)
                {
                    var existing = savePathDatabase.GetMappingsForApp(appId, "windows", includeDisabled: true, onlyApproved: false);
                    if (existing.Count == 0)
                    {
                        filtered.Add(appId);
                    }
                }
                appIds = filtered;
            }

            // 5. Max titles to process
            if (options.MaxTitlesToProcess > 0)
            {
                appIds = appIds.Take(options.MaxTitlesToProcess).ToList();
            }

            return (appIds, knownTitles);
        }

        private static void AddAppIdsFromTextOrCsv(
            string content,
            List<string> appIds,
            Dictionary<string, string> knownTitles,
            HashSet<string> seen)
        {
            string[] lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                return;

            string firstLine = lines[0].Trim();
            if (firstLine.Contains("steamappid", StringComparison.OrdinalIgnoreCase))
            {
                // RFC 4180 / CSV format with header
                string[] headers = firstLine.Split(',');
                int appIdCol = -1;
                int titleCol = -1;

                for (int i = 0; i < headers.Length; i++)
                {
                    string h = headers[i].Trim().Trim('"');
                    if (h.Equals("SteamAppId", StringComparison.OrdinalIgnoreCase))
                        appIdCol = i;
                    else if (h.Equals("Title", StringComparison.OrdinalIgnoreCase))
                        titleCol = i;
                }

                if (appIdCol >= 0)
                {
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        string[] cols = line.Split(',');
                        if (cols.Length > appIdCol)
                        {
                            string rawId = cols[appIdCol].Trim().Trim('"');
                            if (rawId.All(char.IsDigit) && rawId.Length > 0 && seen.Add(rawId))
                            {
                                appIds.Add(rawId);
                                if (titleCol >= 0 && cols.Length > titleCol)
                                {
                                    string title = cols[titleCol].Trim().Trim('"');
                                    if (!string.IsNullOrWhiteSpace(title))
                                        knownTitles[rawId] = title;
                                }
                            }
                        }
                    }
                    return;
                }
            }

            // Plain text with space/newline/comma-separated AppIDs
            string[] parts = content.Split(
                new[] { '\r', '\n', ',', ';', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string part in parts)
            {
                string cleaned = part.Trim();
                if (cleaned.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (cleaned.All(char.IsDigit) && cleaned.Length > 0 && seen.Add(cleaned))
                {
                    appIds.Add(cleaned);
                }
            }
        }

        private async Task WriteAppIdInputIndexAsync(
            List<string> appIds,
            CancellationToken cancellationToken)
        {
            string indexPath = Path.Combine(
                _options.OutputRoot,
                "index",
                "steam-appids.input.json");

            string json = JsonSerializer.Serialize(
                appIds,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            await File.WriteAllTextAsync(indexPath, json, cancellationToken);
        }

        private async Task<int> HarvestOneTitleAsync(
            IPcgwApiClient apiClient,
            PcgwTitle title,
            string requestedSteamAppId,
            string? fallbackTitle,
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

            string rawSha256 = ComputeSha256(wikitext);

            string effectiveGameName = title.DisplayTitle
                ?? fallbackTitle
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

            string extractedJson = JsonSerializer.Serialize(
                extracted,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

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

            string metadataJson = JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

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

        private static string ComputeSha256(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
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