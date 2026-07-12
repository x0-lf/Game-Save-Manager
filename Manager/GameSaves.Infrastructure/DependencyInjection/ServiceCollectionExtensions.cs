using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Platform;
using GameSaves.Infrastructure.Profiles;
using GameSaves.Infrastructure.Registry;
using GameSaves.Infrastructure.Save;
using GameSaves.Infrastructure.Steam;
using GameSaves.Infrastructure.Transfers;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Infrastructure.DependencyInjection
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddGameSavesInfrastructure(
            this IServiceCollection services)
        {
            services.AddSingleton<IAppDatabasePathProvider, DefaultAppDatabasePathProvider>();
            services.AddSingleton<ICurrentPlatformProvider, CurrentPlatformProvider>();

            services.AddSingleton<ISteamRootLocator, RegistrySteamLocator>();
            services.AddSingleton<ISteamLibraryFoldersReader, SteamLibraryFoldersReader>();
            services.AddSingleton<ISteamAppManifestReader, SteamAppManifestReader>();
            services.AddSingleton<ISteamFallbackScanner, SteamFallbackScanner>();
            services.AddSingleton<ISteamDiscoveryService, SteamDiscoveryService>();
            services.AddSingleton<ISteamProfileDetector, SteamProfileDetector>();

            services.AddSingleton<ISavePathVerifier, SavePathVerifier>();
            services.AddSingleton<IInstalledGameSaveStatusService, InstalledGameSaveStatusService>();
            services.AddSingleton<ITransferPreviewService, TransferPreviewService>();
            services.AddSingleton<ITransferOverwriteBackupService, TransferOverwriteBackupService>();
            services.AddSingleton<ISaveTransferService, SaveTransferService>();
            services.AddSingleton<IBackupHistoryService, BackupHistoryService>();
            services.AddSingleton<IBackupRestoreService, BackupRestoreService>();
            services.AddSingleton<IManualBackupService, ManualBackupService>();
            services.AddSingleton<IBackupCleanupService, BackupCleanupService>();
            services.AddSingleton<IBackupArchiveService, BackupArchiveService>();
            services.AddSingleton<ISyncProviderFactory, SyncProviderFactory>();

            services.AddSingleton<ISavePathMappingRepository>(provider =>
            {
                IAppDatabasePathProvider pathProvider =
                    provider.GetRequiredService<IAppDatabasePathProvider>();

                return new SqliteSavePathMappingRepository(
                    pathProvider.GetDatabasePath());
            });

            services.AddSingleton<ITransferHistoryRepository>(provider =>
            {
                IAppDatabasePathProvider pathProvider =
                    provider.GetRequiredService<IAppDatabasePathProvider>();

                return new SqliteTransferHistoryRepository(
                    pathProvider.GetDatabasePath());
            });

            services.AddSingleton<IManualBackupPresetRepository>(provider =>
            {
                IAppDatabasePathProvider pathProvider =
                    provider.GetRequiredService<IAppDatabasePathProvider>();

                return new SqliteManualBackupPresetRepository(
                    pathProvider.GetDatabasePath());
            });

            return services;
        }
    }
}