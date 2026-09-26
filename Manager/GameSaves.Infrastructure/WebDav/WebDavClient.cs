using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.WebDav
{
    /// <summary>One resource from a PROPFIND answer.</summary>
    internal sealed record WebDavEntry(string Name, bool IsCollection, long? Length);

    /// <summary>
    /// A WebDAV request that failed. Messages are fixed sentences plus the
    /// HTTP status: no response body, URL, or credential ever reaches them.
    /// StatusCode is null when no response arrived (network error, timeout).
    /// </summary>
    internal sealed class WebDavException : Exception, IRetryDelayCarrier
    {
        public WebDavException(
            int? statusCode,
            string message,
            TimeSpan? retryAfterDelay = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            RetryAfterDelay = retryAfterDelay;
        }

        public int? StatusCode { get; }

        public TimeSpan? RetryAfterDelay { get; }
    }

    /// <summary>
    /// The RFC 4918 subset the sync engine needs, over one shared HttpClient:
    /// PROPFIND (depth 0 and 1), MKCOL, PUT, and GET. Paths are relative to the
    /// server URL and '/'-separated; every segment is escaped, and '.' or '..'
    /// is refused, so no request can leave the configured server path.
    /// </summary>
    internal sealed class WebDavClient
    {
        /// <summary>Cap on PROPFIND answers and text files read into memory.</summary>
        internal const int MaxResponseBytes = 16 * 1024 * 1024;

        private const string PropfindBody =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>";

        private static readonly HttpMethod Propfind = new("PROPFIND");
        private static readonly HttpMethod Mkcol = new("MKCOL");

        // Control requests are small and should answer quickly. A transfer may
        // legitimately take long; its bound only stops a dead connection from
        // hanging a sync forever. ponytail: no stall watchdog, add one if
        // multi-hour single-file transfers ever matter.
        private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(100);
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(1);

        private readonly HttpClient _httpClient;
        private readonly Uri _serverUrl;
        private readonly string _username;
        private readonly Func<CancellationToken, Task<string?>> _passwordSource;
        private AuthenticationHeaderValue? _authorization;

        public WebDavClient(
            HttpClient httpClient,
            string serverUrl,
            string username,
            Func<CancellationToken, Task<string?>> passwordSource)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _passwordSource = passwordSource ?? throw new ArgumentNullException(nameof(passwordSource));
            _serverUrl = new Uri(
                WebDavSyncRemoteSettings.NormalizeServerUrl(serverUrl),
                UriKind.Absolute);
            _username = WebDavSyncRemoteSettings.NormalizeUsername(username);
        }

        /// <summary>The resource itself, or null when it does not exist.</summary>
        public async Task<WebDavEntry?> StatAsync(
            string relativePath,
            bool collection,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<(string Path, WebDavEntry Entry)>? entries =
                await PropfindAsync(ResourceUri(relativePath, collection), depth: 0, cancellationToken);

            return entries is { Count: > 0 } ? entries[0].Entry : null;
        }

        /// <summary>The direct children of a collection, or null when it does not exist.</summary>
        public async Task<IReadOnlyList<WebDavEntry>?> ListAsync(
            string relativeFolder,
            CancellationToken cancellationToken)
        {
            Uri folderUri = ResourceUri(relativeFolder, collection: true);
            IReadOnlyList<(string Path, WebDavEntry Entry)>? entries =
                await PropfindAsync(folderUri, depth: 1, cancellationToken);

            if (entries is null)
                return null;

            // Hrefs may be absolute paths or full URLs, escaped either way.
            // Only direct children of the requested path are kept; the folder
            // itself and anything a server lists from elsewhere are not.
            string folderPath = Uri.UnescapeDataString(folderUri.AbsolutePath).TrimEnd('/');
            var children = new List<WebDavEntry>();

            foreach ((string path, WebDavEntry entry) in entries)
            {
                string trimmed = path.TrimEnd('/');

                if (!trimmed.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = trimmed[(folderPath.Length + 1)..];

                if (name.Length == 0 || name.Contains('/') || name is "." or "..")
                    continue;

                children.Add(entry with { Name = name });
            }

            return children;
        }

        /// <summary>Creates one collection; an existing one (405) is fine.</summary>
        public async Task MakeCollectionAsync(
            string relativeFolder,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(
                Mkcol,
                ResourceUri(relativeFolder, collection: true),
                configure: null,
                transfer: false,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed)
                return;

            throw Failure(response);
        }

        /// <summary>
        /// Uploads content. With createOnly the request carries
        /// If-None-Match: *, so the server itself refuses (412) to replace a
        /// resource that exists, even one created a moment ago by someone else.
        /// </summary>
        public async Task PutAsync(
            string relativePath,
            HttpContent content,
            bool createOnly,
            bool transfer,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Put,
                ResourceUri(relativePath, collection: false),
                request =>
                {
                    request.Content = content;
                    if (createOnly)
                        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                },
                transfer,
                cancellationToken);

            if (response.IsSuccessStatusCode)
                return;

            if (createOnly && response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {relativePath}");
            }

            throw Failure(response);
        }

        /// <summary>A small text file, or null when it does not exist.</summary>
        public async Task<string?> GetStringAsync(
            string relativePath,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Get,
                ResourceUri(relativePath, collection: false),
                configure: null,
                transfer: false,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw Failure(response);

            return await ReadBoundedStringAsync(response.Content, cancellationToken);
        }

        /// <summary>
        /// Streams a file to a new local file. The target is created with
        /// CreateNew, so an existing local file is never replaced; a partial
        /// file this call created is removed when the download fails.
        /// </summary>
        public async Task<long> DownloadAsync(
            string relativePath,
            string localFilePath,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Get,
                ResourceUri(relativePath, collection: false),
                configure: null,
                transfer: true,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw Failure(response);

            string? directory = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var target = new FileStream(
                localFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            try
            {
                await using (target)
                {
                    await using Stream source =
                        await response.Content.ReadAsStreamAsync(cancellationToken);
                    await source.CopyToAsync(target, cancellationToken);
                    return target.Length;
                }
            }
            catch
            {
                try { File.Delete(localFilePath); } catch { }
                throw;
            }
        }

        internal Uri ResourceUri(string relativePath, bool collection)
        {
            ArgumentNullException.ThrowIfNull(relativePath);

            if (relativePath.Length == 0)
                return _serverUrl;

            string[] segments = relativePath.Split('/');

            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            {
                throw new ArgumentException(
                    "A WebDAV path must not contain empty, '.' or '..' segments.",
                    nameof(relativePath));
            }

            string escaped = string.Join('/', segments.Select(Uri.EscapeDataString));
            return new Uri(_serverUrl, collection ? escaped + "/" : escaped);
        }

        private async Task<IReadOnlyList<(string Path, WebDavEntry Entry)>?> PropfindAsync(
            Uri uri,
            int depth,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(
                Propfind,
                uri,
                request =>
                {
                    request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
                    request.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
                },
                transfer: false,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (response.StatusCode != HttpStatusCode.MultiStatus)
                throw Failure(response);

            return WebDavMultistatus.Parse(
                await ReadBoundedStringAsync(response.Content, cancellationToken));
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            Uri uri,
            Action<HttpRequestMessage>? configure,
            bool transfer,
            CancellationToken cancellationToken)
        {
            AuthenticationHeaderValue authorization = await AuthorizationAsync(cancellationToken);

            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = authorization;
            configure?.Invoke(request);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(transfer ? TransferTimeout : ControlTimeout);

            try
            {
                return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
            }
            catch (HttpRequestException exception)
            {
                throw new WebDavException(
                    null,
                    "The WebDAV server could not be reached. Check the address, the network, and that Windows trusts the server's certificate.",
                    innerException: exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout, not the user: a transient failure the retry
                // wrapper may repeat, never reported as "cancelled by you".
                throw new WebDavException(
                    null,
                    "The WebDAV server did not answer in time.",
                    innerException: exception);
            }
        }

        private async Task<AuthenticationHeaderValue> AuthorizationAsync(
            CancellationToken cancellationToken)
        {
            if (_authorization is not null)
                return _authorization;

            string? password = await _passwordSource(cancellationToken);

            if (string.IsNullOrEmpty(password))
            {
                throw new WebDavException(
                    (int)HttpStatusCode.Unauthorized,
                    "No WebDAV password is stored for this profile's server. Enter it on the Sync page and choose Store password.");
            }

            // Basic over https only: the settings refuse any other scheme.
            _authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{password}")));
            return _authorization;
        }

        private static async Task<string> ReadBoundedStringAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength > MaxResponseBytes)
                throw TooLarge();

            await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw TooLarge();

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static InvalidDataException TooLarge() =>
            new("The WebDAV server sent a response larger than a backup manifest or folder listing may be.");

        private static WebDavException Failure(HttpResponseMessage response)
        {
            int status = (int)response.StatusCode;
            RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
            TimeSpan? delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);

            string message = status switch
            {
                >= 300 and < 400 =>
                    "The WebDAV server redirected the request. Enter the final https:// address of the WebDAV folder; redirects are not followed so the password cannot be sent elsewhere.",
                401 => "The WebDAV server rejected the user name or password.",
                403 => "The WebDAV server refused access to this folder.",
                404 => "The WebDAV file or folder was not found.",
                409 => "The WebDAV server refused the request because a parent folder is missing.",
                423 => "The WebDAV file or folder is locked by another client.",
                429 => "The WebDAV server is limiting how fast requests may be sent.",
                507 => "The WebDAV server has no storage space left.",
                >= 500 => "The WebDAV server reported an internal error.",
                _ => "The WebDAV server refused the request."
            };

            return new WebDavException(
                status,
                $"{message} (HTTP {status})",
                delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        }
    }

    /// <summary>
    /// Parses an RFC 4918 207 Multi-Status body. DTDs are refused, so a
    /// hostile server cannot use entity expansion or external entities.
    /// </summary>
    internal static class WebDavMultistatus
    {
        private static readonly XNamespace Dav = "DAV:";

        public static IReadOnlyList<(string Path, WebDavEntry Entry)> Parse(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                document = XDocument.Load(reader);
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException(
                    "The WebDAV server sent a folder listing that is not valid XML.",
                    exception);
            }

            if (document.Root?.Name != Dav + "multistatus")
            {
                throw new InvalidDataException(
                    "The WebDAV server sent a folder listing that is not a multistatus document.");
            }

            var results = new List<(string, WebDavEntry)>();

            foreach (XElement response in document.Root.Elements(Dav + "response"))
            {
                string? href = response.Element(Dav + "href")?.Value.Trim();

                if (string.IsNullOrEmpty(href))
                    continue;

                bool isCollection = false;
                long? length = null;
                bool anyPropstat = false;
                bool anyOk = false;

                foreach (XElement propstat in response.Elements(Dav + "propstat"))
                {
                    anyPropstat = true;

                    if (!IsOk(propstat.Element(Dav + "status")?.Value))
                        continue;

                    anyOk = true;
                    XElement? prop = propstat.Element(Dav + "prop");
                    isCollection |= prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;

                    if (long.TryParse(
                            prop?.Element(Dav + "getcontentlength")?.Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out long parsed))
                    {
                        length = parsed;
                    }
                }

                // A response may carry its own status instead of propstats
                // (for example 404 for a member that vanished mid-listing).
                if (anyPropstat ? !anyOk : !IsOk(response.Element(Dav + "status")?.Value))
                    continue;

                string path = DecodeHrefPath(href);
                string name = path.TrimEnd('/');
                name = name[(name.LastIndexOf('/') + 1)..];
                results.Add((path, new WebDavEntry(name, isCollection, length)));
            }

            return results;
        }

        // "HTTP/1.1 200 OK": the second token is the status code.
        private static bool IsOk(string? status)
        {
            string[] parts = (status ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts[1] == "200";
        }

        private static string DecodeHrefPath(string href)
        {
            // An absolute path ("/remote.php/dav/...") is the common form;
            // some servers send the full URL instead.
            string path = !href.StartsWith('/') &&
                          Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) &&
                          (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.AbsolutePath
                : href;

            return Uri.UnescapeDataString(path);
        }
    }
}
