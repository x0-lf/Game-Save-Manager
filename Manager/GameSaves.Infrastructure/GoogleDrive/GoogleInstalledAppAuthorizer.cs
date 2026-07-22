using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using System.ComponentModel;
using System.Net;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleAuthorizationFailure
    {
        Cancelled,
        Denied,
        PolicyDenied,
        BrowserFailed,
        CallbackFailed,
        InvalidClient,
        RedirectMismatch,
        NetworkFailed,
        TokenExchangeFailed,
        RefreshFailed,
        Failed
    }

    internal sealed class GoogleAuthorizationException : Exception
    {
        public GoogleAuthorizationException(GoogleAuthorizationFailure failure)
            : base("The Google authorization operation did not complete.") =>
            Failure = failure;

        public GoogleAuthorizationFailure Failure { get; }
    }

    internal sealed class GoogleAuthorizedCredential : IDisposable
    {
        public GoogleAuthorizedCredential(UserCredential credential)
        {
            Credential = credential;
        }

        public UserCredential Credential { get; }

        public void Dispose()
        {
            if (Credential.Flow is IDisposable disposable)
                disposable.Dispose();
        }
    }

    internal interface IGoogleInstalledAppAuthorizer
    {
        Task<GoogleAuthorizedCredential> ConnectAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken);

        Task<GoogleAuthorizedCredential?> RestoreAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken);
    }

    internal sealed class GoogleInstalledAppAuthorizer : IGoogleInstalledAppAuthorizer
    {
        private const string ClosePage =
            "<html><body><h2>Google Drive sign-in completed.</h2><p>You can close this window and return to Game Save Manager.</p></body></html>";

        public async Task<GoogleAuthorizedCredential> ConnectAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken)
        {
            ValidateScopes(scopes);
            string userKey = profileId.ToString("D");

            try
            {
                var initializer = CreateInitializer(configuration);
                var receiver = new LocalServerCodeReceiver(
                    ClosePage,
                    LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp);
                var interactiveStore = new InteractiveAuthorizationDataStore(dataStore);
                UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    initializer,
                    scopes,
                    userKey,
                    usePkce: true,
                    cancellationToken,
                    interactiveStore,
                    receiver);

                return new GoogleAuthorizedCredential(credential);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.Cancelled);
            }
            catch (OperationCanceledException)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.NetworkFailed);
            }
            catch (TokenResponseException ex) when (
                string.Equals(ex.Error?.Error, "access_denied", StringComparison.OrdinalIgnoreCase))
            {
                bool policy = ex.Error?.ErrorDescription?.Contains(
                    "policy",
                    StringComparison.OrdinalIgnoreCase) == true;
                throw new GoogleAuthorizationException(
                    policy ? GoogleAuthorizationFailure.PolicyDenied : GoogleAuthorizationFailure.Denied);
            }
            catch (TokenResponseException ex) when (
                string.Equals(ex.Error?.Error, "invalid_client", StringComparison.OrdinalIgnoreCase))
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.InvalidClient);
            }
            catch (TokenResponseException ex) when (
                string.Equals(ex.Error?.Error, "redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase) ||
                ex.Error?.ErrorDescription?.Contains(
                    "redirect_uri_mismatch",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.RedirectMismatch);
            }
            catch (TokenResponseException)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.TokenExchangeFailed);
            }
            catch (HttpRequestException)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.NetworkFailed);
            }
            catch (HttpListenerException)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.CallbackFailed);
            }
            catch (Win32Exception)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.BrowserFailed);
            }
            catch (NotSupportedException)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.BrowserFailed);
            }
            catch (IOException)
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.CallbackFailed);
            }
            catch (GoogleSecretDataStoreException)
            {
                throw;
            }
            catch
            {
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.Failed);
            }
        }

        public async Task<GoogleAuthorizedCredential?> RestoreAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken)
        {
            ValidateScopes(scopes);
            string userKey = profileId.ToString("D");
            var preservingStore = new PreserveOnRefreshFailureDataStore(dataStore);
            var flow = new GoogleAuthorizationCodeFlow(
                CreateInitializer(configuration, preservingStore));

            try
            {
                TokenResponse? token = await dataStore.GetAsync<TokenResponse>(userKey);

                if (token is null)
                {
                    flow.Dispose();
                    return null;
                }

                var credential = new UserCredential(flow, userKey, token);
                string accessToken = await credential.GetAccessTokenForRequestAsync(
                    null,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(accessToken))
                    throw new GoogleAuthorizationException(GoogleAuthorizationFailure.RefreshFailed);

                return new GoogleAuthorizedCredential(credential);
            }
            catch (OperationCanceledException)
            {
                flow.Dispose();
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.Cancelled);
            }
            catch (GoogleSecretDataStoreException)
            {
                flow.Dispose();
                throw;
            }
            catch (GoogleAuthorizationException)
            {
                flow.Dispose();
                throw;
            }
            catch
            {
                flow.Dispose();
                throw new GoogleAuthorizationException(GoogleAuthorizationFailure.RefreshFailed);
            }
        }

        private static GoogleAuthorizationCodeFlow.Initializer CreateInitializer(
            GoogleOAuthClientConfiguration configuration,
            IDataStore? dataStore = null) =>
            new()
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = configuration.ClientId,
                    ClientSecret = configuration.ClientSecret
                },
                DataStore = dataStore
            };

        private static void ValidateScopes(IReadOnlyList<string> scopes)
        {
            if (scopes.Count != 1 ||
                !string.Equals(
                    scopes[0],
                    GameSaves.Core.Sync.GoogleDriveAuthorizationScopes.DriveFile,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Google Drive OAuth scope configuration is invalid.");
            }
        }

        /// <summary>Forces Connect to open consent while preserving an old token if cancelled.</summary>
        private sealed class InteractiveAuthorizationDataStore : IDataStore
        {
            private readonly IDataStore _inner;

            public InteractiveAuthorizationDataStore(IDataStore inner) => _inner = inner;
            public Task ClearAsync() => Task.CompletedTask;
            public Task DeleteAsync<T>(string key) => Task.CompletedTask;
            public Task<T?> GetAsync<T>(string key) => Task.FromResult<T?>(default);
            public Task StoreAsync<T>(string key, T value) => _inner.StoreAsync(key, value);
        }

        /// <summary>Google's refresh flow may delete rejected tokens; J keeps them for explicit K cleanup.</summary>
        private sealed class PreserveOnRefreshFailureDataStore : IDataStore
        {
            private readonly IDataStore _inner;

            public PreserveOnRefreshFailureDataStore(IDataStore inner) => _inner = inner;
            public Task ClearAsync() => Task.CompletedTask;
            public Task DeleteAsync<T>(string key) => Task.CompletedTask;
            public async Task<T?> GetAsync<T>(string key) =>
                await _inner.GetAsync<T>(key);
            public Task StoreAsync<T>(string key, T value) => _inner.StoreAsync(key, value);
        }
    }
}
