using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Infrastructure.Platform;
using GameSaves.Infrastructure.Profiles;
using GameSaves.Infrastructure.Registry;
using GameSaves.Infrastructure.Save;
using GameSaves.Infrastructure.Steam;
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

            services.AddSingleton<ISavePathMappingRepository>(provider =>
            {
                IAppDatabasePathProvider pathProvider =
                    provider.GetRequiredService<IAppDatabasePathProvider>();

                return new SqliteSavePathMappingRepository(
                    pathProvider.GetDatabasePath());
            });

            return services;
        }
    }
}