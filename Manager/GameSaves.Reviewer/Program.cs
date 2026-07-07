namespace GameSaves.Reviewer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            string databasePath = args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0])
                ? args[0]
                : GetDefaultDatabasePath();

            Application.Run(new MainForm(databasePath));
        }

        private static string GetDefaultDatabasePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(
                appData,
                "GameSave",
                "gamesave.db");
        }
    }
}