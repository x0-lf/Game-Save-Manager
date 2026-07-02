using System.Net;
using System.Text.Json;

namespace GameSave.External.Http
{
    public sealed class PoliteHttpClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly RateLimitOptions _options;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;
        private int _requestCount;

        public PoliteHttpClient(
            string userAgent,
            RateLimitOptions options)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
                throw new ArgumentException("A meaningful User-Agent is required.", nameof(userAgent));

            _options = options;

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            };

            _httpClient = new HttpClient(handler, disposeHandler: true);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        public async Task<Uri?> GetFinalUriWithHeadAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            string currentUrl = url;

            for (int redirectCount = 0; redirectCount < 10; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, currentUrl);

                HttpResponseMessage response = await SendOnceWithRetriesAsync(
                    request,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Dispose();
                    return null;
                }

                if (!IsRedirect(response.StatusCode))
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        response.Dispose();
                        return null;
                    }

                    EnsureSuccessOrThrow(response, body, currentUrl);

                    Uri? finalUri = response.RequestMessage?.RequestUri;
                    response.Dispose();
                    return finalUri;
                }

                Uri? location = response.Headers.Location;

                if (location is null)
                {
                    response.Dispose();
                    return null;
                }

                Uri nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(currentUrl), location);

                Console.WriteLine($"HTTP HEAD redirect: {currentUrl} -> {nextUri}");

                response.Dispose();
                currentUrl = nextUri.ToString();
            }

            throw new HttpRequestException($"Too many redirects while requesting {url}");
        }

        public async Task<Uri?> GetFinalUriAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await SendFollowingRedirectsAsync(
                url,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccessOrThrow(response, body, url);

            return response.RequestMessage?.RequestUri;
        }

        public async Task<JsonDocument> GetJsonAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await SendFollowingRedirectsAsync(
                url,
                cancellationToken);

            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            EnsureSuccessOrThrow(response, body, url);

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                string preview = CreatePreview(body);

                throw new InvalidOperationException(
                    $"Expected JSON but received non-JSON response from {url}. " +
                    $"Final URL: {response.RequestMessage?.RequestUri}. " +
                    $"Status: {(int)response.StatusCode} {response.StatusCode}. " +
                    $"Content-Type: {response.Content.Headers.ContentType}. " +
                    $"Body preview: {preview}",
                    ex);
            }
        }

        public async Task<string> GetStringAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await SendFollowingRedirectsAsync(
                url,
                cancellationToken);

            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            EnsureSuccessOrThrow(response, body, url);

            return body;
        }

        private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
            string url,
            CancellationToken cancellationToken)
        {
            string currentUrl = url;

            for (int redirectCount = 0; redirectCount < 10; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);

                HttpResponseMessage response = await SendOnceWithRetriesAsync(
                    request,
                    cancellationToken);

                if (!IsRedirect(response.StatusCode))
                    return response;

                Uri? location = response.Headers.Location;

                if (location is null)
                    return response;

                Uri nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(currentUrl), location);

                Console.WriteLine($"HTTP GET redirect: {currentUrl} -> {nextUri}");

                response.Dispose();
                currentUrl = nextUri.ToString();
            }

            throw new HttpRequestException($"Too many redirects while requesting {url}");
        }

        private async Task<HttpResponseMessage> SendOnceWithRetriesAsync(
            HttpRequestMessage originalRequest,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
            {
                await WaitForTurnAsync(cancellationToken);

                HttpRequestMessage request = CloneRequest(originalRequest);
                HttpResponseMessage response;

                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken);
                }
                catch when (attempt < _options.MaxRetries)
                {
                    await DelayForRetryAsync(null, attempt, cancellationToken);
                    continue;
                }

                if (response.IsSuccessStatusCode || IsRedirect(response.StatusCode))
                    return response;

                Console.WriteLine(
                    $"HTTP {(int)response.StatusCode} {response.StatusCode}: {originalRequest.RequestUri}");

                if (IsRetryable(response.StatusCode) && attempt < _options.MaxRetries)
                {
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                    response.Dispose();

                    await DelayForRetryAsync(retryAfter, attempt, cancellationToken);
                    continue;
                }

                return response;
            }

            throw new InvalidOperationException("Unreachable retry state.");
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);

            foreach (var header in source.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }

        private async Task WaitForTurnAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);

            try
            {
                if (_options.RequestsPerMinute <= 0)
                    throw new InvalidOperationException("RequestsPerMinute must be greater than zero.");

                TimeSpan minimumDelay = TimeSpan.FromMinutes(1.0 / _options.RequestsPerMinute);

                DateTimeOffset now = DateTimeOffset.UtcNow;
                TimeSpan elapsed = now - _lastRequestUtc;

                if (elapsed < minimumDelay)
                    await Task.Delay(minimumDelay - elapsed, cancellationToken);

                _requestCount++;

                if (_options.PauseEveryRequests > 0 &&
                    _requestCount % _options.PauseEveryRequests == 0)
                {
                    Console.WriteLine(
                        $"Polite pause after {_requestCount} requests: {_options.PauseEveryRequestsDuration.TotalSeconds:n0} seconds.");

                    await Task.Delay(_options.PauseEveryRequestsDuration, cancellationToken);
                }

                _lastRequestUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task DelayForRetryAsync(
            TimeSpan? retryAfter,
            int attempt,
            CancellationToken cancellationToken)
        {
            TimeSpan delay = retryAfter
                ?? TimeSpan.FromSeconds(_options.BaseRetryDelay.TotalSeconds * Math.Pow(2, attempt - 1));

            Console.WriteLine($"HTTP retry delay: {delay.TotalSeconds:n0} seconds. Attempt: {attempt}.");

            await Task.Delay(delay, cancellationToken);
        }

        private static void EnsureSuccessOrThrow(
            HttpResponseMessage response,
            string body,
            string originalUrl)
        {
            if (response.IsSuccessStatusCode)
                return;

            string preview = CreatePreview(body);

            throw new HttpRequestException(
                $"HTTP request failed. " +
                $"Original URL: {originalUrl}. " +
                $"Final URL: {response.RequestMessage?.RequestUri}. " +
                $"Status: {(int)response.StatusCode} {response.StatusCode}. " +
                $"Content-Type: {response.Content.Headers.ContentType}. " +
                $"Body preview: {preview}");
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.MovedPermanently ||
                   statusCode == HttpStatusCode.Found ||
                   statusCode == HttpStatusCode.SeeOther ||
                   statusCode == HttpStatusCode.TemporaryRedirect ||
                   (int)statusCode == 308;
        }

        private static bool IsRetryable(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.TooManyRequests ||
                   statusCode == HttpStatusCode.ServiceUnavailable ||
                   statusCode == HttpStatusCode.GatewayTimeout ||
                   statusCode == HttpStatusCode.BadGateway;
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
            _gate.Dispose();
        }
    }
}