using Avalonia;

namespace GameSaves.Reviewer
{
    internal static class Program
    {
        public static string DatabasePath { get; private set; } = string.Empty;

        [STAThread]
        public static void Main(string[] args)
        {
            DatabasePath = args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0])
                ? args[0]
                : GetDefaultDatabasePath();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }

        private static string GetDefaultDatabasePath()
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(appData, "GameSave", "gamesave.db");
        }
    }
}
