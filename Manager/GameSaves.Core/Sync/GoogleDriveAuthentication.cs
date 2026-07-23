namespace GameSaves.Core.Sync
{
    public enum GoogleDriveAuthenticationStatus
    {
        Connected = 0,
        NoStoredAuthentication = 1,
        Cancelled = 2,
        AuthorizationDenied = 3,
        ClientConfigurationMissing = 4,
        ProfileNotFound = 5,
        WrongProviderKind = 6,
        SettingsInvalid = 7,
        SecretStoreUnavailable = 8,
        TokenCorrupted = 9,
        ReauthenticationRequired = 10,
        BrowserLaunchFailed = 11,
        CallbackFailed = 12,
        AccountLookupFailed = 13,
        Unavailable = 14,
        Failed = 15,
        AuthorizationRevoked = 16
    }

    public enum GoogleDriveOAuthClientConfigurationStatus
    {
        Available = 0,
        Missing = 1,
        Invalid = 2
    }

    public static class GoogleDriveOAuthErrorCodes
    {
        public const string ClientIdMissing = "GoogleDriveOAuthClientIdMissing";
        public const string ClientIdInvalid = "GoogleDriveOAuthClientIdInvalid";
        public const string InvalidClient = "GoogleDriveOAuthInvalidClient";
        public const string Cancelled = "GoogleDriveOAuthCancelled";
        public const string Denied = "GoogleDriveOAuthDenied";
        public const string PolicyDenied = "GoogleDriveOAuthPolicyDenied";
        public const string BrowserFailed = "GoogleDriveOAuthBrowserFailed";
        public const string CallbackFailed = "GoogleDriveOAuthCallbackFailed";
        public const string RedirectMismatch = "GoogleDriveOAuthRedirectMismatch";
        public const string NetworkFailed = "GoogleDriveOAuthNetworkFailed";
        public const string TokenExchangeFailed = "GoogleDriveOAuthTokenExchangeFailed";
        public const string TokenStoreUnavailable = "GoogleDriveOAuthTokenStoreUnavailable";
        public const string TokenCorrupted = "GoogleDriveOAuthTokenCorrupted";
        public const string RefreshFailed = "GoogleDriveOAuthRefreshFailed";
        public const string ReauthenticationRequired = "GoogleDriveOAuthReauthenticationRequired";
        public const string AuthorizationRevoked = "GoogleDriveOAuthAuthorizationRevoked";
        public const string RevokedTokenCleanupFailed = "GoogleDriveOAuthRevokedTokenCleanupFailed";
        public const string AccountLookupFailed = "GoogleDriveOAuthAccountLookupFailed";
        public const string DriveUnavailable = "GoogleDriveOAuthDriveUnavailable";
        public const string ProfileNotFound = "GoogleDriveOAuthProfileNotFound";
        public const string WrongProviderKind = "GoogleDriveOAuthWrongProviderKind";
        public const string SettingsInvalid = "GoogleDriveOAuthSettingsInvalid";
        public const string OperationInProgress = "GoogleDriveOAuthOperationInProgress";
        public const string Failed = "GoogleDriveOAuthFailed";
    }

    public enum GoogleDriveDisconnectionStatus
    {
        Disconnected = 0,
        AlreadyDisconnected = 1,
        ProfileNotFound = 2,
        WrongProviderKind = 3,
        SecretStoreUnavailable = 4,
        CleanupFailed = 5,
        Failed = 6
    }

    public static class GoogleDriveDisconnectionErrorCodes
    {
        public const string ProfileNotFound = "GoogleDriveDisconnectProfileNotFound";
        public const string WrongProviderKind = "GoogleDriveDisconnectWrongProviderKind";
        public const string SecretStoreUnavailable = "GoogleDriveDisconnectSecretStoreUnavailable";
        public const string CleanupFailed = "GoogleDriveDisconnectCleanupFailed";
        public const string Failed = "GoogleDriveDisconnectFailed";
    }

    /// <summary>
    /// Safe local-disconnect outcome. Disconnect never revokes a remote Google
    /// grant and never deletes the saved profile or remote data.
    /// </summary>
    public sealed record GoogleDriveDisconnectionResult(
        GoogleDriveDisconnectionStatus Status,
        bool LocalAuthenticationRemoved,
        bool ProfilePreserved,
        bool AccountMetadataCleared,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool Succeeded =>
            Status is GoogleDriveDisconnectionStatus.Disconnected or
                GoogleDriveDisconnectionStatus.AlreadyDisconnected;

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    /// <summary>Non-secret availability only; the configured client ID is never exposed.</summary>
    public sealed record GoogleDriveOAuthClientConfigurationState(
        GoogleDriveOAuthClientConfigurationStatus Status,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool IsAvailable => Status == GoogleDriveOAuthClientConfigurationStatus.Available;

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    /// <summary>
    /// Safe authentication outcome. OAuth credentials and raw provider errors
    /// never cross the Infrastructure boundary.
    /// </summary>
    public sealed record GoogleDriveAuthenticationResult(
        GoogleDriveAuthenticationStatus Status,
        GoogleDriveConnectionSettings? ConnectionSettings = null,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool IsConnected =>
            Status == GoogleDriveAuthenticationStatus.Connected &&
            ConnectionSettings?.ConnectionStatus == GoogleDriveConnectionStatus.Connected;

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    public interface IGoogleDriveOAuthService
    {
        GoogleDriveOAuthClientConfigurationState GetClientConfigurationState();

        Task<GoogleDriveAuthenticationResult> ConnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveAuthenticationResult> RestoreAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveAuthenticationResult> ReconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }
}
