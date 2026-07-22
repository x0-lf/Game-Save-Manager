using GameSaves.Core.Sync;
using System.Collections.Concurrent;

namespace GameSaves.Infrastructure.GoogleDrive
{
    public sealed class GoogleDriveOAuthService : IGoogleDriveOAuthService
    {
        internal static IReadOnlyList<string> RequestedScopes { get; } =
            new[] { GoogleDriveAuthorizationScopes.DriveFile };

        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IGoogleOAuthClientConfigurationProvider _configurationProvider;
        private readonly IGoogleSecretDataStoreFactory _dataStoreFactory;
        private readonly IGoogleInstalledAppAuthorizer _authorizer;
        private readonly IGoogleDriveAccountReader _accountReader;
        private readonly IUtcClock _clock;
        private readonly ConcurrentDictionary<Guid, byte> _operations = new();

        internal GoogleDriveOAuthService(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleOAuthClientConfigurationProvider configurationProvider,
            IGoogleSecretDataStoreFactory dataStoreFactory,
            IGoogleInstalledAppAuthorizer authorizer,
            IGoogleDriveAccountReader accountReader,
            IUtcClock clock)
        {
            _profileRepository = profileRepository;
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
            AuthenticateAsync(remoteProfileId, interactive: true, cancellationToken);

        public Task<GoogleDriveAuthenticationResult> RestoreAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            AuthenticateAsync(remoteProfileId, interactive: false, cancellationToken);

        private async Task<GoogleDriveAuthenticationResult> AuthenticateAsync(
            Guid profileId,
            bool interactive,
            CancellationToken cancellationToken)
        {
            if (profileId == Guid.Empty)
                return ProfileFailure(GoogleDriveAuthenticationStatus.ProfileNotFound);

            if (!_operations.TryAdd(profileId, 0))
            {
                return Failure(
                    GoogleDriveAuthenticationStatus.Failed,
                    GoogleDriveOAuthErrorCodes.OperationInProgress,
                    "A Google Drive sign-in operation is already running for this profile.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncRemoteProfile? profile = SafeGetProfile(profileId);

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
                GoogleAuthorizedCredential? credential = interactive
                    ? await _authorizer.ConnectAsync(
                        configuration.Configuration,
                        profileId,
                        dataStore,
                        RequestedScopes,
                        cancellationToken)
                    : await _authorizer.RestoreAsync(
                        configuration.Configuration,
                        profileId,
                        dataStore,
                        RequestedScopes,
                        cancellationToken);

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
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return Cancelled();
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

                    SyncRemoteProfile? current = SafeGetProfile(profileId);

                    if (current is null)
                        return ProfileFailure(GoogleDriveAuthenticationStatus.ProfileNotFound);

                    var currentSettings = current.ProviderSettings as GoogleDriveSyncRemoteSettings;

                    if (currentSettings is null)
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

                        // SQLite deliberately keeps operational timestamps out of the
                        // general profile update so an older editor cannot overwrite
                        // newer usage data. These best-effort calls use the repository's
                        // dedicated columns without turning a successful authorization
                        // into a failure if timestamp bookkeeping is unavailable.
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

                    var connection = new GoogleDriveConnectionSettings(
                        updated.Id,
                        updated.AccountDisplayName,
                        ((GoogleDriveSyncRemoteSettings)updated.ProviderSettings!).AccountEmail,
                        updated.RemoteFolderId,
                        updated.RemoteRootDisplayName,
                        GoogleDriveAuthorizationScopes.DriveFile,
                        GoogleDriveConnectionStatus.Connected,
                        hasStoredToken: true);

                    return new GoogleDriveAuthenticationResult(
                        GoogleDriveAuthenticationStatus.Connected,
                        connection,
                        Message: "Google Drive account connected. Backup synchronization is not available yet.");
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
                        "Stored Google Drive authentication is unreadable. Reauthentication is required."),
                    _ => Failure(
                        GoogleDriveAuthenticationStatus.Failed,
                        GoogleDriveOAuthErrorCodes.Failed,
                        "Protected Google Drive authentication storage failed.")
                };
            }
            catch (GoogleAuthorizationException ex)
            {
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
                _operations.TryRemove(profileId, out _);
            }
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
                    "Google Drive authorization was denied. No account was connected and no backup data was changed."),
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
                    "Stored Google Drive authentication is no longer valid. Sign in again to continue."),
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
            "Google Drive sign-in was cancelled. No backup data was changed.");

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
    }
}
