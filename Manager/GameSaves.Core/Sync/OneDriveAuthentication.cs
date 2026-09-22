namespace GameSaves.Core.Sync
{
    public enum OneDriveAuthenticationStatus
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

    public enum OneDriveOAuthClientConfigurationStatus
    {
        Available = 0,
        Missing = 1,
        Invalid = 2
    }

    public static class OneDriveOAuthErrorCodes
    {
        public const string ClientIdMissing = "OneDriveOAuthClientIdMissing";
        public const string ClientIdInvalid = "OneDriveOAuthClientIdInvalid";
        public const string InvalidClient = "OneDriveOAuthInvalidClient";
        public const string Cancelled = "OneDriveOAuthCancelled";
        public const string Denied = "OneDriveOAuthDenied";
        public const string PolicyDenied = "OneDriveOAuthPolicyDenied";
        public const string BrowserFailed = "OneDriveOAuthBrowserFailed";
        public const string CallbackFailed = "OneDriveOAuthCallbackFailed";
        public const string RedirectMismatch = "OneDriveOAuthRedirectMismatch";
        public const string NetworkFailed = "OneDriveOAuthNetworkFailed";
        public const string TokenExchangeFailed = "OneDriveOAuthTokenExchangeFailed";
        public const string TokenStoreUnavailable = "OneDriveOAuthTokenStoreUnavailable";
        public const string TokenCorrupted = "OneDriveOAuthTokenCorrupted";
        public const string RefreshFailed = "OneDriveOAuthRefreshFailed";
        public const string ReauthenticationRequired = "OneDriveOAuthReauthenticationRequired";
        public const string AuthorizationRevoked = "OneDriveOAuthAuthorizationRevoked";
        public const string RevokedTokenCleanupFailed = "OneDriveOAuthRevokedTokenCleanupFailed";
        public const string AccountLookupFailed = "OneDriveOAuthAccountLookupFailed";
        public const string DriveUnavailable = "OneDriveOAuthDriveUnavailable";
        public const string ProfileNotFound = "OneDriveOAuthProfileNotFound";
        public const string WrongProviderKind = "OneDriveOAuthWrongProviderKind";
        public const string SettingsInvalid = "OneDriveOAuthSettingsInvalid";
        public const string OperationInProgress = "OneDriveOAuthOperationInProgress";
        public const string Failed = "OneDriveOAuthFailed";
    }

    public enum OneDriveDisconnectionStatus
    {
        Disconnected = 0,
        AlreadyDisconnected = 1,
        ProfileNotFound = 2,
        WrongProviderKind = 3,
        SecretStoreUnavailable = 4,
        CleanupFailed = 5,
        Failed = 6
    }

    public static class OneDriveDisconnectionErrorCodes
    {
        public const string ProfileNotFound = "OneDriveDisconnectProfileNotFound";
        public const string WrongProviderKind = "OneDriveDisconnectWrongProviderKind";
        public const string SecretStoreUnavailable = "OneDriveDisconnectSecretStoreUnavailable";
        public const string CleanupFailed = "OneDriveDisconnectCleanupFailed";
        public const string Failed = "OneDriveDisconnectFailed";
    }

    /// <summary>
    /// Safe local-disconnect outcome. Disconnect never revokes a remote Microsoft
    /// grant and never deletes the saved profile or remote data.
    /// </summary>
    public sealed record OneDriveDisconnectionResult(
        OneDriveDisconnectionStatus Status,
        bool LocalAuthenticationRemoved,
        bool ProfilePreserved,
        bool AccountMetadataCleared,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool Succeeded =>
            Status is OneDriveDisconnectionStatus.Disconnected or
                OneDriveDisconnectionStatus.AlreadyDisconnected;

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    /// <summary>Non-secret availability only; the configured client ID is never exposed.</summary>
    public sealed record OneDriveOAuthClientConfigurationState(
        OneDriveOAuthClientConfigurationStatus Status,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool IsAvailable =>
            Status == OneDriveOAuthClientConfigurationStatus.Available;

        public static OneDriveOAuthClientConfigurationState Available() =>
            new(OneDriveOAuthClientConfigurationStatus.Available);

        public static OneDriveOAuthClientConfigurationState Missing(
            string message = "Microsoft OneDrive client configuration is missing.") =>
            new(
                OneDriveOAuthClientConfigurationStatus.Missing,
                OneDriveOAuthErrorCodes.ClientIdMissing,
                message);

        public static OneDriveOAuthClientConfigurationState Invalid(
            string message = "Microsoft OneDrive client configuration is invalid.") =>
            new(
                OneDriveOAuthClientConfigurationStatus.Invalid,
                OneDriveOAuthErrorCodes.ClientIdInvalid,
                message);
    }

    public sealed record OneDriveAuthenticationResult(
        OneDriveAuthenticationStatus Status,
        bool Succeeded,
        string? AccountEmail = null,
        string? AccountDisplayName = null,
        string? ErrorCode = null,
        string? Message = null)
    {
        public static OneDriveAuthenticationResult Success(
            string? accountEmail,
            string? accountDisplayName) =>
            new(
                OneDriveAuthenticationStatus.Connected,
                Succeeded: true,
                AccountEmail: accountEmail,
                AccountDisplayName: accountDisplayName);

        public static OneDriveAuthenticationResult Failure(
            OneDriveAuthenticationStatus status,
            string errorCode,
            string message) =>
            new(
                status,
                Succeeded: false,
                ErrorCode: errorCode,
                Message: message);
    }

    /// <summary>
    /// Storage quota information for Microsoft OneDrive.
    /// Values are in bytes; state may be "normal", "nearing", "critical", or "exceeded".
    /// </summary>
    public sealed record OneDriveQuotaInfo(
        long TotalBytes,
        long UsedBytes,
        long RemainingBytes,
        string? State = null)
    {
        public double UsedPercent =>
            TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0.0;

        public string FormattedTotal => FormatBytes(TotalBytes);
        public string FormattedUsed => FormatBytes(UsedBytes);
        public string FormattedRemaining => FormatBytes(RemainingBytes);

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "Unknown";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
