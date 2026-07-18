using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameSaves.Reviewer.ViewModels;
using GameSaves.Reviewer.Views;

namespace GameSaves.Reviewer
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(Program.DatabasePath)
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
