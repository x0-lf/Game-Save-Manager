using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;
        private CancellationTokenSource? _startupCts;

        // Retained so the fire-and-start initialization task is observed rather
        // than left to the finalizer. InitializeAllAsync never faults (it
        // isolates per-ViewModel failures internally), so this task completes
        // cleanly even when individual tabs fail to load.
        private Task? _startupTask;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            _serviceProvider = AppServices.Build();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                string storedTheme = _serviceProvider
                    .GetRequiredService<IUiSettingsStore>()
                    .Load().ThemeChoice;

                RequestedThemeVariant = storedTheme switch
                {
                    AppUiSettings.ThemeLight => Avalonia.Styling.ThemeVariant.Light,
                    AppUiSettings.ThemeDark => Avalonia.Styling.ThemeVariant.Dark,
                    _ => Avalonia.Styling.ThemeVariant.Default,
                };

                desktop.MainWindow = new Views.MainWindow
                {
                    DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>()
                };

                _startupCts = new CancellationTokenSource();

                // Cancel any in-flight startup loading when the app is closing,
                // and dispose the DI container (and its singletons) on exit.
                desktop.ShutdownRequested += (_, _) => _startupCts?.Cancel();
                desktop.Exit += (_, _) =>
                {
                    _startupCts?.Cancel();
                    _startupCts?.Dispose();
                    _serviceProvider?.Dispose();
                };

                // Kick off data loading after the window is assigned. The first
                // await inside the coordinator yields to the UI thread, so the
                // window renders while data streams in tab by tab.
                IStartupInitializer initializer =
                    _serviceProvider.GetRequiredService<IStartupInitializer>();

                _startupTask = initializer.InitializeAllAsync(_startupCts.Token);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
