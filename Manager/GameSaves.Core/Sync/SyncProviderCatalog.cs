namespace GameSaves.Core.Sync
{
    public sealed record SyncProviderCapabilities(
        bool RequiresInteractiveLogin,
        bool RequiresServerCredentials,
        bool SupportsResumableUpload,
        bool SupportsRemoteQuota,
        bool SupportsRemoteFolderSelection,
        bool SupportsPersistentAuthentication,
        bool SupportsConnectionTesting,
        bool SupportsLogout,
        bool SupportsOpenRemoteLocation)
    {
        public static SyncProviderCapabilities None { get; } = new(
            false, false, false, false, false, false, false, false, false);
    }

    /// <summary>
    /// Selects the provider-specific configuration editor without coupling
    /// provider metadata to Avalonia controls.
    /// </summary>
    public enum SyncProviderConfigurationSurface
    {
        Unavailable = 0,
        LocalFolder = 1,
        Sftp = 2,
        InteractiveOAuth = 3,
        ServerCredentials = 4
    }

    public sealed record SyncProviderDescriptor(
        SyncProviderKind Kind,
        string DisplayName,
        bool IsImplemented,
        SyncProviderCapabilities Capabilities,
        SyncProviderConfigurationSurface ConfigurationSurface,
        string? UnavailableMessage = null);

    /// <summary>
    /// Authoritative provider metadata. Provider construction remains the
    /// separate responsibility of <see cref="ISyncProviderFactory"/>.
    /// </summary>
    public interface ISyncProviderCatalog
    {
        IReadOnlyList<SyncProviderDescriptor> GetAll();

        SyncProviderDescriptor GetDescriptor(SyncProviderKind kind);

        bool IsImplemented(SyncProviderKind kind);
    }
}
