using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class BackupHistoryViewModel : ViewModelBase
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly IBackupRestoreService _backupRestoreService;
        private readonly IBackupCleanupService _backupCleanupService;
        private readonly IBackupArchiveService _backupArchiveService;
        private readonly Services.IFolderPickerService _folderPickerService;
        private readonly ProfilesViewModel _profilesViewModel;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Refresh to list backup runs.";

        [ObservableProperty]
        private BackupRunRowViewModel? selectedRun;

        [ObservableProperty]
        private bool confirmRestore;

        [ObservableProperty]
        private bool overwriteCurrentFiles;

        [ObservableProperty]
        private bool restoreToOriginal = true;

        [ObservableProperty]
        private bool restoreToSelectedProfile;

        [ObservableProperty]
        private bool restoreToMappingLocation;

        [ObservableProperty]
        private SteamProfileRowViewModel? selectedTargetProfile;

        [ObservableProperty]
        private RestoreMappingOptionRowViewModel? selectedMappingOption;

        [ObservableProperty]
        private bool isLoadingMappings;

        [ObservableProperty]
        private string resolvedTargetDisplay = "";

        public ObservableCollection<SteamProfileRowViewModel> Profiles =>
            _profilesViewModel.Profiles;

        public ObservableCollection<RestoreMappingOptionRowViewModel> MappingOptions { get; } = new();

        partial void OnRestoreToOriginalChanged(bool value)
        {
            if (value)
            {
                RestoreToSelectedProfile = false;
                RestoreToMappingLocation = false;
            }

            UpdateResolvedTargetDisplay();
        }

        partial void OnRestoreToSelectedProfileChanged(bool value)
        {
            if (value)
            {
                RestoreToOriginal = false;
                RestoreToMappingLocation = false;
            }

            UpdateResolvedTargetDisplay();
        }

        partial void OnRestoreToMappingLocationChanged(bool value)
        {
            if (value)
            {
                RestoreToOriginal = false;
                RestoreToSelectedProfile = false;
                _ = LoadMappingOptionsAsync();
            }

            UpdateResolvedTargetDisplay();
        }

        partial void OnSelectedTargetProfileChanged(SteamProfileRowViewModel? value)
        {
            UpdateResolvedTargetDisplay();
        }

        partial void OnSelectedMappingOptionChanged(RestoreMappingOptionRowViewModel? value)
        {
            UpdateResolvedTargetDisplay();
        }

        private async Task LoadMappingOptionsAsync()
        {
            MappingOptions.Clear();
            SelectedMappingOption = null;

            if (SelectedRun is null)
                return;

            var run = SelectedRun;

            try
            {
                IsLoadingMappings = true;

                var options = await _backupRestoreService.GetApprovedMappingTargetsAsync(run.Run);

                // The selection may have changed while resolving.
                if (SelectedRun != run)
                    return;

                foreach (RestoreMappingTargetOption option in options)
                    MappingOptions.Add(new RestoreMappingOptionRowViewModel(option));

                SelectedMappingOption = MappingOptions.FirstOrDefault(o => o.CanUse);
            }
            catch (Exception ex)
            {
                RestoreStatusMessage = $"Failed to load approved mappings: {ex.Message}";
            }
            finally
            {
                IsLoadingMappings = false;
                UpdateResolvedTargetDisplay();
            }
        }

        private void UpdateResolvedTargetDisplay()
        {
            if (SelectedRun is null)
            {
                ResolvedTargetDisplay = "";
                return;
            }

            if (RestoreToSelectedProfile)
            {
                ResolvedTargetDisplay = SelectedTargetProfile is null
                    ? "Resolved target path: select a target profile first."
                    : $"Resolved target path: {Path.Combine(SelectedTargetProfile.Profile.UserDataPath, SelectedRun.Run.Manifest.SteamAppId)}";
            }
            else if (RestoreToMappingLocation)
            {
                if (IsLoadingMappings)
                    ResolvedTargetDisplay = "Resolving approved mappings...";
                else if (MappingOptions.Count == 0)
                    ResolvedTargetDisplay = "No approved save-path mappings exist for this game on this platform.";
                else if (SelectedMappingOption is null)
                    ResolvedTargetDisplay = "Resolved target path: select an approved mapping first.";
                else if (!SelectedMappingOption.CanUse)
                    ResolvedTargetDisplay = $"This mapping cannot be used: {SelectedMappingOption.StatusText}";
                else
                    ResolvedTargetDisplay = $"Resolved target path: {SelectedMappingOption.ResolvedPath}";
            }
            else
            {
                ResolvedTargetDisplay =
                    "Resolved target path: the original locations recorded in the backup manifest.";
            }
        }

        [ObservableProperty]
        private string restoreStatusMessage = "No restore executed.";

        [ObservableProperty]
        private int filesRestored;

        [ObservableProperty]
        private int filesSkipped;

        [ObservableProperty]
        private string bytesRestoredDisplay = "0 B";

        [ObservableProperty]
        private int filesBackedUp;

        [ObservableProperty]
        private string preRestoreBackupMessage = "";

        public ObservableCollection<BackupRunRowViewModel> Runs { get; } = new();

        public ObservableCollection<BackupItemRowViewModel> RunItems { get; } = new();

        public ObservableCollection<BackupRestoreItemResultRowViewModel> RestoreResults { get; } = new();

        public BackupHistoryViewModel(
            IBackupHistoryService backupHistoryService,
            IBackupRestoreService backupRestoreService,
            IBackupCleanupService backupCleanupService,
            IBackupArchiveService backupArchiveService,
            Services.IFolderPickerService folderPickerService,
            ProfilesViewModel profilesViewModel)
        {
            _backupHistoryService = backupHistoryService;
            _backupRestoreService = backupRestoreService;
            _backupCleanupService = backupCleanupService;
            _backupArchiveService = backupArchiveService;
            _folderPickerService = folderPickerService;
            _profilesViewModel = profilesViewModel;
        }

        [ObservableProperty]
        private string archiveStatusMessage = "Export a run as a single ZIP file, or import a previously exported backup ZIP.";

        [RelayCommand]
        private async Task ExportSelectedRunAsync()
        {
            if (IsLoading)
                return;

            if (SelectedRun is null)
            {
                ArchiveStatusMessage = "Select a backup run to export.";
                return;
            }

            try
            {
                string? destination = await _folderPickerService.PickFolderAsync(
                    "Select where the backup ZIP should be created.");

                // Cancel changes nothing.
                if (string.IsNullOrWhiteSpace(destination))
                    return;

                IsLoading = true;
                ArchiveStatusMessage = "Creating the backup ZIP...";

                BackupArchiveExportResult result =
                    await _backupArchiveService.ExportRunAsync(SelectedRun.Run, destination);

                ArchiveStatusMessage = result.Message;
            }
            catch (Exception ex)
            {
                ArchiveStatusMessage = $"Export failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task ImportArchiveAsync()
        {
            if (IsLoading)
                return;

            bool refreshRuns = false;

            try
            {
                string? zipPath = await _folderPickerService.PickFileAsync(
                    "Select a backup ZIP to import.",
                    "Backup ZIP archives",
                    new[] { "*.zip" });

                // Cancel changes nothing.
                if (string.IsNullOrWhiteSpace(zipPath))
                    return;

                IsLoading = true;
                ArchiveStatusMessage = "Importing the backup ZIP...";

                BackupArchiveImportResult result =
                    await _backupArchiveService.ImportArchiveAsync(zipPath);

                ArchiveStatusMessage = result.Message;
                refreshRuns = result.Success;
            }
            catch (Exception ex)
            {
                ArchiveStatusMessage = $"Import failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }

            if (refreshRuns)
                await RefreshRunsAsync();
        }

        [ObservableProperty]
        private string keepNewestRunsText = "10";

        [ObservableProperty]
        private string olderThanDaysText = "";

        [ObservableProperty]
        private bool confirmCleanup;

        [ObservableProperty]
        private bool confirmDeleteRun;

        [ObservableProperty]
        private string cleanupStatusMessage = "No cleanup executed. Preview first to see what would be deleted.";

        public ObservableCollection<BackupCleanupItemRowViewModel> CleanupResults { get; } = new();

        [RelayCommand]
        private Task PreviewCleanupAsync() => RunCleanupAsync(dryRun: true);

        [RelayCommand]
        private Task ExecuteCleanupAsync() => RunCleanupAsync(dryRun: false);

        private async Task RunCleanupAsync(bool dryRun)
        {
            if (IsLoading)
                return;

            if (!int.TryParse(KeepNewestRunsText.Trim(), out int keepNewest) || keepNewest < 0)
            {
                CleanupStatusMessage = "Enter how many newest runs to keep (0 or more).";
                return;
            }

            int? olderThanDays = null;

            if (!string.IsNullOrWhiteSpace(OlderThanDaysText))
            {
                if (!int.TryParse(OlderThanDaysText.Trim(), out int days) || days < 0)
                {
                    CleanupStatusMessage = "\"Older than\" must be empty or a number of days (0 or more).";
                    return;
                }

                olderThanDays = days;
            }

            if (!dryRun && !ConfirmCleanup)
            {
                CleanupStatusMessage = "Cleanup blocked. Confirm the checkbox first.";
                return;
            }

            bool refreshRuns = false;

            try
            {
                IsLoading = true;
                CleanupStatusMessage = dryRun
                    ? "Building cleanup preview (dry run, nothing is deleted)..."
                    : "Deleting old backup runs...";

                BackupCleanupResult result = await _backupCleanupService.CleanupAsync(
                    new BackupCleanupOptions
                    {
                        DryRun = dryRun,
                        ConfirmExecution = ConfirmCleanup,
                        KeepNewestRuns = keepNewest,
                        DeleteOlderThanDays = olderThanDays
                    });

                CleanupResults.Clear();

                foreach (BackupCleanupItemResult item in result.Items)
                    CleanupResults.Add(new BackupCleanupItemRowViewModel(item));

                TransferPreviewWarning? blocker = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error);

                if (blocker is not null)
                {
                    CleanupStatusMessage = $"Cleanup blocked: {blocker.Message}";
                }
                else if (result.RunsConsidered == 0)
                {
                    CleanupStatusMessage = "No backup runs match the retention policy. Nothing to delete.";
                }
                else
                {
                    CleanupStatusMessage = dryRun
                        ? $"Cleanup preview: {result.RunsConsidered} run(s) would be deleted, freeing {FormatBytes(result.BytesFreed)}. Nothing was deleted."
                        : $"Cleanup finished. Deleted {result.RunsDeleted} run(s), skipped {result.RunsSkipped}, freed {FormatBytes(result.BytesFreed)}.";
                }

                if (!dryRun && result.RunsDeleted > 0)
                {
                    ConfirmCleanup = false;
                    refreshRuns = true;
                }
            }
            catch (Exception ex)
            {
                CleanupStatusMessage = $"Cleanup failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }

            if (refreshRuns)
                await RefreshRunsAsync();
        }

        [RelayCommand]
        private async Task DeleteSelectedRunAsync()
        {
            if (IsLoading)
                return;

            if (SelectedRun is null)
            {
                CleanupStatusMessage = "Select a backup run to delete.";
                return;
            }

            if (!ConfirmDeleteRun)
            {
                CleanupStatusMessage = "Deleting the selected run is blocked. Confirm the checkbox first.";
                return;
            }

            bool refreshRuns = false;

            try
            {
                IsLoading = true;
                CleanupStatusMessage = "Deleting the selected backup run...";

                BackupCleanupResult result = await _backupCleanupService.DeleteRunAsync(
                    SelectedRun.Run,
                    confirmExecution: true);

                CleanupResults.Clear();

                foreach (BackupCleanupItemResult item in result.Items)
                    CleanupResults.Add(new BackupCleanupItemRowViewModel(item));

                CleanupStatusMessage = result.RunsDeleted == 1
                    ? $"Backup run deleted, freeing {FormatBytes(result.BytesFreed)}."
                    : $"The run was not deleted: {result.Items.FirstOrDefault()?.Error ?? "see the results below."}";

                if (result.RunsDeleted > 0)
                {
                    ConfirmDeleteRun = false;
                    refreshRuns = true;
                }
            }
            catch (Exception ex)
            {
                CleanupStatusMessage = $"Deleting the run failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }

            if (refreshRuns)
                await RefreshRunsAsync();
        }

        partial void OnSelectedRunChanged(BackupRunRowViewModel? value)
        {
            RunItems.Clear();
            RestoreResults.Clear();
            ConfirmRestore = false;
            RestoreStatusMessage = "No restore executed.";
            FilesRestored = 0;
            FilesSkipped = 0;
            BytesRestoredDisplay = "0 B";
            FilesBackedUp = 0;
            PreRestoreBackupMessage = "";

            MappingOptions.Clear();
            SelectedMappingOption = null;

            if (value is null)
            {
                UpdateResolvedTargetDisplay();
                return;
            }

            foreach (TransferOverwriteBackupItem item in value.Run.Manifest.Items)
                RunItems.Add(new BackupItemRowViewModel(item));

            if (RestoreToMappingLocation)
                _ = LoadMappingOptionsAsync();

            UpdateResolvedTargetDisplay();
        }

        [RelayCommand]
        private async Task RefreshRunsAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Reading backup history...";

                Runs.Clear();
                SelectedRun = null;

                if (Profiles.Count == 0)
                    await _profilesViewModel.RefreshProfilesCommand.ExecuteAsync(null);

                var runs = await _backupHistoryService.GetRunsAsync();

                foreach (TransferBackupRunInfo run in runs)
                    Runs.Add(new BackupRunRowViewModel(run));

                StatusMessage = Runs.Count == 0
                    ? "No backup runs found. Backups are created automatically before files are overwritten."
                    : $"Found {Runs.Count} backup run(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to read backup history: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private Task PreviewRestoreAsync() => RunRestoreAsync(dryRun: true);

        [RelayCommand]
        private Task ExecuteRestoreAsync() => RunRestoreAsync(dryRun: false);

        private async Task RunRestoreAsync(bool dryRun)
        {
            if (IsLoading)
                return;

            if (SelectedRun is null)
            {
                RestoreStatusMessage = "Select a backup run first.";
                return;
            }

            if (!dryRun && !ConfirmRestore)
            {
                RestoreStatusMessage = "Restore blocked. Confirm the checkbox first.";
                return;
            }

            if (RestoreToSelectedProfile && SelectedTargetProfile is null)
            {
                RestoreStatusMessage = "Select a target profile first, or switch back to \"Restore to Original Location\".";
                return;
            }

            if (RestoreToMappingLocation)
            {
                if (MappingOptions.Count == 0)
                {
                    RestoreStatusMessage = "No approved save-path mappings exist for this game, so there is nothing to restore into. Choose a different restore target.";
                    return;
                }

                if (SelectedMappingOption is null || !SelectedMappingOption.CanUse)
                {
                    RestoreStatusMessage = "Select a usable approved mapping first, or switch to a different restore target.";
                    return;
                }
            }

            try
            {
                IsLoading = true;
                RestoreStatusMessage = dryRun
                    ? "Building restore preview (dry run, nothing is copied)..."
                    : "Copying backup files to the selected target...";

                var options = new BackupRestoreOptions
                {
                    DryRun = dryRun,
                    ConfirmExecution = ConfirmRestore,
                    OverwriteExisting = OverwriteCurrentFiles,
                    VerifyHashes = true,
                    BackupBeforeOverwrite = true,
                    PreserveTimestamps = true,
                    TargetMode = RestoreToSelectedProfile
                        ? BackupRestoreTargetMode.SelectedSteamProfileUserData
                        : RestoreToMappingLocation
                            ? BackupRestoreTargetMode.ApprovedMappingLocation
                            : BackupRestoreTargetMode.OriginalPath,
                    TargetProfile = RestoreToSelectedProfile
                        ? SelectedTargetProfile?.Profile
                        : null,
                    TargetMappingId = RestoreToMappingLocation
                        ? SelectedMappingOption?.MappingId
                        : null
                };

                BackupRestoreResult result =
                    await _backupRestoreService.RestoreAsync(
                        SelectedRun.Run,
                        options);

                RestoreResults.Clear();

                foreach (BackupRestoreItemResult item in result.Items)
                    RestoreResults.Add(new BackupRestoreItemResultRowViewModel(item));

                FilesRestored = result.FilesRestored;
                FilesSkipped = result.FilesSkipped;
                BytesRestoredDisplay = FormatBytes(result.BytesRestored);
                FilesBackedUp = result.FilesBackedUp;
                PreRestoreBackupMessage = result.BackupRootPath is null
                    ? ""
                    : $"Replaced files were backed up to: {result.BackupRootPath}";

                RestoreStatusMessage = dryRun
                    ? $"Restore preview ready. {result.FilesConsidered} file(s) considered, nothing was copied."
                    : $"Restore finished. Restored {FilesRestored} file(s), skipped {FilesSkipped} file(s).";
            }
            catch (Exception ex)
            {
                RestoreStatusMessage = $"Restore failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";

            double kb = bytes / 1024.0;

            if (kb < 1024)
                return $"{kb:0.##} KB";

            double mb = kb / 1024.0;

            if (mb < 1024)
                return $"{mb:0.##} MB";

            double gb = mb / 1024.0;

            return $"{gb:0.##} GB";
        }
    }
}
