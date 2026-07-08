using GameSaves.Core.Steam;
using System;
using System.IO;
using System.Linq;
using System.Security;

namespace GameSaves.Infrastructure.Steam
{
    public static class SteamLibraryValidator
    {
        public static SteamLibraryInfo Validate(string libraryPath)
        {
            string steamAppsPath = Path.Combine(libraryPath, "steamapps");
            string commonPath = Path.Combine(steamAppsPath, "common");

            bool hasSteamApps = Directory.Exists(steamAppsPath);
            bool hasCommonFolder = Directory.Exists(commonPath);
            int manifestCount = 0;

            if (hasSteamApps)
            {
                try
                {
                    manifestCount = Directory
                        .EnumerateFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly)
                        .Count();
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is SecurityException)
                {
                    manifestCount = 0;
                }
            }

            return new SteamLibraryInfo(
                libraryPath,
                hasSteamApps,
                hasCommonFolder,
                manifestCount);
        }
    }
}