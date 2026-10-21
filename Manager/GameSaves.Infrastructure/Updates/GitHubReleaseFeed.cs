using GameSaves.Core.Updates;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameSaves.Infrastructure.Updates
{
    /// <summary>
    /// The GitHub Releases feed of this repository, read with one anonymous
    /// HTTPS GET: no token, no account, nothing that identifies the machine or
    /// the user beyond a fixed product name as <c>User-Agent</c>, which GitHub
    /// requires. Only the tag, the notes page and the two installer assets
    /// are read. A release whose tag is not <c>v&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;</c>,
    /// or that lacks the installer or its <c>.sha256</c> digest, is refused in
    /// words: without a published digest nothing could be verified.
    /// </summary>
    public sealed class GitHubReleaseFeed : IUpdateFeed
    {
        public const string DefaultLatestReleaseUrl =
            "https://api.github.com/repos/x0-lf/Game-Save-Manager/releases/latest";

        internal const string UserAgent = "GameSaveManager";

        private static readonly Regex TagPattern =
            new(@"^v(?<version>\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);

        private readonly HttpClient _httpClient;
        private readonly Uri _latestReleaseUri;

        public GitHubReleaseFeed(HttpClient? httpClient = null, string? latestReleaseUrl = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _latestReleaseUri = new Uri(latestReleaseUrl ?? DefaultLatestReleaseUrl, UriKind.Absolute);
        }

        public async Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // GitHub answers 404 for a repository that has published no release.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            using Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Parse(document.RootElement);
        }

        internal static UpdateRelease Parse(JsonElement release)
        {
            string tag = ReadString(release, "tag_name");
            Match match = TagPattern.Match(tag);

            if (!match.Success)
            {
                throw new FormatException(
                    $"The release tag '{tag}' is not v<major>.<minor>.<patch>, so its version cannot be compared.");
            }

            string versionText = match.Groups["version"].Value;
            string installerName = $"GameSaveManager-Setup-{versionText}.exe";
            Uri releaseNotes = ReadHttpsUri(release, "html_url");

            Uri? installer = null;
            long installerBytes = 0;
            Uri? digest = null;

            if (release.TryGetProperty("assets", out JsonElement assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string name = ReadString(asset, "name");

                    if (string.Equals(name, installerName, StringComparison.Ordinal))
                    {
                        installer = ReadHttpsUri(asset, "browser_download_url");
                        installerBytes =
                            asset.TryGetProperty("size", out JsonElement size) &&
                            size.TryGetInt64(out long bytes)
                                ? bytes
                                : 0;
                    }
                    else if (string.Equals(name, installerName + ".sha256", StringComparison.Ordinal))
                    {
                        digest = ReadHttpsUri(asset, "browser_download_url");
                    }
                }
            }

            if (installer is null)
                throw new FormatException($"Release {tag} has no {installerName} asset.");

            if (digest is null)
            {
                throw new FormatException(
                    $"Release {tag} has no {installerName}.sha256 digest, so its installer cannot be verified.");
            }

            return new UpdateRelease(
                Version.Parse(versionText),
                installerName,
                installer,
                installerBytes,
                digest,
                releaseNotes);
        }

        private static string ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : throw new FormatException($"The release feed has no '{property}'.");

        private static Uri ReadHttpsUri(JsonElement element, string property)
        {
            string text = ReadString(element, property);

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                throw new FormatException($"The release feed's '{property}' is not an https address.");
            }

            return uri;
        }
    }
}
