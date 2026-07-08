using GameSaves.Core.Steam;
using System.Net;
using System.Text.Json;

namespace GameSaves.External.Steam
{
    public sealed class SteamStoreCatalogClient : IDisposable
    {
        private const string GetAppListEndpoint =
            "https://api.steampowered.com/IStoreService/GetAppList/v1/";

        private readonly HttpClient _httpClient;

        public SteamStoreCatalogClient(string userAgent)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            };

            _httpClient = new HttpClient(handler, disposeHandler: true);
            _httpClient.Timeout = TimeSpan.FromSeconds(60);

            if (!string.IsNullOrWhiteSpace(userAgent))
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        public async Task<List<SteamCatalogApp>> GetAllAppsAsync(
            string steamWebApiKey,
            SteamCatalogAppKind kind,
            int maxResultsPerPage = 50_000,
            int maxAppsToFetch = 0,
            long? ifModifiedSinceUnix = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(steamWebApiKey))
                throw new ArgumentException("Steam Web API key is required.", nameof(steamWebApiKey));

            maxResultsPerPage = Math.Clamp(maxResultsPerPage, 1, 50_000);

            var results = new List<SteamCatalogApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            uint lastAppId = 0;

            while (true)
            {
                string url = BuildGetAppListUrl(
                    steamWebApiKey,
                    kind,
                    lastAppId,
                    maxResultsPerPage,
                    ifModifiedSinceUnix);

                using HttpResponseMessage response = await _httpClient.GetAsync(
                    url,
                    cancellationToken);

                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Steam catalog request failed. " +
                        $"Status: {(int)response.StatusCode} {response.StatusCode}. " +
                        $"Body preview: {CreatePreview(body)}");
                }

                SteamCatalogPage pageResult = ParsePage(body, kind);

                List<SteamCatalogApp> page = pageResult.Apps
                    .Where(app => seen.Add(app.SteamAppId))
                    .OrderBy(app => int.TryParse(app.SteamAppId, out int parsed) ? parsed : int.MaxValue)
                    .ToList();

                if (page.Count == 0)
                    break;

                foreach (SteamCatalogApp app in page)
                {
                    results.Add(app);

                    if (maxAppsToFetch > 0 && results.Count >= maxAppsToFetch)
                        return results;
                }

                Console.WriteLine(
                    $"Steam catalog {kind}: fetched {results.Count:n0}; response last_appid={pageResult.LastAppId}; have_more_results={pageResult.HaveMoreResults}");

                if (!pageResult.HaveMoreResults)
                    break;

                if (pageResult.LastAppId <= lastAppId)
                {
                    uint fallbackLastAppId = page
                        .Select(app => uint.TryParse(app.SteamAppId, out uint parsed) ? parsed : 0)
                        .Max();

                    if (fallbackLastAppId <= lastAppId)
                        break;

                    lastAppId = fallbackLastAppId;
                    continue;
                }

                lastAppId = pageResult.LastAppId;
            }

            return results;
        }

        private static string BuildGetAppListUrl(
            string steamWebApiKey,
            SteamCatalogAppKind kind,
            uint lastAppId,
            int maxResultsPerPage,
            long? ifModifiedSinceUnix)
        {
            var input = new Dictionary<string, object?>
            {
                ["include_games"] = kind == SteamCatalogAppKind.Game,
                ["include_dlc"] = kind == SteamCatalogAppKind.Dlc,
                ["include_software"] = false,
                ["include_videos"] = false,
                ["include_hardware"] = false,
                ["max_results"] = maxResultsPerPage
            };

            if (lastAppId > 0)
                input["last_appid"] = lastAppId;

            if (ifModifiedSinceUnix is not null)
                input["if_modified_since"] = ifModifiedSinceUnix.Value;

            string inputJson = JsonSerializer.Serialize(input);

            var query = new Dictionary<string, string>
            {
                ["key"] = steamWebApiKey,
                ["input_json"] = inputJson
            };

            return GetAppListEndpoint + "?" + BuildQueryString(query);
        }

        private static SteamCatalogPage ParsePage(
            string json,
            SteamCatalogAppKind kind)
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("response", out JsonElement response))
                return new SteamCatalogPage(new List<SteamCatalogApp>(), false, 0);

            bool haveMoreResults = GetBool(response, "have_more_results");
            uint lastAppId = GetUInt(response, "last_appid");

            if (!response.TryGetProperty("apps", out JsonElement apps) ||
                apps.ValueKind != JsonValueKind.Array)
            {
                return new SteamCatalogPage(new List<SteamCatalogApp>(), haveMoreResults, lastAppId);
            }

            var results = new List<SteamCatalogApp>();

            foreach (JsonElement app in apps.EnumerateArray())
            {
                string? appId = GetString(app, "appid");

                if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit))
                    continue;

                string name = GetString(app, "name")?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                long? lastModified = GetLong(app, "last_modified");
                string? priceChangeNumber = GetString(app, "price_change_number");

                results.Add(new SteamCatalogApp(
                    appId,
                    name,
                    kind,
                    lastModified,
                    priceChangeNumber));
            }

            return new SteamCatalogPage(results, haveMoreResults, lastAppId);
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToString()
            };
        }

        private static bool GetBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return false;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) && parsed,
                _ => false
            };
        }

        private static uint GetUInt(JsonElement element, string propertyName)
        {
            string? value = GetString(element, propertyName);

            return uint.TryParse(value, out uint parsed)
                ? parsed
                : 0;
        }

        private static long? GetLong(JsonElement element, string propertyName)
        {
            string? value = GetString(element, propertyName);

            return long.TryParse(value, out long parsed)
                ? parsed
                : null;
        }

        private static string BuildQueryString(Dictionary<string, string> values)
        {
            return string.Join(
                "&",
                values.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        private static string CreatePreview(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "<empty>";

            string flattened = value
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

            return flattened.Length <= 500
                ? flattened
                : flattened[..500] + "...";
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private sealed record SteamCatalogPage(
            List<SteamCatalogApp> Apps,
            bool HaveMoreResults,
            uint LastAppId);
    }
}