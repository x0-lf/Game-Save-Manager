using GameSaves.Core.Platform;

namespace GameSaves.Infrastructure.Platform
{
    public sealed class DefaultAppDatabasePathProvider : IAppDatabasePathProvider
    {
        public string GetDatabasePath()
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(
                appData,
                "GameSave",
                "gamesave.db");
        }
    }
}