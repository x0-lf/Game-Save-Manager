using GameSaves.App.ViewModels;
using GameSaves.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.App
{
    public static class AppServices
    {
        public static ServiceProvider Build()
        {
            var services = new ServiceCollection();

            services.AddGameSavesInfrastructure();

            services.AddSingleton<MainWindowViewModel>();

            services.AddSingleton<InstalledGamesViewModel>();
            services.AddSingleton<ProfilesViewModel>();
            services.AddSingleton<TransferPreviewViewModel>();
            services.AddSingleton<BackupHistoryViewModel>();

            return services.BuildServiceProvider();
        }
    }
}