using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Save;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class TransferPreviewService : ITransferPreviewService
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ISavePathMappingRepository _mappingRepository;
        private readonly ICurrentPlatformProvider _platformProvider;
        private readonly SavePathExpander _expander = new();

        public TransferPreviewService(
            ISteamDiscoveryService steamDiscoveryService,
            ISavePathMappingRepository mappingRepository,
            ICurrentPlatformProvider platformProvider)
        {
            _steamDiscoveryService = steamDiscoveryService;
            _mappingRepository = mappingRepository;
            _platformProvider = platformProvider;
        }

        public Task<TransferPreviewPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            TransferPreviewOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => CreatePreview(
                    game,
                    sourceProfile,
                    targetProfile,
                    options ?? TransferPreviewOptions.Default,
                    cancellationToken),
                cancellationToken);
        }

        private TransferPreviewPlan CreatePreview(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            TransferPreviewOptions options,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>();
            var items = new List<TransferPreviewItem>();

            if (sourceProfile.AccountId.Equals(
                    targetProfile.AccountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new TransferPreviewWarning(
                    "SameProfile",
                    "Source and target profile are the same. Copying is not safe.",
                    TransferWarningSeverity.Error));
            }

            TransferPreviewItem? userDataItem = null;

            if (options.IncludeSteamUserDataGameFolder)
            {
                userDataItem = BuildSteamUserDataGameFolderItem(
                    game,
                    sourceProfile,
                    targetProfile,
                    warnings);

                if (userDataItem is not null)
                    items.Add(userDataItem);
            }

            if (options.IncludeApprovedMappings)
            {
                AddApprovedMappingItems(
                    game,
                    sourceProfile,
                    targetProfile,
                    userDataItem,
                    items,
                    warnings,
                    hasUserDataSource: userDataItem?.SourceExists == true,
                    cancellationToken);
            }

            if (items.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoPreviewItems",
                    "No copy preview items were created.",
                    TransferWarningSeverity.Error));
            }
            else if (!items.Any(item => item.SourceExists))
            {
                warnings.Add(new TransferPreviewWarning(
                    "NothingToCopy",
                    "No source path exists for this game with the selected source profile. There is nothing to copy.",
                    TransferWarningSeverity.Error));
            }

            int totalFiles = items
                .Where(item => item.SourceExists)
                .Sum(item => item.FileCount);

            long totalBytes = items
                .Where(item => item.SourceExists)
                .Sum(item => item.TotalBytes);

            bool canExecute =
                items.Count > 0 &&
                warnings.All(warning => warning.Severity != TransferWarningSeverity.Error) &&
                items.Any(item => item.SourceExists) &&
                items.All(item =>
                    item.ConflictStatus != TransferConflictStatus.SameSourceAndTarget &&
                    item.ConflictStatus != TransferConflictStatus.OutsideExpectedRoot);

            return new TransferPreviewPlan(
                Game: game,
                SourceProfile: sourceProfile,
                TargetProfile: targetProfile,
                Items: items,
                Warnings: warnings,
                CanExecute: canExecute,
                TotalFiles: totalFiles,
                TotalBytes: totalBytes);
        }

        // ---------------------------------------------------------------
        // Steam userdata game folder (first-class, mapping-independent)
        // ---------------------------------------------------------------

        private static TransferPreviewItem? BuildSteamUserDataGameFolderItem(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            List<TransferPreviewWarning> warnings)
        {
            if (string.IsNullOrWhiteSpace(sourceProfile.UserDataPath) ||
                string.IsNullOrWhiteSpace(targetProfile.UserDataPath))
            {
                warnings.Add(new TransferPreviewWarning(
                    "UserDataPathUnknown",
                    "A selected profile has no Steam userdata path. The userdata game folder cannot be previewed.",
                    TransferWarningSeverity.Warning));

                return null;
            }

            string sourceRoot = Path.Combine(sourceProfile.UserDataPath, game.AppId);
            string targetRoot = Path.Combine(targetProfile.UserDataPath, game.AppId);

            bool sourceContained = TransferPathGuard.IsValidUserDataGameFolderRoot(
                sourceRoot,
                sourceProfile.UserDataPath,
                game.AppId);

            bool targetContained = TransferPathGuard.IsValidUserDataGameFolderRoot(
                targetRoot,
                targetProfile.UserDataPath,
                game.AppId);

            bool sourceExists = Directory.Exists(sourceRoot);
            bool targetExists = Directory.Exists(targetRoot);

            int fileCount = 0;
            long totalBytes = 0;

            if (sourceExists)
                (fileCount, totalBytes) = CountDirectoryContents(sourceRoot);

            TransferConflictStatus conflictStatus;
            string statusText;
            string actionText;

            if (!sourceContained || !targetContained)
            {
                conflictStatus = TransferConflictStatus.OutsideExpectedRoot;
                statusText = "Outside expected userdata root";
                actionText = "Blocked: path containment check failed.";

                warnings.Add(new TransferPreviewWarning(
                    "UserDataContainment",
                    $"Userdata game-folder paths failed containment checks. Source: {sourceRoot} Target: {targetRoot}",
                    TransferWarningSeverity.Error));
            }
            else if (TransferPathGuard.PathsEqual(sourceRoot, targetRoot))
            {
                conflictStatus = TransferConflictStatus.SameSourceAndTarget;
                statusText = "Same source and target";
                actionText = "Blocked: source and target resolve to the same folder.";

                warnings.Add(new TransferPreviewWarning(
                    "UserDataSamePath",
                    $"Userdata source and target resolve to the same folder: {sourceRoot}",
                    TransferWarningSeverity.Error));
            }
            else if (!sourceExists)
            {
                conflictStatus = TransferConflictStatus.SourceMissing;
                statusText = "Source missing";
                actionText = "Nothing to copy: the source profile has no userdata folder for this game.";

                warnings.Add(new TransferPreviewWarning(
                    "UserDataSourceMissing",
                    $"The source profile has no userdata folder for this game: {sourceRoot}",
                    TransferWarningSeverity.Warning));
            }
            else if (targetExists)
            {
                conflictStatus = TransferConflictStatus.TargetExists;
                statusText = "Target exists";
                actionText = "Copy missing files. Existing target files are skipped unless overwrite is enabled. Nothing is deleted.";

                warnings.Add(new TransferPreviewWarning(
                    "UserDataTargetExists",
                    $"The target profile already has a userdata folder for this game: {targetRoot}",
                    TransferWarningSeverity.Warning));
            }
            else
            {
                conflictStatus = TransferConflictStatus.None;
                statusText = "Ready";
                actionText = "Create the target folder and copy all files, preserving relative paths and timestamps.";
            }

            return new TransferPreviewItem(
                SourceType: TransferSourceType.SteamUserDataGameFolder,
                MappingId: null,
                MappingTemplate: null,
                SteamAppId: game.AppId,
                GameName: game.Name,
                SourceRoot: sourceRoot,
                TargetRoot: targetRoot,
                SourcePath: sourceRoot,
                TargetPath: targetRoot,
                CopyScope: TransferCopyScope.DirectoryContents,
                SourceExists: sourceExists,
                TargetExists: targetExists,
                FileCount: fileCount,
                TotalBytes: totalBytes,
                ConflictStatus: conflictStatus,
                StatusText: statusText,
                ActionText: actionText);
        }

        // ---------------------------------------------------------------
        // Approved save-path mappings
        // ---------------------------------------------------------------

        private void AddApprovedMappingItems(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            TransferPreviewItem? userDataItem,
            List<TransferPreviewItem> items,
            List<TransferPreviewWarning> warnings,
            bool hasUserDataSource,
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
                _mappingRepository.GetApprovedMappingsForApp(
                    game.AppId,
                    platform);

            if (mappings.Count == 0)
            {
                // Only fatal when the userdata folder cannot cover the copy either.
                warnings.Add(new TransferPreviewWarning(
                    "NoApprovedMappings",
                    $"No approved save-path mappings exist for {game.Name} ({game.AppId}).",
                    hasUserDataSource
                        ? TransferWarningSeverity.Warning
                        : TransferWarningSeverity.Info));

                return;
            }

            foreach (SavePathMapping mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> sourcePaths =
                    ExpandMappingForProfile(mapping, game, discovery.SteamRoot, sourceProfile);

                IReadOnlyList<string> targetPaths =
                    ExpandMappingForProfile(mapping, game, discovery.SteamRoot, targetProfile);

                if (sourcePaths.Count == 0)
                {
                    string fallbackTarget = targetPaths.FirstOrDefault() ?? string.Empty;

                    items.Add(new TransferPreviewItem(
                        SourceType: TransferSourceType.ApprovedMapping,
                        MappingId: mapping.Id,
                        MappingTemplate: mapping.PathTemplate,
                        SteamAppId: game.AppId,
                        GameName: game.Name,
                        SourceRoot: string.Empty,
                        TargetRoot: fallbackTarget,
                        SourcePath: string.Empty,
                        TargetPath: fallbackTarget,
                        CopyScope: TransferCopyScope.DirectoryContents,
                        SourceExists: false,
                        TargetExists: !string.IsNullOrWhiteSpace(fallbackTarget) &&
                                      PathExists(fallbackTarget),
                        FileCount: 0,
                        TotalBytes: 0,
                        ConflictStatus: TransferConflictStatus.SourceMissing,
                        StatusText: "Source path could not be resolved",
                        ActionText: "Nothing to copy: the source path could not be resolved."));

                    warnings.Add(new TransferPreviewWarning(
                        "SourceUnresolved",
                        $"Could not resolve a source path for mapping: {mapping.PathTemplate}",
                        TransferWarningSeverity.Warning));

                    continue;
                }

                foreach (string sourcePath in sourcePaths)
                {
                    // The userdata game folder already covers this exact path;
                    // avoid copying the same files twice in one run.
                    if (userDataItem is not null &&
                        TransferPathGuard.PathsEqual(sourcePath, userDataItem.SourceRoot))
                    {
                        warnings.Add(new TransferPreviewWarning(
                            "DuplicateOfUserDataFolder",
                            $"Mapping {mapping.PathTemplate} resolves to the Steam userdata game folder, which is already included.",
                            TransferWarningSeverity.Info));

                        continue;
                    }

                    string targetPath = PickTargetPath(
                        sourcePath,
                        targetPaths,
                        sourceProfile,
                        targetProfile);

                    TransferPreviewItem item = BuildMappingPreviewItem(
                        mapping,
                        game,
                        sourcePath,
                        targetPath);

                    items.Add(item);

                    AddMappingItemWarnings(warnings, item, mapping);
                }
            }
        }

        private IReadOnlyList<string> ExpandMappingForProfile(
            SavePathMapping mapping,
            SteamGame game,
            string? steamRoot,
            SteamProfile profile)
        {
            return _expander
                .ExpandCandidatePaths(
                    mapping.PathTemplate,
                    game,
                    steamRoot,
                    profile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static TransferPreviewItem BuildMappingPreviewItem(
            SavePathMapping mapping,
            SteamGame game,
            string sourcePath,
            string targetPath)
        {
            bool sourceIsFile = File.Exists(sourcePath);
            bool sourceExists = sourceIsFile || Directory.Exists(sourcePath);
            bool targetExists = PathExists(targetPath);

            TransferCopyScope copyScope = sourceIsFile
                ? TransferCopyScope.SingleFile
                : TransferCopyScope.DirectoryContents;

            int fileCount = 0;
            long totalBytes = 0;

            if (sourceIsFile)
            {
                var fileInfo = new FileInfo(sourcePath);
                fileCount = 1;
                totalBytes = fileInfo.Length;
            }
            else if (sourceExists)
            {
                (fileCount, totalBytes) = CountDirectoryContents(sourcePath);
            }

            TransferConflictStatus conflictStatus =
                GetMappingConflictStatus(
                    sourcePath,
                    targetPath,
                    sourceExists,
                    targetExists,
                    mapping.PathTemplate);

            (string statusText, string actionText) = conflictStatus switch
            {
                TransferConflictStatus.None => (
                    "Ready",
                    "Copy to the target path, preserving relative paths and timestamps."),
                TransferConflictStatus.SourceMissing => (
                    "Source missing",
                    "Nothing to copy: the source path does not exist."),
                TransferConflictStatus.TargetExists => (
                    "Target exists",
                    "Copy missing files. Existing target files are skipped unless overwrite is enabled. Nothing is deleted."),
                TransferConflictStatus.SameSourceAndTarget => (
                    "Same source and target",
                    "Blocked: source and target resolve to the same path."),
                TransferConflictStatus.MappingNotProfileSpecific => (
                    "Not profile-specific",
                    "This save location is shared by all Steam profiles on this machine. Copying between profiles may not make sense."),
                _ => (
                    "Error",
                    "Blocked: this item cannot be copied safely.")
            };

            return new TransferPreviewItem(
                SourceType: TransferSourceType.ApprovedMapping,
                MappingId: mapping.Id,
                MappingTemplate: mapping.PathTemplate,
                SteamAppId: game.AppId,
                GameName: game.Name,
                SourceRoot: sourcePath,
                TargetRoot: targetPath,
                SourcePath: sourcePath,
                TargetPath: targetPath,
                CopyScope: copyScope,
                SourceExists: sourceExists,
                TargetExists: targetExists,
                FileCount: fileCount,
                TotalBytes: totalBytes,
                ConflictStatus: conflictStatus,
                StatusText: statusText,
                ActionText: actionText);
        }

        private static TransferConflictStatus GetMappingConflictStatus(
            string sourcePath,
            string targetPath,
            bool sourceExists,
            bool targetExists,
            string mappingTemplate)
        {
            if (!sourceExists)
                return TransferConflictStatus.SourceMissing;

            if (TransferPathGuard.PathsEqual(sourcePath, targetPath))
                return TransferConflictStatus.SameSourceAndTarget;

            if (targetExists)
                return TransferConflictStatus.TargetExists;

            if (!LooksProfileSpecific(mappingTemplate))
                return TransferConflictStatus.MappingNotProfileSpecific;

            return TransferConflictStatus.None;
        }

        private static void AddMappingItemWarnings(
            List<TransferPreviewWarning> warnings,
            TransferPreviewItem item,
            SavePathMapping mapping)
        {
            switch (item.ConflictStatus)
            {
                case TransferConflictStatus.SourceMissing:
                    warnings.Add(new TransferPreviewWarning(
                        "SourceMissing",
                        $"Source path does not exist: {item.SourcePath}",
                        TransferWarningSeverity.Warning));
                    break;

                case TransferConflictStatus.TargetExists:
                    warnings.Add(new TransferPreviewWarning(
                        "TargetExists",
                        $"Target path already exists. Existing files will be skipped unless overwrite is enabled: {item.TargetPath}",
                        TransferWarningSeverity.Warning));
                    break;

                case TransferConflictStatus.SameSourceAndTarget:
                    warnings.Add(new TransferPreviewWarning(
                        "SamePath",
                        $"Source and target resolve to the same path: {item.SourcePath}",
                        TransferWarningSeverity.Error));
                    break;

                case TransferConflictStatus.MappingNotProfileSpecific:
                    warnings.Add(new TransferPreviewWarning(
                        "NotProfileSpecific",
                        $"Mapping is not profile-specific. Copying between Steam profiles may not make sense: {mapping.PathTemplate}",
                        TransferWarningSeverity.Warning));
                    break;
            }
        }

        private static string PickTargetPath(
            string sourcePath,
            IReadOnlyList<string> targetPaths,
            SteamProfile sourceProfile,
            SteamProfile targetProfile)
        {
            if (targetPaths.Count > 0)
                return targetPaths[0];

            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                return sourcePath.Replace(
                    sourceProfile.AccountId,
                    targetProfile.AccountId,
                    StringComparison.OrdinalIgnoreCase);
            }

            return string.Empty;
        }

        private static bool LooksProfileSpecific(string mappingTemplate)
        {
            return mappingTemplate.Contains("{SteamUserData}", StringComparison.OrdinalIgnoreCase) ||
                   mappingTemplate.Contains("{AccountId}", StringComparison.OrdinalIgnoreCase) ||
                   mappingTemplate.Contains("{SteamAccountId}", StringComparison.OrdinalIgnoreCase) ||
                   mappingTemplate.Contains("{SteamUserId}", StringComparison.OrdinalIgnoreCase) ||
                   mappingTemplate.Contains("{SteamProfileId}", StringComparison.OrdinalIgnoreCase) ||
                   mappingTemplate.Contains(@"userdata\*", StringComparison.OrdinalIgnoreCase) ||
                   mappingTemplate.Contains(@"userdata/*", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return Directory.Exists(path) || File.Exists(path);
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
