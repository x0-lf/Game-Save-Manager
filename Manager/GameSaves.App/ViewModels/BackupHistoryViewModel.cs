using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class BackupHistoryViewModel : ViewModelBase
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly IBackupRestoreService _backupRestoreService;

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
            IBackupRestoreService backupRestoreService)
        {
            _backupHistoryService = backupHistoryService;
            _backupRestoreService = backupRestoreService;
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

            if (value is null)
                return;

            foreach (TransferOverwriteBackupItem item in value.Run.Manifest.Items)
                RunItems.Add(new BackupItemRowViewModel(item));
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

            try
            {
                IsLoading = true;
                RestoreStatusMessage = dryRun
                    ? "Building restore preview (dry run, nothing is copied)..."
                    : "Restoring files from backup...";

                var options = new BackupRestoreOptions
                {
                    DryRun = dryRun,
                    ConfirmExecution = ConfirmRestore,
                    OverwriteExisting = OverwriteCurrentFiles,
                    VerifyHashes = true,
                    BackupBeforeOverwrite = true,
                    PreserveTimestamps = true
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
