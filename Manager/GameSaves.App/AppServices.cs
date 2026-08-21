using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace GameSaves.App
{
    public static class AppServices
    {
        public static ServiceProvider Build()
        {
            return Build(overrides: null);
        }

        // The overrides hook exists for development-only hosts (the UI capture
        // harness) that must never touch the real database, registry-located
        // Steam installation, or secret store. Last registration wins.
        public static ServiceProvider Build(
            Action<IServiceCollection>? overrides)
        {
            var services = new ServiceCollection();

            services.AddGameSavesInfrastructure();

            services.AddSingleton<IFolderPickerService, FolderPickerService>();
            services.AddSingleton<IUiSettingsStore, UiSettingsStore>();
            services.AddSingleton<ISyncSettingsStore, SyncSettingsStore>();
            services.AddSingleton<ISyncRemoteProfileMigrationService, SyncRemoteProfileMigrationService>();

            services.AddSingleton<MainWindowViewModel>();

            services.AddSingleton<InstalledGamesViewModel>();
            services.AddSingleton<ProfilesViewModel>();
            services.AddSingleton<TransferPreviewViewModel>();
            services.AddSingleton<BackupHistoryViewModel>();
            services.AddSingleton<ManualBackupViewModel>();
            services.AddSingleton<TransferHistoryViewModel>();
            services.AddSingleton<SyncViewModel>();

           // The Sync tab
           // is intentionally absent - it manages its own connection, OAuth,
           // and remote - profile state and must not be driven by startup loading.
           services.AddSingleton<IStartupInitializer>(provider =>
                new StartupInitializer(new IInitializableViewModel[]
                {
                    provider.GetRequiredService<MainWindowViewModel>(),
                    provider.GetRequiredService<InstalledGamesViewModel>(),
                    provider.GetRequiredService<ProfilesViewModel>(),
                    provider.GetRequiredService<TransferPreviewViewModel>(),
                    provider.GetRequiredService<ManualBackupViewModel>(),
                    provider.GetRequiredService<BackupHistoryViewModel>(),
                    provider.GetRequiredService<TransferHistoryViewModel>(),
                }));

            overrides?.Invoke(services);

            return services.BuildServiceProvider();
        }
    }
}
