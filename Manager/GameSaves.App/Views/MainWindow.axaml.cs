using Avalonia;
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace GameSaves.App.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Below 900px the navigation rail collapses to icons; styles in
            // MainWindow.axaml key off the compactNav class.
            SizeChanged += (_, e) =>
            {
                bool compact = e.NewSize.Width < 840;

                if (Classes.Contains("compactNav") != compact)
                {
                    if (compact)
                        Classes.Add("compactNav");
                    else
                        Classes.Remove("compactNav");
                }
            };
        }

        // Theme choice is applied at the application level so every window,
        // including future torn-out panels, follows it. "Use system theme"
        // maps to the Default variant, which tracks the OS setting.
        private bool _applyingStoredTheme;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is not ViewModels.MainWindowViewModel viewModel)
                return;

            _applyingStoredTheme = true;
            try
            {
                RadioButton radio = viewModel.ThemeChoice switch
                {
                    Services.AppUiSettings.ThemeLight => ThemeLightRadio,
                    Services.AppUiSettings.ThemeDark => ThemeDarkRadio,
                    _ => ThemeSystemRadio,
                };
                radio.IsChecked = true;
            }
            finally
            {
                _applyingStoredTheme = false;
            }
        }

        private void OnThemeChoiceChanged(object? sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { IsChecked: true } choice ||
                Application.Current is not { } application)
            {
                return;
            }

            (ThemeVariant variant, string name) = choice.Name switch
            {
                "ThemeLightRadio" => (ThemeVariant.Light, Services.AppUiSettings.ThemeLight),
                "ThemeDarkRadio" => (ThemeVariant.Dark, Services.AppUiSettings.ThemeDark),
                _ => (ThemeVariant.Default, Services.AppUiSettings.ThemeSystem),
            };

            application.RequestedThemeVariant = variant;

            if (!_applyingStoredTheme &&
                DataContext is ViewModels.MainWindowViewModel viewModel)
            {
                viewModel.SetThemeChoice(name);
            }
        }
    }
}
