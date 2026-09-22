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
    public sealed class OneDriveApiClient : IOneDriveApiClient, IDisposable
    {
        private const string DefaultGraphBaseUrl = "https://graph.microsoft.com/v1.0";
        private const string DefaultTokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string DefaultClientId = "a3f5a2b8-7c1e-4f3d-9a8b-2e4c6d8f0a1b"; // GameSave Manager Public Desktop App Client ID

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _graphBaseUrl;
        private readonly string _tokenEndpoint;
        private readonly string _clientId;

        public OneDriveApiClient(
            HttpClient? httpClient = null,
            string? graphBaseUrl = null,
            string? tokenEndpoint = null,
            string? clientId = null)
        {
            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient();
            _graphBaseUrl = (graphBaseUrl ?? DefaultGraphBaseUrl).TrimEnd('/');
            _tokenEndpoint = tokenEndpoint ?? DefaultTokenEndpoint;
            _clientId = !string.IsNullOrWhiteSpace(clientId)
                ? clientId
                : Environment.GetEnvironmentVariable("GAMESAVE_ONEDRIVE_CLIENT_ID") ?? DefaultClientId;
        }

        public OneDriveApiClient(HttpMessageHandler handler)
            : this(new HttpClient(handler), null, null, null)
        {
            _ownsHttpClient = true;
        }

        public async Task<OneDriveTokenResponse> ExchangeCodeForTokenAsync(
            string code,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken = default)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
                new KeyValuePair<string, string>("scope", OneDriveAuthorizationScopes.DefaultScopes)
            });

            using var response = await _httpClient.PostAsync(_tokenEndpoint, content, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive token exchange failed (HTTP {(int)response.StatusCode}): {json}");
            }

            return ParseTokenResponse(json);
        }

        public async Task<OneDriveTokenResponse> RefreshTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("scope", OneDriveAuthorizationScopes.DefaultScopes)
            });

            using var response = await _httpClient.PostAsync(_tokenEndpoint, content, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive token refresh failed (HTTP {(int)response.StatusCode}): {json}");
            }

            return ParseTokenResponse(json);
        }

        public async Task<OneDriveAccountInfo> GetAccountInfoAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            string url = $"{_graphBaseUrl}/me";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive account info request failed (HTTP {(int)response.StatusCode}): {json}");
            }

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string displayName = root.TryGetProperty("displayName", out var pName) ? pName.GetString() ?? "OneDrive User" : "OneDrive User";
            string emailOrUpn = root.TryGetProperty("userPrincipalName", out var pUpn) && !string.IsNullOrWhiteSpace(pUpn.GetString())
                ? pUpn.GetString()!
                : (root.TryGetProperty("mail", out var pMail) ? pMail.GetString() ?? "" : "");
            string id = root.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "";

            return new OneDriveAccountInfo(displayName, emailOrUpn, id);
        }

        public async Task<OneDriveQuotaInfo> GetQuotaAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            string url = $"{_graphBaseUrl}/me/drive";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive drive quota request failed (HTTP {(int)response.StatusCode}): {json}");
            }

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

        public async Task<OneDriveItemInfo?> GetAppRootAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            string url = $"{_graphBaseUrl}/me/drive/special/approot";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive approot request failed (HTTP {(int)response.StatusCode}): {json}");
            }

            return ParseItem(json, "");
        }

        public async Task<IReadOnlyList<OneDriveItemInfo>> ListChildrenAsync(
            string accessToken,
            string pathUnderAppRoot = "",
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            string url = string.IsNullOrEmpty(normalized)
                ? $"{_graphBaseUrl}/me/drive/special/approot/children"
                : $"{_graphBaseUrl}/me/drive/special/approot:{EncodePath(normalized)}:/children";

            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Array.Empty<OneDriveItemInfo>();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive list children request failed for '{pathUnderAppRoot}' (HTTP {(int)response.StatusCode}): {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var results = new List<OneDriveItemInfo>();

            if (doc.RootElement.TryGetProperty("value", out var valueArray) &&
                valueArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in valueArray.EnumerateArray())
                {
                    var item = ParseItemElement(el, normalized);
                    if (item != null)
                        results.Add(item);
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
            if (string.IsNullOrEmpty(normalized))
                return await GetAppRootAsync(accessToken, cancellationToken);

            string url = $"{_graphBaseUrl}/me/drive/special/approot:{EncodePath(normalized)}";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive get item request failed for '{pathUnderAppRoot}' (HTTP {(int)response.StatusCode}): {json}");
            }

            string parent = GetParentPath(normalized);
            return ParseItem(json, parent);
        }

        public async Task<string?> ReadTextAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            if (string.IsNullOrEmpty(normalized))
                return null;

            string url = $"{_graphBaseUrl}/me/drive/special/approot:{EncodePath(normalized)}:/content";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"OneDrive read text request failed for '{pathUnderAppRoot}' (HTTP {(int)response.StatusCode}): {err}");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        public async Task<OneDriveItemInfo> UploadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream contentStream,
            CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pathUnderAppRoot);
            if (string.IsNullOrEmpty(normalized))
                throw new ArgumentException("Cannot upload content directly to approot without a filename.", nameof(pathUnderAppRoot));

            string url = $"{_graphBaseUrl}/me/drive/special/approot:{EncodePath(normalized)}:/content";
            using var request = CreateAuthorizedRequest(HttpMethod.Put, url, accessToken);
            request.Content = new StreamContent(contentStream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OneDrive upload request failed for '{pathUnderAppRoot}' (HTTP {(int)response.StatusCode}): {json}");
            }

            string parent = GetParentPath(normalized);
            OneDriveItemInfo? item = ParseItem(json, parent);
            return item ?? throw new InvalidOperationException("Failed to parse uploaded item response.");
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

            string url = $"{_graphBaseUrl}/me/drive/special/approot:{EncodePath(normalized)}:/content";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException(
                    $"OneDrive file not found for download: {pathUnderAppRoot}");
            }

            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"OneDrive download request failed for '{pathUnderAppRoot}' (HTTP {(int)response.StatusCode}): {err}");
            }

            using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
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

            string accessToken = root.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Token response missing access_token");
            string? refreshToken = root.TryGetProperty("refresh_token", out var pRef) ? pRef.GetString() : null;
            int expiresIn = root.TryGetProperty("expires_in", out var pExp) ? pExp.GetInt32() : 3600;
            string tokenType = root.TryGetProperty("token_type", out var pType) ? pType.GetString() ?? "Bearer" : "Bearer";
            string? scope = root.TryGetProperty("scope", out var pScope) ? pScope.GetString() : null;

            DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            return new OneDriveTokenResponse(
                accessToken,
                refreshToken,
                expiresIn,
                tokenType,
                scope,
                expiresAtUtc);
        }

        private static OneDriveItemInfo? ParseItem(string json, string parentPath)
        {
            using var doc = JsonDocument.Parse(json);
            return ParseItemElement(doc.RootElement, parentPath);
        }

        private static OneDriveItemInfo? ParseItemElement(JsonElement el, string parentPath)
        {
            if (!el.TryGetProperty("id", out var idProp) || !el.TryGetProperty("name", out var nameProp))
                return null;

            string id = idProp.GetString() ?? "";
            string name = nameProp.GetString() ?? "";
            long size = el.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;

            bool isFolder = el.TryGetProperty("folder", out _);
            bool isFile = el.TryGetProperty("file", out _);

            string relativePath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

            DateTimeOffset? lastModified = null;
            if (el.TryGetProperty("lastModifiedDateTime", out var modProp) &&
                DateTimeOffset.TryParse(modProp.GetString(), out var dt))
            {
                lastModified = dt;
            }

            string? etag = el.TryGetProperty("eTag", out var etagProp) ? etagProp.GetString() : null;

            string? quickXor = null;
            string? sha1 = null;
            if (el.TryGetProperty("file", out var fileProp) &&
                fileProp.TryGetProperty("hashes", out var hashes))
            {
                if (hashes.TryGetProperty("quickXorHash", out var qx)) quickXor = qx.GetString();
                if (hashes.TryGetProperty("sha1Hash", out var s1)) sha1 = s1.GetString();
            }

            return new OneDriveItemInfo(
                id,
                name,
                size,
                isFolder,
                isFile,
                relativePath,
                lastModified,
                etag,
                quickXor,
                sha1);
        }

        private static string NormalizePath(string path) =>
            path.Replace('\\', '/').Trim('/');

        private static string EncodePath(string normalizedPath)
        {
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var encoded = segments.Select(Uri.EscapeDataString);
            return "/" + string.Join("/", encoded);
        }

        private static string GetParentPath(string normalizedPath)
        {
            int idx = normalizedPath.LastIndexOf('/');
            return idx > 0 ? normalizedPath.Substring(0, idx) : "";
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }
    }
}
