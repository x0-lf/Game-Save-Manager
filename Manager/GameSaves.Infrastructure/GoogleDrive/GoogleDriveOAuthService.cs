using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using System.Collections.Concurrent;

namespace GameSaves.Infrastructure.GoogleDrive
{
    public sealed class GoogleDriveOAuthService : IGoogleDriveOAuthService
    {
        internal static IReadOnlyList<string> RequestedScopes { get; } =
            new[] { GoogleDriveAuthorizationScopes.DriveFile };

        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly IGoogleOAuthClientConfigurationProvider _configurationProvider;
        private readonly IGoogleSecretDataStoreFactory _dataStoreFactory;
        private readonly IGoogleInstalledAppAuthorizer _authorizer;
        private readonly IGoogleDriveAccountReader _accountReader;
        private readonly IUtcClock _clock;
        private readonly ConcurrentDictionary<Guid, LifecycleOperation> _operations = new();

        internal GoogleDriveOAuthService(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            IGoogleOAuthClientConfigurationProvider configurationProvider,
            IGoogleSecretDataStoreFactory dataStoreFactory,
            IGoogleInstalledAppAuthorizer authorizer,
            IGoogleDriveAccountReader accountReader,
            IUtcClock clock)
        {
            _profileRepository = profileRepository;
            _secretStore = secretStore;
            _configurationProvider = configurationProvider;
            _dataStoreFactory = dataStoreFactory;
            _authorizer = authorizer;
            _accountReader = accountReader;
            _clock = clock;
        }

        public GoogleDriveOAuthClientConfigurationState GetClientConfigurationState() =>
            _configurationProvider.Read().State;

        public Task<GoogleDriveAuthenticationResult> ConnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            AuthenticateAsync(
                remoteProfileId,
                AuthenticationOperation.Connect,
                cancellationToken);

        public Task<GoogleDriveAuthenticationResult> RestoreAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            AuthenticateAsync(
                remoteProfileId,
                AuthenticationOperation.Restore,
                cancellationToken);

        public async Task<GoogleDriveAuthenticationResult> ReconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            await CancelActiveOperationAsync(remoteProfileId, cancellationToken);
            return await AuthenticateAsync(
                remoteProfileId,
                AuthenticationOperation.Reconnect,
                cancellationToken);
        }

        public async Task<GoogleDriveDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                return DisconnectProfileFailure();

            await CancelActiveOperationAsync(remoteProfileId, cancellationToken);

            var operation = new LifecycleOperation();

            if (!_operations.TryAdd(remoteProfileId, operation))
            {
                operation.Dispose();
                return new GoogleDriveDisconnectionResult(
                    GoogleDriveDisconnectionStatus.Failed,
                    LocalAuthenticationRemoved: false,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false,
                    GoogleDriveDisconnectionErrorCodes.Failed,
                    "Another Google Drive account operation is still finishing. Try disconnecting again.");
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operation.Cancellation.Token);

