using System;

namespace GameSave
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var discoveryService = new SteamDiscoveryService();
            SteamDiscoveryResult result = discoveryService.Discover();

            if (result.SteamRoot is null)
            {
                Console.WriteLine("Steam was not found.");
                PrintWarnings(result);
                WaitForExit();
                return;
            }

            Console.WriteLine($"Steam root: {result.SteamRoot}");
            Console.WriteLine();

            if (result.SteamRootValidation is not null)
            {
                Console.WriteLine("Steam root validation:");
                Console.WriteLine($" - steam.exe: {(result.SteamRootValidation.HasSteamExe ? "OK" : "Missing")}");
                Console.WriteLine($" - steam.dll: {(result.SteamRootValidation.HasSteamDll ? "OK" : "Missing")}");
                Console.WriteLine($" - steamapps: {(result.SteamRootValidation.HasSteamAppsDirectory ? "OK" : "Missing")}");
                Console.WriteLine($" - config: {(result.SteamRootValidation.HasConfigDirectory ? "OK" : "Missing")}");
                Console.WriteLine();
            }

            Console.WriteLine("Steam libraries:");

            if (result.Libraries.Count == 0)
            {
                Console.WriteLine("No valid Steam libraries found.");
            }
            else
            {
                foreach (SteamLibraryInfo library in result.Libraries)
                {
                    Console.WriteLine($"Library: {library.LibraryPath}");
                    Console.WriteLine($" - steamapps: {(library.HasSteamApps ? "OK" : "Missing")}");
                    Console.WriteLine($" - common folder: {(library.HasCommonFolder ? "OK" : "Missing")}");
                    Console.WriteLine($" - manifests: {library.ManifestCount} found");
                    Console.WriteLine();
                }
            }

            Console.WriteLine("Installed Steam games:");

            if (result.Games.Count == 0)
            {
                Console.WriteLine("No installed games found from app manifests.");
            }
            else
            {
                foreach (SteamGame game in result.Games)
                {
                    Console.WriteLine($"Game: {game.Name}");
                    Console.WriteLine($" - AppId: {game.AppId}");
                    Console.WriteLine($" - Library: {game.LibraryPath}");
                    Console.WriteLine($" - Manifest: {game.ManifestPath}");
                    Console.WriteLine($" - Folder: {(game.FolderExists ? "OK" : "Missing")}");
                    Console.WriteLine($" - Confidence: {game.Confidence}");
                    Console.WriteLine();
                }
            }

            PrintWarnings(result);
            WaitForExit();
        }

        private static void PrintWarnings(SteamDiscoveryResult result)
        {
            if (result.Warnings.Count == 0)
                return;

            Console.WriteLine();
            Console.WriteLine("Warnings:");

            foreach (string warning in result.Warnings)
                Console.WriteLine($" - {warning}");
        }

        private static void WaitForExit()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
        }
    }
}