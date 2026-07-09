using GameSaves.Core.Platform;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;

namespace GameSaves.Infrastructure.Save
{
    public sealed class InstalledGameSaveStatusService : IInstalledGameSaveStatusService
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ISavePathMappingRepository _mappingRepository;
        private readonly ISavePathVerifier _savePathVerifier;
        private readonly ICurrentPlatformProvider _platformProvider;

        public InstalledGameSaveStatusService(
            ISteamDiscoveryService steamDiscoveryService,
            ISavePathMappingRepository mappingRepository,
            ISavePathVerifier savePathVerifier,
            ICurrentPlatformProvider platformProvider)
        {
            _steamDiscoveryService = steamDiscoveryService;
            _mappingRepository = mappingRepository;
            _savePathVerifier = savePathVerifier;
            _platformProvider = platformProvider;
        }

        public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => GetInstalledGameStatuses(cancellationToken),
                cancellationToken);
        }

        private IReadOnlyList<InstalledGameSaveStatus> GetInstalledGameStatuses(
            CancellationToken cancellationToken)
        {
            string platform = _platformProvider.GetCurrentPlatformKey();

            SteamDiscoveryResult discovery = _steamDiscoveryService.Discover(
                new SteamDiscoveryOptions
                {
                    FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                    FallbackTimeout = TimeSpan.FromSeconds(30),
                    FallbackMaxDepth = 5
                },
                fallbackProgress: null,
                cancellationToken);

            if (discovery.Games.Count == 0)
                return Array.Empty<InstalledGameSaveStatus>();

            IReadOnlyDictionary<string, SavePathMappingStatus> mappingStatuses =
                _mappingRepository.GetMappingStatusesForApps(
                    discovery.Games.Select(game => game.AppId),
                    platform);

            var rows = new List<InstalledGameSaveStatus>();

            foreach (SteamGame game in discovery.Games
                         .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                rows.Add(BuildStatusForGame(
                    discovery,
                    game,
                    platform,
                    mappingStatuses));
            }

            return rows;
        }

        private InstalledGameSaveStatus BuildStatusForGame(
            SteamDiscoveryResult discovery,
            SteamGame game,
            string platform,
            IReadOnlyDictionary<string, SavePathMappingStatus> mappingStatuses)
        {
            try
            {
                mappingStatuses.TryGetValue(
                    game.AppId,
                    out SavePathMappingStatus? mappingStatus);

                mappingStatus ??= new SavePathMappingStatus(
                    game.AppId,
                    TotalMappings: 0,
                    EnabledMappings: 0,
                    ApprovedMappings: 0,
                    PendingMappings: 0,
                    NeedsFixMappings: 0,
                    RejectedMappings: 0);

                if (!game.FolderExists)
                {
                    return new InstalledGameSaveStatus(
                        Game: game,
                        Status: GameSaveStatusKind.NotInstalledClean,
                        StatusText: "Game folder missing",
                        ApprovedMappings: mappingStatus.ApprovedMappings,
                        PendingMappings: mappingStatus.PendingMappings,
                        NeedsFixMappings: mappingStatus.NeedsFixMappings,
                        SavePathExists: false,
                        FileCount: 0,
                        TotalBytes: 0,
                        VerificationResults: Array.Empty<SavePathVerificationResult>(),
                        Error: null);
                }

                if (!mappingStatus.HasAnyMapping)
                {
                    return new InstalledGameSaveStatus(
                        Game: game,
                        Status: GameSaveStatusKind.MappingMissing,
                        StatusText: "No mapping",
                        ApprovedMappings: 0,
                        PendingMappings: 0,
                        NeedsFixMappings: 0,
                        SavePathExists: false,
                        FileCount: 0,
                        TotalBytes: 0,
                        VerificationResults: Array.Empty<SavePathVerificationResult>(),
                        Error: null);
                }

                if (mappingStatus.ApprovedMappings == 0)
                {
                    return new InstalledGameSaveStatus(
                        Game: game,
                        Status: GameSaveStatusKind.NeedsFixOnly,
                        StatusText: "Needs review",
                        ApprovedMappings: mappingStatus.ApprovedMappings,
                        PendingMappings: mappingStatus.PendingMappings,
                        NeedsFixMappings: mappingStatus.NeedsFixMappings,
                        SavePathExists: false,
                        FileCount: 0,
                        TotalBytes: 0,
                        VerificationResults: Array.Empty<SavePathVerificationResult>(),
                        Error: null);
                }

                IReadOnlyList<SavePathMapping> approvedMappings =
                    _mappingRepository.GetApprovedMappingsForApp(
                        game.AppId,
                        platform);

                List<SavePathVerificationResult> verificationResults =
                    _savePathVerifier.Verify(
                        game,
                        discovery.SteamRoot,
                        approvedMappings);

                bool savePathExists = verificationResults.Any(result => result.Exists);

                int fileCount = verificationResults
                    .Where(result => result.Exists)
                    .Sum(result => result.FileCount);

                long totalBytes = verificationResults
                    .Where(result => result.Exists)
                    .Sum(result => result.TotalBytes);

                GameSaveStatusKind status = savePathExists
                    ? GameSaveStatusKind.Ready
                    : GameSaveStatusKind.PathMissing;

                string statusText = savePathExists
                    ? "Ready"
                    : "Path missing";

                return new InstalledGameSaveStatus(
                    Game: game,
                    Status: status,
                    StatusText: statusText,
                    ApprovedMappings: mappingStatus.ApprovedMappings,
                    PendingMappings: mappingStatus.PendingMappings,
                    NeedsFixMappings: mappingStatus.NeedsFixMappings,
                    SavePathExists: savePathExists,
                    FileCount: fileCount,
                    TotalBytes: totalBytes,
                    VerificationResults: verificationResults,
                    Error: null);
            }
            catch (Exception ex)
            {
                return new InstalledGameSaveStatus(
                    Game: game,
                    Status: GameSaveStatusKind.Error,
                    StatusText: "Error",
                    ApprovedMappings: 0,
                    PendingMappings: 0,
                    NeedsFixMappings: 0,
                    SavePathExists: false,
                    FileCount: 0,
                    TotalBytes: 0,
                    VerificationResults: Array.Empty<SavePathVerificationResult>(),
                    Error: ex.Message);
            }
        }
    }
}