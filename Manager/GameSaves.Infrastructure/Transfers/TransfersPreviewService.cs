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
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => CreatePreview(
                    game,
                    sourceProfile,
                    targetProfile,
                    cancellationToken),
                cancellationToken);
        }

        private TransferPreviewPlan CreatePreview(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>();
            var items = new List<TransferPreviewItem>();

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

            if (sourceProfile.AccountId.Equals(
                    targetProfile.AccountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new TransferPreviewWarning(
                    "SameProfile",
                    "Source and target profile are the same. Transfer is not safe.",
                    TransferWarningSeverity.Error));
            }

            IReadOnlyList<SavePathMapping> mappings =
                _mappingRepository.GetApprovedMappingsForApp(
                    game.AppId,
                    platform);

            if (mappings.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoApprovedMappings",
                    $"No approved save-path mappings exist for {game.Name} ({game.AppId}).",
                    TransferWarningSeverity.Error));
            }

            foreach (SavePathMapping mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> sourcePaths =
                    ExpandMappingForProfile(
                        mapping,
                        game,
                        discovery.SteamRoot,
                        sourceProfile);

                IReadOnlyList<string> targetPaths =
                    ExpandMappingForProfile(
                        mapping,
                        game,
                        discovery.SteamRoot,
                        targetProfile);

                if (sourcePaths.Count == 0)
                {
                    string fallbackTarget = targetPaths.FirstOrDefault() ?? string.Empty;

                    items.Add(new TransferPreviewItem(
                        MappingId: mapping.Id,
                        SteamAppId: game.AppId,
                        GameName: game.Name,
                        MappingTemplate: mapping.PathTemplate,
                        SourcePath: string.Empty,
                        TargetPath: fallbackTarget,
                        SourceExists: false,
                        TargetExists: !string.IsNullOrWhiteSpace(fallbackTarget) &&
                                      PathExists(fallbackTarget),
                        FileCount: 0,
                        TotalBytes: 0,
                        ConflictStatus: TransferConflictStatus.SourceMissing,
                        StatusText: "Source path could not be resolved"));

                    warnings.Add(new TransferPreviewWarning(
                        "SourceUnresolved",
                        $"Could not resolve a source path for mapping: {mapping.PathTemplate}",
                        TransferWarningSeverity.Warning));

                    continue;
                }

                foreach (string sourcePath in sourcePaths)
                {
                    string targetPath = PickTargetPath(
                        sourcePath,
                        targetPaths,
                        sourceProfile,
                        targetProfile);

                    TransferPreviewItem item = BuildPreviewItem(
                        mapping,
                        game,
                        sourcePath,
                        targetPath);

                    items.Add(item);

                    AddItemWarnings(
                        warnings,
                        item,
                        mapping);
                }
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
                items.All(item => item.ConflictStatus != TransferConflictStatus.SameSourceAndTarget);

            if (items.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoPreviewItems",
                    "No transfer preview items were created.",
                    TransferWarningSeverity.Error));
            }

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

        private static TransferPreviewItem BuildPreviewItem(
            SavePathMapping mapping,
            SteamGame game,
            string sourcePath,
            string targetPath)
        {
            bool sourceExists = PathExists(sourcePath);
            bool targetExists = PathExists(targetPath);

            int fileCount = 0;
            long totalBytes = 0;

            if (sourceExists)
                (fileCount, totalBytes) = CountPath(sourcePath);

            TransferConflictStatus conflictStatus =
                GetConflictStatus(
                    sourcePath,
                    targetPath,
                    sourceExists,
                    targetExists,
                    mapping.PathTemplate);

            string statusText = conflictStatus switch
            {
                TransferConflictStatus.None => "Ready",
                TransferConflictStatus.SourceMissing => "Source missing",
                TransferConflictStatus.TargetExists => "Target exists",
                TransferConflictStatus.SameSourceAndTarget => "Same source and target",
                TransferConflictStatus.MappingNotProfileSpecific => "Not profile-specific",
                _ => "Error"
            };

            return new TransferPreviewItem(
                MappingId: mapping.Id,
                SteamAppId: game.AppId,
                GameName: game.Name,
                MappingTemplate: mapping.PathTemplate,
                SourcePath: sourcePath,
                TargetPath: targetPath,
                SourceExists: sourceExists,
                TargetExists: targetExists,
                FileCount: fileCount,
                TotalBytes: totalBytes,
                ConflictStatus: conflictStatus,
                StatusText: statusText);
        }

        private static TransferConflictStatus GetConflictStatus(
            string sourcePath,
            string targetPath,
            bool sourceExists,
            bool targetExists,
            string mappingTemplate)
        {
            if (!sourceExists)
                return TransferConflictStatus.SourceMissing;

            if (PathsEqual(sourcePath, targetPath))
                return TransferConflictStatus.SameSourceAndTarget;

            if (targetExists)
                return TransferConflictStatus.TargetExists;

            if (!LooksProfileSpecific(mappingTemplate))
                return TransferConflictStatus.MappingNotProfileSpecific;

            return TransferConflictStatus.None;
        }

        private static void AddItemWarnings(
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
                        $"Target path already exists and would need conflict handling later: {item.TargetPath}",
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
                        $"Mapping may not be profile-specific: {mapping.PathTemplate}",
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

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                string normalizedLeft = Path.GetFullPath(left)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string normalizedRight = Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return normalizedLeft.Equals(
                    normalizedRight,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return left.Equals(right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static (int FileCount, long TotalBytes) CountPath(string path)
        {
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                return (1, fileInfo.Length);
            }

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