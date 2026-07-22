using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>Single authoritative capability matrix for sync providers.</summary>
    public sealed class SyncProviderCatalog : ISyncProviderCatalog
    {
        private static readonly SyncProviderDescriptor Unknown = new(
            SyncProviderKind.Unknown,
            "Unknown provider",
            IsImplemented: false,
            SyncProviderCapabilities.None,
            SyncProviderConfigurationSurface.Unavailable,
            "This sync provider is not supported by this version.",
            IsConfigurationAvailable: false);

        private static readonly IReadOnlyList<SyncProviderDescriptor> Descriptors =
            new[]
            {
                Unknown,
                new SyncProviderDescriptor(
                    SyncProviderKind.LocalFolder,
                    "Local or mounted folder",
                    IsImplemented: true,
                    new SyncProviderCapabilities(
                        RequiresInteractiveLogin: false,
                        RequiresServerCredentials: false,
                        SupportsResumableUpload: false,
                        SupportsRemoteQuota: false,
                        SupportsRemoteFolderSelection: true,
                        SupportsPersistentAuthentication: false,
                        SupportsConnectionTesting: true,
                        SupportsLogout: false,
                        SupportsOpenRemoteLocation: true),
                    SyncProviderConfigurationSurface.LocalFolder,
                    IsConfigurationAvailable: true),
                new SyncProviderDescriptor(
                    SyncProviderKind.Sftp,
                    "SFTP server (SSH)",
                    IsImplemented: true,
                    new SyncProviderCapabilities(
                        RequiresInteractiveLogin: false,
                        RequiresServerCredentials: true,
                        SupportsResumableUpload: false,
                        SupportsRemoteQuota: false,
                        SupportsRemoteFolderSelection: false,
                        SupportsPersistentAuthentication: false,
                        SupportsConnectionTesting: true,
                        SupportsLogout: false,
                        SupportsOpenRemoteLocation: false),
                    SyncProviderConfigurationSurface.Sftp,
                    IsConfigurationAvailable: true),
                new SyncProviderDescriptor(
                    SyncProviderKind.GoogleDrive,
                    "Google Drive",
                    IsImplemented: false,
                    CloudCapabilities(),
                    SyncProviderConfigurationSurface.InteractiveOAuth,
                    "Google Drive account connection is available. Backup synchronization is implemented in later milestones.",
                    IsConfigurationAvailable: true),
                new SyncProviderDescriptor(
                    SyncProviderKind.WebDav,
                    "WebDAV",
                    IsImplemented: false,
                    new SyncProviderCapabilities(
                        RequiresInteractiveLogin: false,
                        RequiresServerCredentials: true,
                        SupportsResumableUpload: false,
                        SupportsRemoteQuota: false,
                        SupportsRemoteFolderSelection: false,
                        SupportsPersistentAuthentication: true,
                        SupportsConnectionTesting: true,
                        SupportsLogout: true,
                        SupportsOpenRemoteLocation: true),
                    SyncProviderConfigurationSurface.ServerCredentials,
                    "WebDAV sync is not implemented yet.",
                    IsConfigurationAvailable: false),
                new SyncProviderDescriptor(
                    SyncProviderKind.OneDrive,
                    "OneDrive",
                    IsImplemented: false,
                    CloudCapabilities(),
                    SyncProviderConfigurationSurface.InteractiveOAuth,
                    "OneDrive sync is not implemented yet.",
                    IsConfigurationAvailable: false)
            };

        private static readonly IReadOnlyDictionary<SyncProviderKind, SyncProviderDescriptor> ByKind =
            Descriptors.ToDictionary(descriptor => descriptor.Kind);

        public IReadOnlyList<SyncProviderDescriptor> GetAll() => Descriptors;

        public SyncProviderDescriptor GetDescriptor(SyncProviderKind kind) =>
            ByKind.GetValueOrDefault(kind, Unknown);

        public bool IsImplemented(SyncProviderKind kind) =>
            GetDescriptor(kind).IsImplemented;

        private static SyncProviderCapabilities CloudCapabilities() => new(
            RequiresInteractiveLogin: true,
            RequiresServerCredentials: false,
            SupportsResumableUpload: true,
            SupportsRemoteQuota: true,
            SupportsRemoteFolderSelection: true,
            SupportsPersistentAuthentication: true,
            SupportsConnectionTesting: true,
            SupportsLogout: true,
            SupportsOpenRemoteLocation: true);
    }
}
