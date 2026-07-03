using GameSave.Data;
using GameSave.External.Http;
using GameSave.SavePaths;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSave.External
{
    public sealed class PcgwHarvester
    {
        private readonly PcgwHarvestOptions _options;
        private readonly ExternalHarvestDatabase _externalDatabase;
        private readonly SavePathDatabase _savePathDatabase;
        private readonly PcgwSavePathExtractor _extractor = new();

        public PcgwHarvester(PcgwHarvestOptions options)
        {
            _options = options;
            _externalDatabase = new ExternalHarvestDatabase(options.DatabasePath);
            _savePathDatabase = new SavePathDatabase(options.DatabasePath);
        }

        public async Task<PcgwHarvestResult> HarvestAsync(
            CancellationToken cancellationToken = default)
        {
            _externalDatabase.Initialize();
            _savePathDatabase.Initialize();

            Directory.CreateDirectory(_options.OutputRoot);
            Directory.CreateDirectory(Path.Combine(_options.OutputRoot, "index"));

            List<string> appIds = NormalizeAppIds(_options.SteamAppIds);

            if (_options.MaxTitlesToProcess > 0)
                appIds = appIds.Take(_options.MaxTitlesToProcess).ToList();

            if (appIds.Count == 0)
                throw new InvalidOperationException("No Steam AppIDs were provided to the PCGamingWiki harvester.");

            long runId = _externalDatabase.StartHarvestRun(
                _options.OutputRoot,
                _options.RequestsPerMinute);

            int titlesProcessed = 0;
            int titlesFailed = 0;
            int mappingsExtracted = 0;
            string stoppedReason = "Completed";

            try
            {
                using var politeHttp = new PoliteHttpClient(
                    _options.UserAgent,
                    _options.ToRateLimitOptions());

                var apiClient = new PcgwApiClient(politeHttp);

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
            PcgwApiClient apiClient,
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

            string rawSha256 = ComputeSha256(wikitext);

            var extractionTitle = new PcgwTitle(
                title.PageId,
                title.PageName,
                title.DisplayTitle,
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
                title.DisplayTitle,
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
                _savePathDatabase.ImportMappings(
                    extracted,
                    enabled: !_options.ImportExtractedMappingsDisabled);
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

        private static List<string> NormalizeAppIds(IEnumerable<string> appIds)
        {
            return appIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(value => value.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => int.TryParse(value, out int parsed) ? parsed : int.MaxValue)
                .ToList();
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