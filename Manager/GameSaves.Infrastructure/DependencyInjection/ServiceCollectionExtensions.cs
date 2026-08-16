using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Secrets;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Platform;
using GameSaves.Infrastructure.Profiles;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Registry;
using GameSaves.Infrastructure.Save;
using GameSaves.Infrastructure.Secrets;
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
            services.AddSingleton<ISyncProviderCatalog, SyncProviderCatalog>();
            services.AddSingleton<ISyncProviderFactory, SyncProviderFactory>();
            services.AddSingleton<IUtcClock, SystemUtcClock>();
            services.AddSingleton<SyncRemoteProfileSettingsSerializer>();

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

            services.AddSingleton<ISyncRemoteProfileRepository>(provider =>
            {
                IAppDatabasePathProvider pathProvider =
                    provider.GetRequiredService<IAppDatabasePathProvider>();
                SyncRemoteProfileSettingsSerializer serializer =
                    provider.GetRequiredService<SyncRemoteProfileSettingsSerializer>();

                return new SqliteSyncRemoteProfileRepository(
                    pathProvider.GetDatabasePath(),
                    serializer);
            });

            services.AddSingleton<ISecretStore>(provider =>
            {
                IAppDatabasePathProvider pathProvider =
                    provider.GetRequiredService<IAppDatabasePathProvider>();
                IUtcClock clock = provider.GetRequiredService<IUtcClock>();

                return new WindowsDpapiSecretStore(
                    pathProvider.GetDatabasePath(),
                    clock);
            });
            services.AddSingleton<IGoogleDriveValidationCoordinator,
                GoogleDriveValidationCoordinator>();
            services.AddSingleton<ISyncRemoteProfileService>(provider =>
                new SyncRemoteProfileService(
                    provider.GetRequiredService<ISyncRemoteProfileRepository>(),
                    provider.GetRequiredService<ISecretStore>(),
                    provider.GetRequiredService<IGoogleDriveValidationCoordinator>()));
            services.AddSingleton<
                IGoogleDriveConnectionSettingsService,
                GoogleDriveConnectionSettingsService>();
            services.AddSingleton<IGoogleOAuthClientConfigurationProvider,
                EnvironmentGoogleOAuthClientConfigurationProvider>();
            services.AddSingleton<IGoogleSecretDataStoreFactory,
                GoogleSecretDataStoreFactory>();
            services.AddSingleton<IGoogleInstalledAppAuthorizer,
                GoogleInstalledAppAuthorizer>();
            services.AddSingleton<IGoogleDriveAccountReader,
                GoogleDriveAccountReader>();
            services.AddSingleton<IGoogleDriveAuthorizedSessionFactory,
                GoogleDriveAuthorizedSessionFactory>();
            services.AddSingleton<IGoogleDriveRootFolderApi,
                GoogleDriveRootFolderApi>();
            services.AddSingleton<IGoogleDriveRootMembershipApi>(provider =>
                (IGoogleDriveRootMembershipApi)provider.GetRequiredService<
                    IGoogleDriveRootFolderApi>());
            services.AddSingleton<IGoogleDriveRootValidationClientFactory,
                GoogleDriveRootValidationClientFactory>();
            services.AddSingleton<IGoogleDriveRootValidationApi,
                GoogleDriveRootValidationApi>();
            services.AddSingleton<IGoogleDriveRemoteValidationService,
                GoogleDriveRemoteValidationService>();
            services.AddSingleton<IGoogleDriveRemoteFileSystemFactory,
                GoogleDriveRemoteFileSystemFactory>();
            services.AddSingleton<GoogleDriveQueryBuilder>();
            services.AddSingleton<IGoogleDriveObjectClientFactory,
                GoogleDriveObjectClientFactory>();
            services.AddSingleton<GoogleDriveObjectApi>();
            services.AddSingleton<IGoogleDriveObjectApi>(provider =>
                provider.GetRequiredService<GoogleDriveObjectApi>());
            services.AddSingleton<IGoogleDriveObjectListingApi>(provider =>
                provider.GetRequiredService<GoogleDriveObjectApi>());
            services.AddSingleton<IGoogleDriveTextContentClientFactory,
                GoogleDriveTextContentClientFactory>();
            services.AddSingleton<IGoogleDriveTextContentApi,
                GoogleDriveTextContentApi>();
            services.AddSingleton<IGoogleDriveTextCreationClientFactory,
                GoogleDriveTextCreationClientFactory>();
            services.AddSingleton<IGoogleDriveTextCreationApi,
                GoogleDriveTextCreationApi>();
            services.AddSingleton<IGoogleDriveTextReplacementClientFactory,
                GoogleDriveTextReplacementClientFactory>();
            services.AddSingleton<IGoogleDriveTextReplacementApi,
                GoogleDriveTextReplacementApi>();
            services.AddSingleton<IGoogleDriveTextFileReadService,
                GoogleDriveTextFileReadService>();
            services.AddSingleton<IGoogleDriveProviderMetadataReadService,
                GoogleDriveProviderMetadataReadService>();
            services.AddSingleton<GoogleDriveProviderMetadataReplacementCoordinator>();
            services.AddSingleton<IGoogleDriveProviderMetadataReplacementService,
                GoogleDriveProviderMetadataReplacementService>();
            services.AddSingleton<IGoogleDriveCreateOnlyTextFileService,
                GoogleDriveCreateOnlyTextFileService>();
            services.AddSingleton<IGoogleDriveObjectIdCache,
                GoogleDriveObjectIdCache>();
            services.AddSingleton<GoogleDriveObjectCreationCoordinator>();
            services.AddSingleton<IGoogleDriveObjectPathResolverFactory,
                GoogleDriveObjectPathResolverFactory>();
            services.AddSingleton<IGoogleDriveRemoteOperationContextFactory,
                GoogleDriveRemoteOperationContextFactory>();
            services.AddSingleton<IGoogleDriveRootExistenceService,
                GoogleDriveRootExistenceService>();
            services.AddSingleton<IGoogleDriveFolderExistenceService,
                GoogleDriveFolderExistenceService>();
            services.AddSingleton<IGoogleDriveRunFolderDiscoveryService,
                GoogleDriveRunFolderDiscoveryService>();
            services.AddSingleton<IGoogleDriveRunFolderNameService,
                GoogleDriveRunFolderNameService>();
            services.AddSingleton<IGoogleDriveFolderChildEnumerationService,
                GoogleDriveFolderChildEnumerationService>();
            services.AddSingleton<IGoogleDriveOneLevelFileListingService,
                GoogleDriveOneLevelFileListingService>();
            services.AddSingleton<IGoogleDriveRunFolderResolver,
                GoogleDriveRunFolderResolver>();
            services.AddSingleton<IGoogleDriveRecursiveFileListingService,
                GoogleDriveRecursiveFileListingService>();
            services.AddSingleton<GoogleDriveCreateOnlyUploadTargetGuard>();
            services.AddSingleton<GoogleDriveUploadParentPreparationService>();
            services.AddSingleton<GoogleDriveLocalUploadSourceOpener>();
            services.AddSingleton<IGoogleDriveMediaUploadClientFactory,
                GoogleDriveMediaUploadClientFactory>();
            services.AddSingleton<IGoogleDriveBinaryUploadService>(provider =>
                new GoogleDriveBinaryUploadService(
                    provider.GetRequiredService<GoogleDriveLocalUploadSourceOpener>()
                        .OpenAsync,
                    provider.GetRequiredService<
                        IGoogleDriveRemoteOperationContextFactory>(),
                    provider.GetRequiredService<
                        GoogleDriveUploadParentPreparationService>(),
                    provider.GetRequiredService<
                        GoogleDriveCreateOnlyUploadTargetGuard>(),
                    provider.GetRequiredService<
                        IGoogleDriveMediaUploadClientFactory>(),
                    provider.GetRequiredService<IGoogleDriveObjectIdCache>()));
            services.AddSingleton<IGoogleDriveRootFolderService>(provider =>
                new GoogleDriveRootFolderService(
                    provider.GetRequiredService<ISyncRemoteProfileRepository>(),
                    provider.GetRequiredService<ISecretStore>(),
                    provider.GetRequiredService<IGoogleDriveAuthorizedSessionFactory>(),
                    provider.GetRequiredService<IGoogleDriveRootFolderApi>(),
                    provider.GetRequiredService<IUtcClock>(),
                    provider.GetRequiredService<IGoogleDriveObjectIdCache>(),
                    provider.GetRequiredService<IGoogleDriveValidationCoordinator>()));
            services.AddSingleton<IGoogleDriveOAuthService>(provider =>
                new GoogleDriveOAuthService(
                    provider.GetRequiredService<ISyncRemoteProfileRepository>(),
                    provider.GetRequiredService<ISecretStore>(),
                    provider.GetRequiredService<IGoogleOAuthClientConfigurationProvider>(),
                    provider.GetRequiredService<IGoogleSecretDataStoreFactory>(),
                    provider.GetRequiredService<IGoogleInstalledAppAuthorizer>(),
                    provider.GetRequiredService<IGoogleDriveAccountReader>(),
                    provider.GetRequiredService<IUtcClock>(),
                    provider.GetRequiredService<IGoogleDriveObjectIdCache>(),
                    provider.GetRequiredService<IGoogleDriveValidationCoordinator>()));

            return services;
        }
    }
}
