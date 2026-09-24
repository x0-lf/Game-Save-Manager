using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    internal interface IOneDriveInteractiveAuthorizer
    {
        /// <summary>
        /// Opens the system browser at <paramref name="authorizationUrl"/> and returns
        /// the authorization code delivered to <paramref name="redirectUri"/> with
        /// <paramref name="expectedState"/>.
        /// </summary>
        Task<string> AuthorizeAsync(
            string authorizationUrl,
            string redirectUri,
            string expectedState,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The authorization callback carried an OAuth error instead of a code.
    /// Only whether the user denied access is kept; nothing from the callback is echoed.
    /// </summary>
    internal sealed class OneDriveAuthorizationCallbackException(bool denied)
        : Exception(denied
            ? "Microsoft OneDrive sign-in was denied."
            : "Microsoft OneDrive sign-in did not return an authorization code.")
    {
        public bool Denied { get; } = denied;
    }

    internal sealed class LoopbackOneDriveInteractiveAuthorizer : IOneDriveInteractiveAuthorizer
    {
        internal static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

        // Static on purpose: nothing from the callback query is reflected into the page.
        private static readonly byte[] ResponsePage = Encoding.UTF8.GetBytes(
            "<!doctype html><html><body style='font-family:sans-serif;text-align:center;padding:50px;'>" +
            "<h2>Microsoft OneDrive sign-in finished</h2>" +
            "<p>You can close this window and return to GameSave Manager.</p></body></html>");

        public async Task<string> AuthorizeAsync(
            string authorizationUrl,
            string redirectUri,
            string expectedState,
            CancellationToken cancellationToken = default)
        {
            var redirect = new Uri(redirectUri);

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect.GetLeftPart(UriPartial.Authority) + "/");
            listener.Start();

            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallbackTimeout);

            try
            {
                while (true)
                {
                    HttpListenerContext context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                    var query = context.Request.QueryString;

                    // Anything but the redirect path carrying this flow's state (a favicon
                    // request, a stale or forged callback) is refused and the wait continues.
                    if (context.Request.Url?.AbsolutePath != redirect.AbsolutePath ||
                        query["state"] != expectedState)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        context.Response.Close();
                        continue;
                    }

                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = ResponsePage.Length;
                    await context.Response.OutputStream.WriteAsync(ResponsePage, timeout.Token);
                    context.Response.Close();

                    return query["code"] is { Length: > 0 } code
                        ? code
                        : throw new OneDriveAuthorizationCallbackException(
                            denied: query["error"] == "access_denied");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Microsoft OneDrive sign-in timed out.");
            }
        }
    }

    /// <summary>
    /// Stored token document in ISecretStore for a OneDrive profile.
    /// </summary>
    internal sealed record StoredOneDriveTokenData(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset ExpiresAtUtc,
        string TokenType,
        string? Scope)
    {
        // The generated record ToString would print both tokens.
        public override string ToString() => nameof(StoredOneDriveTokenData);
    }

    internal sealed class OneDriveOAuthService : IOneDriveOAuthService
    {
        /// <summary>Application (client) ID of the Microsoft app registration, read like GAMESAVES_GOOGLE_CLIENT_ID.</summary>
        internal const string ClientIdVariable = "GAMESAVES_ONEDRIVE_CLIENT_ID";

        private const string AuthorizationEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
        private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly IOneDriveApiClient _apiClient;
        private readonly IUtcClock _clock;
        private readonly IOneDriveInteractiveAuthorizer _authorizer;
        private readonly OneDriveOAuthClientConfigurationState _configuration;
        private readonly string? _clientId;

        /// <param name="clientId">Overrides the environment; null reads <see cref="ClientIdVariable"/>.</param>
        public OneDriveOAuthService(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            IOneDriveApiClient apiClient,
            IUtcClock clock,
            IOneDriveInteractiveAuthorizer? authorizer = null,
            string? clientId = null)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _authorizer = authorizer ?? new LoopbackOneDriveInteractiveAuthorizer();

            string? configured = (clientId ?? ReadEnvironmentClientId())?.Trim();

            if (string.IsNullOrEmpty(configured))
            {
                _configuration = OneDriveOAuthClientConfigurationState.Missing(
                    $"Microsoft OneDrive sign-in is not configured. Set {ClientIdVariable} to the application (client) ID of a Microsoft app registration.");
            }
            else if (!Guid.TryParse(configured, out Guid parsed) || parsed == Guid.Empty)
            {
                _configuration = OneDriveOAuthClientConfigurationState.Invalid(
                    $"The configured {ClientIdVariable} is not a valid application (client) ID.");
            }
            else
            {
                _configuration = OneDriveOAuthClientConfigurationState.Available();
                _clientId = parsed.ToString("D");
            }
        }

        public OneDriveOAuthClientConfigurationState GetClientConfigurationState() => _configuration;

        public Task<OneDriveAuthenticationResult> ConnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            ExecuteAuthenticationFlowAsync(remoteProfileId, cancellationToken);

        public Task<OneDriveAuthenticationResult> ReconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            ExecuteAuthenticationFlowAsync(remoteProfileId, cancellationToken);

        public async Task<OneDriveAuthenticationResult> RestoreAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (CheckProfile(remoteProfileId) is { } profileFailure)
                return profileFailure;

            (string? accessToken, OneDriveAuthenticationResult? tokenFailure) =
                await GetAccessTokenAsync(remoteProfileId, cancellationToken);

            if (tokenFailure is not null)
                return tokenFailure;

            OneDriveAccountInfo account;
            try
            {
                account = await _apiClient.GetAccountInfoAsync(accessToken!, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.AccountLookupFailed,
                    OneDriveOAuthErrorCodes.AccountLookupFailed,
                    "Microsoft OneDrive account details could not be retrieved.");
            }

            _profileRepository.UpdateLastSuccessfulConnection(remoteProfileId, _clock.UtcNow);
            return OneDriveAuthenticationResult.Success(account.EmailOrUpn, account.DisplayName);
        }

        public async Task<OneDriveDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            SyncRemoteProfile? profile = remoteProfileId == Guid.Empty
                ? null
                : _profileRepository.GetById(remoteProfileId);

            if (profile == null)
            {
                return DisconnectFailure(
                    OneDriveDisconnectionStatus.ProfileNotFound,
                    OneDriveDisconnectionErrorCodes.ProfileNotFound,
                    "The selected remote profile was not found.");
            }

            if (profile.ProviderKind != SyncProviderKind.OneDrive)
            {
                return DisconnectFailure(
                    OneDriveDisconnectionStatus.WrongProviderKind,
                    OneDriveDisconnectionErrorCodes.WrongProviderKind,
                    "The selected remote profile is not a Microsoft OneDrive profile.");
            }

            SecretOperationResult cleanup;
            try
            {
                cleanup = await _secretStore.DeleteAsync(
                    new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                cleanup = new SecretOperationResult(SecretOperationStatus.Failed);
            }

            if (!cleanup.Succeeded)
            {
                return cleanup.Status == SecretOperationStatus.Unavailable
                    ? DisconnectFailure(
                        OneDriveDisconnectionStatus.SecretStoreUnavailable,
                        OneDriveDisconnectionErrorCodes.SecretStoreUnavailable,
                        "Protected Microsoft OneDrive authentication storage is unavailable.")
                    : DisconnectFailure(
                        OneDriveDisconnectionStatus.CleanupFailed,
                        OneDriveDisconnectionErrorCodes.CleanupFailed,
                        "Locally stored Microsoft OneDrive authentication could not be removed.");
            }

            bool removed = cleanup.AffectedCount > 0;

            try
            {
                _profileRepository.Update(profile with
                {
                    AccountDisplayName = null,
                    RemoteRootDisplayName = null,
                    ProviderSettings = new OneDriveSyncRemoteSettings(
                        accountEmail: null,
                        (profile.ProviderSettings as OneDriveSyncRemoteSettings)?.RequestedScope ??
                            OneDriveAuthorizationScopes.DefaultScopes),
                    UpdatedUtc = _clock.UtcNow
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new OneDriveDisconnectionResult(
                    OneDriveDisconnectionStatus.CleanupFailed,
                    LocalAuthenticationRemoved: removed,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false,
                    OneDriveDisconnectionErrorCodes.CleanupFailed,
                    "Local authentication was removed, but saved account metadata could not be cleared.");
            }

            return new OneDriveDisconnectionResult(
                removed
                    ? OneDriveDisconnectionStatus.Disconnected
                    : OneDriveDisconnectionStatus.AlreadyDisconnected,
                LocalAuthenticationRemoved: removed,
                ProfilePreserved: true,
                AccountMetadataCleared: true,
                Message: removed
                    ? "Microsoft OneDrive was disconnected from this installation. Locally stored authentication was removed. The saved profile, backup data, and OneDrive files were not deleted."
                    : "Microsoft OneDrive was already disconnected. The saved profile, backup data, and OneDrive files were not deleted.");
        }

        public async Task<OneDriveQuotaInfo?> GetQuotaAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            string? token = await GetValidAccessTokenAsync(remoteProfileId, cancellationToken);
            if (string.IsNullOrEmpty(token))
                return null;

            try
            {
                return await _apiClient.GetQuotaAsync(token, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>A usable access token, refreshed and re-stored when near expiry; null when there is none.</summary>
        internal async Task<string?> GetValidAccessTokenAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            (await GetAccessTokenAsync(remoteProfileId, cancellationToken)).AccessToken;

        private async Task<(string? AccessToken, OneDriveAuthenticationResult? Failure)> GetAccessTokenAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken)
        {
            var key = new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData);
            SecretReadResult read = await _secretStore.ReadAsync(key, cancellationToken);

            switch (read.Status)
            {
                case SecretReadStatus.NotFound:
                    return (null, OneDriveAuthenticationResult.Failure(
                        OneDriveAuthenticationStatus.NoStoredAuthentication,
                        OneDriveOAuthErrorCodes.ReauthenticationRequired,
                        "No Microsoft OneDrive sign-in is stored for this profile."));
                case SecretReadStatus.Unavailable or SecretReadStatus.Failed:
                    return (null, StoreUnavailable());
                case SecretReadStatus.Corrupted:
                    return (null, TokenCorrupted());
            }

            StoredOneDriveTokenData? tokenData = null;
            if (read.Value is { Length: > 0 } rawBytes)
            {
                try
                {
                    tokenData = JsonSerializer.Deserialize<StoredOneDriveTokenData>(rawBytes);
                }
                catch (JsonException)
                {
                }
            }

            if (tokenData is not { AccessToken.Length: > 0 })
                return (null, TokenCorrupted());

            if (_clock.UtcNow < tokenData.ExpiresAtUtc - RefreshWindow)
                return (tokenData.AccessToken, null);

            if (string.IsNullOrEmpty(tokenData.RefreshToken))
            {
                return (null, OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.ReauthenticationRequired,
                    OneDriveOAuthErrorCodes.ReauthenticationRequired,
                    "Microsoft OneDrive sign-in has expired. Reconnect to continue."));
            }

            if (_clientId is null)
                return (null, ConfigurationFailure());

            OneDriveTokenResponse refreshed;
            try
            {
                refreshed = await _apiClient.RefreshTokenAsync(_clientId, tokenData.RefreshToken, cancellationToken);
            }
            catch (OneDriveApiException ex) when (ex.OAuthError == "invalid_grant")
            {
                return (null, OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.ReauthenticationRequired,
                    OneDriveOAuthErrorCodes.ReauthenticationRequired,
                    "Microsoft OneDrive sign-in has expired or was revoked. Reconnect to continue."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.Unavailable,
                    OneDriveOAuthErrorCodes.RefreshFailed,
                    "Microsoft OneDrive could not be reached to renew the sign-in. Try again later."));
            }

            tokenData = ToStoredTokenData(refreshed, tokenData);

            if (!(await SaveTokenDataAsync(key, tokenData, cancellationToken)).Succeeded)
                return (null, StoreUnavailable());

            return (tokenData.AccessToken, null);
        }

        private async Task<OneDriveAuthenticationResult> ExecuteAuthenticationFlowAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken)
        {
            if (CheckProfile(remoteProfileId) is { } profileFailure)
                return profileFailure;

            if (_clientId is null)
                return ConfigurationFailure();

            string verifier = RandomBase64Url(32);
            string challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomBase64Url(16);

            // Microsoft ignores the port of an http://localhost redirect URI for
            // public clients, so any free loopback port matches the registration.
            string redirectUri = $"http://localhost:{GetFreeLoopbackPort()}/";

            string authUrl =
                $"{AuthorizationEndpoint}?client_id={_clientId}&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_mode=query" +
                $"&scope={Uri.EscapeDataString(OneDriveAuthorizationScopes.DefaultScopes)}" +
                $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";

            string code;
            try
            {
                code = await _authorizer.AuthorizeAsync(authUrl, redirectUri, state, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.Cancelled,
                    OneDriveOAuthErrorCodes.Cancelled,
                    "Microsoft OneDrive sign-in was cancelled. No backup data was changed.");
            }
            catch (OneDriveAuthorizationCallbackException ex) when (ex.Denied)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.AuthorizationDenied,
                    OneDriveOAuthErrorCodes.Denied,
                    "Microsoft OneDrive access was not granted. No backup data was changed.");
            }
            catch (Win32Exception)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.BrowserLaunchFailed,
                    OneDriveOAuthErrorCodes.BrowserFailed,
                    "The system browser could not be opened for Microsoft OneDrive sign-in.");
            }
            catch (Exception)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.CallbackFailed,
                    OneDriveOAuthErrorCodes.CallbackFailed,
                    "Microsoft OneDrive sign-in did not complete. Try again.");
            }

            OneDriveTokenResponse tokenResponse;
            try
            {
                tokenResponse = await _apiClient.ExchangeCodeForTokenAsync(
                    _clientId,
                    code,
                    verifier,
                    redirectUri,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.Failed,
                    OneDriveOAuthErrorCodes.TokenExchangeFailed,
                    "Microsoft OneDrive did not complete the sign-in. Try again.");
            }

            OneDriveAccountInfo accountInfo;
            try
            {
                accountInfo = await _apiClient.GetAccountInfoAsync(
                    tokenResponse.AccessToken,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.AccountLookupFailed,
                    OneDriveOAuthErrorCodes.AccountLookupFailed,
                    "Microsoft OneDrive account details could not be retrieved after sign-in.");
            }

            SecretOperationResult stored = await SaveTokenDataAsync(
                new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData),
                ToStoredTokenData(tokenResponse, previous: null),
                cancellationToken);

            if (!stored.Succeeded)
                return StoreUnavailable();

            // Re-read rather than reuse the snapshot from before the browser flow,
            // which can take minutes: a rename saved meanwhile must survive.
            SyncRemoteProfile? current = _profileRepository.GetById(remoteProfileId);
            if (current == null)
                return ProfileNotFound();

            DateTimeOffset now = _clock.UtcNow;
            _profileRepository.Update(current with
            {
                AccountDisplayName = accountInfo.DisplayName,
                RemoteRootDisplayName = OneDriveRemoteFileSystem.DisplayRootName,
                ProviderSettings = new OneDriveSyncRemoteSettings(
                    accountInfo.EmailOrUpn,
                    OneDriveAuthorizationScopes.DefaultScopes),
                UpdatedUtc = now,
                LastSuccessfulConnectionUtc = now
            });

            return OneDriveAuthenticationResult.Success(
                accountInfo.EmailOrUpn,
                accountInfo.DisplayName);
        }

        private OneDriveAuthenticationResult? CheckProfile(Guid remoteProfileId)
        {
            SyncRemoteProfile? profile = remoteProfileId == Guid.Empty
                ? null
                : _profileRepository.GetById(remoteProfileId);

            if (profile == null)
                return ProfileNotFound();

            return profile.ProviderKind == SyncProviderKind.OneDrive
                ? null
                : OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.WrongProviderKind,
                    OneDriveOAuthErrorCodes.WrongProviderKind,
                    "The selected remote profile is not a Microsoft OneDrive profile.");
        }

        private StoredOneDriveTokenData ToStoredTokenData(
            OneDriveTokenResponse response,
            StoredOneDriveTokenData? previous) =>
            new(
                response.AccessToken,
                response.RefreshToken ?? previous?.RefreshToken,
                _clock.UtcNow.AddSeconds(response.ExpiresInSeconds),
                response.TokenType,
                response.Scope ?? previous?.Scope);

        private Task<SecretOperationResult> SaveTokenDataAsync(
            SecretKey key,
            StoredOneDriveTokenData data,
            CancellationToken cancellationToken) =>
            _secretStore.StoreAsync(key, JsonSerializer.SerializeToUtf8Bytes(data), cancellationToken);

        private OneDriveAuthenticationResult ConfigurationFailure() =>
            OneDriveAuthenticationResult.Failure(
                OneDriveAuthenticationStatus.ClientConfigurationMissing,
                _configuration.ErrorCode ?? OneDriveOAuthErrorCodes.ClientIdMissing,
                _configuration.Message ?? "Microsoft OneDrive client configuration is missing.");

        private static OneDriveAuthenticationResult ProfileNotFound() =>
            OneDriveAuthenticationResult.Failure(
                OneDriveAuthenticationStatus.ProfileNotFound,
                OneDriveOAuthErrorCodes.ProfileNotFound,
                "The selected remote profile was not found.");

        private static OneDriveAuthenticationResult StoreUnavailable() =>
            OneDriveAuthenticationResult.Failure(
                OneDriveAuthenticationStatus.SecretStoreUnavailable,
                OneDriveOAuthErrorCodes.TokenStoreUnavailable,
                "Protected Microsoft OneDrive authentication storage is unavailable.");

        private static OneDriveAuthenticationResult TokenCorrupted() =>
            OneDriveAuthenticationResult.Failure(
                OneDriveAuthenticationStatus.TokenCorrupted,
                OneDriveOAuthErrorCodes.TokenCorrupted,
                "The stored Microsoft OneDrive sign-in is unreadable. Reconnect to continue.");

        private static OneDriveDisconnectionResult DisconnectFailure(
            OneDriveDisconnectionStatus status,
            string errorCode,
            string message) =>
            new(
                status,
                LocalAuthenticationRemoved: false,
                ProfilePreserved: true,
                AccountMetadataCleared: false,
                errorCode,
                message);

        private static string? ReadEnvironmentClientId()
        {
            string? value = Environment.GetEnvironmentVariable(ClientIdVariable);
            if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
                value = Environment.GetEnvironmentVariable(ClientIdVariable, EnvironmentVariableTarget.User);

            return value;
        }

        // ponytail: the port is released before HttpListener binds it; another
        // process grabbing it in that gap fails the sign-in, which the user retries.
        private static int GetFreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        private static string RandomBase64Url(int byteCount) =>
            Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