            try
            {
                return await DisconnectCoreAsync(
                    remoteProfileId,
                    linkedCancellation.Token);
            }
            finally
            {
                CompleteOperation(remoteProfileId, operation);
            }
        }

        private async Task<GoogleDriveAuthenticationResult> AuthenticateAsync(
            Guid profileId,
            AuthenticationOperation operationKind,
            CancellationToken cancellationToken)
        {
            if (profileId == Guid.Empty)
                return ProfileFailure(GoogleDriveAuthenticationStatus.ProfileNotFound);

            var operation = new LifecycleOperation();

            if (!_operations.TryAdd(profileId, operation))
            {
                operation.Dispose();
                return Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    GoogleDriveOAuthErrorCodes.OperationInProgress,
                    "A Google Drive account operation is already running for this profile.");
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operation.Cancellation.Token);
            CancellationToken operationToken = linkedCancellation.Token;
            SyncRemoteProfile? profile = null;

            try
            {
                operationToken.ThrowIfCancellationRequested();
                profile = SafeGetProfile(profileId);

                if (profile is null)
                    return ProfileFailure(GoogleDriveAuthenticationStatus.ProfileNotFound);

                if (profile.ProviderKind != SyncProviderKind.GoogleDrive)
                {
                    return Failure(
                        GoogleDriveAuthenticationStatus.WrongProviderKind,
                        GoogleDriveOAuthErrorCodes.WrongProviderKind,
                        "The selected remote profile is not a Google Drive profile.");
                }

                if (profile.ProviderSettings is not GoogleDriveSyncRemoteSettings settings ||
                    settings.SchemaVersion != GoogleDriveSyncRemoteSettings.CurrentSchemaVersion ||
                    !string.Equals(
                        settings.RequestedScope,
                        GoogleDriveAuthorizationScopes.DriveFile,
                        StringComparison.Ordinal))
                {
                    return Failure(
                        GoogleDriveAuthenticationStatus.SettingsInvalid,
                        GoogleDriveOAuthErrorCodes.SettingsInvalid,
                        "The saved Google Drive profile settings are invalid.");
                }

                GoogleOAuthClientConfigurationReadResult configuration =
                    _configurationProvider.Read();

                if (configuration.Configuration is null)
                {
                    return Failure(
                        GoogleDriveAuthenticationStatus.ClientConfigurationMissing,
                        configuration.State.ErrorCode ?? GoogleDriveOAuthErrorCodes.ClientIdMissing,
                        configuration.State.Message ??
                        "Google Drive OAuth client configuration is missing.");
                }

                GoogleSecretDataStore dataStore = _dataStoreFactory.Create(profileId);
                bool interactive = operationKind is
                    AuthenticationOperation.Connect or AuthenticationOperation.Reconnect;
                GoogleAuthorizedCredential? credential = interactive
                    ? await _authorizer.ConnectAsync(
                        configuration.Configuration,
                        profileId,
                        dataStore,
                        RequestedScopes,
                        operationToken)
                    : await _authorizer.RestoreAsync(
                        configuration.Configuration,
                        profileId,
                        dataStore,
                        RequestedScopes,
                        operationToken);

                operationToken.ThrowIfCancellationRequested();

                if (credential is null)
                {
                    return Failure(
                        GoogleDriveAuthenticationStatus.NoStoredAuthentication,
                        null,
                        "No stored Google Drive authentication is available.");
                }

                using (credential)
                {
                    GoogleDriveAccountInfo account;

                    try
                    {
                        account = await _accountReader.ReadAsync(
                            credential,
                            operationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return Cancelled();
                    }
                    catch (GoogleDriveAccountReadException ex) when (
                        ex.Failure == GoogleDriveAccountReadFailure.AuthorizationRevoked)
                    {
                        return await HandleRevokedAuthorizationAsync(
                            profile,
                            operationToken);
                    }
                    catch (GoogleDriveAccountReadException ex) when (
                        ex.Failure == GoogleDriveAccountReadFailure.Unavailable)
                    {
                        return Failure(
                            GoogleDriveAuthenticationStatus.AccountLookupFailed,
                            GoogleDriveOAuthErrorCodes.DriveUnavailable,
                            "Google Drive is temporarily unavailable. The protected authentication was kept; try again later.");
                    }
                    catch
                    {
                        return Failure(
                            GoogleDriveAuthenticationStatus.AccountLookupFailed,
                            GoogleDriveOAuthErrorCodes.AccountLookupFailed,
                            "Google Drive authorized the account, but its account details could not be read. Try again later.");
                    }

                    operationToken.ThrowIfCancellationRequested();

                    SyncRemoteProfile? current = SafeGetProfile(profileId);

                    if (current is null)
                        return ProfileFailure(GoogleDriveAuthenticationStatus.ProfileNotFound);

                    if (current.ProviderSettings is not GoogleDriveSyncRemoteSettings)
                    {
                        return Failure(
                            GoogleDriveAuthenticationStatus.SettingsInvalid,
                            GoogleDriveOAuthErrorCodes.SettingsInvalid,
                            "The saved Google Drive profile settings changed during sign-in.");
                    }

                    DateTimeOffset now = _clock.UtcNow;
                    SyncRemoteProfile updated;

                    try
                    {
                        updated = _profileRepository.Update(current with
                        {
                            AccountDisplayName = account.DisplayName,
                            ProviderSettings = new GoogleDriveSyncRemoteSettings(
                                account.EmailAddress,
                                GoogleDriveAuthorizationScopes.DriveFile),
                            UpdatedUtc = now
                        });

                        // Interactive tokens remain staged until both the account
                        // lookup and its non-secret profile update have succeeded.
                        // If secure token replacement fails, restore the previous
                        // metadata so reconnect remains transactional to the App.
                        try
                        {
                            if (interactive)
                                await credential.CommitTokenAsync(operationToken);
                        }
                        catch
                        {
                            try
                            {
                                _profileRepository.Update(current);
                            }
                            catch
                            {
                            }

                            throw;
                        }

                        // Timestamp bookkeeping remains best-effort and cannot turn
                        // a validated connection into an authentication failure.
                        try
                        {
                            updated = _profileRepository.UpdateLastUsed(profileId, now);
                        }
                        catch
                        {
                        }

                        try
                        {
                            updated = _profileRepository.UpdateLastSuccessfulConnection(
                                profileId,
                                now);
                        }
                        catch
                        {
                        }
                    }
                    catch
                    {
                        return Failure(
                            GoogleDriveAuthenticationStatus.Failed,
                            GoogleDriveOAuthErrorCodes.Failed,
                            "Google Drive connected, but the non-secret account metadata could not be saved.");
                    }

                    var connection = CreateConnectionSettings(
                        updated,
                        GoogleDriveConnectionStatus.Connected,
                        hasStoredToken: true);

                    string message = operationKind == AuthenticationOperation.Reconnect
                        ? BuildReconnectSuccessMessage(profile, updated)
                        : "Google Drive account connected. Backup synchronization is not available yet.";

                    return new GoogleDriveAuthenticationResult(
                        GoogleDriveAuthenticationStatus.Connected,
                        connection,
                        Message: message);
                }
            }
            catch (GoogleSecretDataStoreException ex)
            {
                return ex.Failure switch
                {
                    GoogleSecretDataStoreFailure.Unavailable => Failure(
                        GoogleDriveAuthenticationStatus.SecretStoreUnavailable,
                        GoogleDriveOAuthErrorCodes.TokenStoreUnavailable,
                        "Protected Google Drive authentication storage is unavailable."),
                    GoogleSecretDataStoreFailure.Corrupted => Failure(
                        GoogleDriveAuthenticationStatus.TokenCorrupted,
                        GoogleDriveOAuthErrorCodes.TokenCorrupted,
                        "Stored Google Drive authentication is unreadable. Remove the local authentication or reconnect the account."),
                    _ => Failure(
                        GoogleDriveAuthenticationStatus.Failed,
                        GoogleDriveOAuthErrorCodes.Failed,
                        "Protected Google Drive authentication storage failed.")
                };
            }
            catch (GoogleAuthorizationException ex) when (
                ex.Failure == GoogleAuthorizationFailure.AuthorizationRevoked &&
                profile is not null)
            {
                return await HandleRevokedAuthorizationAsync(profile, CancellationToken.None);
            }
            catch (GoogleAuthorizationException ex)
            {
                bool interactive = operationKind is
                    AuthenticationOperation.Connect or AuthenticationOperation.Reconnect;
                return MapAuthorizationFailure(ex.Failure, interactive);
            }
            catch (OperationCanceledException)
            {
                return Cancelled();
            }
            catch
            {
                return Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    GoogleDriveOAuthErrorCodes.Failed,
                    "Google Drive sign-in failed. Review the developer OAuth configuration and try again.");
            }
            finally
            {
                CompleteOperation(profileId, operation);
            }
        }

        private async Task<GoogleDriveDisconnectionResult> DisconnectCoreAsync(
            Guid profileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyncRemoteProfile? profile = SafeGetProfile(profileId);

            if (profile is null)
                return DisconnectProfileFailure();

            if (profile.ProviderKind != SyncProviderKind.GoogleDrive)
            {
                return new GoogleDriveDisconnectionResult(
                    GoogleDriveDisconnectionStatus.WrongProviderKind,
                    LocalAuthenticationRemoved: false,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false,
                    GoogleDriveDisconnectionErrorCodes.WrongProviderKind,
                    "The selected remote profile is not a Google Drive profile.");
            }

            SecretOperationResult cleanup;

            try
            {
                cleanup = await _secretStore.DeleteAsync(
                    new SecretKey(profileId, SecretNames.OAuthTokenData),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return DisconnectCleanupFailure(
                    GoogleDriveDisconnectionStatus.CleanupFailed,
                    GoogleDriveDisconnectionErrorCodes.CleanupFailed,
                    "Locally stored Google Drive authentication could not be removed.");
            }

            if (!cleanup.Succeeded)
            {
                return cleanup.Status == SecretOperationStatus.Unavailable
                    ? DisconnectCleanupFailure(
                        GoogleDriveDisconnectionStatus.SecretStoreUnavailable,
                        GoogleDriveDisconnectionErrorCodes.SecretStoreUnavailable,
                        "Protected Google Drive authentication storage is unavailable.")
                    : DisconnectCleanupFailure(
                        GoogleDriveDisconnectionStatus.CleanupFailed,
                        GoogleDriveDisconnectionErrorCodes.CleanupFailed,
                        "Locally stored Google Drive authentication could not be removed.");
            }

            bool removed = cleanup.AffectedCount > 0;

            try
            {
                if (profile.ProviderSettings is not GoogleDriveSyncRemoteSettings settings)
                {
                    return new GoogleDriveDisconnectionResult(
                        GoogleDriveDisconnectionStatus.CleanupFailed,
                        LocalAuthenticationRemoved: removed,
                        ProfilePreserved: true,
                        AccountMetadataCleared: false,
                        GoogleDriveDisconnectionErrorCodes.CleanupFailed,
                        "Local authentication was removed, but saved account metadata could not be cleared.");
                }

                _profileRepository.Update(profile with
                {
                    AccountDisplayName = null,
                    ProviderSettings = new GoogleDriveSyncRemoteSettings(
                        accountEmail: null,
                        settings.RequestedScope),
                    UpdatedUtc = _clock.UtcNow
                });
            }
            catch
            {
                return new GoogleDriveDisconnectionResult(
                    GoogleDriveDisconnectionStatus.CleanupFailed,
                    LocalAuthenticationRemoved: removed,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false,
                    GoogleDriveDisconnectionErrorCodes.CleanupFailed,
                    "Local authentication was removed, but saved account metadata could not be cleared.");
            }

            return new GoogleDriveDisconnectionResult(
                removed
                    ? GoogleDriveDisconnectionStatus.Disconnected
                    : GoogleDriveDisconnectionStatus.AlreadyDisconnected,
                LocalAuthenticationRemoved: removed,
                ProfilePreserved: true,
                AccountMetadataCleared: true,
                Message: removed
                    ? "Google Drive was disconnected from this installation. Locally stored authentication was removed. The saved profile, backup data, and Google Drive files were not deleted."
                    : "Google Drive was already disconnected. The saved profile, backup data, and Google Drive files were not deleted.");
        }

        private async Task<GoogleDriveAuthenticationResult> HandleRevokedAuthorizationAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken)
        {
            SecretOperationResult cleanup;

            try
            {
                cleanup = await _secretStore.DeleteAsync(
                    new SecretKey(profile.Id, SecretNames.OAuthTokenData),
                    cancellationToken);
            }
            catch
            {
                cleanup = SecretOperationResult.Failed(
                    GoogleDriveOAuthErrorCodes.RevokedTokenCleanupFailed);
            }

            bool removed = cleanup.Succeeded;
            GoogleDriveConnectionSettings connection = CreateConnectionSettings(
                profile,
                GoogleDriveConnectionStatus.ReauthenticationRequired,
                hasStoredToken: !removed);

            return new GoogleDriveAuthenticationResult(
                GoogleDriveAuthenticationStatus.AuthorizationRevoked,
                connection,
                removed
                    ? GoogleDriveOAuthErrorCodes.AuthorizationRevoked
                    : GoogleDriveOAuthErrorCodes.RevokedTokenCleanupFailed,
                removed
                    ? "Google Drive authorization is no longer valid. The invalid local authentication was removed; reconnect the account to continue."
                    : "Google Drive authorization is no longer valid. The invalid local authentication could not be removed; retry local disconnect, then reconnect.");
        }

        private static GoogleDriveConnectionSettings CreateConnectionSettings(
            SyncRemoteProfile profile,
            GoogleDriveConnectionStatus status,
            bool hasStoredToken)
        {
            string? email =
                (profile.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail;
            return new GoogleDriveConnectionSettings(
                profile.Id,
                profile.AccountDisplayName,
                email,
                profile.RemoteFolderId,
                profile.RemoteRootDisplayName,
                GoogleDriveAuthorizationScopes.DriveFile,
                status,
                hasStoredToken);
        }

        private static string BuildReconnectSuccessMessage(
            SyncRemoteProfile previous,
            SyncRemoteProfile updated)
        {
            bool accountChanged =
                !string.Equals(
                    previous.AccountDisplayName,
                    updated.AccountDisplayName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    (previous.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail,
                    (updated.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail,
                    StringComparison.OrdinalIgnoreCase);

            if (accountChanged && updated.RemoteFolderId is not null)
            {
                return "Google Drive account reconnected. The saved root-folder identity was preserved but must be validated for the new account in the root-folder milestone. Backup synchronization remains unavailable.";
            }

            return "Google Drive account reconnected. Backup synchronization remains unavailable.";
        }

        private async Task CancelActiveOperationAsync(
            Guid profileId,
            CancellationToken cancellationToken)
        {
            if (profileId == Guid.Empty ||
                !_operations.TryGetValue(profileId, out LifecycleOperation? active))
            {
                return;
            }

            active.Cancellation.Cancel();
            await active.Completion.Task.WaitAsync(cancellationToken);
        }

        private void CompleteOperation(Guid profileId, LifecycleOperation operation)
        {
            _operations.TryRemove(
                new KeyValuePair<Guid, LifecycleOperation>(profileId, operation));
            operation.Completion.TrySetResult();
            operation.Dispose();
        }

        private SyncRemoteProfile? SafeGetProfile(Guid profileId)
        {
            try
            {
                return _profileRepository.GetById(profileId);
            }
            catch
            {
                return null;
            }
        }

        private static GoogleDriveAuthenticationResult MapAuthorizationFailure(
            GoogleAuthorizationFailure failure,
            bool interactive) => failure switch
            {
                GoogleAuthorizationFailure.Cancelled => Cancelled(),
                GoogleAuthorizationFailure.Denied => Failure(
                    GoogleDriveAuthenticationStatus.AuthorizationDenied,
                    GoogleDriveOAuthErrorCodes.Denied,
                    "Google Drive authorization was denied. No account was changed and no backup data was modified."),
                GoogleAuthorizationFailure.PolicyDenied => Failure(
                    GoogleDriveAuthenticationStatus.AuthorizationDenied,
                    GoogleDriveOAuthErrorCodes.PolicyDenied,
                    "This Google account or organization does not allow the requested Drive access."),
                GoogleAuthorizationFailure.BrowserFailed => Failure(
                    GoogleDriveAuthenticationStatus.BrowserLaunchFailed,
                    GoogleDriveOAuthErrorCodes.BrowserFailed,
                    "The system browser could not be opened for Google Drive sign-in."),
                GoogleAuthorizationFailure.CallbackFailed => Failure(
                    GoogleDriveAuthenticationStatus.CallbackFailed,
                    GoogleDriveOAuthErrorCodes.CallbackFailed,
                    "Google Drive sign-in could not return to the application."),
                GoogleAuthorizationFailure.InvalidClient => Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    GoogleDriveOAuthErrorCodes.InvalidClient,
                    "The Google OAuth desktop client configuration was rejected."),
                GoogleAuthorizationFailure.RedirectMismatch => Failure(
                    GoogleDriveAuthenticationStatus.CallbackFailed,
                    GoogleDriveOAuthErrorCodes.RedirectMismatch,
                    "Google Drive rejected the local callback. Verify the desktop OAuth client configuration."),
                GoogleAuthorizationFailure.NetworkFailed => Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    GoogleDriveOAuthErrorCodes.NetworkFailed,
                    "Google Drive sign-in could not reach Google's authorization service."),
                GoogleAuthorizationFailure.TokenExchangeFailed => Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    GoogleDriveOAuthErrorCodes.TokenExchangeFailed,
                    "Google Drive could not complete the authorization exchange. " +
                    "If this desktop OAuth client has a client secret, set " +
                    "GAMESAVES_GOOGLE_CLIENT_SECRET in the local environment and try again."),
                GoogleAuthorizationFailure.RefreshFailed when !interactive => Failure(
                    GoogleDriveAuthenticationStatus.ReauthenticationRequired,
                    GoogleDriveOAuthErrorCodes.ReauthenticationRequired,
                    "Stored Google Drive authentication could not be refreshed. Reconnect the account to continue."),
                _ => Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    interactive
                        ? GoogleDriveOAuthErrorCodes.Failed
                        : GoogleDriveOAuthErrorCodes.RefreshFailed,
                    interactive
                        ? "Google Drive sign-in failed. Review the developer OAuth configuration and try again."
                        : "Stored Google Drive authentication could not be refreshed.")
            };

        private static GoogleDriveAuthenticationResult Cancelled() => Failure(
            GoogleDriveAuthenticationStatus.Cancelled,
            GoogleDriveOAuthErrorCodes.Cancelled,
            "Google Drive sign-in was cancelled. Existing account authentication and backup data were not changed.");

        private static GoogleDriveAuthenticationResult ProfileFailure(
            GoogleDriveAuthenticationStatus status) => Failure(
            status,
            GoogleDriveOAuthErrorCodes.ProfileNotFound,
            "The saved Google Drive profile was not found.");

        private static GoogleDriveAuthenticationResult Failure(
            GoogleDriveAuthenticationStatus status,
            string? errorCode,
            string message) =>
            new(status, ErrorCode: errorCode, Message: message);

        private static GoogleDriveDisconnectionResult DisconnectProfileFailure() => new(
            GoogleDriveDisconnectionStatus.ProfileNotFound,
            LocalAuthenticationRemoved: false,
            ProfilePreserved: false,
            AccountMetadataCleared: false,
            GoogleDriveDisconnectionErrorCodes.ProfileNotFound,
            "The saved Google Drive profile was not found.");

        private static GoogleDriveDisconnectionResult DisconnectCleanupFailure(
            GoogleDriveDisconnectionStatus status,
            string errorCode,
            string message) => new(
                status,
                LocalAuthenticationRemoved: false,
                ProfilePreserved: true,
                AccountMetadataCleared: false,
                errorCode,
                message);

        private enum AuthenticationOperation
        {
            Connect,
            Restore,
            Reconnect
        }

        private sealed class LifecycleOperation : IDisposable
        {
            public CancellationTokenSource Cancellation { get; } = new();
            public TaskCompletionSource Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public void Dispose() => Cancellation.Dispose();
        }
    }
}
