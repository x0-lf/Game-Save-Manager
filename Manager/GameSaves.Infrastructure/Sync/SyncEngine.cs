using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Text.Json;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// The shared sync engine: planning, manifest comparison, conflict
    /// detection, sync-log handling, and history recording. Backends only
    /// implement IRemoteFileSystem; the engine guarantees the safety model
    /// (copy-only, never overwrite, never delete, conflicts never copied) and
    /// uploads manifest.json last so an interrupted upload can never leave a
    /// folder that passes for a complete run.
    /// </summary>
    internal sealed class SyncEngine
    {
        private const string SyncLogRelativePath = RemoteProviderMetadataPath.SyncLog;

        private readonly IRemoteFileSystem _remote;
        private readonly string _providerName;
        private readonly string _remoteRootRaw;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;
        private readonly IBackupArchiveService _archiveService;
        private readonly IBackupMetadataReader _metadataReader;
        private readonly Dictionary<string, RemoteRunDescriptor> _remoteRuns = new(StringComparer.OrdinalIgnoreCase);

        private sealed record RemoteRunDescriptor(
            string RunName,
            TransferBackupManifest Manifest,
            BackupContainerFormat Format,
            string RemotePath,
            bool IsSidecar = false);

        public SyncEngine(
            IRemoteFileSystem remote,
            string providerName,
            string remoteRootRaw,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            IBackupArchiveService? archiveService = null,
            IBackupMetadataReader? metadataReader = null)
        {
            _remote = remote;
            _providerName = providerName;
            _remoteRootRaw = remoteRootRaw;
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;
            _metadataReader = metadataReader ?? new BackupMetadataReader();
            _archiveService = archiveService ?? new BackupArchiveService(backupHistoryService, _metadataReader);
        }

        // ---------------------------------------------------------------
        // Preview
        // ---------------------------------------------------------------

        public async Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            var warnings = new List<TransferPreviewWarning>();
            var items = new List<SyncItem>();

            TransferPreviewWarning? invalid = await _remote.ValidateAsync(cancellationToken);

            if (invalid is not null)
                warnings.Add(invalid);

            if (!options.Upload && !options.Download)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NoSyncDirectionSelected",
                    "No sync direction is selected. Enable upload, download, or both.",
                    TransferWarningSeverity.Error));
            }

            if (warnings.Any(w => w.Severity == TransferWarningSeverity.Error))
                return BuildPlan(items, warnings, invalid is null);

            IReadOnlyList<TransferBackupRunInfo> localRuns =
                await _backupHistoryService.GetRunsAsync(cancellationToken);

            // A folder run and a same-stem container both resolve to one run name.
            // ToDictionary threw on that, which broke every preview until the user
            // found and removed one of them by hand with no idea which.
            var local = new Dictionary<string, TransferBackupRunInfo>(
                StringComparer.OrdinalIgnoreCase);

            foreach (TransferBackupRunInfo run in localRuns)
            {
                string runName = GetLocalRunName(run);

                if (!local.TryGetValue(runName, out TransferBackupRunInfo? existing))
                {
                    local[runName] = run;
                    continue;
                }

                // The folder form wins: it restores directly, while a container has
                // to be imported first.
                bool keepExisting = existing.ContainerFormat == BackupContainerFormat.Folder;
                TransferBackupRunInfo hidden = keepExisting ? run : existing;

                local[runName] = keepExisting ? existing : run;

                warnings.Add(new TransferPreviewWarning(
                    "LocalRunNameCollision",
                    $"Two local backups share the run name \"{runName}\", so only one of them " +
                    $"can be synced under that name. This one is left out of the plan and is " +
                    $"neither uploaded nor changed: {hidden.BackupRootPath}",
                    TransferWarningSeverity.Warning));
            }

            Dictionary<string, TransferBackupManifest> remoteRuns =
                await ReadRemoteRunsAsync(warnings, cancellationToken);

            if (!await _remote.RootExistsAsync(cancellationToken))
            {
                warnings.Add(new TransferPreviewWarning(
                    "RemoteRootMissing",
                    "The sync folder does not exist yet. It will be created when the sync runs.",
                    TransferWarningSeverity.Info));
            }

            // A backend that cannot list containers would accept the upload and then
            // never show the run again, so say so instead of quietly losing it.
            if (options.ArchiveSync && !_remote.SupportsArchiveContainers)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ArchiveSyncUnsupported",
                    "This sync location cannot list compressed containers, so runs are copied " +
                    "as folders instead. Nothing is lost: folder runs upload, download and " +
                    "verify exactly as before.",
                    TransferWarningSeverity.Warning));
            }

            var names = local.Keys
                .Union(remoteRuns.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase);

            string localBase = _backupHistoryService.GetBackupBasePath();
            int ignoredByDirection = 0;

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hasLocal = local.TryGetValue(name, out TransferBackupRunInfo? localRun);
                bool hasRemote = remoteRuns.TryGetValue(name, out TransferBackupManifest? remoteManifest);

                if (hasLocal && !hasRemote)
                {
                    if (!options.Upload)
                    {
                        ignoredByDirection++;
                        continue;
                    }

                    TransferBackupRunInfo nonNullLocal = localRun!;
                    bool localIsContainer = nonNullLocal.ContainerFormat != BackupContainerFormat.Folder;
                    string remotePathName = name;
                    if (SendsAsContainer(options, nonNullLocal.ContainerFormat))
                    {
                        string ext = nonNullLocal.ContainerFormat switch
                        {
                            BackupContainerFormat.SevenZip => ".7z",
                            BackupContainerFormat.Zip => ".zip",
                            _ => options.ArchiveFormat == BackupContainerFormat.SevenZip ? ".7z" : ".zip"
                        };
                        remotePathName = $"{name}{ext}";
                    }

                    string statusText = "Copy to the sync folder";
                    if (localIsContainer && !_remote.SupportsArchiveContainers)
                    {
                        statusText = "Cannot upload: remote location does not support archive containers";
                        warnings.Add(new TransferPreviewWarning(
                            "LocalContainerUnsupported",
                            $"Backup run \"{name}\" is a compressed container, but this sync location cannot store containers. " +
                            "It cannot be uploaded to this location. Import the run locally first to sync it as a folder.",
                            TransferWarningSeverity.Warning));
                    }

                    items.Add(new SyncItem(
                        RunName: name,
                        Action: SyncItemAction.UploadToRemote,
                        ExistsLocally: true,
                        ExistsRemotely: false,
                        LocalPath: localRun!.BackupRootPath,
                        RemotePath: _remote.GetDisplayPath(remotePathName),
                        GameName: localRun.Manifest.Game,
                        FileCount: localRun.Manifest.FileCount,
                        TotalBytes: localRun.Manifest.TotalBytes,
                        StatusText: statusText));
                }
                else if (!hasLocal && hasRemote)
                {
                    if (!options.Download)
                    {
                        ignoredByDirection++;
                        continue;
                    }

                    string remotePathName = _remoteRuns.TryGetValue(name, out RemoteRunDescriptor? desc)
                        ? desc.RemotePath
                        : name;

                    items.Add(new SyncItem(
                        RunName: name,
                        Action: SyncItemAction.DownloadToLocal,
                        ExistsLocally: false,
                        ExistsRemotely: true,
                        LocalPath: Path.Combine(localBase, name),
                        RemotePath: _remote.GetDisplayPath(remotePathName),
                        GameName: remoteManifest!.Game,
                        FileCount: remoteManifest.FileCount,
                        TotalBytes: remoteManifest.TotalBytes,
                        StatusText: "Copy to the local backup base"));
                }
                else if (hasLocal && hasRemote)
                {
                    bool equivalent = ManifestsEquivalent(
                        localRun!.Manifest,
                        remoteManifest!);

                    string remotePathName = _remoteRuns.TryGetValue(name, out RemoteRunDescriptor? desc)
                        ? desc.RemotePath
                        : name;

                    if (equivalent)
                    {
                        bool isSidecar = desc?.IsSidecar ?? false;
                        VerificationStrength strength = isSidecar
                            ? VerificationStrength.SidecarManifestMatch
                            : VerificationStrength.ManifestMatch;

                        items.Add(new SyncItem(
                            name, SyncItemAction.InSync, true, true,
                            localRun.BackupRootPath, _remote.GetDisplayPath(remotePathName),
                            localRun.Manifest.Game,
                            localRun.Manifest.FileCount,
                            localRun.Manifest.TotalBytes,
                            isSidecar ? "In sync (sidecar manifest match)" : "In sync (manifest match)",
                            Verification: strength));
                    }
                    else
                    {
                        items.Add(new SyncItem(
                            name, SyncItemAction.Conflict, true, true,
                            localRun.BackupRootPath, _remote.GetDisplayPath(remotePathName),
                            localRun.Manifest.Game,
                            localRun.Manifest.FileCount,
                            localRun.Manifest.TotalBytes,
                            "Conflict: same name, different content. Never copied automatically.",
                            Verification: VerificationStrength.ManifestMismatch));

                        warnings.Add(new TransferPreviewWarning(
                            "SyncConflict",
                            $"Run \"{name}\" differs between the local base and the sync folder. It will not be copied; resolve it manually (for example export one side as ZIP, or delete one side via Cleanup).",
                            TransferWarningSeverity.Warning));
                    }
                }
            }

            if (ignoredByDirection > 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "IgnoredByDirection",
                    $"{ignoredByDirection} run(s) were ignored because the corresponding sync direction is disabled.",
                    TransferWarningSeverity.Info));
            }

            SyncPlan plan = BuildPlan(items, warnings, validationSucceeded: true);

            if (plan.UploadCount + plan.DownloadCount == 0 &&
                warnings.All(w => w.Severity != TransferWarningSeverity.Error))
            {
                warnings.Add(new TransferPreviewWarning(
                    "NothingToSync",
                    plan.ConflictCount > 0
                        ? "Nothing can be synced automatically; only conflicts remain."
                        : "Everything is in sync. There is nothing to copy.",
                    TransferWarningSeverity.Info));

                plan = BuildPlan(items, warnings, validationSucceeded: true);
            }

            return plan;
        }

        private async Task<Dictionary<string, TransferBackupManifest>> ReadRemoteRunsAsync(
            List<TransferPreviewWarning> warnings,
            CancellationToken cancellationToken)
        {
            var runs = new Dictionary<string, TransferBackupManifest>(StringComparer.OrdinalIgnoreCase);
            _remoteRuns.Clear();

            // 1. Process folder runs
            foreach (string name in await _remote.ListRunFolderNamesAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A run folder name becomes a local directory under the backup
                // base, so an unsafe name from the remote is never planned.
                if (!TransferPathGuard.IsSafeRemoteRelativePath(name))
                {
                    warnings.Add(new TransferPreviewWarning(
                        "RemoteRunNameUnsafe",
                        "A remote folder was skipped because its name is not a safe backup run name.",
                        TransferWarningSeverity.Warning));
                    continue;
                }

                try
                {
                    string? manifestText = await _remote.ReadTextFileAsync(
                        $"{name}/{TransferBackupLocations.ManifestFileName}",
                        cancellationToken);

                    // A folder without a manifest is not a backup run; ignore it.
                    if (manifestText is null)
                        continue;

                    TransferBackupManifest? manifest = ParseRemoteManifest(manifestText);

                    // A remote manifest is untrusted JSON. Deserialization does
                    // not enforce the record's non-nullable members, so an
                    // interrupted upload can leave a file that parses into a
                    // manifest whose members are null. Comparing such a manifest
                    // used to throw and break every later preview.
                    if (manifest is null)
                        continue;

                    if (!IsUsableManifest(manifest))
                    {
                        // Name the folder, exactly as the conflict warning names
                        // its run. A warning the operator cannot act on is worse
                        // than none, and this one persists until the folder is
                        // removed, so it has to say which folder to remove.
                        warnings.Add(new TransferPreviewWarning(
                            "RemoteManifestUnreadable",
                            $"Remote folder \"{name}\" has an incomplete or unreadable manifest, " +
                            "so it is not treated as a backup run. This usually means an upload was " +
                            "interrupted. Nothing is deleted automatically, and unticking runs does not " +
                            "clear this: delete that folder in the remote, then run the check again.",
                            TransferWarningSeverity.Warning));
                        continue;
                    }

                    runs[name] = manifest;
                    _remoteRuns[name] = new RemoteRunDescriptor(name, manifest, BackupContainerFormat.Folder, name);
                }
                catch
                {
                    warnings.Add(new TransferPreviewWarning(
                        "RemoteRunUnreadable",
                        $"A folder in the sync location has an unreadable manifest and was ignored: {_remote.GetDisplayPath(name)}",
                        TransferWarningSeverity.Warning));
                }
            }

            // 2. Process archive container runs (.7z, .zip)
            foreach (string archiveName in await _remote.ListRunArchiveNamesAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TransferPathGuard.IsSafeRemoteRelativePath(archiveName))
                {
                    warnings.Add(new TransferPreviewWarning(
                        "RemoteRunNameUnsafe",
                        "A remote archive container was skipped because its name is not a safe backup run name.",
                        TransferWarningSeverity.Warning));
                    continue;
                }

                string runName = GetArchiveRunName(archiveName);
                BackupContainerFormat format = archiveName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                    ? BackupContainerFormat.SevenZip
                    : BackupContainerFormat.Zip;

                try
                {
                    // Inspect manifest via sidecar descriptor first (1 request, zero payload download)
                    string sidecarPath = $"{archiveName}.manifest.json";
                    string? manifestText = await _remote.ReadTextFileAsync(sidecarPath, cancellationToken);
                    TransferBackupManifest? manifest = null;
                    bool isSidecar = false;

                    if (manifestText is not null)
                    {
                        manifest = ParseRemoteManifest(manifestText);
                        if (manifest is not null)
                        {
                            isSidecar = true;
                        }
                    }

                    // If no sidecar text, try local/direct header inspection via metadata reader if available
                    if (manifest is null)
                    {
                        string localPath = _remote.GetDisplayPath(archiveName);
                        if (File.Exists(localPath) && _metadataReader.TryReadManifest(localPath, out TransferBackupManifest? headerManifest, out _, allowSidecar: false))
                        {
                            manifest = headerManifest;
                            isSidecar = false;
                        }
                    }

                    if (manifest is null)
                    {
                        // A container whose sidecar manifest never arrived has no
                        // identity, so it cannot be offered as a run. Staying silent
                        // about it strands the run forever: the create-only check sees
                        // the payload and skips every retry, while nothing tells the
                        // operator which file to remove. Folder runs already warn here.
                        warnings.Add(new TransferPreviewWarning(
                            "RemoteContainerIncomplete",
                            $"Remote archive container \"{archiveName}\" has no manifest beside it, " +
                            "so it is not treated as a backup run. This usually means an upload was " +
                            "interrupted between the container and its manifest. Nothing is deleted " +
                            "automatically: delete that file in the remote, then run the check again " +
                            "to upload the run cleanly.",
                            TransferWarningSeverity.Warning));
                        continue;
                    }

                    if (!IsUsableManifest(manifest))
                    {
                        warnings.Add(new TransferPreviewWarning(
                            "RemoteManifestUnreadable",
                            $"Remote archive container \"{archiveName}\" has an incomplete or unreadable manifest, " +
                            "so it is not treated as a backup run. This usually means an upload was " +
                            "interrupted. Nothing is deleted automatically, and unticking runs does not " +
                            "clear this: delete that file in the remote, then run the check again.",
                            TransferWarningSeverity.Warning));
                        continue;
                    }

                    runs[runName] = manifest;
                    _remoteRuns[runName] = new RemoteRunDescriptor(runName, manifest, format, archiveName, isSidecar);
                }
                catch
                {
                    warnings.Add(new TransferPreviewWarning(
                        "RemoteRunUnreadable",
                        $"An archive container in the sync location has an unreadable manifest and was ignored: {_remote.GetDisplayPath(archiveName)}",
                        TransferWarningSeverity.Warning));
                }
            }

            return runs;
        }

        private SyncPlan BuildPlan(
            List<SyncItem> items,
            List<TransferPreviewWarning> warnings,
            bool validationSucceeded)
        {
            int uploads = items.Count(i => i.Action == SyncItemAction.UploadToRemote);
            int downloads = items.Count(i => i.Action == SyncItemAction.DownloadToLocal);

            return new SyncPlan(
                ProviderName: _providerName,
                RemoteRoot: _remoteRootRaw,
                Items: items,
                Warnings: warnings.ToList(),
                CanExecute:
                    warnings.All(w => w.Severity != TransferWarningSeverity.Error) &&
                    uploads + downloads > 0,
                UploadCount: uploads,
                DownloadCount: downloads,
                InSyncCount: items.Count(i => i.Action == SyncItemAction.InSync),
                ConflictCount: items.Count(i => i.Action == SyncItemAction.Conflict),
                BytesToUpload: items.Where(i => i.Action == SyncItemAction.UploadToRemote).Sum(i => i.TotalBytes),
                BytesToDownload: items.Where(i => i.Action == SyncItemAction.DownloadToLocal).Sum(i => i.TotalBytes))
            {
                ProviderValidationSucceeded = validationSucceeded
            };
        }

        // ---------------------------------------------------------------
        // Execution
        // ---------------------------------------------------------------

        public async Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

            var warnings = new List<TransferPreviewWarning>();
            var results = new List<SyncItemResult>();

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Sync was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            if (plan.HasErrors)
            {
                warnings.Add(new TransferPreviewWarning(
                    "PlanHasErrors",
                    "Sync was blocked because the preview plan contains errors.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            // Re-validate the remote at execution time; the plan may be stale.
            TransferPreviewWarning? invalid = await _remote.ValidateAsync(cancellationToken);

            if (invalid is not null)
            {
                warnings.Add(invalid);
                return RecordAndBuild(plan, options, results, warnings, startedUtc);
            }

            bool IsSelected(SyncItem item) =>
                options.OnlyRunNames is null ||
                options.OnlyRunNames.Contains(item.RunName, StringComparer.OrdinalIgnoreCase);

            var progressState = new ProgressState
            {
                RunsTotal = plan.Items.Count(i =>
                    i.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal &&
                    IsSelected(i)),
                BytesTotal = plan.Items
                    .Where(i => i.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal &&
                                IsSelected(i))
                    .Sum(i => i.TotalBytes)
            };

            foreach (SyncItem item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (item.Action)
                {
                    case SyncItemAction.UploadToRemote when !IsSelected(item):
                    case SyncItemAction.DownloadToLocal when !IsSelected(item):
                        results.Add(new SyncItemResult(
                            item,
                            0,
                            SyncItemStatus.SkippedDeselected,
                            "Deselected in the sync plan; not copied."));
                        break;

                    case SyncItemAction.UploadToRemote:
                        results.Add(await UploadRunAsync(item, options, progressState, cancellationToken));
                        progressState.RunsDone++;
                        break;

                    case SyncItemAction.DownloadToLocal:
                        results.Add(await DownloadRunAsync(item, options, progressState, cancellationToken));
                        progressState.RunsDone++;
                        break;

                    case SyncItemAction.Conflict:
                        results.Add(new SyncItemResult(
                            item,
                            0,
                            SyncItemStatus.SkippedConflict,
                            "Conflicts are never copied automatically."));
                        break;
                }
            }

            if (!options.DryRun)
                await AppendSyncLogAsync(results, warnings, cancellationToken);

            return RecordAndBuild(plan, options, results, warnings, startedUtc);
        }

        private async Task<SyncItemResult> UploadRunAsync(
            SyncItem item,
            SyncOptions options,
            ProgressState progressState,
            CancellationToken cancellationToken)
        {
            // Declared outside the try so a failure can report what was
            // actually transferred before it stopped.
            long bytes = 0;

            try
            {
                string localPath = item.LocalPath!;
                bool isLocalFolder = Directory.Exists(localPath);
                bool isLocalFile = File.Exists(localPath);

                if (!isLocalFolder && !isLocalFile)
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.Failed,
                        "The source run folder or its manifest no longer exists.");
                }

                if (isLocalFolder && !File.Exists(Path.Combine(localPath, TransferBackupLocations.ManifestFileName)))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.Failed,
                        "The source run folder or its manifest no longer exists.");
                }

                // A run that is already a container cannot be sent as a folder, so a
                // backend without container support has to refuse it outright rather
                // than fall through to the folder path and fail obscurely.
                if (isLocalFile && !_remote.SupportsArchiveContainers)
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.Failed,
                        "This backup run is a compressed container and this sync location " +
                        "cannot store containers. Import the run locally first, then sync it.");
                }

                bool uploadAsContainer =
                    isLocalFile || (options.ArchiveSync && _remote.SupportsArchiveContainers);

                if (uploadAsContainer)
                {
                    string extension = isLocalFile
                        ? Path.GetExtension(localPath)
                        : (options.ArchiveFormat == BackupContainerFormat.SevenZip ? ".7z" : ".zip");
                    string remoteContainerName = $"{item.RunName}{extension}";
                    string remoteSidecarName = $"{remoteContainerName}.manifest.json";

                    if (await _remote.FolderExistsAsync(item.RunName, cancellationToken) ||
                        await _remote.FileExistsAsync(remoteContainerName, cancellationToken))
                    {
                        return new SyncItemResult(
                            item, 0, SyncItemStatus.SkippedAlreadyExists,
                            "The target appeared since the preview. Nothing is ever overwritten.");
                    }

                    if (options.DryRun)
                    {
                        return new SyncItemResult(
                            item, item.TotalBytes, SyncItemStatus.DryRun,
                            "Would be copied to the sync folder.");
                    }

                    string? tempExportDir = null;

                    // The packaging step writes a full copy of the run into the backup
                    // base, so its cleanup has to cover every exit from here on, not
                    // just the upload itself.
                    try
                    {
                    string archiveFileToUpload;
                    string manifestJson;

                    if (isLocalFolder)
                    {
                        if (!_metadataReader.TryBuildRunInfo(localPath, out TransferBackupRunInfo? runInfo, out string? runInfoError) || runInfo is null)
                        {
                            return new SyncItemResult(
                                item, 0, SyncItemStatus.Failed,
                                $"Source manifest could not be read: {runInfoError}");
                        }

                        string localBase = _backupHistoryService.GetBackupBasePath();
                        tempExportDir = Path.Combine(localBase, $".export_{Guid.NewGuid():N}");
                        Directory.CreateDirectory(tempExportDir);

                        BackupContainerFormat exportFormat = options.ArchiveFormat;
                        BackupArchiveExportResult exportResult = await _archiveService.ExportRunAsync(
                            runInfo,
                            tempExportDir,
                            exportFormat,
                            options.CompressionPreset,
                            cancellationToken);

                        if (!exportResult.Success || string.IsNullOrEmpty(exportResult.ArchivePath))
                        {
                            return new SyncItemResult(
                                item, 0, SyncItemStatus.Failed,
                                $"Failed to package backup run container: {exportResult.Message}");
                        }

                        archiveFileToUpload = exportResult.ArchivePath;
                        manifestJson = JsonSerializer.Serialize(
                            runInfo.Manifest,
                            new JsonSerializerOptions { WriteIndented = true });
                    }
                    else
                    {
                        archiveFileToUpload = localPath;
                        if (!_metadataReader.TryReadManifest(localPath, out TransferBackupManifest? manifest, out _) || manifest is null)
                        {
                            return new SyncItemResult(
                                item, 0, SyncItemStatus.Failed,
                                "The source container manifest could not be read.");
                        }

                        manifestJson = JsonSerializer.Serialize(
                            manifest,
                            new JsonSerializerOptions { WriteIndented = true });
                    }

                        // 1. Upload container payload
                        long fileBytes = await _remote.UploadFileAsync(
                            archiveFileToUpload,
                            remoteContainerName,
                            cancellationToken);

                        bytes += fileBytes;
                        progressState.BytesDone += fileBytes;
                        ReportProgress(options, progressState, item.RunName, remoteContainerName);

                        // 2. Upload sidecar manifest. Until this lands the container
                        // carries no identity, so preview reports it as an incomplete
                        // upload rather than passing over it in silence.
                        await _remote.CreateTextFileIfMissingAsync(
                            remoteSidecarName,
                            manifestJson,
                            cancellationToken);

                        return new SyncItemResult(item, bytes, SyncItemStatus.Uploaded, null, Verification: VerificationStrength.Copied);
                    }
                    finally
                    {
                        if (tempExportDir is not null && Directory.Exists(tempExportDir))
                        {
                            try { Directory.Delete(tempExportDir, recursive: true); } catch { }
                        }
                    }
                }
                else
                {
                    if (await _remote.FolderExistsAsync(item.RunName, cancellationToken))
                    {
                        return new SyncItemResult(
                            item, 0, SyncItemStatus.SkippedAlreadyExists,
                            "The target appeared since the preview. Nothing is ever overwritten.");
                    }

                    if (options.DryRun)
                    {
                        return new SyncItemResult(
                            item, item.TotalBytes, SyncItemStatus.DryRun,
                            "Would be copied to the sync folder.");
                    }

                    var enumeration = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };

                    // Upload manifest.json last: a folder without a manifest is
                    // never mistaken for a complete run if the upload is interrupted.
                    var files = Directory.EnumerateFiles(localPath, "*", enumeration)
                        .OrderBy(file => IsRunManifest(localPath, file) ? 1 : 0)
                        .ToList();

                    foreach (string localFile in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string relative = Path.GetRelativePath(localPath, localFile)
                            .Replace(Path.DirectorySeparatorChar, '/');

                        long fileBytes = await _remote.UploadFileAsync(
                            localFile,
                            $"{item.RunName}/{relative}",
                            cancellationToken);

                        bytes += fileBytes;
                        progressState.BytesDone += fileBytes;
                        ReportProgress(options, progressState, item.RunName, relative);
                    }

                    return new SyncItemResult(item, bytes, SyncItemStatus.Uploaded, null, Verification: VerificationStrength.Copied);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Reporting zero bytes here would tell the user nothing was
                // copied while a partial run sits on the remote. Say what
                // actually happened, and say it without the exception text,
                // which can carry a path.
                return bytes > 0
                    ? new SyncItemResult(
                        item, bytes, SyncItemStatus.Incomplete,
                        "The upload stopped partway. Some files were copied and " +
                        "the run has no manifest, so it is not offered for " +
                        "download. Nothing was deleted or replaced, and running " +
                        "the sync again is safe.")
                    : new SyncItemResult(item, 0, SyncItemStatus.Failed, ex.Message);
            }
        }

        private async Task<SyncItemResult> DownloadRunAsync(
            SyncItem item,
            SyncOptions options,
            ProgressState progressState,
            CancellationToken cancellationToken)
        {
            // Declared outside the try so a failure can report what was
            // actually transferred before it stopped.
            long bytes = 0;

            try
            {
                string localTarget = item.LocalPath!;

                if (Directory.Exists(localTarget) || File.Exists(localTarget))
                {
                    return new SyncItemResult(
                        item, 0, SyncItemStatus.SkippedAlreadyExists,
                        "The target appeared since the preview. Nothing is ever overwritten.");
                }

                // Detect whether the remote run is a container archive
                bool isContainer = false;
                string? remoteArchiveName = null;

                if (_remoteRuns.TryGetValue(item.RunName, out RemoteRunDescriptor? descriptor))
                {
                    if (descriptor.Format != BackupContainerFormat.Folder)
                    {
                        isContainer = true;
                        remoteArchiveName = descriptor.RemotePath;
                    }
                }
                else if (item.RemotePath?.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) == true ||
                         item.RemotePath?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                {
                    isContainer = true;
                    remoteArchiveName = Path.GetFileName(item.RemotePath);
                }

                if (isContainer)
                {
                    if (!_remote.SupportsArchiveContainers)
                    {
                        return new SyncItemResult(
                            item, 0, SyncItemStatus.Failed,
                            "This remote run is an archive container, but this sync location does not support container downloads. Download or copy the container file directly.");
                    }

                    remoteArchiveName ??= $"{item.RunName}.7z";

                    if (options.DryRun)
                    {
                        return new SyncItemResult(
                            item, item.TotalBytes, SyncItemStatus.DryRun,
                            "Would be copied to the local backup base.");
                    }

                    string localBase = _backupHistoryService.GetBackupBasePath();
                    Directory.CreateDirectory(localBase);
                    string tempDownloadDir = Path.Combine(localBase, $".download_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDownloadDir);
                    string tempDownloadFile = Path.Combine(tempDownloadDir, remoteArchiveName);

                    try
                    {
                        long fileBytes = await _remote.DownloadFileAsync(
                            remoteArchiveName,
                            tempDownloadFile,
                            cancellationToken);

                        bytes += fileBytes;
                        progressState.BytesDone += fileBytes;
                        ReportProgress(options, progressState, item.RunName, remoteArchiveName);

                        BackupArchiveImportResult importResult = await _archiveService.ImportArchiveAsync(
                            tempDownloadFile,
                            cancellationToken);

                        if (!importResult.Success)
                        {
                            return new SyncItemResult(
                                item, bytes, SyncItemStatus.Failed,
                                $"Archive container import failed: {importResult.Message}");
                        }

                        return new SyncItemResult(item, bytes, SyncItemStatus.Downloaded, null, Verification: VerificationStrength.Copied);
                    }
                    finally
                    {
                        if (Directory.Exists(tempDownloadDir))
                        {
                            try { Directory.Delete(tempDownloadDir, recursive: true); } catch { }
                        }
                    }
                }
                else
                {
                    string manifestRelative = $"{item.RunName}/{TransferBackupLocations.ManifestFileName}";

                    if (await _remote.ReadTextFileAsync(manifestRelative, cancellationToken) is null)
                    {
                        return new SyncItemResult(
                            item, 0, SyncItemStatus.Failed,
                            "The source run folder or its manifest no longer exists.");
                    }

                    if (options.DryRun)
                    {
                        return new SyncItemResult(
                            item, item.TotalBytes, SyncItemStatus.DryRun,
                            "Would be copied to the local backup base.");
                    }

                    IReadOnlyList<string> remoteFiles =
                        await _remote.ListFilesAsync(item.RunName, cancellationToken);

                    // The remote controls every one of these names. Check the whole
                    // listing before writing anything, so an unsafe name late in the
                    // run cannot leave a half-copied folder that still carries a
                    // manifest and therefore passes for a complete run.
                    foreach (string relative in remoteFiles)
                    {
                        string candidate = Path.Combine(
                            localTarget,
                            relative.Replace('/', Path.DirectorySeparatorChar));

                        // The second check is belt and braces: a name that passes
                        // the first must still resolve inside the run folder.
                        if (!TransferPathGuard.IsSafeRemoteRelativePath(relative) ||
                            !TransferPathGuard.IsStrictlyUnderRoot(candidate, localTarget))
                        {
                            return new SyncItemResult(
                                item, 0, SyncItemStatus.Failed,
                                "The remote run contains a file name that is not safe to write locally.");
                        }
                    }

                    foreach (string relative in remoteFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string localFilePath = Path.Combine(
                            localTarget,
                            relative.Replace('/', Path.DirectorySeparatorChar));

                        long fileBytes = await _remote.DownloadFileAsync(
                            $"{item.RunName}/{relative}",
                            localFilePath,
                            cancellationToken);

                        bytes += fileBytes;
                        progressState.BytesDone += fileBytes;
                        ReportProgress(options, progressState, item.RunName, relative);
                    }

                    // The downloaded manifest records backup-file paths from the
                    // machine the run was created on; rewrite them to this one.
                    string manifestPath = Path.Combine(
                        localTarget,
                        TransferBackupLocations.ManifestFileName);

                    TransferBackupManifest? manifest =
                        ParseRemoteManifest(File.ReadAllText(manifestPath));

                    if (manifest is null ||
                        !BackupManifestPathRewriter.TryRewrite(manifest, localTarget, out TransferBackupManifest rewritten))
                    {
                        return new SyncItemResult(
                            item, bytes, SyncItemStatus.Failed,
                            "The run was copied, but its manifest paths could not be rewritten. It may not be restorable; it was left in place for inspection.");
                    }

                    File.WriteAllText(
                        manifestPath,
                        JsonSerializer.Serialize(
                            rewritten,
                            new JsonSerializerOptions { WriteIndented = true }));

                    return new SyncItemResult(item, bytes, SyncItemStatus.Downloaded, null, Verification: VerificationStrength.Copied);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return bytes > 0
                    ? new SyncItemResult(
                        item, bytes, SyncItemStatus.Incomplete,
                        "The download stopped partway. Some files were written " +
                        "and the run has no usable manifest, so it is not " +
                        "presented as a restorable backup. Nothing was deleted " +
                        "or replaced, and running the sync again is safe.")
                    : new SyncItemResult(item, 0, SyncItemStatus.Failed, ex.Message);
            }
        }

        private sealed class ProgressState
        {
            public int RunsDone;
            public int RunsTotal;
            public long BytesDone;
            public long BytesTotal;
        }

        private static void ReportProgress(
            SyncOptions options,
            ProgressState state,
            string runName,
            string currentFile)
        {
            // Actual bytes can slightly exceed the planned total (manifests are
            // not counted in run sizes), so clamp for a clean progress bar.
            options.Progress?.Report(new SyncProgress(
                RunName: runName,
                CurrentFile: currentFile,
                RunsDone: state.RunsDone,
                RunsTotal: state.RunsTotal,
                BytesDone: Math.Min(state.BytesDone, state.BytesTotal),
                BytesTotal: state.BytesTotal));
        }

        private static bool IsRunManifest(string runRoot, string filePath)
        {
            return string.Equals(
                       Path.GetFileName(filePath),
                       TransferBackupLocations.ManifestFileName,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       Path.GetDirectoryName(Path.GetFullPath(filePath)),
                       Path.GetFullPath(runRoot).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A backend that cannot enumerate containers must never be sent one. The
        /// payload would upload successfully and then be invisible to every later
        /// preview, so the run could never be downloaded back and every later sync
        /// would upload it again.
        /// </summary>
        private bool SendsAsContainer(SyncOptions options, BackupContainerFormat localFormat) =>
            _remote.SupportsArchiveContainers &&
            (options.ArchiveSync || localFormat != BackupContainerFormat.Folder);

        /// <summary>
        /// Every manifest reaching the engine comes from a remote listing or a file
        /// that was just downloaded, so it is untrusted text. The archive readers have
        /// bounded their manifest reads since OBS-004; these three call sites were
        /// parsing whatever arrived. An oversized document is rejected before the
        /// parser allocates for it, and the failure travels as an exception so it
        /// surfaces through the same "unreadable manifest" warning as any other.
        /// </summary>
        private static TransferBackupManifest? ParseRemoteManifest(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            if (json.Length > BackupArchiveSafetyBounds.DefaultMaxManifestBytes)
            {
                throw new InvalidDataException(
                    "The manifest exceeds the maximum size a backup manifest is allowed to have.");
            }

            return JsonSerializer.Deserialize<TransferBackupManifest>(json);
        }

        private static string GetLocalRunName(TransferBackupRunInfo run)
        {
            if (run.ContainerFormat is BackupContainerFormat.SevenZip or BackupContainerFormat.Zip)
            {
                return Path.GetFileNameWithoutExtension(run.BackupRootPath);
            }

            return Path.GetFileName(run.BackupRootPath);
        }

        private static string GetArchiveRunName(string archiveFileName)
        {
            if (archiveFileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                archiveFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(archiveFileName);
            }

            return archiveFileName;
        }

        // ---------------------------------------------------------------
        // Sync log (version-history metadata)
        // ---------------------------------------------------------------

        public async Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default)
        {
            List<SyncLogEntry> log = ParseSyncLog(
                await _remote.ReadProviderMetadataAsync(
                    SyncLogRelativePath,
                    cancellationToken));

            return log
                .OrderByDescending(entry => entry.TimestampUtc)
                .ToList();
        }

        private async Task AppendSyncLogAsync(
            IReadOnlyList<SyncItemResult> results,
            List<TransferPreviewWarning> warnings,
            CancellationToken cancellationToken)
        {
            try
            {
                List<SyncLogEntry> log = ParseSyncLog(
                    await _remote.ReadProviderMetadataAsync(
                        SyncLogRelativePath,
                        cancellationToken));

                log.Add(new SyncLogEntry(
                    DeviceName: Environment.MachineName,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Uploaded: results.Count(r => r.Status == SyncItemStatus.Uploaded),
                    Downloaded: results.Count(r => r.Status == SyncItemStatus.Downloaded),
                    Conflicts: results.Count(r => r.Status == SyncItemStatus.SkippedConflict),
                    BytesCopied: results
                        .Where(r => r.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded)
                        .Sum(r => r.Bytes),
                    UploadedRuns: results
                        .Where(r => r.Status == SyncItemStatus.Uploaded)
                        .Select(r => r.Item.RunName)
                        .ToList(),
                    DownloadedRuns: results
                        .Where(r => r.Status == SyncItemStatus.Downloaded)
                        .Select(r => r.Item.RunName)
                        .ToList()));

                await _remote.ReplaceProviderMetadataAsync(
                    SyncLogRelativePath,
                    JsonSerializer.Serialize(
                        log,
                        new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(new TransferPreviewWarning(
                    "SyncLogWriteFailed",
                    $"The sync itself succeeded, but the sync log could not be updated: {ex.Message}",
                    TransferWarningSeverity.Warning));
            }
        }

        private static List<SyncLogEntry> ParseSyncLog(string? content)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    return new List<SyncLogEntry>();

                return JsonSerializer.Deserialize<List<SyncLogEntry>>(content)
                       ?? new List<SyncLogEntry>();
            }
            catch
            {
                // An unreadable log never blocks syncing.
                return new List<SyncLogEntry>();
            }
        }

        // ---------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------

        // Equivalence ignores backup-file paths (they are machine-specific)
        // and compares identity, counts, and per-file content hashes.
        /// <summary>
        /// True when a deserialized manifest can be compared and reported
        /// without dereferencing a null. `System.Text.Json` does not enforce a
        /// record's non-nullable reference members, so any manifest read from a
        /// remote, or from a local file written by an older or interrupted run,
        /// can arrive with null members.
        /// </summary>
        private static bool IsUsableManifest(TransferBackupManifest? manifest)
        {
            if (manifest is null ||
                manifest.Kind is null ||
                manifest.Game is null ||
                manifest.SteamAppId is null ||
                manifest.Items is null)
            {
                return false;
            }

            foreach (TransferOverwriteBackupItem item in manifest.Items)
            {
                if (item is null || item.OriginalFile is null || item.Sha256 is null)
                    return false;
            }

            return true;
        }

        private static bool ManifestsEquivalent(
            TransferBackupManifest left,
            TransferBackupManifest right)
        {
            // Either side can come from a file on disk or from a remote, so
            // neither is trusted to be fully populated. An unusable manifest is
            // never "equivalent"; the run is reported as a conflict instead,
            // which is the safe outcome because conflicts are never copied.
            if (!IsUsableManifest(left) || !IsUsableManifest(right))
                return false;

            if (!left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase) ||
                !left.SteamAppId.Equals(right.SteamAppId, StringComparison.OrdinalIgnoreCase) ||
                left.StartedUtc != right.StartedUtc ||
                left.FileCount != right.FileCount ||
                left.TotalBytes != right.TotalBytes)
            {
                return false;
            }

            var leftFiles = left.Items
                .Select(i => (i.OriginalFile.ToUpperInvariant(), i.Sha256.ToUpperInvariant()))
                .ToHashSet();

            var rightFiles = right.Items
                .Select(i => (i.OriginalFile.ToUpperInvariant(), i.Sha256.ToUpperInvariant()))
                .ToHashSet();

            return leftFiles.SetEquals(rightFiles);
        }

        private void RecordHistory(
            SyncOptions options,
            SyncResult result,
            DateTimeOffset startedUtc)
        {
            try
            {
                string? blockedReason = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?
                    .Message;

                _historyRepository.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.Sync,
                    GameName: "(backup sync)",
                    SteamAppId: "-",
                    SourceAccountId: Environment.MachineName,
                    TargetAccountId: _remoteRootRaw,
                    DryRun: options.DryRun,
                    OverwriteEnabled: false,
                    BackupEnabled: false,
                    FilesConsidered: result.Items.Count,
                    FilesCopied: result.Uploaded + result.Downloaded,
                    FilesSkipped: result.Skipped,
                    FilesFailed: result.Items.Count(i => i.Status == SyncItemStatus.Failed),
                    BytesCopied: result.BytesCopied,
                    FilesBackedUp: 0,
                    BackupRootPath: null,
                    BlockedReason: blockedReason,
                    StartedUtc: startedUtc,
                    CompletedUtc: DateTimeOffset.UtcNow,
                    Items: result.Items
                        .Select(i => new TransferRunItemRecord(
                            i.Item.LocalPath ?? i.Item.RunName,
                            i.Item.RemotePath ?? string.Empty,
                            i.Bytes,
                            i.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded,
                            i.Status.ToString(),
                            i.Error,
                            BackupFile: null))
                        .ToList()));
            }
            catch
            {
                // History is an audit trail; a recording failure must never
                // fail the sync itself.
            }
        }

        private SyncResult RecordAndBuild(
            SyncPlan plan,
            SyncOptions options,
            IReadOnlyList<SyncItemResult> results,
            List<TransferPreviewWarning> warnings,
            DateTimeOffset startedUtc)
        {
            int uploaded = results.Count(r => r.Status == SyncItemStatus.Uploaded);
            int downloaded = results.Count(r => r.Status == SyncItemStatus.Downloaded);

            int skipped = results.Count(r =>
                r.Status is SyncItemStatus.SkippedConflict
                    or SyncItemStatus.SkippedAlreadyExists
                    or SyncItemStatus.SkippedDeselected);

            long bytesCopied = results
                .Where(r => r.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded)
                .Sum(r => r.Bytes);

            var result = new SyncResult(
                Plan: plan,
                DryRun: options.DryRun,
                Uploaded: uploaded,
                Downloaded: downloaded,
                Skipped: skipped,
                BytesCopied: bytesCopied,
                Items: results,
                Warnings: warnings);

            RecordHistory(options, result, startedUtc);

            return result;
        }
    }
}
