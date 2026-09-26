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
                    IsImplemented: true,
                    CloudCapabilities(),
                    SyncProviderConfigurationSurface.InteractiveOAuth,
                    IsConfigurationAvailable: true),
                // A typed folder under a typed https URL; the password is kept
                // (encrypted) so it can be forgotten again. A DAV URL is not a
                // page a browser can usefully open, so there is no Open action.
                new SyncProviderDescriptor(
                    SyncProviderKind.WebDav,
                    "WebDAV / Nextcloud",
                    IsImplemented: true,
                    new SyncProviderCapabilities(
                        RequiresInteractiveLogin: false,
                        RequiresServerCredentials: true,
                        SupportsResumableUpload: false,
                        SupportsRemoteQuota: false,
                        SupportsRemoteFolderSelection: false,
                        SupportsPersistentAuthentication: true,
                        SupportsConnectionTesting: true,
                        SupportsLogout: true,
                        SupportsOpenRemoteLocation: false),
                    SyncProviderConfigurationSurface.ServerCredentials,
                    UnavailableMessage: null,
                    IsConfigurationAvailable: true),
                new SyncProviderDescriptor(
                    SyncProviderKind.OneDrive,
                    "OneDrive",
                    IsImplemented: true,
                    // Not Google's record: OneDrive is confined to its app
                    // folder (no folder choice, nothing to open) and upload
                    // sessions are not resumed across runs.
                    CloudCapabilities() with
                    {
                        SupportsResumableUpload = false,
                        SupportsRemoteFolderSelection = false,
                        SupportsOpenRemoteLocation = false
                    },
                    SyncProviderConfigurationSurface.InteractiveOAuth,
                    UnavailableMessage: null,
                    IsConfigurationAvailable: true),
                // Catalogued but not implemented. The first MEGA client did not
                // implement MEGA's login or encryption protocol, so it was
                // withdrawn rather than left offering a Connect button that
                // could never succeed. The kind value stays: it is persisted.
                new SyncProviderDescriptor(
                    SyncProviderKind.Mega,
                    "MEGA",
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
                        SupportsOpenRemoteLocation: false),
                    SyncProviderConfigurationSurface.ServerCredentials,
                    "MEGA sync is not implemented yet.",
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
