using System.Threading.Tasks;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using GameSaves.External;
using GameSaves.External.Steam;

using GameSaves.Core.Catalog;
using GameSaves.Core.Data;
using GameSaves.Core.Steam;
using GameSaves.Core.Save;
using GameSaves.Core.Backup;

using GameSaves.Infrastructure.Catalog;
using GameSaves.Infrastructure.Data;
using GameSaves.Infrastructure.Save;
using GameSaves.Infrastructure.Backup;
using GameSaves.Infrastructure.Steam;
using Microsoft.Data.Sqlite;
using System.Security;

namespace GameSaves
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            try
            {
                await RunAsync(args);
            }
            catch (Exception ex) when (
                ex is SqliteException ||
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException)
            {
                string reason = ex switch
                {
                    SqliteException => "SQLite could not open or update the application database.",
                    UnauthorizedAccessException or SecurityException =>
                        "The command does not have permission to access a required file or directory.",
                    _ => "A required file or directory could not be accessed."
                };

                Console.Error.WriteLine($"Command failed: {reason}");
                Environment.ExitCode = 1;
            }
        }

        private static async Task RunAsync(string[] args)
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

                case "migrate":
                case "migrate-status":
                case "migrate-dry-run":
                    // An option such as --help is not a database path: `migrate`
                    // would create a database file of that name.
                    if (args.Length >= 2 && args[1].StartsWith('-'))
                    {
                        UsageError($"Usage: {command} [dbPath]");
                        return;
                    }

                    string targetDb = args.Length >= 2 ? args[1] : dbPath;
                    if (command == "migrate")
                        RunMigrate(targetDb);
                    else if (command == "migrate-status")
                        RunMigrateStatus(targetDb);
                    else
                        RunMigrateDryRun(targetDb);
                    break;

                case "seed-curated":
                    if (args.Length >= 2)
                    {
                        // Only the bundled dataset is seeded as Approved. An external
                        // file goes through the validated, explicit import path.
                        UsageError(
                            "Usage: seed-curated",
                            "To add mappings from a file, use: import <file.json> [--approve]");
                        return;
                    }

                    SeedCurated(dbPath);
                    break;

                case "import":
                case "import-json":
                    if (args.Length < 2)
                    {
                        UsageError("Usage: import <savepaths.json> [--approve]");
                        return;
                    }

                    bool autoApprove = args.Length >= 3 && (args[2].Equals("--approve", StringComparison.OrdinalIgnoreCase) || args[2].Equals("-a", StringComparison.OrdinalIgnoreCase));
                    ImportMappings(dbPath, args[1], autoApprove);
                    break;

                case "approve-mapping":
                    if (args.Length < 2 || !long.TryParse(args[1], out long mappingId))
                    {
                        UsageError("Usage: approve-mapping <id> [notes]");
                        return;
                    }

                    string? mappingNotes = args.Length >= 3 ? args[2] : null;
                    ApproveMapping(dbPath, mappingId, mappingNotes);
                    break;

                case "approve-app":
                    if (args.Length < 2)
                    {
                        UsageError("Usage: approve-app <steamAppId> [notes]");
                        return;
                    }

                    string? appNotes = args.Length >= 3 ? args[2] : null;
                    ApproveApp(dbPath, args[1], appNotes);
                    break;

                case "migrate-legacy-mappings":
                    bool trustLegacy = args.Length >= 2 && args[1].Equals("--approve-all", StringComparison.OrdinalIgnoreCase);
                    MigrateLegacyMappings(dbPath, trustLegacy);
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
                        UsageError("Usage: backup-dry-run <destination>");
                        return;
                    }

                    RunBackup(dbPath, args[1], dryRun: true);
                    break;

                case "backup":
                    if (args.Length < 2)
                    {
                        UsageError("Usage: backup <destination>");
                        return;
                    }

                    RunBackup(dbPath, args[1], dryRun: false);
                    break;

                case "pcgw-harvest-tracklist":
                case "pcgw-harvest":
                    await RunPcgwHarvestTracklist(args, dbPath);
                    break;

                case "pcgw-harvest-appids":
                    await RunPcgwHarvestAppIds(args, dbPath);
                    break;

                case "pcgw-harvest-installed":
                    await RunPcgwHarvestInstalled(args, dbPath);
                    break;

                case "steam-catalog-fetch":
                    await RunSteamCatalogFetch(args, dbPath);
                    break;

                case "steam-catalog-missing":
                    await RunSteamCatalogMissing(args, dbPath);
                    break;

                case "steam-catalog-queue-missing":
                    RunSteamCatalogQueueMissing(dbPath);
                    break;

                case "steam-catalog-export-next":
                    await RunSteamCatalogExportNext(args, dbPath);
                    break;

                case "tracklist":
                case "missing-titles":
                case "export-missing":
                    RunTracklist(args, dbPath);
                    break;

                case "ai-detect-paths":
                case "ai-detect":
                    await RunAiDetectPaths(args, dbPath);
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
                    Environment.ExitCode = ExitCodeUsage;
                    break;
            }
        }

        private static void InitializeDatabase(string dbPath)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var seeder = new CuratedMappingSeeder();
            CuratedSeedResult result = seeder.Seed(dbPath);

            Console.WriteLine($"Database initialized: {dbPath}");
            Console.WriteLine($"Curated mappings seeded: {result.Inserted} inserted, {result.Updated} updated, {result.Unchanged} unchanged, {result.SkippedUserOverrides} user overrides preserved.");
        }

        private static void RunMigrate(string dbPath)
        {
            var migrator = new SchemaMigrator();
            Console.WriteLine($"Running schema migrations for: {dbPath}");

            MigrationExecutionResult result = migrator.Migrate(dbPath);
            if (!result.Success)
            {
                Console.Error.WriteLine($"Migration failed: {result.ErrorMessage}");
                if (result.RolledBack)
                {
                    Console.WriteLine($"All pending migrations were rolled back; the database schema is unchanged at version {result.CurrentVersion}.");
                }
                if (!string.IsNullOrWhiteSpace(result.PreMigrationBackupPath))
                {
                    Console.WriteLine($"Pre-migration snapshot (manual recovery point, without stored sync secrets): {result.PreMigrationBackupPath}");
                }
                Environment.ExitCode = 1;
                return;
            }

            if (result.AppliedMigrations.Count == 0)
            {
                Console.WriteLine($"Database schema is already up to date (version {result.CurrentVersion}). No migrations applied.");
            }
            else
            {
                Console.WriteLine($"Migration completed successfully. Version upgraded from {result.PreviousVersion} to {result.CurrentVersion}.");
                Console.WriteLine($"Applied migrations ({result.AppliedMigrations.Count}):");
                foreach (string migration in result.AppliedMigrations)
                {
                    Console.WriteLine($"  - {migration}");
                }
                if (!string.IsNullOrWhiteSpace(result.PreMigrationBackupPath))
                {
                    Console.WriteLine($"Pre-migration snapshot saved (without stored sync secrets): {result.PreMigrationBackupPath}");
                }
            }
        }

        private static void RunMigrateStatus(string dbPath)
        {
            var migrator = new SchemaMigrator();
            Console.WriteLine($"Database: {dbPath}");

            if (!File.Exists(dbPath))
            {
                Console.WriteLine("Database file does not exist yet.");
                return;
            }

            IReadOnlyList<SchemaMigrationRecord> applied = migrator.GetAppliedMigrations(dbPath);
            int currentVersion = applied.Count > 0 ? applied.Max(m => m.Id) : 0;
            Console.WriteLine($"Current schema version: {currentVersion}");
            Console.WriteLine($"Applied migrations ({applied.Count}):");
            foreach (SchemaMigrationRecord record in applied)
            {
                Console.WriteLine($"  - [{record.Id}] {record.Name} (applied: {record.AppliedUtc:yyyy-MM-dd HH:mm:ss 'UTC'})");
            }

            MigrationPlan plan = migrator.Plan(dbPath);
            Console.WriteLine($"Pending migrations ({plan.PendingMigrations.Count}):");
            foreach (SchemaMigrationInfo pending in plan.PendingMigrations)
            {
                Console.WriteLine($"  - [{pending.Version}] {pending.Name} - {pending.Description}");
            }
        }

        private static void RunMigrateDryRun(string dbPath)
        {
            var migrator = new SchemaMigrator();
            Console.WriteLine($"Dry-run migration plan for: {dbPath}");

            MigrationPlan plan = migrator.Plan(dbPath);
            Console.WriteLine($"Integrity check: {(plan.IntegrityCheckPassed ? "OK" : $"FAILED ({plan.IntegrityMessage})")}");
            if (!plan.IntegrityCheckPassed)
                Environment.ExitCode = 1;
            Console.WriteLine($"Current version: {plan.CurrentVersion}");
            Console.WriteLine($"Target version: {plan.TargetVersion}");

            if (plan.PendingMigrations.Count == 0)
            {
                Console.WriteLine("Database is already at target version. No migrations to apply.");
            }
            else
            {
                Console.WriteLine($"Pending migrations to apply ({plan.PendingMigrations.Count}):");
                foreach (SchemaMigrationInfo pending in plan.PendingMigrations)
                {
                    Console.WriteLine($"  - [{pending.Version}] {pending.Name}: {pending.Description}");
                }
                if (!string.IsNullOrWhiteSpace(plan.PlannedBackupDirectory))
                {
                    Console.WriteLine($"A pre-migration snapshot would be written to: {plan.PlannedBackupDirectory}");
                }
            }
        }

        private static void SeedCurated(string dbPath)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            CuratedSeedResult result = new CuratedMappingSeeder().Seed(dbPath);
            Console.WriteLine("Seeded curated mappings from bundled assembly seed dataset.");

            Console.WriteLine($"Database: {dbPath}");
            Console.WriteLine($"Summary: {result.TotalProcessed} processed — {result.Inserted} inserted, {result.Updated} updated, {result.Unchanged} unchanged, {result.SkippedUserOverrides} user overrides preserved.");
        }

        private static void ImportMappings(string dbPath, string jsonPath, bool autoApprove)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var service = new MappingImportService();
            var options = new MappingImportOptions { AutoApprove = autoApprove };

            MappingImportReport report = service.ImportFile(dbPath, jsonPath, options);
            int written = report.MappingsInserted + report.MappingsUpdated;

            Console.WriteLine($"Imported from: {jsonPath}");
            Console.WriteLine(autoApprove
                ? $"Status: {written} mapping(s) written as Approved and enabled (explicit --approve)."
                : $"Status: {written} mapping(s) written as Pending review (disabled until approved).");
            if (!report.Success)
                Console.WriteLine($"{report.Errors.Count} item(s) were rejected and not imported; see the errors below.");
            Console.WriteLine($"Database: {dbPath}");
            Console.WriteLine();
            Console.WriteLine(report.FormatSummary());

            if (!report.Success)
            {
                Environment.ExitCode = 1;
            }
        }

        private static void ApproveMapping(string dbPath, long id, string? notes)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();
            database.ApproveMapping(id, notes);
            Console.WriteLine($"Mapping {id} has been marked Approved and enabled.");
        }

        private static void ApproveApp(string dbPath, string steamAppId, string? notes)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            int approved = database.ApproveMappingsForApp(steamAppId, notes);

            if (approved == 0)
            {
                Console.WriteLine($"No mappings were approved for AppID {steamAppId}.");
                Console.WriteLine("Either the game has no mappings, they are already approved, or every one of them was rejected during review.");
                return;
            }

            Console.WriteLine($"{approved} mapping(s) for AppID {steamAppId} are now Approved and enabled.");
            Console.WriteLine("Mappings that a reviewer rejected were left untouched.");
        }

        // A CLI that returns success after doing nothing cannot be scripted against.
        private static void UsageError(params string[] lines)
        {
            foreach (string line in lines)
                Console.WriteLine(line);

            Environment.ExitCode = ExitCodeUsage;
        }

        private static bool TryReadCountArgument(string value, string name, out int parsed)
        {
            if (int.TryParse(value, out parsed) && parsed >= 0)
                return true;

            // Falling back to the default here would silently run with a limit the
            // caller never asked for: int.TryParse leaves 0 behind on failure, and
            // 0 means "no limit".
            UsageError($"Invalid {name} \"{value}\": expected a whole number of 0 or more.");
            return false;
        }

        private const int ExitCodeUsage = 2;

        private static void MigrateLegacyMappings(string dbPath, bool trustLegacy)
        {
            var database = new SavePathDatabase(dbPath);
            database.Initialize();
            int migrated = database.MigrateLegacyMappings(trustLegacyEnabledAsApproved: trustLegacy);
            Console.WriteLine($"Legacy mapping migration completed: {migrated} rows updated (TrustLegacyAsApproved: {trustLegacy}).");
        }

        private static void RunTracklist(string[] args, string dbPath)
        {
            string? outputPath = null;
            TracklistExportFormat? format = null;
            bool installedOnly = false;
            MissingTitleResearchStatus? statusFilter = null;
            string? minPriority = null;
            int? limit = null;
            string platform = "windows";
            string? candidatesPath = null;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                string? NextValue()
                {
                    if (i + 1 < args.Length)
                        return args[++i];

                    UsageError($"Missing value for tracklist option {arg}.");
                    return null;
                }

                switch (arg.ToLowerInvariant())
                {
                    case "-o":
                    case "--output":
                        if ((outputPath = NextValue()) is null)
                            return;
                        break;

                    case "-f":
                    case "--format":
                        string? fmt = NextValue();
                        if (fmt is null)
                            return;

                        format = fmt.ToLowerInvariant() switch
                        {
                            "csv" => TracklistExportFormat.Csv,
                            "json" => TracklistExportFormat.Json,
                            _ => null
                        };

                        if (format is null)
                        {
                            UsageError($"Invalid --format \"{fmt}\": expected json or csv.");
                            return;
                        }
                        break;

                    case "-i":
                    case "--installed-only":
                        installedOnly = true;
                        break;

                    case "--status":
                        string? status = NextValue();
                        if (status is null)
                            return;

                        if (status.Equals("all", StringComparison.OrdinalIgnoreCase))
                        {
                            statusFilter = null;
                        }
                        else if (Enum.TryParse(status, ignoreCase: true, out MissingTitleResearchStatus parsedStatus) &&
                                 Enum.IsDefined(parsedStatus))
                        {
                            statusFilter = parsedStatus;
                        }
                        else
                        {
                            UsageError($"Invalid --status \"{status}\": expected Unresearched, InReview, NoSaveLocation or All.");
                            return;
                        }
                        break;

                    case "-p":
                    case "--min-priority":
                        if ((minPriority = NextValue()) is null)
                            return;

                        if (!new[] { "High", "Normal", "Low" }.Contains(minPriority, StringComparer.OrdinalIgnoreCase))
                        {
                            UsageError($"Invalid --min-priority \"{minPriority}\": expected High, Normal or Low.");
                            return;
                        }
                        break;

                    case "-n":
                    case "--limit":
                        string? limitText = NextValue();
                        if (limitText is null || !TryReadCountArgument(limitText, "limit", out int parsedLimit))
                            return;

                        limit = parsedLimit;
                        break;

                    case "--platform":
                        string? platformText = NextValue();
                        if (platformText is null)
                            return;

                        platform = platformText;
                        break;

                    case "-c":
                    case "--candidates":
                        if ((candidatesPath = NextValue()) is null)
                            return;
                        break;

                    default:
                        UsageError($"Unknown tracklist argument \"{arg}\". Run 'help' for the list of tracklist options.");
                        return;
                }
            }

            if (format == null && !string.IsNullOrWhiteSpace(outputPath))
            {
                if (outputPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    format = TracklistExportFormat.Csv;
                else
                    format = TracklistExportFormat.Json;
            }
            format ??= TracklistExportFormat.Json;

            List<MissingTitleCandidate>? candidates = null;
            if (!string.IsNullOrWhiteSpace(candidatesPath))
            {
                if (!File.Exists(candidatesPath))
                {
                    UsageError($"Candidates file not found: {candidatesPath}");
                    return;
                }

                if (!TryReadAppIds(candidatesPath, File.ReadAllText(candidatesPath), out List<string> candidateIds))
                    return;

                // Titles come from the catalog in the database; the file only supplies AppIDs.
                candidates = candidateIds
                    .Select(appId => new MissingTitleCandidate(appId, Title: string.Empty))
                    .ToList();
            }

            var database = new SavePathDatabase(dbPath);
            database.Initialize();

            var options = new TracklistOptions(
                IncludeInstalledOnly: installedOnly,
                StatusFilter: statusFilter,
                MinPriority: minPriority,
                Limit: limit,
                Platform: platform);

            var generator = new TracklistGeneratorService();
            MissingTitlesTracklist tracklist = generator.GenerateTracklist(dbPath, candidates, options);

            Console.WriteLine("=== Missing Titles Tracklist Generator ===");
            Console.WriteLine($"Database                    : {dbPath}");
            Console.WriteLine($"Platform                    : {platform}");
            Console.WriteLine($"Total candidates reconciled : {tracklist.TotalReconciled}");
            Console.WriteLine($"Covered with approved paths : {tracklist.TotalCovered}");
            Console.WriteLine($"Total missing coverage      : {tracklist.TotalMissing}");
            Console.WriteLine($"  - Unresearched            : {tracklist.UnresearchedCount}");
            Console.WriteLine($"  - In Review (candidates)  : {tracklist.InReviewCount}");
            Console.WriteLine($"  - No Save Location        : {tracklist.NoSaveLocationCount}");
            Console.WriteLine($"Exported items count        : {tracklist.Items.Count}");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                generator.ExportToFile(tracklist, outputPath, format.Value);
                Console.WriteLine($"Output written to           : {outputPath} ({format.Value})");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Top missing titles (first 10):");
                int count = 0;
                foreach (MissingTitleEntry item in tracklist.Items.Take(10))
                {
                    count++;
                    string installedTag = item.IsInstalled ? "[Installed] " : "";
                    Console.WriteLine($" {count}. {installedTag}{item.Title} (AppID: {item.SteamAppId}, Status: {item.ResearchStatus}, Priority: {item.Priority})");
                    Console.WriteLine($"    Store: {item.StoreUrl}");
                }

                if (tracklist.Items.Count > 10)
                {
                    Console.WriteLine($" ... and {tracklist.Items.Count - 10} more titles.");
                }
                Console.WriteLine();
                Console.WriteLine("Tip: Use -o <file.json|file.csv> to export the complete tracklist.");
            }
        }

        private static bool TryReadAppIds(string source, string content, out List<string> appIds)
        {
            try
            {
                appIds = PcgwHarvester.ReadAppIds(content);
                return true;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"Error: {source}: {ex.Message}");
                Environment.ExitCode = 1;
                appIds = new List<string>();
                return false;
            }
        }

        private static async Task RunAiDetectPaths(string[] args, string dbPath)
        {
            string? gameDir = null;
            string? steamAppId = null;
            string? gameName = null;
            string? outputPath = null;
            bool saveDb = false;
            bool offline = false;

            int positionalIndex = 0;
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.Equals("-o", StringComparison.OrdinalIgnoreCase) || arg.Equals("--output", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        UsageError($"Missing value for ai-detect option {arg}.");
                        return;
                    }

                    outputPath = args[++i];
                }
                else if (arg.Equals("--save-db", StringComparison.OrdinalIgnoreCase))
                {
                    saveDb = true;
                }
                else if (arg.Equals("--offline", StringComparison.OrdinalIgnoreCase))
                {
                    offline = true;
                }
                else if (arg.Equals("-h", StringComparison.OrdinalIgnoreCase) || arg.Equals("--help", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  ai-detect-paths <game-directory> [steam-app-id] [game-name] [--output <path>] [--save-db] [--offline]");
                    Console.WriteLine("  ai-detect <game-directory> [steam-app-id] [game-name] [--output <path>] [--save-db] [--offline]");
                    Console.WriteLine();
                    Console.WriteLine("Arguments:");
                    Console.WriteLine("  <game-directory>          Path to the game installation folder to inspect");
                    Console.WriteLine("  [steam-app-id]            Steam AppID of the game (required with --output or --save-db)");
                    Console.WriteLine("  [game-name]               Optional game title (inferred from folder or engine if omitted)");
                    Console.WriteLine();
                    Console.WriteLine("Options:");
                    Console.WriteLine("  -o, --output <path>       Export candidate mappings to JSON file (schema v1)");
                    Console.WriteLine("  --save-db                 Import candidate mappings directly into gamesave.db as Pending/disabled");
                    Console.WriteLine("  --offline                 Force offline heuristic detection (skip AI completion query)");
                    return;
                }
                else if (arg.StartsWith("-", StringComparison.Ordinal))
                {
                    UsageError($"Unknown ai-detect option \"{arg}\". Run 'ai-detect --help' for usage.");
                    return;
                }
                else
                {
                    switch (positionalIndex)
                    {
                        case 0:
                            gameDir = arg;
                            break;
                        case 1:
                            steamAppId = arg;
                            break;
                        case 2:
                            gameName = arg;
                            break;
                        default:
                            UsageError($"Unexpected ai-detect argument \"{arg}\". Quote a game name that contains spaces.");
                            return;
                    }
                    positionalIndex++;
                }
            }

            if (string.IsNullOrWhiteSpace(gameDir))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  ai-detect-paths <game-directory> [steam-app-id] [game-name] [--output <path>] [--save-db] [--offline]");
                Console.WriteLine("  ai-detect <game-directory> [steam-app-id] [game-name] [--output <path>] [--save-db] [--offline]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine(@"  dotnet run -- ai-detect-paths ""C:\Games\MyGame"" 123456 ""My Game"" --output savepaths.json");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            // Stored and exported candidates are keyed by AppID; filing them under a typo or
            // under a made-up placeholder would attach them to the wrong game.
            if ((saveDb || outputPath is not null || steamAppId is not null) && !PcgwHarvester.IsAppId(steamAppId))
            {
                UsageError(steamAppId is null
                    ? "A numeric Steam AppID is required with --save-db or --output."
                    : $"Invalid Steam AppID \"{steamAppId}\": expected digits only.");
                return;
            }

            if (!Directory.Exists(gameDir))
            {
                Console.Error.WriteLine($"Error: Game directory not found: {gameDir}");
                Environment.ExitCode = 1;
                return;
            }

            var service = new AiPatternDetectorService();
            var request = new AiDetectionRequest(
                GameDirectory: gameDir,
                SteamAppId: steamAppId,
                GameName: gameName,
                OfflineOnly: offline);

            Console.WriteLine("=== AI-Assisted Save Path Pattern Detector ===");
            Console.WriteLine($"Inspected Directory : {Path.GetFullPath(gameDir)}");
            if (!string.IsNullOrWhiteSpace(steamAppId))
                Console.WriteLine($"Steam AppID         : {steamAppId}");
            if (!string.IsNullOrWhiteSpace(gameName))
                Console.WriteLine($"Specified Game Name : {gameName}");

            AiDetectionResult result;
            int submittedCount = 0;

            if (saveDb)
            {
                var database = new SavePathDatabase(dbPath);
                database.Initialize();
                var tuple = await database.DetectAndImportSavePathsAsync(request, service);
                result = tuple.Result;
                submittedCount = tuple.ImportedCount;
            }
            else
            {
                result = await service.DetectSavePathsAsync(request);
            }

            Console.WriteLine($"Effective Game Name : {result.GameName}");
            Console.WriteLine($"Detected Engine     : {result.DetectedEngine} (Confidence: {result.EngineConfidence})");
            Console.WriteLine($"Detection Mode      : {(result.IsOfflineHeuristic ? "Offline Heuristic" : $"AI Model ({result.ModelVersion})")}");
            Console.WriteLine($"Prompt Hash (SHA256): {result.PromptHash}");

            if (result.EngineEvidence.Count > 0)
            {
                Console.WriteLine($"Identified Markers  : {string.Join(", ", result.EngineEvidence)}");
            }

            Console.WriteLine();
            Console.WriteLine($"Proposed Save Path Candidates ({result.Proposals.Count}):");
            if (result.Proposals.Count == 0)
            {
                Console.WriteLine("  (No candidate save paths could be inferred from directory markers)");
            }
            else
            {
                foreach (var p in result.Proposals)
                {
                    Console.WriteLine($" - [{p.Confidence}] {p.PathTemplate}");
                    Console.WriteLine($"   Kind: {p.PathKind}, Platform: {p.Platform}, Priority: {p.Priority}");
                    Console.WriteLine($"   Rationale: {p.Rationale}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("TRUST & SAFETY NOTICE:");
            Console.WriteLine("All candidate proposals strictly default to review_status = 'Pending' and enabled = 0.");
            Console.WriteLine("AI proposals never take autonomous runtime effect. Human review is required.");
            Console.WriteLine("--------------------------------------------------------------------------------");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                MappingImportDocument doc = service.ToImportDocument(result);
                var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                string json = System.Text.Json.JsonSerializer.Serialize(doc, jsonOptions);
                string? dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outputPath, json);
                Console.WriteLine();
                Console.WriteLine($"Exported {doc.Mappings?.Count ?? 0} candidate mappings to: {outputPath}");
            }

            if (saveDb)
            {
                Console.WriteLine();
                Console.WriteLine($"Submitted {submittedCount} candidate mapping(s) for import into database: {dbPath}");
                Console.WriteLine("Mappings are saved with status 'Pending' and disabled. Review each candidate and run 'approve-mapping <id>' (or use the UI) to activate the correct one.");
            }
        }

        private static void RunSteamCatalogQueueMissing(string dbPath)
        {
            int added = SteamCatalogService.EnqueueMissingGamesForHarvest(dbPath);

            Console.WriteLine();
            Console.WriteLine("Steam catalog harvest queue updated:");
            Console.WriteLine($" - New pending games queued: {added}");
        }

        private static async Task RunSteamCatalogExportNext(string[] args, string dbPath)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  steam-catalog-export-next <output-appids.txt> [limit]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-001.txt 1000");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            string outputPath = args[1];

            int limit = 1000;

            if (args.Length >= 3 && !TryReadCountArgument(args[2], "limit", out limit))
                return;

            SteamCatalogMissingExportResult result =
                await SteamCatalogService.ExportNextQueuedGameAppIdsAsync(
                    dbPath,
                    outputPath,
                    limit);

            Console.WriteLine();
            Console.WriteLine("Steam catalog queue export finished:");
            Console.WriteLine($" - AppIDs exported: {result.MissingCount}");
            Console.WriteLine($" - Output: {result.OutputPath}");
            Console.WriteLine();
            Console.WriteLine("Next command:");
            Console.WriteLine($"  dotnet run -- pcgw-harvest-appids External/Titles \"SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/10.0\" \"{result.OutputPath}\"");
        }
        private static async Task RunSteamCatalogFetch(string[] args, string dbPath)
        {

            if (args.Length < 3)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  steam-catalog-fetch <output-root> <games|dlc|all> [max-apps] [steam-web-api-key]");
                Console.WriteLine();
                Console.WriteLine("Steam API key can also be supplied through STEAM_WEB_API_KEY environment variable.");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  dotnet run -- steam-catalog-fetch External/SteamCatalog games 1000");
                Console.WriteLine("  dotnet run -- steam-catalog-fetch External/SteamCatalog games 0 YOUR_STEAM_WEB_API_KEY");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            string outputRoot = args[1];
            string kindText = args[2];

            int maxApps = 0;

            if (args.Length >= 4 && !TryReadCountArgument(args[3], "max apps", out maxApps))
                return;

            string? apiKey = args.Length >= 5
                ? args[4]
                : Environment.GetEnvironmentVariable("STEAM_WEB_API_KEY");

            Console.WriteLine(
                $"Steam API key source: {(args.Length >= 5 ? "command argument" : "STEAM_WEB_API_KEY environment variable")}");

            Console.WriteLine(
                $"Steam API key length: {(apiKey?.Length ?? 0)}");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine("Missing Steam Web API key.");
                Console.WriteLine("Pass it as the last argument or set STEAM_WEB_API_KEY.");
                return;
            }

            var kinds = kindText.ToLowerInvariant() switch
            {
                "games" => new[] { SteamCatalogAppKind.Game },
                "game" => new[] { SteamCatalogAppKind.Game },
                "dlc" => new[] { SteamCatalogAppKind.Dlc },
                "all" => new[] { SteamCatalogAppKind.Game, SteamCatalogAppKind.Dlc },
                _ => Array.Empty<SteamCatalogAppKind>()
            };

            if (kinds.Length == 0)
            {
                Console.WriteLine("Kind must be one of: games, dlc, all");
                return;
            }

            foreach (SteamCatalogAppKind kind in kinds)
            {
                var options = new SteamCatalogFetchOptions
                {
                    DatabasePath = dbPath,
                    OutputRoot = outputRoot,
                    SteamWebApiKey = apiKey,
                    Kind = kind,
                    MaxResultsPerPage = 50_000,
                    MaxAppsToFetch = maxApps,
                    UserAgent = "SaveGameManager/0.1 .NET/10.0"
                };

                var service = new SteamCatalogService(options);

                SteamCatalogFetchResult result = await service.FetchAsync();

                Console.WriteLine();
                Console.WriteLine($"Steam catalog fetch finished for {result.Kind}:");
                Console.WriteLine($" - Apps fetched: {result.AppsFetched}");
                Console.WriteLine($" - JSON: {result.JsonOutputPath}");
                Console.WriteLine($" - AppIDs: {result.AppIdsOutputPath}");
            }
        }

        private static async Task RunSteamCatalogMissing(string[] args, string dbPath)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  steam-catalog-missing <output-appids.txt> [limit] [exclude-pcgw-linked]");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  dotnet run -- steam-catalog-missing External/SteamCatalog/missing-appids.txt 1000 false");
                Console.WriteLine("  dotnet run -- steam-catalog-missing External/SteamCatalog/missing-new-only.txt 1000 true");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            string outputPath = args[1];

            int limit = 1000;

            if (args.Length >= 3 && !TryReadCountArgument(args[2], "limit", out limit))
                return;

            bool excludeAlreadyPcgwLinked = false;

            if (args.Length >= 4 && !bool.TryParse(args[3], out excludeAlreadyPcgwLinked))
            {
                UsageError($"Invalid exclude-pcgw-linked value \"{args[3]}\": expected true or false.");
                return;
            }

            SteamCatalogMissingExportResult result =
                await SteamCatalogService.ExportMissingGameAppIdsAsync(
                    dbPath,
                    outputPath,
                    limit,
                    excludeAlreadyPcgwLinked);

            Console.WriteLine();
            Console.WriteLine("Missing Steam AppID export finished:");
            Console.WriteLine($" - Missing AppIDs exported: {result.MissingCount}");
            Console.WriteLine($" - Output: {result.OutputPath}");
            Console.WriteLine();
            Console.WriteLine("Next command example:");
            Console.WriteLine($"  dotnet run -- pcgw-harvest-appids External/Titles \"SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/10.0\" \"{result.OutputPath}\"");
        }

        private static async Task RunPcgwHarvestTracklist(string[] args, string dbPath)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  pcgw-harvest-tracklist <missing-titles.json> <output-root> <user-agent> [max-titles]");
                Console.WriteLine("  pcgw-harvest <missing-titles.json> <output-root> <user-agent> [max-titles]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-tracklist missing-titles.json External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/10\" 10");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            string tracklistPath = args[1];
            string outputRoot = args[2];
            string userAgent = args[3];

            int maxTitles = 0;
            if (args.Length >= 5 && !TryReadCountArgument(args[4], "max titles", out maxTitles))
                return;

            if (!File.Exists(tracklistPath))
            {
                Console.Error.WriteLine($"Error: Tracklist file not found: {tracklistPath}");
                Environment.ExitCode = 1;
                return;
            }

            if (!TryReadAppIds(tracklistPath, File.ReadAllText(tracklistPath), out List<string> appIds))
                return;

            if (appIds.Count == 0)
            {
                UsageError($"No valid numeric Steam AppIDs were found in {tracklistPath}.");
                return;
            }

            Console.WriteLine($"Starting PCGamingWiki targeted harvest from tracklist: {tracklistPath}");
            Console.WriteLine($"Output root: {outputRoot}");
            Console.WriteLine($"User-Agent: {userAgent}");
            if (maxTitles > 0)
                Console.WriteLine($"Max titles to harvest: {maxTitles}");

            var options = new PcgwHarvestOptions
            {
                DatabasePath = dbPath,
                OutputRoot = outputRoot,
                UserAgent = userAgent,
                SteamAppIds = appIds,
                RequestsPerMinute = 20,
                PauseEveryRequests = 20,
                PauseEveryRequestsDuration = TimeSpan.FromMinutes(1),
                MaxTitlesToProcess = maxTitles
            };

            var harvester = new PcgwHarvester(options);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            PrintPcgwHarvestResult(result, result.TitlesIndexed);
        }

        private static async Task RunPcgwHarvestAppIds(string[] args, string dbPath)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  pcgw-harvest-appids <output-root> <user-agent> <appid|appid-file> [more-appids]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-appids External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/10\" 413150");
                Console.WriteLine();
                Console.WriteLine("Example with file:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-appids External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/10\" appids.txt");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            string outputRoot = args[1];
            string userAgent = args[2];

            var appIds = new List<string>();
            foreach (string value in args.Skip(3))
            {
                string content = File.Exists(value) ? File.ReadAllText(value) : value;
                if (!TryReadAppIds(value, content, out List<string> valueIds))
                    return;

                appIds.AddRange(valueIds);
            }

            appIds = appIds.Distinct(StringComparer.Ordinal).ToList();

            if (appIds.Count == 0)
            {
                UsageError("No valid numeric Steam AppIDs were provided.");
                return;
            }

            var options = new PcgwHarvestOptions
            {
                DatabasePath = dbPath,
                OutputRoot = outputRoot,
                UserAgent = userAgent,
                SteamAppIds = appIds,
                RequestsPerMinute = 20,
                PauseEveryRequests = 20,
                PauseEveryRequestsDuration = TimeSpan.FromMinutes(1),
                MaxTitlesToProcess = 0
            };

            var harvester = new PcgwHarvester(options);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            PrintPcgwHarvestResult(result, appIds.Count);
        }

        private static async Task RunPcgwHarvestInstalled(string[] args, string dbPath)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  pcgw-harvest-installed <output-root> <user-agent> [max-games]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  dotnet run -- pcgw-harvest-installed External/Titles \"SaveGameManager/0.1 (https://github.com/mynickname; myemail@email.com) .NET/10\" 10");
                Environment.ExitCode = ExitCodeUsage;
                return;
            }

            string outputRoot = args[1];
            string userAgent = args[2];

            int maxGames = 0;

            if (args.Length >= 4 && !TryReadCountArgument(args[3], "max games", out maxGames))
                return;

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
                .Where(PcgwHarvester.IsAppId)
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
                PauseEveryRequests = 20,
                PauseEveryRequestsDuration = TimeSpan.FromMinutes(1),
                MaxTitlesToProcess = 0
            };

            var harvester = new PcgwHarvester(options);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            PrintPcgwHarvestResult(result, appIds.Count);
        }

        private static void PrintPcgwHarvestResult(
            PcgwHarvestResult result,
            int appIdsRequested)
        {
            // A run in which every title failed did no useful work; say so and fail the command.
            bool allFailed = result.TitlesProcessed == 0 && result.TitlesFailed > 0;

            Console.WriteLine();
            Console.WriteLine(allFailed ? "PCGamingWiki harvest failed: no title could be harvested." : "PCGamingWiki harvest finished:");
            Console.WriteLine($" - AppIDs requested: {appIdsRequested}");
            Console.WriteLine($" - Titles processed: {result.TitlesProcessed}");
            Console.WriteLine($" - Titles failed/missing: {result.TitlesFailed}");
            Console.WriteLine($" - Mappings extracted: {result.MappingsExtracted}");

            if (allFailed)
                Environment.ExitCode = 1;
        }

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
                List<SavePathMapping> mappings = database.GetApprovedMappingsForApp(game.AppId, "windows");

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
            Console.WriteLine("  migrate [dbPath]");
            Console.WriteLine("  migrate-status [dbPath]");
            Console.WriteLine("  migrate-dry-run [dbPath]");
            Console.WriteLine("  seed-curated");
            Console.WriteLine("  import <savepaths.json> [--approve]");
            Console.WriteLine("  approve-mapping <id> [notes]");
            Console.WriteLine("  approve-app <steamAppId> [notes]");
            Console.WriteLine("  migrate-legacy-mappings [--approve-all]");
            Console.WriteLine("  discover");
            Console.WriteLine("  discover-deep");
            Console.WriteLine("  verify");
            Console.WriteLine("  backup-dry-run <destination>");
            Console.WriteLine("  backup <destination>");
            Console.WriteLine("  pcgw-harvest-tracklist <missing-titles.json> <output-root> <user-agent> [max-titles]");
            Console.WriteLine("  pcgw-harvest-appids <output-root> <user-agent> <appid|appid-file> [more-appids]");
            Console.WriteLine("  pcgw-harvest-installed <output-root> <user-agent> [max-games]");
            Console.WriteLine("  steam-catalog-fetch <output-root> <games|dlc|all> [max-apps] [steam-web-api-key]");
            Console.WriteLine("  steam-catalog-missing <output-appids.txt> [limit] [exclude-pcgw-linked]");
            Console.WriteLine("  steam-catalog-queue-missing");
            Console.WriteLine("  steam-catalog-export-next <output-appids.txt> [limit]");
            Console.WriteLine("  tracklist [options] (aliases: missing-titles, export-missing)");
            Console.WriteLine("    -o, --output <path>       Output file path (.json or .csv)");
            Console.WriteLine("    -f, --format <json|csv>   Export format (default: inferred or json)");
            Console.WriteLine("    -i, --installed-only      Reconcile locally installed Steam games only");
            Console.WriteLine("    --status <status>         Filter status (Unresearched, InReview, NoSaveLocation, All)");
            Console.WriteLine("    -p, --min-priority <p>    Filter minimum priority (High, Normal, Low)");
            Console.WriteLine("    -n, --limit <count>       Limit number of exported items");
            Console.WriteLine("    -c, --candidates <path>   Reconcile against custom candidate list");
            Console.WriteLine("  ai-detect-paths <game-dir> [appId] [name] [--output <path>] [--save-db] [--offline]");
            Console.WriteLine("    (alias: ai-detect) Detect engine signatures and propose candidate save paths.");
            Console.WriteLine("    -o, --output <path>       Export candidate mappings to JSON file (schema v1)");
            Console.WriteLine("    --save-db                 Import candidate mappings directly to database as Pending/disabled");
            Console.WriteLine("    --offline                 Force offline heuristic detection (skip AI completion query)");
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
