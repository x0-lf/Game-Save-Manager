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
        private AppUiSettings? _uiSettings;
        private WindowMaterialService? _windowMaterial;

        // Retained so the fire-and-start initialization task is observed rather
        // than left to the finalizer. InitializeAllAsync never faults (it
        // isolates per-ViewModel failures internally), so this task completes
        // cleanly even when individual tabs fail to load.
        private Task? _startupTask;

        // Detached windows are created by the view layer without DI access;
        // they register with the material service through this ambient
        // accessor instead. Null before startup completes or in hosts that
        // never build the desktop lifetime.
        internal static WindowMaterialService? CurrentWindowMaterial =>
            Application.Current is App app ? app._windowMaterial : null;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            _serviceProvider = AppServices.Build();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _uiSettings = _serviceProvider
                    .GetRequiredService<IUiSettingsStore>()
                    .Load();

                RequestedThemeVariant = _uiSettings.ThemeChoice switch
                {
                    AppUiSettings.ThemeLight => Avalonia.Styling.ThemeVariant.Light,
                    AppUiSettings.ThemeDark => Avalonia.Styling.ThemeVariant.Dark,
                    _ => Avalonia.Styling.ThemeVariant.Default,
                };

                // Accent palette and transparency are applied after the
                // variant so the overrides are computed against it. The
                // window material is applied after the theme so its hint is
                // in place before the main window is created.
                _serviceProvider.GetRequiredService<ThemeService>().Apply(_uiSettings);
                _windowMaterial = _serviceProvider
                    .GetRequiredService<WindowMaterialService>();
                _windowMaterial.Apply(_uiSettings);

                desktop.MainWindow = new Views.MainWindow
                {
                    DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>()
                };

                // The material hint is given to the window itself (rather
                // than only to the app resources) so the platform can start
                // compositing as soon as the window exists.
                _windowMaterial.Attach(desktop.MainWindow);

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
