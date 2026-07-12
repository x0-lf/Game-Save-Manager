using GameSaves.Core.Platform;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Save;
using System.Security.Cryptography;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Restores one backup run to its original locations. Copy-only, gated by
    /// explicit confirmation, integrity-checked against the manifest SHA-256,
    /// and protected by Safe Mode: a current file is only overwritten after it
    /// has itself been backed up.
    /// </summary>
    public sealed class BackupRestoreService : IBackupRestoreService
    {
        private readonly ITransferOverwriteBackupService _overwriteBackupService;
        private readonly ITransferHistoryRepository _historyRepository;
        private readonly ISavePathMappingRepository _mappingRepository;
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ICurrentPlatformProvider _platformProvider;
        private readonly SavePathExpander _expander = new();

        public BackupRestoreService(
            ITransferOverwriteBackupService overwriteBackupService,
            ITransferHistoryRepository historyRepository,
            ISavePathMappingRepository mappingRepository,
            ISteamDiscoveryService steamDiscoveryService,
            ICurrentPlatformProvider platformProvider)
        {
            _overwriteBackupService = overwriteBackupService;
            _historyRepository = historyRepository;
            _mappingRepository = mappingRepository;
            _steamDiscoveryService = steamDiscoveryService;
            _platformProvider = platformProvider;
        }

        public Task<IReadOnlyList<RestoreMappingTargetOption>> GetApprovedMappingTargetsAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<RestoreMappingTargetOption>>(
                () => GetApprovedMappingTargets(run, cancellationToken),
                cancellationToken);
        }

        private List<RestoreMappingTargetOption> GetApprovedMappingTargets(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken)
        {
            var options = new List<RestoreMappingTargetOption>();

            IReadOnlyList<SavePathMapping> mappings =
                _mappingRepository.GetApprovedMappingsForApp(
                    run.Manifest.SteamAppId,
                    _platformProvider.GetCurrentPlatformKey());

            if (mappings.Count == 0)
                return options;

            SteamGame game = ResolveGame(run, cancellationToken, out string? steamRoot);

            foreach (SavePathMapping mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                options.Add(BuildMappingOption(mapping, game, steamRoot));
            }

            return options;
        }

        private RestoreMappingTargetOption BuildMappingOption(
            SavePathMapping mapping,
            SteamGame game,
            string? steamRoot)
        {
            if (LooksProfileSpecific(mapping.PathTemplate))
            {
                return new RestoreMappingTargetOption(
                    mapping.Id,
                    mapping.PathTemplate,
                    ResolvedPath: null,
                    CanUse: false,
                    "Profile-specific mapping - use \"Restore to Selected Profile\" instead.");
            }

            List<string> resolved = _expander
                .ExpandCandidatePaths(mapping.PathTemplate, game, steamRoot, profile: null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (resolved.Count == 0)
            {
                return new RestoreMappingTargetOption(
                    mapping.Id,
                    mapping.PathTemplate,
                    ResolvedPath: null,
                    CanUse: false,
                    "Could not resolve this mapping to a path on this machine.");
            }

            if (resolved.Count > 1)
            {
                return new RestoreMappingTargetOption(
                    mapping.Id,
                    mapping.PathTemplate,
                    ResolvedPath: null,
                    CanUse: false,
                    $"Resolves to {resolved.Count} paths and cannot be used as a single restore target.");
            }

            return new RestoreMappingTargetOption(
                mapping.Id,
                mapping.PathTemplate,
                ResolvedPath: resolved[0],
                CanUse: true,
                "Ready");
        }

        private SteamGame ResolveGame(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken,
            out string? steamRoot)
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

            steamRoot = discovery.SteamRoot;

            SteamGame? installed = discovery.Games.FirstOrDefault(g =>
                g.AppId.Equals(run.Manifest.SteamAppId, StringComparison.OrdinalIgnoreCase));

            // A minimal game is enough for templates that do not use
            // install-path tokens; those tokens simply fail to resolve.
            return installed ?? new SteamGame(
                AppId: run.Manifest.SteamAppId,
                Name: run.Manifest.Game,
                InstallDirectory: string.Empty,
                LibraryPath: string.Empty,
                ManifestPath: string.Empty,
                GamePath: string.Empty,
                FolderExists: false,
                Confidence: SteamDiscoveryConfidence.Low);
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

        public Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Restore(run, options, cancellationToken),
                cancellationToken);
        }

        private BackupRestoreResult Restore(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

            BackupRestoreResult result = RestoreCore(run, options, cancellationToken);

            TryRecordHistory(run, options, result, startedUtc);

            return result;
        }

        private void TryRecordHistory(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            BackupRestoreResult result,
            DateTimeOffset startedUtc)
        {
            try
            {
                string? blockedReason = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?
                    .Message;

                _historyRepository.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.Restore,
                    GameName: run.Manifest.Game,
                    SteamAppId: run.Manifest.SteamAppId,
                    SourceAccountId: run.Manifest.SourceAccountId,
                    TargetAccountId: options.TargetMode == BackupRestoreTargetMode.SelectedSteamProfileUserData &&
                                     options.TargetProfile is not null
                        ? options.TargetProfile.AccountId
                        : run.Manifest.TargetAccountId,
                    DryRun: options.DryRun,
                    OverwriteEnabled: options.OverwriteExisting,
                    BackupEnabled: options.BackupBeforeOverwrite,
                    FilesConsidered: result.FilesConsidered,
                    FilesCopied: result.FilesRestored,
                    FilesSkipped: result.FilesSkipped,
                    FilesFailed: result.Items.Count(i => i.Status == BackupRestoreItemStatus.Failed),
                    BytesCopied: result.BytesRestored,
                    FilesBackedUp: result.FilesBackedUp,
                    BackupRootPath: result.BackupRootPath,
                    BlockedReason: blockedReason,
                    StartedUtc: startedUtc,
                    CompletedUtc: DateTimeOffset.UtcNow,
                    Items: result.Items
                        .Select(i => new TransferRunItemRecord(
                            i.BackupItem.BackupFile,
                            i.TargetFile,
                            i.Bytes,
                            i.Restored,
                            i.Status.ToString(),
                            i.Error,
                            i.PreRestoreBackupFile))
                        .ToList()));
            }
            catch
            {
                // History is an audit trail; a recording failure must never
                // fail the restore itself.
            }
        }

        private BackupRestoreResult RestoreCore(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken)
        {
            var warnings = new List<TransferPreviewWarning>();
            var results = new List<BackupRestoreItemResult>();

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Restore was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return BuildResult(run, options, results, warnings);
            }

            if (run.Manifest.Items.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoItems",
                    "Restore was blocked because the backup run contains no files.",
                    TransferWarningSeverity.Error));

                return BuildResult(run, options, results, warnings);
            }

            if (options.TargetMode == BackupRestoreTargetMode.SelectedSteamProfileUserData &&
                (options.TargetProfile is null ||
                 string.IsNullOrWhiteSpace(options.TargetProfile.UserDataPath)))
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoTargetProfileSelected",
                    "Restore was blocked: restoring to a selected profile requires choosing a target profile with a known userdata path.",
                    TransferWarningSeverity.Error));

                return BuildResult(run, options, results, warnings);
            }

            if (options.TargetMode == BackupRestoreTargetMode.CustomPathLater)
            {
                warnings.Add(new TransferPreviewWarning(
                    "TargetModeNotSupported",
                    "Restoring to a custom path is not supported yet.",
                    TransferWarningSeverity.Error));

                return BuildResult(run, options, results, warnings);
            }

            string? mappingTargetRoot = null;

            if (options.TargetMode == BackupRestoreTargetMode.ApprovedMappingLocation)
            {
                TransferPreviewWarning? mappingBlocker =
                    TryResolveMappingTargetRoot(run, options, cancellationToken, out mappingTargetRoot);

                if (mappingBlocker is not null)
                {
                    warnings.Add(mappingBlocker);
                    return BuildResult(run, options, results, warnings);
                }
            }

            string effectiveTargetAccount =
                options.TargetMode == BackupRestoreTargetMode.SelectedSteamProfileUserData
                    ? options.TargetProfile!.AccountId
                    : run.Manifest.TargetAccountId;

            ITransferOverwriteBackupSession? preRestoreSession = null;

            ITransferOverwriteBackupSession GetPreRestoreSession() =>
                preRestoreSession ??= _overwriteBackupService.BeginSession(
                    new OverwriteBackupContext(
                        Kind: OverwriteBackupContext.RestoreKind,
                        Game: run.Manifest.Game,
                        SteamAppId: run.Manifest.SteamAppId,
                        SourceAccountId: run.Manifest.SourceAccountId,
                        TargetAccountId: effectiveTargetAccount));

            try
            {
                foreach (TransferOverwriteBackupItem backupItem in run.Manifest.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    results.Add(RestoreOneFile(
                        backupItem,
                        run.Manifest.SteamAppId,
                        options,
                        mappingTargetRoot,
                        GetPreRestoreSession));
                }
            }
            finally
            {
                preRestoreSession?.Complete();
            }

            if (preRestoreSession is not null && preRestoreSession.FilesBackedUp > 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "PreRestoreBackups",
                    $"Backed up {preRestoreSession.FilesBackedUp} current file(s) before restoring over them to: {preRestoreSession.BackupRootPath}",
                    TransferWarningSeverity.Info));
            }

            return BuildResult(
                run,
                options,
                results,
                warnings,
                preRestoreSession?.FilesBackedUp ?? 0,
                preRestoreSession?.FilesBackedUp > 0 ? preRestoreSession.BackupRootPath : null);
        }

        // Resolves the approved mapping selected in the options to a single
        // target root. Re-resolves from the database so a stale or forged
        // options object can never inject an arbitrary path.
        private TransferPreviewWarning? TryResolveMappingTargetRoot(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken,
            out string? mappingTargetRoot)
        {
            mappingTargetRoot = null;

            if (options.TargetMappingId is null)
            {
                return new TransferPreviewWarning(
                    "NoTargetMappingSelected",
                    "Restore was blocked: restoring to an approved mapping location requires choosing a mapping first.",
                    TransferWarningSeverity.Error);
            }

            IReadOnlyList<SavePathMapping> mappings =
                _mappingRepository.GetApprovedMappingsForApp(
                    run.Manifest.SteamAppId,
                    _platformProvider.GetCurrentPlatformKey());

            SavePathMapping? mapping =
                mappings.FirstOrDefault(m => m.Id == options.TargetMappingId.Value);

            if (mapping is null)
            {
                return new TransferPreviewWarning(
                    "MappingNotAvailable",
                    "Restore was blocked: the selected mapping is no longer an approved, enabled mapping for this game.",
                    TransferWarningSeverity.Error);
            }

            SteamGame game = ResolveGame(run, cancellationToken, out string? steamRoot);
            RestoreMappingTargetOption option = BuildMappingOption(mapping, game, steamRoot);

            if (!option.CanUse || option.ResolvedPath is null)
            {
                return new TransferPreviewWarning(
                    "MappingNotUsable",
                    $"Restore was blocked: {option.StatusText}",
                    TransferWarningSeverity.Error);
            }

            // Never restore into the backup run folder itself.
            if (TransferPathGuard.IsUnderRoot(option.ResolvedPath, run.BackupRootPath))
            {
                return new TransferPreviewWarning(
                    "MappingTargetInsideBackup",
                    "Restore was blocked: the mapping resolves to a path inside the backup itself.",
                    TransferWarningSeverity.Error);
            }

            mappingTargetRoot = option.ResolvedPath;
            return null;
        }

        private static BackupRestoreItemResult RestoreOneFile(
            TransferOverwriteBackupItem backupItem,
            string steamAppId,
            BackupRestoreOptions options,
            string? mappingTargetRoot,
            Func<ITransferOverwriteBackupSession> getPreRestoreSession)
        {
            string backupFile = backupItem.BackupFile;
            string targetFile = backupItem.OriginalFile;

            try
            {
                if (options.TargetMode == BackupRestoreTargetMode.ApprovedMappingLocation)
                {
                    string? remapped = TryRemapToMappingRoot(
                        backupItem.OriginalFile,
                        steamAppId,
                        mappingTargetRoot!);

                    if (remapped is null)
                    {
                        return new BackupRestoreItemResult(
                            backupItem,
                            backupItem.OriginalFile,
                            0,
                            Restored: false,
                            BackupRestoreItemStatus.SkippedNotMappable,
                            "This file's original path cannot be mapped into the selected mapping location. Restore it to its original location instead.");
                    }

                    if (!TransferPathGuard.IsUnderRoot(remapped, mappingTargetRoot))
                    {
                        return new BackupRestoreItemResult(
                            backupItem,
                            remapped,
                            0,
                            Restored: false,
                            BackupRestoreItemStatus.Failed,
                            "The redirected target path escaped the mapping location. Skipped for safety.");
                    }

                    targetFile = remapped;
                }
                else if (options.TargetMode == BackupRestoreTargetMode.SelectedSteamProfileUserData)
                {
                    string? remapped = TryRemapToProfileUserData(
                        backupItem.OriginalFile,
                        steamAppId,
                        options.TargetProfile!.UserDataPath);

                    if (remapped is null)
                    {
                        return new BackupRestoreItemResult(
                            backupItem,
                            backupItem.OriginalFile,
                            0,
                            Restored: false,
                            BackupRestoreItemStatus.SkippedNotProfileMappable,
                            "This file is not inside a Steam userdata game folder, so it cannot be redirected to another profile. Restore it to its original location instead.");
                    }

                    // The redirected target must stay inside the selected
                    // profile's userdata game folder.
                    if (!TransferPathGuard.IsUnderRoot(
                            remapped,
                            Path.Combine(options.TargetProfile!.UserDataPath, steamAppId)))
                    {
                        return new BackupRestoreItemResult(
                            backupItem,
                            remapped,
                            0,
                            Restored: false,
                            BackupRestoreItemStatus.Failed,
                            "The redirected target path escaped the selected profile's userdata game folder. Skipped for safety.");
                    }

                    targetFile = remapped;
                }

                if (!File.Exists(backupFile))
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        0,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedBackupMissing,
                        "The backup file no longer exists.");
                }

                if (options.VerifyHashes &&
                    !ComputeSha256(backupFile).Equals(backupItem.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        0,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedHashMismatch,
                        "The backup file does not match the SHA-256 recorded in the manifest. It may be corrupted and was not restored.");
                }

                bool targetExists = File.Exists(targetFile);
                long backupBytes = new FileInfo(backupFile).Length;

                if (targetExists &&
                    ComputeSha256(targetFile).Equals(backupItem.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        backupBytes,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedAlreadyMatches,
                        "The current file already has the backed-up content.");
                }

                if (targetExists && !options.OverwriteExisting)
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        backupBytes,
                        Restored: false,
                        BackupRestoreItemStatus.SkippedTargetExists,
                        "The current file exists and differs from the backup. Overwrite is disabled.");
                }

                if (options.DryRun)
                {
                    return new BackupRestoreItemResult(
                        backupItem,
                        targetFile,
                        backupBytes,
                        Restored: false,
                        BackupRestoreItemStatus.DryRun,
                        targetExists
                            ? "Will overwrite the current file at this path (it is backed up first)."
                            : "Will create the target file at this path.");
                }

                string? preRestoreBackupFile = null;

                if (targetExists && options.BackupBeforeOverwrite)
                {
                    try
                    {
                        preRestoreBackupFile =
                            getPreRestoreSession().BackUpFile(targetFile).BackupFile;
                    }
                    catch (Exception backupEx)
                    {
                        // Safe Mode: never restore over a file that could not be backed up.
                        return new BackupRestoreItemResult(
                            backupItem,
                            targetFile,
                            backupBytes,
                            Restored: false,
                            BackupRestoreItemStatus.SkippedBackupFailed,
                            $"Pre-restore backup failed, restore refused: {backupEx.Message}");
                    }
                }

                string? targetDirectory = Path.GetDirectoryName(targetFile);

                if (!string.IsNullOrWhiteSpace(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                File.Copy(backupFile, targetFile, overwrite: targetExists);

                if (options.PreserveTimestamps)
                {
                    File.SetCreationTimeUtc(targetFile, File.GetCreationTimeUtc(backupFile));
                    File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(backupFile));
                }

                return new BackupRestoreItemResult(
                    backupItem,
                    targetFile,
                    backupBytes,
                    Restored: true,
                    BackupRestoreItemStatus.Restored,
                    null,
                    PreRestoreBackupFile: preRestoreBackupFile);
            }
            catch (Exception ex)
            {
                return new BackupRestoreItemResult(
                    backupItem,
                    targetFile,
                    0,
                    Restored: false,
                    BackupRestoreItemStatus.Failed,
                    ex.Message);
            }
        }

        private static BackupRestoreResult BuildResult(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            IReadOnlyList<BackupRestoreItemResult> results,
            IReadOnlyList<TransferPreviewWarning> warnings,
            int filesBackedUp = 0,
            string? backupRootPath = null)
        {
            int filesRestored = results.Count(item => item.Restored);

            int filesSkipped = results.Count(item =>
                !item.Restored &&
                item.Status != BackupRestoreItemStatus.Unknown);

            long bytesRestored = results
                .Where(item => item.Restored)
                .Sum(item => item.Bytes);

            return new BackupRestoreResult(
                Run: run,
                DryRun: options.DryRun,
                FilesConsidered: results.Count,
                FilesRestored: filesRestored,
                FilesSkipped: filesSkipped,
                BytesRestored: bytesRestored,
                Items: results,
                Warnings: warnings,
                FilesBackedUp: filesBackedUp,
                BackupRootPath: backupRootPath);
        }

        // Redirects a path recorded as ...\userdata\<any account>\<AppId>\<rest>
        // to <targetUserDataPath>\<AppId>\<rest>. Returns null when the path is
        // not inside a userdata game folder for this AppID.
        private static string? TryRemapToProfileUserData(
            string originalFile,
            string steamAppId,
            string targetUserDataPath)
        {
            string? remainder = TryGetUserDataRelativePath(originalFile, steamAppId);

            return remainder is null
                ? null
                : Path.Combine(targetUserDataPath, steamAppId, remainder);
        }

        // Redirects a backup file into a mapping-resolved root: userdata files
        // keep their path relative to the game folder; files already under the
        // root keep their original path. Anything else cannot be mapped.
        private static string? TryRemapToMappingRoot(
            string originalFile,
            string steamAppId,
            string mappingTargetRoot)
        {
            string? remainder = TryGetUserDataRelativePath(originalFile, steamAppId);

            if (remainder is not null)
                return Path.Combine(mappingTargetRoot, remainder);

            string? normalized = TransferPathGuard.TryNormalize(originalFile);

            if (normalized is not null &&
                TransferPathGuard.IsUnderRoot(normalized, mappingTargetRoot))
            {
                return normalized;
            }

            return null;
        }

        // Extracts <rest> from ...\userdata\<account>\<AppId>\<rest>, or null
        // when the path is not inside a userdata game folder for this AppID.
        private static string? TryGetUserDataRelativePath(
            string originalFile,
            string steamAppId)
        {
            string? normalized = TransferPathGuard.TryNormalize(originalFile);

            if (normalized is null || string.IsNullOrWhiteSpace(steamAppId))
                return null;

            string[] parts = normalized.Split(Path.DirectorySeparatorChar);

            // Match ...\userdata\<account>\<AppId>\<at least one more segment>.
            for (int i = 0; i + 3 < parts.Length; i++)
            {
                if (parts[i].Equals("userdata", StringComparison.OrdinalIgnoreCase) &&
                    parts[i + 2].Equals(steamAppId, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join(
                        Path.DirectorySeparatorChar,
                        parts[(i + 3)..]);
                }
            }

            return null;
        }

        private static string ComputeSha256(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
    }
}
