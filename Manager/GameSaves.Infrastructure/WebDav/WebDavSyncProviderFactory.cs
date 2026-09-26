using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.WebDav
{
    /// <summary>Creates a sync provider for one saved WebDAV profile.</summary>
    internal interface IWebDavSyncProviderFactory
    {
        ISyncProvider Create(Guid remoteProfileId);
    }

    /// <summary>
    /// Profile-scoped construction of the WebDAV provider. Refuses an unusable
    /// profile before any provider exists and makes no request; the password
    /// is read from the protected secret store on the first request.
    /// </summary>
    internal sealed class WebDavSyncProviderFactory : IWebDavSyncProviderFactory
    {
        // One client for the process, for connection pooling. Redirects are
        // not followed, so the password can never be re-sent to another host
        // or downgraded to http; the client sets its own per-request timeouts.
        private static readonly Lazy<HttpClient> SharedHttpClient = new(() =>
            new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            });

        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;
        private readonly IDelayProvider _delay;
        private readonly IRetryBackoffNotifier? _backoffNotifier;
        private readonly HttpClient? _httpClient;

        public WebDavSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            IDelayProvider delay,
            IRetryBackoffNotifier? backoffNotifier = null)
            : this(profileRepository, secretStore, backupHistoryService, historyRepository, delay, backoffNotifier, httpClient: null)
        {
        }

        /// <summary>Tests pass an HttpClient over a stub handler.</summary>
        internal WebDavSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            IDelayProvider delay,
            IRetryBackoffNotifier? backoffNotifier,
            HttpClient? httpClient)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
            _backupHistoryService = backupHistoryService ?? throw new ArgumentNullException(nameof(backupHistoryService));
            _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            _backoffNotifier = backoffNotifier;
            _httpClient = httpClient;
        }

        public ISyncProvider Create(Guid remoteProfileId)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            SyncRemoteProfile profile = _profileRepository.GetById(remoteProfileId) ??
                throw new InvalidOperationException("The WebDAV remote profile was not found.");

            if (profile.ProviderKind != SyncProviderKind.WebDav)
                throw new InvalidOperationException("The remote profile is not a WebDAV profile.");

            if (profile.ProviderSettings is not WebDavSyncRemoteSettings settings)
            {
                throw new InvalidOperationException(
                    profile.SettingsError ?? "The saved WebDAV settings are unusable. Save the profile again.");
            }

            var client = new WebDavClient(
                _httpClient ?? SharedHttpClient.Value,
                settings.ServerUrl,
                settings.Username,
                cancellationToken => ReadPasswordAsync(remoteProfileId, settings.ServerUrl, cancellationToken));

            // The root is the URL and folder as configured: the password is
            // never part of it, so plans and history cannot carry it.
            return new EngineSyncProvider(
                "WebDAV",
                settings.DisplayRoot,
                WithRetries(
                    new WebDavRemoteFileSystem(client, settings.RemoteFolder, settings.DisplayRoot),
                    _delay,
                    _backoffNotifier),
                _backupHistoryService,
                _historyRepository);
        }

        /// <summary>
        /// Retries throttling (429), server errors (5xx), and requests that got
        /// no response (network error or timeout), honouring Retry-After.
        /// Retrying a create-only PUT is safe: If-None-Match: * turns a retry
        /// after a lost success into a refusal, never an overwrite.
        /// </summary>
        internal static IRemoteFileSystem WithRetries(
            IRemoteFileSystem fileSystem,
            IDelayProvider delay,
            IRetryBackoffNotifier? backoffNotifier) =>
            new RetryingRemoteFileSystem(
                fileSystem,
                delay,
                exception => exception is WebDavException { StatusCode: null or 429 or >= 500 },
                backoffNotifier: backoffNotifier,
                isRateLimited: exception => exception is WebDavException { StatusCode: 429 });

        private async Task<string?> ReadPasswordAsync(
            Guid remoteProfileId,
            string serverUrl,
            CancellationToken cancellationToken)
        {
            SecretReadResult result = await _secretStore.ReadAsync(
                new SecretKey(remoteProfileId, SecretNames.WebDavPassword),
                cancellationToken);

            return result.Status switch
            {
                SecretReadStatus.Found => WebDavStoredCredential.ReadPassword(result.Value!, serverUrl),
                SecretReadStatus.NotFound => null,
                _ => throw new InvalidOperationException(
                    "The stored WebDAV password could not be read. Store it again on the Sync page.")
            };
        }
    }
}
