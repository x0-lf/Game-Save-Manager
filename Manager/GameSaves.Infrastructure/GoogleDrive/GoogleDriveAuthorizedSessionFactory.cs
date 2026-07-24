using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveAuthorizedSessionFailure
    {
        ClientConfigurationMissing,
        NoStoredAuthentication,
        SecretStoreUnavailable,
        TokenCorrupted,
        ReauthenticationRequired,
        AuthorizationRevoked,
        RevokedTokenCleanupFailed,
        Unavailable,
        Failed
    }

    internal sealed class GoogleDriveAuthorizedSessionException : Exception
    {
        public GoogleDriveAuthorizedSessionException(
            GoogleDriveAuthorizedSessionFailure failure)
            : base("A validated Google Drive session could not be restored.") =>
            Failure = failure;

        public GoogleDriveAuthorizedSessionFailure Failure { get; }
    }

    internal sealed record GoogleDriveAuthorizedSession(
        GoogleAuthorizedCredential Credential,
        GoogleDriveAccountInfo Account);

    internal interface IGoogleDriveAuthorizedSessionFactory
    {
        Task<GoogleDriveAuthorizedSession> RestoreAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Restores and validates the existing protected Google credential without
    /// starting browser authorization. Consumers own the returned credential.
    /// </summary>
    internal sealed class GoogleDriveAuthorizedSessionFactory
        : IGoogleDriveAuthorizedSessionFactory
    {
        private static readonly IReadOnlyList<string> Scopes =
            new[] { GoogleDriveAuthorizationScopes.DriveFile };

        private readonly ISecretStore _secretStore;
        private readonly IGoogleOAuthClientConfigurationProvider _configurationProvider;
        private readonly IGoogleSecretDataStoreFactory _dataStoreFactory;
        private readonly IGoogleInstalledAppAuthorizer _authorizer;
        private readonly IGoogleDriveAccountReader _accountReader;

        public GoogleDriveAuthorizedSessionFactory(
            ISecretStore secretStore,
            IGoogleOAuthClientConfigurationProvider configurationProvider,
            IGoogleSecretDataStoreFactory dataStoreFactory,
            IGoogleInstalledAppAuthorizer authorizer,
            IGoogleDriveAccountReader accountReader)
        {
            _secretStore = secretStore;
            _configurationProvider = configurationProvider;
            _dataStoreFactory = dataStoreFactory;
            _authorizer = authorizer;
            _accountReader = accountReader;
        }

        public async Task<GoogleDriveAuthorizedSession> RestoreAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GoogleOAuthClientConfigurationReadResult configuration =
                _configurationProvider.Read();

            if (configuration.Configuration is null)
            {
                throw new GoogleDriveAuthorizedSessionException(
                    GoogleDriveAuthorizedSessionFailure.ClientConfigurationMissing);
            }

            try
            {
                GoogleSecretDataStore dataStore = _dataStoreFactory.Create(profile.Id);
                GoogleAuthorizedCredential? credential =
                    await _authorizer.RestoreAsync(
                        configuration.Configuration,
                        profile.Id,
                        dataStore,
                        Scopes,
                        cancellationToken);

                if (credential is null)
                {
                    throw new GoogleDriveAuthorizedSessionException(
                        GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication);
                }

                try
                {
                    GoogleDriveAccountInfo account = await _accountReader.ReadAsync(
                        credential,
                        cancellationToken);
                    return new GoogleDriveAuthorizedSession(credential, account);
                }
                catch
                {
                    credential.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveAuthorizedSessionException)
            {
                throw;
            }
            catch (GoogleSecretDataStoreException ex)
            {
                throw new GoogleDriveAuthorizedSessionException(ex.Failure switch
                {
                    GoogleSecretDataStoreFailure.Unavailable =>
                        GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable,
                    GoogleSecretDataStoreFailure.Corrupted =>
                        GoogleDriveAuthorizedSessionFailure.TokenCorrupted,
                    _ => GoogleDriveAuthorizedSessionFailure.Failed
                });
            }
            catch (GoogleAuthorizationException ex) when (
                ex.Failure == GoogleAuthorizationFailure.AuthorizationRevoked)
            {
                throw await CreateRevokedFailureAsync(
                    profile.Id,
                    cancellationToken);
            }
            catch (GoogleAuthorizationException ex) when (
                ex.Failure == GoogleAuthorizationFailure.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (GoogleAuthorizationException)
            {
                throw new GoogleDriveAuthorizedSessionException(
                    GoogleDriveAuthorizedSessionFailure.ReauthenticationRequired);
            }
            catch (GoogleDriveAccountReadException ex) when (
                ex.Failure == GoogleDriveAccountReadFailure.AuthorizationRevoked)
            {
                throw await CreateRevokedFailureAsync(
                    profile.Id,
                    cancellationToken);
            }
            catch (GoogleDriveAccountReadException ex) when (
                ex.Failure == GoogleDriveAccountReadFailure.Unavailable)
            {
                throw new GoogleDriveAuthorizedSessionException(
                    GoogleDriveAuthorizedSessionFailure.Unavailable);
            }
            catch
            {
                throw new GoogleDriveAuthorizedSessionException(
                    GoogleDriveAuthorizedSessionFailure.Failed);
            }
        }

        private async Task<GoogleDriveAuthorizedSessionException>
            CreateRevokedFailureAsync(
                Guid profileId,
                CancellationToken cancellationToken)
        {
            try
            {
                SecretOperationResult cleanup = await _secretStore.DeleteAsync(
                    new SecretKey(profileId, SecretNames.OAuthTokenData),
                    cancellationToken);

                return new GoogleDriveAuthorizedSessionException(
                    cleanup.Succeeded
                        ? GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked
                        : GoogleDriveAuthorizedSessionFailure.RevokedTokenCleanupFailed);
            }
            catch
            {
                return new GoogleDriveAuthorizedSessionException(
                    GoogleDriveAuthorizedSessionFailure.RevokedTokenCleanupFailed);
            }
        }
    }
}
