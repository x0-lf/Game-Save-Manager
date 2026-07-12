using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class SyncViewModel : ViewModelBase
    {
        private readonly ISyncProviderFactory _syncProviderFactory;
        private readonly IFolderPickerService _folderPickerService;
        private SyncPlan? _lastPlan;
        private ISyncProvider? _lastProvider;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Choose a sync folder (NAS share, USB drive, cloud-synced folder) and preview the sync.";

        [ObservableProperty]
        private string remoteRootPath = "";

        [ObservableProperty]
        private bool uploadEnabled = true;

        [ObservableProperty]
        private bool downloadEnabled = true;

        [ObservableProperty]
        private bool confirmSync;

        [ObservableProperty]
        private bool canExecuteSync;

        [ObservableProperty]
        private string summaryDisplay = "";

        [ObservableProperty]
        private string executionStatusMessage = "No sync executed.";

        public ObservableCollection<SyncItemRowViewModel> Items { get; } = new();

        public ObservableCollection<TransferWarningRowViewModel> Warnings { get; } = new();

        public ObservableCollection<SyncItemResultRowViewModel> ExecutionResults { get; } = new();

        public ObservableCollection<SyncLogEntryRowViewModel> SyncLog { get; } = new();

        public SyncViewModel(
            ISyncProviderFactory syncProviderFactory,
            IFolderPickerService folderPickerService)
        {
            _syncProviderFactory = syncProviderFactory;
            _folderPickerService = folderPickerService;
        }

        partial void OnRemoteRootPathChanged(string value) => InvalidatePlan();

        partial void OnUploadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnDownloadEnabledChanged(bool value) => InvalidatePlan();

        // A plan built against different settings must not stay executable.
        private void InvalidatePlan()
        {
            if (_lastPlan is null)
                return;

            _lastPlan = null;
            _lastProvider = null;
            ClearPreview();
            StatusMessage = "Sync settings changed. Build a new sync preview.";
        }

        private void ClearPreview()
        {
            Items.Clear();
            Warnings.Clear();
            SummaryDisplay = "";
            CanExecuteSync = false;
        }

        [RelayCommand]
        private async Task ChooseRemoteFolderAsync()
        {
            try
            {
                string? picked = await _folderPickerService.PickFolderAsync(
                    "Select the folder to sync backups with.",
                    RemoteRootPath);

                // Cancel keeps the current folder unchanged.
                if (!string.IsNullOrWhiteSpace(picked))
                    RemoteRootPath = picked;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open the folder picker: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task PreviewSyncAsync()
        {
            if (IsLoading)
                return;

            _lastPlan = null;
            _lastProvider = null;
            ClearPreview();

            if (string.IsNullOrWhiteSpace(RemoteRootPath))
            {
                StatusMessage = "Choose a sync folder first.";
                return;
            }

            if (!UploadEnabled && !DownloadEnabled)
            {
                StatusMessage = "Preview blocked: enable upload, download, or both.";
                return;
            }

            try
            {
                IsLoading = true;
                StatusMessage = "Building sync preview (dry run, nothing is copied)...";

                ISyncProvider provider =
                    _syncProviderFactory.CreateLocalFolderProvider(RemoteRootPath);

                SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions
                {
                    Upload = UploadEnabled,
                    Download = DownloadEnabled
                });

                _lastPlan = plan;
                _lastProvider = provider;
                ExecutionResults.Clear();
                ExecutionStatusMessage = "No sync executed.";
                ConfirmSync = false;

                foreach (SyncItem item in plan.Items)
                    Items.Add(new SyncItemRowViewModel(item));

                foreach (TransferPreviewWarning warning in plan.Warnings)
                    Warnings.Add(new TransferWarningRowViewModel(warning));

                SummaryDisplay =
                    $"Upload: {plan.UploadCount} run(s) ({FormatBytes(plan.BytesToUpload)})   " +
                    $"Download: {plan.DownloadCount} run(s) ({FormatBytes(plan.BytesToDownload)})   " +
                    $"In sync: {plan.InSyncCount}   Conflicts: {plan.ConflictCount}";

                CanExecuteSync = plan.CanExecute;

                StatusMessage = plan.CanExecute
                    ? "Sync preview ready. Nothing was copied."
                    : plan.ConflictCount > 0 && plan.UploadCount + plan.DownloadCount == 0
                        ? "Only conflicts remain; nothing can be synced automatically."
                        : "Nothing to sync, or the preview has errors. Check the warnings.";

                await RefreshSyncLogAsync(provider);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to build sync preview: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task ExecuteSyncAsync()
        {
            if (IsLoading)
                return;

            if (_lastPlan is null || _lastProvider is null)
            {
                ExecutionStatusMessage = "Build a sync preview first.";
                return;
            }

            if (!ConfirmSync)
            {
                ExecutionStatusMessage = "Sync blocked. Confirm the checkbox first.";
                return;
            }

            try
            {
                IsLoading = true;
                ExecutionStatusMessage = "Syncing backup runs...";

                SyncResult result = await _lastProvider.ExecuteAsync(
                    _lastPlan,
                    new SyncOptions
                    {
                        DryRun = false,
                        ConfirmExecution = ConfirmSync,
                        Upload = UploadEnabled,
                        Download = DownloadEnabled
                    });

                ExecutionResults.Clear();

                foreach (SyncItemResult item in result.Items)
                    ExecutionResults.Add(new SyncItemResultRowViewModel(item));

                TransferPreviewWarning? blocker = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error);

                ExecutionStatusMessage = blocker is not null
                    ? $"Sync blocked: {blocker.Message}"
                    : $"Sync finished. Uploaded {result.Uploaded} run(s), downloaded {result.Downloaded} run(s), skipped {result.Skipped}, copied {FormatBytes(result.BytesCopied)}. Nothing was deleted.";

                await RefreshSyncLogAsync(_lastProvider);
            }
            catch (Exception ex)
            {
                ExecutionStatusMessage = $"Sync failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task RefreshSyncLogAsync(ISyncProvider provider)
        {
            try
            {
                var log = await provider.GetSyncLogAsync();

                SyncLog.Clear();

                foreach (SyncLogEntry entry in log)
                    SyncLog.Add(new SyncLogEntryRowViewModel(entry));
            }
            catch
            {
                // The sync log is informational; failures never block the UI.
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
