using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Http;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Observes HTTP responses from Google Drive API requests and extracts server-supplied
    /// Retry-After timing guidance without enabling the client library's internal backoff policy.
    /// </summary>
    internal sealed class GoogleDriveRetryAfterHandler : IHttpUnsuccessfulResponseHandler
    {
        private readonly IUtcClock _clock;
        private readonly Action<TimeSpan?>? _onDelayObserved;

        public GoogleDriveRetryAfterHandler(
            IUtcClock? clock = null,
            Action<TimeSpan?>? onDelayObserved = null)
        {
            _clock = clock ?? new SystemUtcClock();
            _onDelayObserved = onDelayObserved;
        }

        public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (args?.Response is { } response)
            {
                TimeSpan? delay = HttpRetryAfterParser.ParseOrNull(response, _clock);
                if (delay.HasValue)
                {
                    GoogleDriveRetryAfterAmbientScope.Record(delay.Value);
                    _onDelayObserved?.Invoke(delay.Value);
                }
            }

            // Always return false: retry is owned solely by RetryingRemoteFileSystem,
            // never by the client library underneath it.
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Configures the Google API HTTP client pipeline to attach the Retry-After response observer
    /// alongside the authorized user credential.
    /// </summary>
    internal sealed class GoogleDriveObservedHttpClientInitializer : IConfigurableHttpClientInitializer
    {
        private readonly IConfigurableHttpClientInitializer _credentialInitializer;
        private readonly IHttpUnsuccessfulResponseHandler _retryAfterHandler;

        public GoogleDriveObservedHttpClientInitializer(
            IConfigurableHttpClientInitializer credentialInitializer,
            IHttpUnsuccessfulResponseHandler retryAfterHandler)
        {
            _credentialInitializer = credentialInitializer ??
                throw new ArgumentNullException(nameof(credentialInitializer));
            _retryAfterHandler = retryAfterHandler ??
                throw new ArgumentNullException(nameof(retryAfterHandler));
        }

        public void Initialize(ConfigurableHttpClient httpClient)
        {
            _credentialInitializer.Initialize(httpClient);
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(_retryAfterHandler);
        }
    }

    /// <summary>
    /// Ambient, execution-context-local carrier for server-supplied Retry-After instructions
    /// captured during HTTP request execution.
    /// </summary>
    internal static class GoogleDriveRetryAfterAmbientScope
    {
        private static readonly AsyncLocal<TimeSpan?> CurrentDelay = new();

        public static void Record(TimeSpan delay)
        {
            CurrentDelay.Value = delay;
        }

        public static TimeSpan? Consume()
        {
            TimeSpan? delay = CurrentDelay.Value;
            CurrentDelay.Value = null;
            return delay;
        }

        public static TimeSpan? Peek() => CurrentDelay.Value;
    }
}
