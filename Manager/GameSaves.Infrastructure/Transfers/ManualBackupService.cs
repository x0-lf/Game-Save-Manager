using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Save;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// On-demand backup of one game's saves for one Steam profile. Reuses the
    /// overwrite-backup engine, so every run is a fresh timestamped folder with
    /// mirrored original paths and a SHA-256 manifest.json - restorable through
    /// the existing Backups tab when written to the application backup base.
    /// </summary>
    public sealed class ManualBackupService : IManualBackupService
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ISavePathMappingRepository _mappingRepository;
        private readonly ICurrentPlatformProvider _platformProvider;
        private readonly ITransferOverwriteBackupService _backupEngine;
        private readonly SavePathExpander _expander = new();

        public ManualBackupService(
            ISteamDiscoveryService steamDiscoveryService,
            ISavePathMappingRepository mappingRepository,
            ICurrentPlatformProvider platformProvider,
            ITransferOverwriteBackupService backupEngine)
        {
            _steamDiscoveryService = steamDiscoveryService;
            _mappingRepository = mappingRepository;
            _platformProvider = platformProvider;
            _backupEngine = backupEngine;
        }

        public Task<ManualBackupPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => CreatePreview(
                    game,
                    profile,
                    destinationRoot,
                    options ?? ManualBackupOptions.Default,
                    cancellationToken),
                cancellationToken);
        }

        public Task<ManualBackupResult> ExecuteAsync(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Execute(plan, options, cancellationToken),
                cancellationToken);
        }

        // ---------------------------------------------------------------
        // Preview
        // ---------------------------------------------------------------

        private ManualBackupPlan CreatePreview(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions options,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>();
            var items = new List<TransferPreviewItem>();

            string? normalizedDestination = TransferPathGuard.TryNormalize(destinationRoot);

            if (!options.HasAnySource)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoBackupSourceSelected",
                    "No backup source is selected. Enable the Steam userdata game folder, approved save-path mappings, or both.",
                    TransferWarningSeverity.Error));
            }

            if (normalizedDestination is null)
            {
                warnings.Add(new TransferPreviewWarning(
                    "DestinationInvalid",
                    "The backup destination is empty or not a valid folder path.",
                    TransferWarningSeverity.Error));
            }

            if (warnings.Any(w => w.Severity == TransferWarningSeverity.Error))
            {
                return BuildPlan(game, profile, normalizedDestination ?? destinationRoot,
                    items, warnings);
            }

            TransferPreviewItem? userDataItem = null;

            if (options.IncludeSteamUserDataGameFolder)
            {
                userDataItem = BuildUserDataItem(game, profile, normalizedDestination!, warnings);

                if (userDataItem is not null)
                    items.Add(userDataItem);
            }

            if (options.IncludeApprovedMappings)
            {
                AddApprovedMappingItems(
                    game,
                    profile,
                    normalizedDestination!,
                    userDataItem,
                    items,
                    warnings,
                    cancellationToken);
            }

            // Backing up into a folder that is itself being backed up would
            // copy the backup into itself. Refuse it outright.
            foreach (TransferPreviewItem item in items)
            {
                if (item.SourceExists &&
                    TransferPathGuard.IsUnderRoot(normalizedDestination, item.SourceRoot))
                {
                    warnings.Add(new TransferPreviewWarning(
                        "DestinationInsideSource",
                        $"The backup destination is inside a folder that would be backed up: {item.SourceRoot}. Choose a destination outside the save locations.",
                        TransferWarningSeverity.Error));
                    break;
                }
            }

            if (items.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoPreviewItems",
                    "No backup preview items were created.",
                    TransferWarningSeverity.Error));
            }
            else if (!items.Any(item => item.IsCopyable))
            {
                warnings.Add(new TransferPreviewWarning(
                    "NothingToBackUp",
                    "No save files exist for this game with the selected profile. There is nothing to back up.",
                    TransferWarningSeverity.Error));
            }

            return BuildPlan(game, profile, normalizedDestination!, items, warnings);
        }

        private static ManualBackupPlan BuildPlan(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            List<TransferPreviewItem> items,
            List<TransferPreviewWarning> warnings)
        {
            bool canExecute =
                items.Any(item => item.IsCopyable) &&
                warnings.All(warning => warning.Severity != TransferWarningSeverity.Error);

            return new ManualBackupPlan(
                Game: game,
                Profile: profile,
                DestinationRoot: destinationRoot,
                Items: items,
                Warnings: warnings,
                CanExecute: canExecute,
                TotalFiles: items.Where(i => i.IsCopyable).Sum(i => i.FileCount),
                TotalBytes: items.Where(i => i.IsCopyable).Sum(i => i.TotalBytes));
        }

        private static TransferPreviewItem? BuildUserDataItem(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            List<TransferPreviewWarning> warnings)
        {
            if (string.IsNullOrWhiteSpace(profile.UserDataPath))
            {
                warnings.Add(new TransferPreviewWarning(
                    "UserDataPathUnknown",
                    "The selected profile has no Steam userdata path. The userdata game folder cannot be backed up.",
                    TransferWarningSeverity.Warning));

                return null;
            }

            string sourceRoot = Path.Combine(profile.UserDataPath, game.AppId);

            bool contained = TransferPathGuard.IsValidUserDataGameFolderRoot(
                sourceRoot,
                profile.UserDataPath,
                game.AppId);

            bool sourceExists = Directory.Exists(sourceRoot);

            int fileCount = 0;
            long totalBytes = 0;

            if (sourceExists)
                (fileCount, totalBytes) = CountDirectoryContents(sourceRoot);

            TransferConflictStatus conflictStatus;
            string statusText;
            string actionText;

            if (!contained)
            {
                conflictStatus = TransferConflictStatus.OutsideExpectedRoot;
                statusText = "Outside expected userdata root";
                actionText = "Blocked: path containment check failed.";

                warnings.Add(new TransferPreviewWarning(
                    "UserDataContainment",
                    $"The userdata game-folder path failed containment checks and will not be backed up: {sourceRoot}",
                    TransferWarningSeverity.Warning));
            }
            else if (!sourceExists)
            {
                conflictStatus = TransferConflictStatus.SourceMissing;
                statusText = "Source missing";
                actionText = "Nothing to back up: this profile has no userdata folder for the game.";

                warnings.Add(new TransferPreviewWarning(
                    "UserDataSourceMissing",
                    $"The profile has no userdata folder for this game: {sourceRoot}",
                    TransferWarningSeverity.Warning));
            }
            else
            {
                conflictStatus = TransferConflictStatus.None;
                statusText = "Ready";
                actionText = "Back up all files into a new timestamped run folder, preserving relative paths and timestamps.";
            }

            return new TransferPreviewItem(
                SourceType: TransferSourceType.SteamUserDataGameFolder,
                MappingId: null,
                MappingTemplate: null,
                SteamAppId: game.AppId,
                GameName: game.Name,
                SourceRoot: sourceRoot,
                TargetRoot: destinationRoot,
                SourcePath: sourceRoot,
                TargetPath: destinationRoot,
                CopyScope: TransferCopyScope.DirectoryContents,
                SourceExists: sourceExists,
                TargetExists: false,
                FileCount: fileCount,
                TotalBytes: totalBytes,
                ConflictStatus: conflictStatus,
                StatusText: statusText,
                ActionText: actionText);
        }

        private void AddApprovedMappingItems(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            TransferPreviewItem? userDataItem,
            List<TransferPreviewItem> items,
            List<TransferPreviewWarning> warnings,
            CancellationToken cancellationToken)
        {
            SteamDiscoveryResult discovery = _steamDiscoveryService.Discover(
                new SteamDiscoveryOptions
                {
                    FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                    FallbackTimeout = TimeSpan.FromSeconds(30),
                    FallbackMaxDepth = 5
                },
                fallbackProgress: null,
                cancellationToken);

            string platform = _platformProvider.GetCurrentPlatformKey();

            IReadOnlyList<SavePathMapping> mappings =
                _mappingRepository.GetApprovedMappingsForApp(game.AppId, platform);

            if (mappings.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoApprovedMappings",
                    $"No approved save-path mappings exist for {game.Name} ({game.AppId}).",
                    TransferWarningSeverity.Info));

                return;
            }

            foreach (SavePathMapping mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> sourcePaths = _expander
                    .ExpandCandidatePaths(mapping.PathTemplate, game, discovery.SteamRoot, profile)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (sourcePaths.Count == 0)
                {
                    warnings.Add(new TransferPreviewWarning(
                        "SourceUnresolved",
                        $"Could not resolve a source path for mapping: {mapping.PathTemplate}",
                        TransferWarningSeverity.Warning));

                    continue;
                }

                foreach (string sourcePath in sourcePaths)
                {
                    // Already fully covered by the userdata game-folder item.
                    if (userDataItem is not null &&
                        TransferPathGuard.PathsEqual(sourcePath, userDataItem.SourceRoot))
                    {
                        warnings.Add(new TransferPreviewWarning(
                            "DuplicateOfUserDataFolder",
                            $"Mapping {mapping.PathTemplate} resolves to the Steam userdata game folder, which is already included.",
                            TransferWarningSeverity.Info));

                        continue;
                    }

                    bool sourceIsFile = File.Exists(sourcePath);
                    bool sourceExists = sourceIsFile || Directory.Exists(sourcePath);

                    int fileCount = 0;
                    long totalBytes = 0;

                    if (sourceIsFile)
                    {
                        fileCount = 1;
                        totalBytes = new FileInfo(sourcePath).Length;
                    }
                    else if (sourceExists)
                    {
                        (fileCount, totalBytes) = CountDirectoryContents(sourcePath);
                    }

                    if (!sourceExists)
                    {
                        warnings.Add(new TransferPreviewWarning(
                            "SourceMissing",
                            $"Source path does not exist: {sourcePath}",
                            TransferWarningSeverity.Warning));
                    }

                    items.Add(new TransferPreviewItem(
                        SourceType: TransferSourceType.ApprovedMapping,
                        MappingId: mapping.Id,
                        MappingTemplate: mapping.PathTemplate,
                        SteamAppId: game.AppId,
                        GameName: game.Name,
                        SourceRoot: sourcePath,
                        TargetRoot: destinationRoot,
                        SourcePath: sourcePath,
                        TargetPath: destinationRoot,
                        CopyScope: sourceIsFile
                            ? TransferCopyScope.SingleFile
                            : TransferCopyScope.DirectoryContents,
                        SourceExists: sourceExists,
                        TargetExists: false,
                        FileCount: fileCount,
                        TotalBytes: totalBytes,
                        ConflictStatus: sourceExists
                            ? TransferConflictStatus.None
                            : TransferConflictStatus.SourceMissing,
                        StatusText: sourceExists ? "Ready" : "Source missing",
                        ActionText: sourceExists
                            ? "Back up into a new timestamped run folder, preserving the original path."
                            : "Nothing to back up: the source path does not exist."));
                }
            }
        }

        // ---------------------------------------------------------------
        // Execution
        // ---------------------------------------------------------------

        private ManualBackupResult Execute(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>(plan.Warnings);
            var results = new List<SaveTransferItemResult>();

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Backup was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return BuildResult(plan, options, results, warnings, null);
            }

            if (plan.HasErrors)
            {
                warnings.Add(new TransferPreviewWarning(
                    "PlanHasErrors",
                    "Backup was blocked because the preview plan contains errors.",
                    TransferWarningSeverity.Error));

                return BuildResult(plan, options, results, warnings, null);
            }

            // Re-validate userdata containment at execution time; the plan may be stale.
            foreach (TransferPreviewItem item in plan.Items)
            {
                if (item.SourceType == TransferSourceType.SteamUserDataGameFolder &&
                    item.SourceExists &&
                    !TransferPathGuard.IsValidUserDataGameFolderRoot(
                        item.SourceRoot,
                        plan.Profile.UserDataPath,
                        plan.Game.AppId))
                {
                    warnings.Add(new TransferPreviewWarning(
                        "PathContainmentViolation",
                        $"Backup was blocked: the userdata game-folder path failed containment checks: {item.SourceRoot}",
                        TransferWarningSeverity.Error));

                    return BuildResult(plan, options, results, warnings, null);
                }
            }

            ITransferOverwriteBackupSession? session = null;

            try
            {
                foreach (TransferPreviewItem item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!item.IsCopyable)
                        continue;

                    foreach (string sourceFile in EnumerateSourceFiles(item))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        results.Add(BackUpOneFile(
                            item,
                            sourceFile,
                            options,
                            () => session ??= _backupEngine.BeginSession(
                                new OverwriteBackupContext(
                                    Kind: OverwriteBackupContext.ManualKind,
                                    Game: plan.Game.Name,
                                    SteamAppId: plan.Game.AppId,
                                    SourceAccountId: plan.Profile.AccountId,
                                    TargetAccountId: plan.Profile.AccountId),
                                plan.DestinationRoot)));
                    }
                }
            }
            finally
            {
                session?.Complete();
            }

            if (session is not null && session.FilesBackedUp > 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ManualBackupCreated",
                    $"Backed up {session.FilesBackedUp} file(s) to: {session.BackupRootPath}",
                    TransferWarningSeverity.Info));
            }

            return BuildResult(plan, options, results, warnings, session);
        }

        private static IEnumerable<string> EnumerateSourceFiles(TransferPreviewItem item)
        {
            if (item.CopyScope == TransferCopyScope.SingleFile)
            {
                if (File.Exists(item.SourceRoot))
                    yield return item.SourceRoot;

                yield break;
            }

            if (!Directory.Exists(item.SourceRoot))
                yield break;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(item.SourceRoot, "*", options);
            }
            catch
            {
                yield break;
            }

            foreach (string file in files)
                yield return file;
        }

        private static SaveTransferItemResult BackUpOneFile(
            TransferPreviewItem item,
            string sourceFile,
            ManualBackupExecuteOptions options,
            Func<ITransferOverwriteBackupSession> getSession)
        {
            try
            {
                var sourceInfo = new FileInfo(sourceFile);

                if (options.DryRun)
                {
                    return new SaveTransferItemResult(
                        item,
                        sourceFile,
                        TargetFile: string.Empty,
                        sourceInfo.Length,
                        Copied: false,
                        SaveTransferItemStatus.DryRun,
                        null);
                }

                TransferOverwriteBackupItem backupItem = getSession().BackUpFile(sourceFile);

                return new SaveTransferItemResult(
                    item,
                    sourceFile,
                    backupItem.BackupFile,
                    backupItem.Bytes,
                    Copied: true,
                    SaveTransferItemStatus.Copied,
                    null);
            }
            catch (Exception ex)
            {
                return new SaveTransferItemResult(
                    item,
                    sourceFile,
                    TargetFile: string.Empty,
                    0,
                    Copied: false,
                    SaveTransferItemStatus.Failed,
                    ex.Message);
            }
        }

        private static ManualBackupResult BuildResult(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            IReadOnlyList<SaveTransferItemResult> results,
            IReadOnlyList<TransferPreviewWarning> warnings,
            ITransferOverwriteBackupSession? session)
        {
            int filesBackedUp = session?.FilesBackedUp ?? 0;

            int filesSkipped = results.Count(item =>
                !item.Copied &&
                item.Status != SaveTransferItemStatus.Unknown &&
                item.Status != SaveTransferItemStatus.DryRun);

            long bytesBackedUp = results
                .Where(item => item.Copied)
                .Sum(item => item.Bytes);

            string? backupRootPath = filesBackedUp > 0 ? session!.BackupRootPath : null;

            return new ManualBackupResult(
                Plan: plan,
                DryRun: options.DryRun,
                FilesConsidered: results.Count,
                FilesBackedUp: filesBackedUp,
                FilesSkipped: filesSkipped,
                BytesBackedUp: bytesBackedUp,
                Items: results,
                Warnings: warnings,
                BackupRootPath: backupRootPath,
                ManifestPath: backupRootPath is null
                    ? null
                    : Path.Combine(backupRootPath, TransferBackupLocations.ManifestFileName));
        }

        private static (int FileCount, long TotalBytes) CountDirectoryContents(string path)
        {
            if (!Directory.Exists(path))
                return (0, 0);

            int fileCount = 0;
            long totalBytes = 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        fileCount++;
                        totalBytes += info.Length;
                    }
                    catch
                    {
                        // Ignore individual unreadable files in preview.
                    }
                }
            }
            catch
            {
                return (fileCount, totalBytes);
            }

            return (fileCount, totalBytes);
        }
    }
}
