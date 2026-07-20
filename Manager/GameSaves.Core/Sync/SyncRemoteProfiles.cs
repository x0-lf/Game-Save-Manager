namespace GameSaves.Core.Sync
{
    public abstract record SyncRemoteProfileSettings(int SchemaVersion);

    public sealed record LocalFolderSyncRemoteSettings(
        string LocalFolderPath)
        : SyncRemoteProfileSettings(SchemaVersion: 1);

    public sealed record SftpSyncRemoteSettings(
        string Host,
        int Port,
        string Username,
        SftpAuthMethod AuthenticationMethod,
        string? PrivateKeyFilePath,
        string RemotePath)
        : SyncRemoteProfileSettings(SchemaVersion: 1);

    /// <summary>
    /// A named, non-secret sync target configuration. SettingsError is set
    /// when persisted settings cannot be used safely; the profile remains
    /// visible and can still be renamed or deleted.
    /// </summary>
    public sealed record SyncRemoteProfile(
        Guid Id,
        string DisplayName,
        SyncProviderKind ProviderKind,
        string? AccountDisplayName,
        string? RemoteRootDisplayName,
        SyncRemoteProfileSettings? ProviderSettings,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        DateTimeOffset? LastUsedUtc,
        DateTimeOffset? LastSuccessfulConnectionUtc,
        string? RemoteFolderId,
        string? SettingsError = null);

    public static class SyncRemoteProfileValidation
    {
        public const int MaximumDisplayNameLength = 100;

        public static string NormalizeDisplayName(string? displayName)
        {
            string normalized = displayName?.Trim() ?? string.Empty;

            if (normalized.Length == 0)
                throw new ArgumentException("A remote profile display name is required.", nameof(displayName));

            if (normalized.Length > MaximumDisplayNameLength)
            {
                throw new ArgumentException(
                    $"A remote profile display name cannot exceed {MaximumDisplayNameLength} characters.",
                    nameof(displayName));
            }

            return normalized;
        }
    }

    public interface ISyncRemoteProfileRepository
    {
        IReadOnlyList<SyncRemoteProfile> GetAll();

        SyncRemoteProfile? GetById(Guid id);

        SyncRemoteProfile Create(SyncRemoteProfile profile);

        SyncRemoteProfile Update(SyncRemoteProfile profile);

        SyncRemoteProfile Rename(Guid id, string displayName, DateTimeOffset updatedUtc);

        void Delete(Guid id);

        SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset lastUsedUtc);

        SyncRemoteProfile UpdateLastSuccessfulConnection(
            Guid id,
            DateTimeOffset lastSuccessfulConnectionUtc);
    }

    public sealed class SyncRemoteProfileDuplicateNameException : InvalidOperationException
    {
        public SyncRemoteProfileDuplicateNameException(string displayName)
            : base($"A remote profile named '{displayName}' already exists.")
        {
        }
    }

    public sealed class SyncRemoteProfileNotFoundException : InvalidOperationException
    {
        public SyncRemoteProfileNotFoundException(Guid id)
            : base($"Remote profile '{id}' was not found.")
        {
        }
    }

    public interface IUtcClock
    {
        DateTimeOffset UtcNow { get; }
    }
}
