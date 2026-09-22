using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    public interface IOneDriveInteractiveAuthorizer
    {
        Task<string> AuthorizeAsync(
            string authorizationUrl,
            string redirectUri,
            CancellationToken cancellationToken = default);
    }

    public sealed class LoopbackOneDriveInteractiveAuthorizer : IOneDriveInteractiveAuthorizer
    {
        public async Task<string> AuthorizeAsync(
            string authorizationUrl,
            string redirectUri,
            CancellationToken cancellationToken = default)
        {
            var uri = new Uri(redirectUri);
            string prefix = $"http://localhost:{uri.Port}/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            try
            {
                // Launch default system browser
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = authorizationUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);

                using var registration = cancellationToken.Register(() =>
                {
                    try { listener.Stop(); } catch { }
                });

                var context = await listener.GetContextAsync();
                var query = context.Request.QueryString;

                string? code = query["code"];
                string? error = query["error"];
                string? errorDesc = query["error_description"];

                string responseHtml = code != null
                    ? "<html><body style='font-family:sans-serif;text-align:center;padding:50px;'><h2>Authentication successful!</h2><p>You can now return to GameSave Manager and close this window.</p></body></html>"
                    : $"<html><body style='font-family:sans-serif;text-align:center;padding:50px;color:red;'><h2>Authentication failed</h2><p>{error}: {errorDesc}</p></body></html>";

                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentType = "text/html";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, cancellationToken);
                context.Response.OutputStream.Close();

                if (code == null)
                {
                    throw new InvalidOperationException($"OneDrive authorization failed: {error} - {errorDesc}");
                }

                return code;
            }
            finally
            {
                try { listener.Stop(); } catch { }
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
        string? Scope);

    public sealed class OneDriveOAuthService : IOneDriveOAuthService
    {
        private const string AuthorizationEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
        private const string DefaultClientId = "a3f5a2b8-7c1e-4f3d-9a8b-2e4c6d8f0a1b";

        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly IOneDriveApiClient _apiClient;
        private readonly IUtcClock _clock;
        private readonly IOneDriveInteractiveAuthorizer _authorizer;
        private readonly string _clientId;

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
            _clientId = !string.IsNullOrWhiteSpace(clientId)
                ? clientId
                : Environment.GetEnvironmentVariable("GAMESAVE_ONEDRIVE_CLIENT_ID") ?? DefaultClientId;
        }

        public OneDriveOAuthClientConfigurationState GetClientConfigurationState()
        {
            return string.IsNullOrWhiteSpace(_clientId)
                ? OneDriveOAuthClientConfigurationState.Missing()
                : OneDriveOAuthClientConfigurationState.Available();
        }

        public async Task<OneDriveAuthenticationResult> ConnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthenticationFlowAsync(remoteProfileId, cancellationToken);
        }

        public async Task<OneDriveAuthenticationResult> RestoreAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.ProfileNotFound,
                    OneDriveOAuthErrorCodes.ProfileNotFound,
                    "A non-empty remote profile ID is required.");
            }

            SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
            if (profile == null)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.ProfileNotFound,
                    OneDriveOAuthErrorCodes.ProfileNotFound,
                    $"Remote profile '{remoteProfileId}' was not found.");
            }

            var key = new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData);
            var readResult = await _secretStore.ReadAsync(key, cancellationToken);

            if (readResult.Status != SecretReadStatus.Found || readResult.Value is not { Length: > 0 } rawBytes)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.NoStoredAuthentication,
                    OneDriveOAuthErrorCodes.TokenCorrupted,
                    "No stored OneDrive credentials found for this profile.");
            }

            StoredOneDriveTokenData? tokenData;
            try
            {
                string json = Encoding.UTF8.GetString(rawBytes);
                tokenData = JsonSerializer.Deserialize<StoredOneDriveTokenData>(json);
            }
            catch
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.TokenCorrupted,
                    OneDriveOAuthErrorCodes.TokenCorrupted,
                    "The stored OneDrive credentials are corrupted.");
            }

            if (tokenData == null)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.TokenCorrupted,
                    OneDriveOAuthErrorCodes.TokenCorrupted,
                    "The stored OneDrive credentials could not be deserialized.");
            }

            // Check if token needs refresh (if expired or expiring within 5 minutes)
            string validAccessToken = tokenData.AccessToken;
            if (_clock.UtcNow >= tokenData.ExpiresAtUtc.AddMinutes(-5))
            {
                if (string.IsNullOrEmpty(tokenData.RefreshToken))
                {
                    return OneDriveAuthenticationResult.Failure(
                        OneDriveAuthenticationStatus.ReauthenticationRequired,
                        OneDriveOAuthErrorCodes.ReauthenticationRequired,
                        "OneDrive token expired and no refresh token is available.");
                }

                try
                {
                    var refreshed = await _apiClient.RefreshTokenAsync(tokenData.RefreshToken, cancellationToken);
                    tokenData = new StoredOneDriveTokenData(
                        refreshed.AccessToken,
                        refreshed.RefreshToken ?? tokenData.RefreshToken,
                        refreshed.ExpiresAtUtc,
                        refreshed.TokenType,
                        refreshed.Scope ?? tokenData.Scope);

                    await SaveTokenDataAsync(key, tokenData, cancellationToken);
                    validAccessToken = tokenData.AccessToken;
                }
                catch (Exception ex)
                {
                    return OneDriveAuthenticationResult.Failure(
                        OneDriveAuthenticationStatus.ReauthenticationRequired,
                        OneDriveOAuthErrorCodes.RefreshFailed,
                        $"Failed to refresh OneDrive token: {ex.Message}");
                }
            }

            // Fetch account info
            try
            {
                var account = await _apiClient.GetAccountInfoAsync(validAccessToken, cancellationToken);
                _profileRepository.UpdateLastSuccessfulConnection(remoteProfileId, _clock.UtcNow);

                return OneDriveAuthenticationResult.Success(account.EmailOrUpn, account.DisplayName);
            }
            catch (Exception ex)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.AccountLookupFailed,
                    OneDriveOAuthErrorCodes.AccountLookupFailed,
                    $"Failed to retrieve account details: {ex.Message}");
            }
        }

        public async Task<OneDriveAuthenticationResult> ReconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthenticationFlowAsync(remoteProfileId, cancellationToken);
        }

        public async Task<OneDriveDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
            {
                return new OneDriveDisconnectionResult(
                    OneDriveDisconnectionStatus.ProfileNotFound,
                    LocalAuthenticationRemoved: false,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false,
                    ErrorCode: OneDriveDisconnectionErrorCodes.ProfileNotFound,
                    Message: "A non-empty remote profile ID is required.");
            }

            SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
            if (profile == null)
            {
                return new OneDriveDisconnectionResult(
                    OneDriveDisconnectionStatus.ProfileNotFound,
                    LocalAuthenticationRemoved: false,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false,
                    ErrorCode: OneDriveDisconnectionErrorCodes.ProfileNotFound,
                    Message: $"Remote profile '{remoteProfileId}' was not found.");
            }

            var key = new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData);
            await _secretStore.DeleteAsync(key, cancellationToken);

            // Update profile with cleared account details
            var updatedProfile = profile with
            {
                AccountDisplayName = null,
                RemoteRootDisplayName = null,
                UpdatedUtc = _clock.UtcNow
            };
            _profileRepository.Update(updatedProfile);

            return new OneDriveDisconnectionResult(
                OneDriveDisconnectionStatus.Disconnected,
                LocalAuthenticationRemoved: true,
                ProfilePreserved: true,
                AccountMetadataCleared: true);
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
            catch
            {
                return null;
            }
        }

        internal async Task<string?> GetValidAccessTokenAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            var key = new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData);
            var readResult = await _secretStore.ReadAsync(key, cancellationToken);

            if (readResult.Status != SecretReadStatus.Found || readResult.Value is not { Length: > 0 } rawBytes)
                return null;

            StoredOneDriveTokenData? tokenData;
            try
            {
                string json = Encoding.UTF8.GetString(rawBytes);
                tokenData = JsonSerializer.Deserialize<StoredOneDriveTokenData>(json);
            }
            catch
            {
                return null;
            }

            if (tokenData == null)
                return null;

            if (_clock.UtcNow >= tokenData.ExpiresAtUtc.AddMinutes(-5))
            {
                if (string.IsNullOrEmpty(tokenData.RefreshToken))
                    return null;

                try
                {
                    var refreshed = await _apiClient.RefreshTokenAsync(tokenData.RefreshToken, cancellationToken);
                    tokenData = new StoredOneDriveTokenData(
                        refreshed.AccessToken,
                        refreshed.RefreshToken ?? tokenData.RefreshToken,
                        refreshed.ExpiresAtUtc,
                        refreshed.TokenType,
                        refreshed.Scope ?? tokenData.Scope);

                    await SaveTokenDataAsync(key, tokenData, cancellationToken);
                }
                catch
                {
                    return null;
                }
            }

            return tokenData.AccessToken;
        }

        private async Task<OneDriveAuthenticationResult> ExecuteAuthenticationFlowAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken)
        {
            if (remoteProfileId == Guid.Empty)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.ProfileNotFound,
                    OneDriveOAuthErrorCodes.ProfileNotFound,
                    "A non-empty remote profile ID is required.");
            }

            SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
            if (profile == null)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.ProfileNotFound,
                    OneDriveOAuthErrorCodes.ProfileNotFound,
                    $"Remote profile '{remoteProfileId}' was not found.");
            }

            // 1. Generate PKCE verifier and challenge
            string verifier = GenerateCodeVerifier();
            string challenge = GenerateCodeChallenge(verifier);

            // 2. Prepare redirect URI on random local port
            int port = 52143;
            string redirectUri = $"http://localhost:{port}/";

            string scopesEncoded = Uri.EscapeDataString(OneDriveAuthorizationScopes.DefaultScopes);
            string authUrl = $"{AuthorizationEndpoint}?client_id={_clientId}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_mode=query&scope={scopesEncoded}&code_challenge={challenge}&code_challenge_method=S256";

            // 3. Authorize via interactive loopback
            string code;
            try
            {
                code = await _authorizer.AuthorizeAsync(authUrl, redirectUri, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.Cancelled,
                    OneDriveOAuthErrorCodes.Cancelled,
                    "OneDrive authorization was cancelled by the user.");
            }
            catch (Exception ex)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.BrowserLaunchFailed,
                    OneDriveOAuthErrorCodes.BrowserFailed,
                    $"Failed to authorize with OneDrive: {ex.Message}");
            }

            // 4. Exchange code for token
            OneDriveTokenResponse tokenResponse;
            try
            {
                tokenResponse = await _apiClient.ExchangeCodeForTokenAsync(
                    code,
                    verifier,
                    redirectUri,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.Failed,
                    OneDriveOAuthErrorCodes.TokenExchangeFailed,
                    $"Failed to exchange authorization code: {ex.Message}");
            }

            // 5. Get Account info
            OneDriveAccountInfo accountInfo;
            try
            {
                accountInfo = await _apiClient.GetAccountInfoAsync(
                    tokenResponse.AccessToken,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return OneDriveAuthenticationResult.Failure(
                    OneDriveAuthenticationStatus.AccountLookupFailed,
                    OneDriveOAuthErrorCodes.AccountLookupFailed,
                    $"Failed to retrieve account details after authentication: {ex.Message}");
            }

            // 6. Save token to secret store
            var key = new SecretKey(remoteProfileId, SecretNames.OneDriveTokenData);
            var storedData = new StoredOneDriveTokenData(
                tokenResponse.AccessToken,
                tokenResponse.RefreshToken,
                tokenResponse.ExpiresAtUtc,
                tokenResponse.TokenType,
                tokenResponse.Scope);

            await SaveTokenDataAsync(key, storedData, cancellationToken);

            // 7. Update profile
            var updatedProfile = profile with
            {
                AccountDisplayName = accountInfo.DisplayName,
                RemoteRootDisplayName = $"OneDrive: AppRoot ({accountInfo.EmailOrUpn})",
                UpdatedUtc = _clock.UtcNow,
                LastSuccessfulConnectionUtc = _clock.UtcNow
            };
            _profileRepository.Update(updatedProfile);

            return OneDriveAuthenticationResult.Success(
                accountInfo.EmailOrUpn,
                accountInfo.DisplayName);
        }

        private async Task SaveTokenDataAsync(
            SecretKey key,
            StoredOneDriveTokenData data,
            CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(data);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _secretStore.StoreAsync(key, bytes, cancellationToken);
        }

        private static string GenerateCodeVerifier()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string GenerateCodeChallenge(string verifier)
        {
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
