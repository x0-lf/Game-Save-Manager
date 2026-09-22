using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Core.Sync
{
    public static class OneDriveAuthorizationScopes
    {
        public const string AppFolder = "Files.ReadWrite.AppFolder";
        public const string OfflineAccess = "offline_access";
        public const string UserRead = "User.Read";

        public static readonly string DefaultScopes = $"{AppFolder} {OfflineAccess} {UserRead}";

        public static string ValidateRequestedScope(string? requestedScope)
        {
            if (string.IsNullOrWhiteSpace(requestedScope))
                return AppFolder;

            if (!requestedScope.Contains(AppFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The requested Microsoft OneDrive authorization scope must include '{AppFolder}'.",
                    nameof(requestedScope));
            }

            return requestedScope;
        }
    }

    /// <summary>
    /// Explicitly persisted, non-secret Microsoft OneDrive profile settings.
    /// Profile identity and root-folder metadata remain on SyncRemoteProfile.
    /// </summary>
    public sealed record OneDriveSyncRemoteSettings : SyncRemoteProfileSettings
    {
        public const int CurrentSchemaVersion = 1;

        public OneDriveSyncRemoteSettings(
            string? accountEmail,
            string requestedScope,
            string? accountDisplayName = null,
            string? appRootFolderId = null)
            : base(CurrentSchemaVersion)
        {
            AccountEmail = string.IsNullOrWhiteSpace(accountEmail) ? null : accountEmail.Trim();
            AccountDisplayName = string.IsNullOrWhiteSpace(accountDisplayName) ? null : accountDisplayName.Trim();
            AppRootFolderId = string.IsNullOrWhiteSpace(appRootFolderId) ? null : appRootFolderId.Trim();
            RequestedScope = OneDriveAuthorizationScopes.ValidateRequestedScope(requestedScope);
        }

        public string? AccountEmail { get; }

        public string? AccountDisplayName { get; }

        public string? AppRootFolderId { get; }

        public string RequestedScope { get; }

        public override string ToString() =>
            $"{nameof(OneDriveSyncRemoteSettings)} {{ SchemaVersion = {SchemaVersion}, RequestedScope = {RequestedScope} }}";
    }

    public enum OneDriveConnectionStatus
    {
        Unknown = 0,
        NotConfigured = 1,
        Disconnected = 2,
        StoredAuthenticationAvailable = 3,
        Connecting = 4,
        Connected = 5,
        ReauthenticationRequired = 6,
        Unavailable = 7,
        Failed = 8
    }

    /// <summary>
    /// Pure runtime view of Microsoft OneDrive connection metadata. It never owns or
    /// exposes OAuth token contents, and is not persisted as profile truth.
    /// </summary>
    public sealed record OneDriveConnectionSettings
    {
        public OneDriveConnectionSettings(
            Guid remoteProfileId,
            string? accountDisplayName,
            string? accountEmail,
            string? rootFolderId,
            string? rootFolderDisplayName,
            string requestedScope,
            OneDriveConnectionStatus connectionStatus,
            bool hasStoredToken,
            OneDriveQuotaInfo? quota = null)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A non-empty remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            RemoteProfileId = remoteProfileId;
            AccountDisplayName = string.IsNullOrWhiteSpace(accountDisplayName) ? null : accountDisplayName.Trim();
            AccountEmail = string.IsNullOrWhiteSpace(accountEmail) ? null : accountEmail.Trim();
            RootFolderId = string.IsNullOrWhiteSpace(rootFolderId) ? null : rootFolderId.Trim();
            RootFolderDisplayName = string.IsNullOrWhiteSpace(rootFolderDisplayName) ? null : rootFolderDisplayName.Trim();
            RequestedScope = OneDriveAuthorizationScopes.ValidateRequestedScope(requestedScope);
            ConnectionStatus = connectionStatus;
            HasStoredToken = hasStoredToken;
            Quota = quota;
        }

        public Guid RemoteProfileId { get; }

        public string? AccountDisplayName { get; }

        public string? AccountEmail { get; }

        public string? RootFolderId { get; }

        public string? RootFolderDisplayName { get; }

        public string RequestedScope { get; }

        public OneDriveConnectionStatus ConnectionStatus { get; }

        public bool HasStoredToken { get; }

        public OneDriveQuotaInfo? Quota { get; }

        public bool IsConnected =>
            ConnectionStatus == OneDriveConnectionStatus.Connected;

        public static OneDriveConnectionSettings Disconnected(
            Guid remoteProfileId,
            string requestedScope = OneDriveAuthorizationScopes.AppFolder) =>
            new(
                remoteProfileId,
                accountDisplayName: null,
                accountEmail: null,
                rootFolderId: null,
                rootFolderDisplayName: null,
                requestedScope,
                OneDriveConnectionStatus.Disconnected,
                hasStoredToken: false);
    }

    /// <summary>
    /// Manages OAuth 2.0 PKCE authentication lifecycle and quota checks for Microsoft OneDrive.
    /// </summary>
    public interface IOneDriveOAuthService
    {
        OneDriveOAuthClientConfigurationState GetClientConfigurationState();

        Task<OneDriveAuthenticationResult> ConnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<OneDriveAuthenticationResult> RestoreAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<OneDriveAuthenticationResult> ReconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<OneDriveDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<OneDriveQuotaInfo?> GetQuotaAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }
}
