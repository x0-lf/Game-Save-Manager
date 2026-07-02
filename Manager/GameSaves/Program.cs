using GameSave.Backup;
using GameSave.Data;
using GameSave.SavePaths;
using GameSave.External;

using System.Threading.Tasks;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameSave
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dbPath = Path.Combine(appData, "GameSave", "gamesave.db");

            if (args.Length == 0)
            {
                RunDiscoveryTest(useDeepFallbackScan: true);
                WaitForExit();
                return;
            }

            string command = args[0].ToLowerInvariant();

            switch (command)
            {
                case "init-db":
                    InitializeDatabase(dbPath);
                    break;

                case "import":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Usage: import <savepaths.json>");
                        return;
                    }

                    ImportMappings(dbPath, args[1]);
                    break;

                case "discover":
                    RunDiscoveryTest(useDeepFallbackScan: false);
                    break;

                case "discover-deep":
                    RunDiscoveryTest(useDeepFallbackScan: true);
                    break;

                case "verify":
                    RunVerify(dbPath);
                    break;

                case "backup-dry-run":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Usage: backup-dry-run <destination>");
                        return;
                    }

                    RunBackup(dbPath, args[1], dryRun: true);
                    break;

                case "backup":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Usage: backup <destination>");
                        return;
                    }

                    RunBackup(dbPath, args[1], dryRun: false);
                    break;

                //case "pcgw-harvest":
                //    await RunPcgwHarvest(args, dbPath);
                //    break;
                case "pcgw-harvest-appids":
                    await RunPcgwHarvestAppIds(args, dbPath);
                    break;

                case "pcgw-harvest-installed":
                    await RunPcgwHarvestInstalled(args, dbPath);
                    break;

                case "help":
                case "--help":
                case "-h":
                    PrintHelp(dbPath);
                    break;

                default:
                    Console.WriteLine($"Unknown command: {command}");
                    Console.WriteLine();
                    PrintHelp(dbPath);
                    break;
            }
        }

        private static void InitializeDatabase(string dbPath)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            Console.WriteLine($"Database initialized: {dbPath}");
        }

        private static void ImportMappings(string dbPath, string jsonPath)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            database.ImportMappingsFromJson(jsonPath);

            Console.WriteLine($"Imported mappings from: {jsonPath}");
            Console.WriteLine($"Database: {dbPath}");
        }

        private static async Task RunPcgwHarvestAppIds(string[] args, string dbPath)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  pcgw-harvest-appids <output-root> <user-agent> <appid|appid-file> [more-appids]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-appids External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/8\" 413150");
                Console.WriteLine();
                Console.WriteLine("Example with file:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-appids External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/8\" appids.txt");
                return;
            }

            string outputRoot = args[1];
            string userAgent = args[2];

            List<string> appIds = ReadAppIdsFromArguments(args, startIndex: 3);

            if (appIds.Count == 0)
            {
                Console.WriteLine("No valid numeric Steam AppIDs were provided.");
                return;
            }

            var options = new PcgwHarvestOptions
            {
                DatabasePath = dbPath,
                OutputRoot = outputRoot,
                UserAgent = userAgent,
                SteamAppIds = appIds,
                RequestsPerMinute = 20,
                PauseEveryRequests = 100,
                PauseEveryRequestsDuration = TimeSpan.FromMinutes(1),
                MaxTitlesToProcess = 0,
                ImportExtractedMappingsDisabled = true
            };

            var harvester = new PcgwHarvester(options);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            PrintPcgwHarvestResult(result);
        }

        private static async Task RunPcgwHarvestInstalled(string[] args, string dbPath)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  pcgw-harvest-installed <output-root> <user-agent> [max-games]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-installed External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/8\" 10");
                return;
            }

            string outputRoot = args[1];
            string userAgent = args[2];

            int maxGames = 0;

            if (args.Length >= 4)
                int.TryParse(args[3], out maxGames);

            var discoveryService = new SteamDiscoveryService();

            SteamDiscoveryResult discovery = discoveryService.Discover(new SteamDiscoveryOptions
            {
                FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                FallbackTimeout = TimeSpan.FromSeconds(30),
                FallbackMaxDepth = 5
            });

            if (discovery.Games.Count == 0)
            {
                Console.WriteLine("No installed Steam games were discovered.");
                PrintWarnings(discovery);
                return;
            }

            List<string> appIds = discovery.Games
                .Select(game => game.AppId)
                .Where(appId => !string.IsNullOrWhiteSpace(appId))
                .Where(appId => appId.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(appId => int.TryParse(appId, out int parsed) ? parsed : int.MaxValue)
                .ToList();

            if (maxGames > 0)
                appIds = appIds.Take(maxGames).ToList();

            Console.WriteLine($"Installed Steam AppIDs selected for PCGamingWiki harvest: {appIds.Count}");

            var options = new PcgwHarvestOptions
            {
                DatabasePath = dbPath,
                OutputRoot = outputRoot,
                UserAgent = userAgent,
                SteamAppIds = appIds,
                RequestsPerMinute = 20,
                PauseEveryRequests = 100,
                PauseEveryRequestsDuration = TimeSpan.FromMinutes(1),
                MaxTitlesToProcess = 0,
                ImportExtractedMappingsDisabled = true
            };

            var harvester = new PcgwHarvester(options);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            PrintPcgwHarvestResult(result);
        }

        private static List<string> ReadAppIdsFromArguments(string[] args, int startIndex)
        {
            var appIds = new List<string>();

            foreach (string argument in args.Skip(startIndex))
            {
                if (File.Exists(argument))
                {
                    foreach (string line in File.ReadLines(argument))
                    {
                        AddAppIdsFromText(appIds, line);
                    }
                }
                else
                {
                    AddAppIdsFromText(appIds, argument);
                }
            }

            return appIds
                .Where(value => value.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddAppIdsFromText(List<string> appIds, string text)
        {
            foreach (string value in text.Split(
                         new[] { ',', ';', ' ', '\t', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = value.Trim();

                if (trimmed.All(char.IsDigit))
                    appIds.Add(trimmed);
            }
        }

        private static void PrintPcgwHarvestResult(PcgwHarvestResult result)
        {
            Console.WriteLine();
            Console.WriteLine("PCGamingWiki harvest finished:");
            Console.WriteLine($" - AppIDs requested: {result.TitlesIndexed}");
            Console.WriteLine($" - Titles processed: {result.TitlesProcessed}");
            Console.WriteLine($" - Titles failed/missing: {result.TitlesFailed}");
            Console.WriteLine($" - Mappings extracted: {result.MappingsExtracted}");
        }

        //private static async Task RunPcgwHarvest(string[] args, string dbPath)
        //{
        //    if (args.Length < 3)
        //    {
        //        Console.WriteLine("Usage:");
        //        Console.WriteLine("  pcgw-harvest <output-root> <user-agent> [max-titles]");
        //        Console.WriteLine();
        //        Console.WriteLine("Example:");
        //        Console.WriteLine("  dotnet run -- pcgw-harvest External/Titles \"SteamSaveManagerHarvester/0.1 (https://example.org/SteamSaveManager; you@example.org) .NET/8\" 100");
        //        return;
        //    }

        //    string outputRoot = args[1];
        //    string userAgent = args[2];

        //    int maxTitles = 0;

        //    if (args.Length >= 4)
        //        int.TryParse(args[3], out maxTitles);

        //    var options = new PcgwHarvestOptions
        //    {
        //        DatabasePath = dbPath,
        //        OutputRoot = outputRoot,
        //        UserAgent = userAgent,
        //        RequestsPerMinute = 20,
        //        PauseEveryRequests = 100,
        //        PauseEveryRequestsDuration = TimeSpan.FromMinutes(1),
        //        CargoPageSize = 500,
        //        MaxTitlesToProcess = maxTitles,
        //        RefreshTitleIndex = true,
        //        ImportExtractedMappingsDisabled = true
        //    };

        //    var harvester = new PcgwHarvester(options);

        //    PcgwHarvestResult result = await harvester.HarvestAsync();

        //    Console.WriteLine();
        //    Console.WriteLine("PCGamingWiki harvest finished:");
        //    Console.WriteLine($" - Titles indexed: {result.TitlesIndexed}");
        //    Console.WriteLine($" - Titles processed: {result.TitlesProcessed}");
        //    Console.WriteLine($" - Titles failed: {result.TitlesFailed}");
        //    Console.WriteLine($" - Mappings extracted: {result.MappingsExtracted}");
        //}

        private static void RunDiscoveryTest(bool useDeepFallbackScan)
        {
            var discoveryService = new SteamDiscoveryService();

            /*
             * For now, keep discover-deep as your testing command, but use normal discover, verify, and backup with:
             *
             *  FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails
             *
             *  That avoids slow full scans during normal usage.
             */

            SteamFallbackScanMode fallbackMode = useDeepFallbackScan
                ? SteamFallbackScanMode.Always
                : SteamFallbackScanMode.WhenNormalDiscoveryFails;

            SteamDiscoveryResult result = discoveryService.Discover(new SteamDiscoveryOptions
            {
                FallbackScanMode = fallbackMode,
                FallbackTimeout = TimeSpan.FromSeconds(30),
                FallbackMaxDepth = 5
            });

            PrintDetailedDiscovery(result);
        }

        private static void RunVerify(string dbPath)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var discoveryService = new SteamDiscoveryService();

            SteamDiscoveryResult discovery = discoveryService.Discover(new SteamDiscoveryOptions
            {
                FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                FallbackTimeout = TimeSpan.FromSeconds(30),
                FallbackMaxDepth = 5
            });

            PrintShortDiscovery(discovery);

            if (discovery.SteamRoot is null)
            {
                Console.WriteLine("Cannot verify save paths because Steam root was not discovered.");
                return;
            }

            var verifier = new SavePathVerifier();

            foreach (SteamGame game in discovery.Games)
            {
                List<SavePathMapping> mappings = database.GetMappingsForApp(game.AppId, "windows");

                if (mappings.Count == 0)
                    continue;

                Console.WriteLine();
                Console.WriteLine($"Save-path verification for {game.Name} ({game.AppId}):");

                List<SavePathVerificationResult> verificationResults = verifier.Verify(
                    game,
                    discovery.SteamRoot,
                    mappings);

                foreach (SavePathVerificationResult verification in verificationResults)
                {
                    database.SaveVerificationResult(verification);

                    Console.WriteLine($" - {verification.NormalizedPath}");
                    Console.WriteLine($"   Exists: {verification.Exists}");
                    Console.WriteLine($"   Files: {verification.FileCount}");
                    Console.WriteLine($"   Bytes: {verification.TotalBytes}");
                    Console.WriteLine($"   Confidence: {verification.Confidence}");
                    Console.WriteLine($"   Source: {verification.Mapping.SourceName}");

                    if (!string.IsNullOrWhiteSpace(verification.Error))
                        Console.WriteLine($"   Error: {verification.Error}");
                }
            }
        }

        private static void RunBackup(
            string dbPath,
            string destination,
            bool dryRun)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var discoveryService = new SteamDiscoveryService();

            SteamDiscoveryResult discovery = discoveryService.Discover(new SteamDiscoveryOptions
            {
                FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                FallbackTimeout = TimeSpan.FromSeconds(30),
                FallbackMaxDepth = 5
            });

            if (discovery.SteamRoot is null)
            {
                Console.WriteLine("Cannot back up saves because Steam root was not discovered.");
                return;
            }

            var verifier = new SavePathVerifier();
            var allVerifiedPaths = new List<SavePathVerificationResult>();

            foreach (SteamGame game in discovery.Games)
            {
                List<SavePathMapping> mappings = database.GetMappingsForApp(game.AppId, "windows");

                if (mappings.Count == 0)
                    continue;

                List<SavePathVerificationResult> verified = verifier.Verify(
                    game,
                    discovery.SteamRoot,
                    mappings);

                foreach (SavePathVerificationResult verification in verified)
                {
                    database.SaveVerificationResult(verification);
                    allVerifiedPaths.Add(verification);
                }
            }

            var backupManager = new BackupManager(database);

            List<BackupItemResult> backupResults = backupManager.BackupVerifiedPaths(
                allVerifiedPaths,
                destination,
                dryRun,
                computeHashes: true);

            Console.WriteLine(dryRun ? "Dry-run backup plan:" : "Backup result:");

            foreach (BackupItemResult item in backupResults)
            {
                Console.WriteLine($" - {item.SourcePath}");
                Console.WriteLine($"   -> {item.DestinationPath}");
                Console.WriteLine($"   Copied: {item.Copied}");
                Console.WriteLine($"   Bytes: {item.Bytes}");
                Console.WriteLine($"   SHA256: {item.Sha256 ?? "not computed"}");

                if (!string.IsNullOrWhiteSpace(item.Error))
                    Console.WriteLine($"   Error: {item.Error}");
            }

            Console.WriteLine();
            Console.WriteLine($"Items: {backupResults.Count}");
            Console.WriteLine($"Total bytes: {backupResults.Sum(item => item.Bytes)}");
        }

        private static void PrintDetailedDiscovery(SteamDiscoveryResult result)
        {
            if (result.SteamRoot is null)
            {
                Console.WriteLine("Steam was not found.");
                PrintWarnings(result);
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
        }

        private static void PrintShortDiscovery(SteamDiscoveryResult result)
        {
            if (result.SteamRoot is null)
            {
                Console.WriteLine("Steam was not found.");
                PrintWarnings(result);
                return;
            }

            Console.WriteLine($"Steam root: {result.SteamRoot}");
            Console.WriteLine($"Libraries: {result.Libraries.Count}");
            Console.WriteLine($"Games: {result.Games.Count}");
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

        private static void PrintHelp(string dbPath)
        {
            Console.WriteLine("Steam Save-Game Manager");
            Console.WriteLine();
            Console.WriteLine($"Database: {dbPath}");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  init-db");
            Console.WriteLine("  import <savepaths.json>");
            Console.WriteLine("  discover");
            Console.WriteLine("  discover-deep");
            Console.WriteLine("  verify");
            Console.WriteLine("  backup-dry-run <destination>");
            Console.WriteLine("  backup <destination>");
            //Console.WriteLine("  pcgw-harvest <output-root> <user-agent> [max-titles]");
            Console.WriteLine("  pcgw-harvest-appids <output-root> <user-agent> <appid|appid-file> [more-appids]");
            Console.WriteLine("  pcgw-harvest-installed <output-root> <user-agent> [max-games]");
            Console.WriteLine();
            Console.WriteLine("No arguments:");
            Console.WriteLine("  Runs your current detailed discovery test with fallback scan enabled.");
        }
        private static void WaitForExit()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
        }
    }
}