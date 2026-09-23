using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Core.Sync
{
    public sealed record MegaSyncRemoteSettings : SyncRemoteProfileSettings
    {
        public const int CurrentSchemaVersion = 1;
        public const string DefaultRootFolderName = "GameSave Manager Backups";

        public MegaSyncRemoteSettings(
            string? userEmail,
            string? rootFolderNodeId = null,
            string? rootFolderName = DefaultRootFolderName)
            : base(CurrentSchemaVersion)
        {
            UserEmail = string.IsNullOrWhiteSpace(userEmail) ? null : userEmail.Trim();
            RootFolderNodeId = string.IsNullOrWhiteSpace(rootFolderNodeId) ? null : rootFolderNodeId.Trim();
            RootFolderName = string.IsNullOrWhiteSpace(rootFolderName) ? DefaultRootFolderName : rootFolderName.Trim();
        }

        public string? UserEmail { get; }

        public string? RootFolderNodeId { get; }

        public string RootFolderName { get; }

        public override string ToString() =>
            $"{nameof(MegaSyncRemoteSettings)} {{ SchemaVersion = {SchemaVersion}, RootFolderName = '{RootFolderName}', HasEmail = {UserEmail is not null} }}";
    }

    public enum MegaConnectionStatus
    {
        Disconnected = 0,
        StoredAuthenticationAvailable = 1,
        Connecting = 2,
        Connected = 3,
        TwoFactorRequired = 4,
        SessionExpired = 5,
        Failed = 6
    }

    public sealed record MegaConnectionSettings(
        Guid RemoteProfileId,
        string? UserEmail,
        string? RootFolderNodeId,
        string? RootFolderName,
        MegaConnectionStatus ConnectionStatus,
        MegaQuotaInfo? QuotaInfo = null)
    {
        public bool IsConnected => ConnectionStatus == MegaConnectionStatus.Connected;

        public bool CanSync => IsConnected && !string.IsNullOrWhiteSpace(RootFolderNodeId);
    }

    public interface IMegaSessionService
    {
        Task<MegaAuthenticationResult> AuthenticateAsync(
            Guid remoteProfileId,
            string email,
            string password,
            string? twoFactorCode = null,
            CancellationToken cancellationToken = default);

        Task<MegaSessionToken?> GetSessionAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<bool> HasValidSessionAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<MegaDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<MegaQuotaInfo?> GetQuotaAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }
}
