using GameSaves.Core.Secrets;

namespace GameSaves.Core.Sync
{
    public sealed record SyncRemoteProfileDeleteResult(
        bool ProfileDeleted,
        bool SecretsDeleted,
        string? CleanupWarning = null)
    {
        public bool Succeeded => ProfileDeleted && SecretsDeleted;
    }

    public sealed record SyncRemoteProfileAuthenticationResult(
        bool SecretsDeleted,
        bool ProfilePreserved,
        string? CleanupWarning = null)
    {
        public bool Succeeded => SecretsDeleted && ProfilePreserved;
    }

    /// <summary>
    /// Coordinates profile lifecycle changes that cross profile-row and
    /// secret-store boundaries. It never deletes backups or remote content.
    /// </summary>
    public interface ISyncRemoteProfileService
    {
        Task<SyncRemoteProfileDeleteResult> DeleteAsync(
            Guid profileId,
            CancellationToken cancellationToken = default);

        Task<SyncRemoteProfileAuthenticationResult> DisconnectAuthenticationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default);

        Task<bool> HasStoredAuthenticationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores the password or app password of a saved WebDAV profile in the
        /// protected secret store, replacing any earlier one. Refused for a
        /// profile that does not exist or is not a WebDAV profile.
        /// </summary>
        Task<SecretOperationResult> StoreWebDavPasswordAsync(
            Guid profileId,
            string password,
            CancellationToken cancellationToken = default);
    }
}
