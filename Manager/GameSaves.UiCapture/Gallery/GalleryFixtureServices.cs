using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.App.Services;
using GameSaves.Core.Platform;
using GameSaves.Core.Steam;
using GameSaves.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// The isolation layer every capture run is built on, shared by both
    /// harnesses so neither can drift from the other's privacy guarantees.
    ///
    /// A capture process changes its working directory to a throwaway folder
    /// and then registers these overrides, so:
    ///
    ///   * the database is a new empty file inside that folder;
    ///   * the UI settings and sync settings files are inside it too, which
    ///     also keeps the real account name out of Settings &gt; Data locations;
    ///   * Steam discovery and its directory-scanning fallback both find
    ///     nothing, so no real game name or install path can be rendered;
    ///   * Google Drive authentication is never consulted, so no OAuth client
    ///     configuration, stored token, or account is read, and no network
    ///     call is possible.
    ///
    /// Relative paths are handed to the stores on purpose: an absolute path
    /// would embed the Windows account name and appear in a screenshot.
    /// </summary>
    public static class GalleryFixtureServices
    {
        public const string DatabaseFileName = "capture.db";
        public const string UiSettingsFileName = "ui-settings.json";
        public const string SyncSettingsFileName = "sync-settings.json";

        /// <summary>
        /// Creates the throwaway working directory and returns it. The caller
        /// owns deletion; it is the only path a capture run may ever delete.
        /// </summary>
        public static string CreateTemporaryRoot(string label)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"gamesave-{label}-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            return root;
        }

        public static void TryDeleteTemporaryRoot(string root)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// The service overrides a capture host installs. <paramref name="isolateSync"/>
        /// is false only for the pre-existing regression harnesses, which have
        /// always used the real sync settings file; gallery runs always isolate
        /// it, because the Sync page is published.
        /// </summary>
        public static void Register(IServiceCollection services, bool isolateSync = true)
        {
            services.AddSingleton<IAppDatabasePathProvider>(
                new GameSaves.Infrastructure.Platform
                    .SchemaInitializingAppDatabasePathProvider(
                        new FixedDatabasePathProvider(DatabaseFileName)));
            services.AddSingleton<ISteamRootLocator>(new NoSteamRootLocator());
            services.AddSingleton<ISteamFallbackScanner>(new NoSteamFallbackScanner());
            services.AddSingleton<IUiSettingsStore>(new UiSettingsStore(UiSettingsFileName));

            if (!isolateSync)
                return;

            services.AddSingleton<ISyncSettingsStore>(
                new SyncSettingsStore(SyncSettingsFileName));
            services.AddSingleton<IGoogleDriveOAuthService>(new OfflineGoogleDriveOAuthService());
            services.AddSingleton<IGoogleDriveRootFolderService>(
                new OfflineGoogleDriveRootFolderService());
        }

        private sealed class FixedDatabasePathProvider : IAppDatabasePathProvider
        {
            private readonly string _path;

            public FixedDatabasePathProvider(string path) => _path = path;

            public string GetDatabasePath() => _path;
        }

        private sealed class NoSteamRootLocator : ISteamRootLocator
        {
            public bool TryLocate(out string steamPath)
            {
                steamPath = string.Empty;
                return false;
            }
        }

        private sealed class NoSteamFallbackScanner : ISteamFallbackScanner
        {
            public SteamFallbackScanResult Scan(
                SteamDiscoveryOptions options,
                IProgress<SteamFallbackScanProgress>? progress = null,
                CancellationToken cancellationToken = default) => new();
        }

        // Reports the client configuration as present, so the Sync page shows
        // the same affordances a configured developer build shows, and refuses
        // every operation that would need the network or a stored token. The
        // real service is never constructed, so nothing on the capture machine
        // is read.
        private sealed class OfflineGoogleDriveOAuthService : IGoogleDriveOAuthService
        {
            private static readonly GoogleDriveAuthenticationResult Offline =
                new(GoogleDriveAuthenticationStatus.NoStoredAuthentication,
                    Message: "Capture harness: Google Drive authentication is never performed.");

            public GoogleDriveOAuthClientConfigurationState GetClientConfigurationState() =>
                new(GoogleDriveOAuthClientConfigurationStatus.Available);

            public Task<GoogleDriveAuthenticationResult> ConnectAsync(
                Guid remoteProfileId, CancellationToken cancellationToken = default) =>
                Task.FromResult(Offline);

            public Task<GoogleDriveAuthenticationResult> RestoreAsync(
                Guid remoteProfileId, CancellationToken cancellationToken = default) =>
                Task.FromResult(Offline);

            public Task<GoogleDriveAuthenticationResult> ReconnectAsync(
                Guid remoteProfileId, CancellationToken cancellationToken = default) =>
                Task.FromResult(Offline);

            public Task<GoogleDriveDisconnectionResult> DisconnectAsync(
                Guid remoteProfileId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new GoogleDriveDisconnectionResult(
                    GoogleDriveDisconnectionStatus.AlreadyDisconnected,
                    LocalAuthenticationRemoved: false,
                    ProfilePreserved: true,
                    AccountMetadataCleared: false));
        }

        private sealed class OfflineGoogleDriveRootFolderService : IGoogleDriveRootFolderService
        {
            private static Task<GoogleDriveRootFolderResult> Unavailable(Guid profileId) =>
                Task.FromResult(new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Unavailable,
                    profileId,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.Unavailable,
                    Message: "Capture harness: Google Drive is never contacted."));

            public Task<GoogleDriveRootFolderResult> InspectAsync(
                Guid remoteProfileId, CancellationToken cancellationToken = default) =>
                Unavailable(remoteProfileId);

            public Task<GoogleDriveRootFolderResult> EnsureAsync(
                Guid remoteProfileId, CancellationToken cancellationToken = default) =>
                Unavailable(remoteProfileId);

            public Task<GoogleDriveRootFolderResult> RecreateAsync(
                Guid remoteProfileId,
                GoogleDriveRootFolderRecreationConfirmation confirmation,
                CancellationToken cancellationToken = default) =>
                Unavailable(remoteProfileId);
        }
    }
}
