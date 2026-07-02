using GameSave.External.Http;
using System.Text.Json;

namespace GameSave.External
{
    public sealed class PcgwApiClient
    {
        private readonly PoliteHttpClient _http;

        public PcgwApiClient(PoliteHttpClient http)
        {
            _http = http;
        }

        public async Task<PcgwTitle?> ResolveSteamAppIdAsync(
            string steamAppId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(steamAppId))
                return null;

            steamAppId = steamAppId.Trim();

            if (!steamAppId.All(char.IsDigit))
                return null;

            Console.WriteLine($"Resolving PCGamingWiki page for Steam AppID {steamAppId} using Cargo API...");

            PcgwTitle? title = await ResolveSteamAppIdWithCargoAsync(
                steamAppId,
                cancellationToken);

            if (title is not null)
            {
                Console.WriteLine($"Resolved AppID {steamAppId} -> {title.PageName} ({title.PageId})");
                return title;
            }

            Console.WriteLine($"No PCGamingWiki Cargo match found for Steam AppID {steamAppId}.");
            return null;
        }

        private async Task<PcgwTitle?> ResolveSteamAppIdWithCargoAsync(
            string steamAppId,
            CancellationToken cancellationToken)
        {
            var query = new Dictionary<string, string>
            {
                ["action"] = "cargoquery",
                ["format"] = "json",
                ["tables"] = "Infobox_game",
                ["fields"] =
                    "Infobox_game._pageID=PageID," +
                    "Infobox_game._pageName=Page," +
                    "Infobox_game.Steam_AppID=SteamAppID",
                ["where"] = $"Infobox_game.Steam_AppID HOLDS \"{steamAppId}\"",
                ["limit"] = "5"
            };

            string url = "https://www.pcgamingwiki.com/w/api.php?" + BuildQueryString(query);

            using JsonDocument document = await _http.GetJsonAsync(url, cancellationToken);

            if (!document.RootElement.TryGetProperty("cargoquery", out JsonElement cargoQuery))
                return null;

            if (cargoQuery.ValueKind != JsonValueKind.Array || cargoQuery.GetArrayLength() == 0)
                return null;

            foreach (JsonElement row in cargoQuery.EnumerateArray())
            {
                if (!row.TryGetProperty("title", out JsonElement titleElement))
                    continue;

                string? pageIdText = GetString(titleElement, "PageID");
                string? pageName = GetString(titleElement, "Page");
                string? steamAppIdRaw = GetString(titleElement, "SteamAppID");

                if (!int.TryParse(pageIdText, out int pageId))
                    continue;

                if (string.IsNullOrWhiteSpace(pageName))
                    continue;

                List<string> steamAppIds = ParseSteamAppIds(steamAppIdRaw);

                if (!steamAppIds.Contains(steamAppId, StringComparer.OrdinalIgnoreCase))
                    steamAppIds.Add(steamAppId);

                string normalizedPageName = pageName.Replace(' ', '_');

                string sourceUrl =
                    "https://www.pcgamingwiki.com/wiki/" +
                    Uri.EscapeDataString(normalizedPageName);

                return new PcgwTitle(
                    pageId,
                    normalizedPageName,
                    pageName,
                    steamAppIds,
                    sourceUrl);
            }

            return null;
        }

        public async Task<string> GetWikitextByPageIdAsync(
            int pageId,
            CancellationToken cancellationToken = default)
        {
            var query = new Dictionary<string, string>
            {
                ["action"] = "parse",
                ["format"] = "json",
                ["pageid"] = pageId.ToString(),
                ["prop"] = "wikitext"
            };

            string url = "https://www.pcgamingwiki.com/w/api.php?" + BuildQueryString(query);

            using JsonDocument document = await _http.GetJsonAsync(url, cancellationToken);

            if (!document.RootElement.TryGetProperty("parse", out JsonElement parse))
                return string.Empty;

            if (!parse.TryGetProperty("wikitext", out JsonElement wikitext))
                return string.Empty;

            if (wikitext.TryGetProperty("*", out JsonElement raw))
                return raw.GetString() ?? string.Empty;

            return string.Empty;
        }

        private static List<string> ParseSteamAppIds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? GetString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value))
                    return value.ToString();
            }

            return null;
        }

        private static string BuildQueryString(Dictionary<string, string> values)
        {
            return string.Join(
                "&",
                values.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }
    }
}