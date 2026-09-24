using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    /// <summary>
    /// Microsoft Graph API and identity endpoint client for Microsoft OneDrive.
    /// Operates strictly within the sandboxed application folder (drive/special/approot).
    /// </summary>
    internal sealed class OneDriveApiClient : IOneDriveApiClient, IDisposable
    {
        private const string DefaultGraphBaseUrl = "https://graph.microsoft.com/v1.0";
        private const string DefaultTokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

        /// <summary>Larger uploads go through an upload session; Graph's simple PUT tops out at 250 MB.</summary>
        internal const int SimpleUploadLimit = 4 * 1024 * 1024;

        /// <summary>10 MiB. Graph requires session chunks in multiples of 320 KiB.</summary>
        internal const int UploadChunkSize = 32 * 320 * 1024;

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _graphBaseUrl;
        private readonly string _tokenEndpoint;

        public OneDriveApiClient(
            HttpClient? httpClient = null,
            string? graphBaseUrl = null,
            string? tokenEndpoint = null)
        {
            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient();
            _graphBaseUrl = (graphBaseUrl ?? DefaultGraphBaseUrl).TrimEnd('/');
            _tokenEndpoint = tokenEndpoint ?? DefaultTokenEndpoint;
        }

        public Task<OneDriveTokenResponse> ExchangeCodeForTokenAsync(
            string clientId,
            string code,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken = default) =>
            RequestTokenAsync(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["code_verifier"] = codeVerifier,
                    ["scope"] = OneDriveAuthorizationScopes.DefaultScopes
                },
                cancellationToken);

        public Task<OneDriveTokenResponse> RefreshTokenAsync(
            string clientId,
            string refreshToken,
            CancellationToken cancellationToken = default) =>
            RequestTokenAsync(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["scope"] = OneDriveAuthorizationScopes.DefaultScopes
                },
                cancellationToken);

        public async Task<OneDriveAccountInfo> GetAccountInfoAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            string json = await GetStringAsync($"{_graphBaseUrl}/me", accessToken, cancellationToken)
                ?? throw new OneDriveApiException((int)HttpStatusCode.NotFound);

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string displayName = root.TryGetProperty("displayName", out var pName) ? pName.GetString() ?? "OneDrive User" : "OneDrive User";
            string emailOrUpn = root.TryGetProperty("userPrincipalName", out var pUpn) && !string.IsNullOrWhiteSpace(pUpn.GetString())
                ? pUpn.GetString()!
                : (root.TryGetProperty("mail", out var pMail) ? pMail.GetString() ?? "" : "");

            return new OneDriveAccountInfo(displayName, emailOrUpn);
        }

        public async Task<OneDriveQuotaInfo> GetQuotaAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            string json = await GetStringAsync($"{_graphBaseUrl}/me/drive", accessToken, cancellationToken)
                ?? throw new OneDriveApiException((int)HttpStatusCode.NotFound);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("quota", out var quota))
            {
                long total = quota.TryGetProperty("total", out var pTotal) ? pTotal.GetInt64() : 0;
                long used = quota.TryGetProperty("used", out var pUsed) ? pUsed.GetInt64() : 0;
                long remaining = quota.TryGetProperty("remaining", out var pRem) ? pRem.GetInt64() : 0;
                string? state = quota.TryGetProperty("state", out var pState) ? pState.GetString() : "normal";

                return new OneDriveQuotaInfo(total, used, remaining, state);
            }

            return new OneDriveQuotaInfo(0, 0, 0, "unknown");
        }

        public async Task<IReadOnlyList<OneDriveItemInfo>> ListChildrenAsync(
            string accessToken,
            string pathUnderAppRoot = "",
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            string? url = string.IsNullOrEmpty(normalized)
                ? $"{_graphBaseUrl}/me/drive/special/approot/children"
                : $"{ItemUrl(normalized)}:/children";

            var results = new List<OneDriveItemInfo>();

            // Graph pages children (200 per page by default). Every page must be
            // read: a missed page would hide runs and make a partial run look in sync.
            for (bool firstPage = true; url is not null; firstPage = false)
            {
                string? json = await GetStringAsync(url, accessToken, cancellationToken);
                if (json is null)
                {
                    if (firstPage)
                        return results;

                    throw new OneDriveApiException((int)HttpStatusCode.NotFound);
                }

                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("value", out var valueArray) &&
                    valueArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in valueArray.EnumerateArray())
                    {
                        if (ParseItem(el) is { } item)
                            results.Add(item);
                    }
                }

                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString()
                    : null;

                // The next page is requested with the bearer token, so the link must stay on Graph.
                if (url is not null && !url.StartsWith(_graphBaseUrl + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Microsoft OneDrive returned a paging link outside Microsoft Graph.");
                }
            }

            return results;
        }

        public async Task<OneDriveItemInfo?> GetItemAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            string url = string.IsNullOrEmpty(normalized)
                ? $"{_graphBaseUrl}/me/drive/special/approot"
                : ItemUrl(normalized);

            string? json = await GetStringAsync(url, accessToken, cancellationToken);
            if (json is null)
                return null;

            using var doc = JsonDocument.Parse(json);
            return ParseItem(doc.RootElement);
        }

        public Task<string?> ReadTextAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            if (string.IsNullOrEmpty(normalized))
                return Task.FromResult<string?>(null);

            return GetStringAsync($"{ItemUrl(normalized)}:/content", accessToken, cancellationToken);
        }

        public async Task UploadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream contentStream,
            bool createOnly,
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            if (string.IsNullOrEmpty(normalized))
                throw new ArgumentException("Cannot upload content directly to approot without a filename.", nameof(pathUnderAppRoot));

            if (contentStream.Length > SimpleUploadLimit)
            {
                await UploadInSessionAsync(accessToken, normalized, contentStream, createOnly, cancellationToken);
                return;
            }

            // A plain PUT replaces by default; create-only content must ask Graph to refuse instead,
            // so a file created by another machine between any check and this request is never overwritten.
            string url = $"{ItemUrl(normalized)}:/content" +
                (createOnly ? "?@microsoft.graph.conflictBehavior=fail" : "");

            using var request = CreateAuthorizedRequest(HttpMethod.Put, url, accessToken);
            request.Content = new StreamContent(contentStream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await SendAsync(request, cancellationToken);
            await EnsureUploadSuccessAsync(response, normalized, createOnly, cancellationToken);
        }

        public async Task DownloadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream destinationStream,
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            if (string.IsNullOrEmpty(normalized))
                throw new ArgumentException("Path to download cannot be empty.", nameof(pathUnderAppRoot));

            using var request = CreateAuthorizedRequest(HttpMethod.Get, $"{ItemUrl(normalized)}:/content", accessToken);
            using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new FileNotFoundException("The Microsoft OneDrive file to download was not found.");

            await EnsureSuccessAsync(response, cancellationToken);

            using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }

        private async Task UploadInSessionAsync(
            string accessToken,
            string normalizedPath,
            Stream contentStream,
            bool createOnly,
            CancellationToken cancellationToken)
        {
            string conflictBehavior = createOnly ? "fail" : "replace";
            string uploadUrl;

            using (var request = CreateAuthorizedRequest(HttpMethod.Post, $"{ItemUrl(normalizedPath)}:/createUploadSession", accessToken))
            {
                request.Content = new StringContent(
                    $"{{\"item\":{{\"@microsoft.graph.conflictBehavior\":\"{conflictBehavior}\"}}}}",
                    Encoding.UTF8,
                    "application/json");

                using var response = await SendAsync(request, cancellationToken);
                await EnsureUploadSuccessAsync(response, normalizedPath, createOnly, cancellationToken);

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                string? candidate = doc.RootElement.TryGetProperty("uploadUrl", out var pUrl) ? pUrl.GetString() : null;

                if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Microsoft OneDrive returned an invalid upload session.");

                uploadUrl = candidate!;
            }

            long total = contentStream.Length;
            byte[] buffer = new byte[(int)Math.Min(UploadChunkSize, total)];

            for (long offset = 0; offset < total;)
            {
                int count = (int)Math.Min(buffer.Length, total - offset);
                await contentStream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);

                // The session URL is pre-authenticated; the bearer token must not be sent to it.
                // One chunk per request keeps each request well inside the HttpClient timeout.
                using var chunk = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(buffer, 0, count)
                };
                chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, total);

                using var response = await SendAsync(chunk, cancellationToken);
                await EnsureUploadSuccessAsync(response, normalizedPath, createOnly, cancellationToken);
                offset += count;
            }
        }

        private async Task<OneDriveTokenResponse> RequestTokenAsync(
            Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };

            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            return ParseTokenResponse(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        /// <summary>GETs a Graph resource as text; null on 404.</summary>
        private async Task<string?> GetStringAsync(
            string url,
            string accessToken,
            CancellationToken cancellationToken)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
        {
            try
            {
                return await _httpClient.SendAsync(request, completionOption, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new OneDriveApiException(null, innerException: ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout surfaces as a cancellation. It is a transient
                // failure, not the user cancelling, so it must stay retryable.
                throw new OneDriveApiException(null, innerException: ex);
            }
        }

        private static async Task EnsureUploadSuccessAsync(
            HttpResponseMessage response,
            string normalizedPath,
            bool createOnly,
            CancellationToken cancellationToken)
        {
            // With conflictBehavior=fail Graph answers 409 when the file already exists.
            if (createOnly && response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {normalizedPath}");
            }

            await EnsureSuccessAsync(response, cancellationToken);
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;

            // Only the OAuth error code is kept from the body; the rest never leaves this method.
            string? oauthError = null;
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("error", out var error) &&
                        error.ValueKind == JsonValueKind.String)
                    {
                        oauthError = error.GetString();
                    }
                }
                catch (JsonException)
                {
                }
            }

            RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
            TimeSpan? delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);

            throw new OneDriveApiException(
                (int)response.StatusCode,
                delay < TimeSpan.Zero ? TimeSpan.Zero : delay,
                oauthError);
        }

        private static HttpRequestMessage CreateAuthorizedRequest(
            HttpMethod method,
            string url,
            string accessToken)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }

        private static OneDriveTokenResponse ParseTokenResponse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string accessToken = root.TryGetProperty("access_token", out var pAccess) && pAccess.GetString() is { Length: > 0 } access
                ? access
                : throw new InvalidOperationException("The Microsoft OneDrive token response did not contain an access token.");
            string? refreshToken = root.TryGetProperty("refresh_token", out var pRef) ? pRef.GetString() : null;
            int expiresIn = root.TryGetProperty("expires_in", out var pExp) ? pExp.GetInt32() : 3600;
            string tokenType = root.TryGetProperty("token_type", out var pType) ? pType.GetString() ?? "Bearer" : "Bearer";
            string? scope = root.TryGetProperty("scope", out var pScope) ? pScope.GetString() : null;

            return new OneDriveTokenResponse(accessToken, refreshToken, expiresIn, tokenType, scope);
        }

        private static OneDriveItemInfo? ParseItem(JsonElement el) =>
            el.TryGetProperty("name", out var nameProp)
                ? new OneDriveItemInfo(
                    nameProp.GetString() ?? "",
                    IsFolder: el.TryGetProperty("folder", out _),
                    IsFile: el.TryGetProperty("file", out _))
                : null;

        private string ItemUrl(string normalizedPath) =>
            $"{_graphBaseUrl}/me/drive/special/approot:{EncodePath(normalizedPath)}";

        private static string NormalizePath(string path) =>
            path.Replace('\\', '/').Trim('/');

        private static string EncodePath(string normalizedPath)
        {
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var encoded = segments.Select(Uri.EscapeDataString);
            return "/" + string.Join("/", encoded);
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }
    }
}
