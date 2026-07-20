using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class SyncViewModel : ViewModelBase
    {
        private readonly ISyncProviderFactory _syncProviderFactory;
        private readonly IFolderPickerService _folderPickerService;
        private readonly ISyncSettingsStore _syncSettingsStore;
        private SyncPlan? _lastPlan;
        private ISyncProvider? _lastProvider;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Choose a sync folder (NAS share, USB drive, cloud-synced folder) and preview the sync.";

        [ObservableProperty]
        private string remoteRootPath = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLocalFolderSelected))]
        [NotifyPropertyChangedFor(nameof(IsSftpSelected))]
        private SyncProviderKind selectedProviderKind = SyncProviderKind.LocalFolder;

        [ObservableProperty]
        private string sftpHost = "";

        [ObservableProperty]
        private string sftpPort = "22";

        [ObservableProperty]
        private string sftpUsername = "";

        [ObservableProperty]
        private bool sftpUsePassword = true;

        [ObservableProperty]
        private bool sftpUsePrivateKey;

        [ObservableProperty]
        private string sftpPassword = "";

        [ObservableProperty]
        private string sftpKeyFilePath = "";

        [ObservableProperty]
        private string sftpKeyPassphrase = "";

        [ObservableProperty]
        private string sftpRemotePath = "/gamesave-sync";

        [ObservableProperty]
        private bool sftpTrustNewHostKey;

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

        [ObservableProperty]
        private bool targetSectionExpanded = true;

        [ObservableProperty]
        private bool planSectionExpanded = true;

        [ObservableProperty]
        private bool warningsSectionExpanded = true;

        [ObservableProperty]
        private bool resultsSectionExpanded = true;

        [ObservableProperty]
        private bool historySectionExpanded = true;

        [ObservableProperty]
        private string selectedSummaryDisplay = "";

        [ObservableProperty]
        private bool isSyncRunning;

        [ObservableProperty]
        private double progressValue;

        [ObservableProperty]
        private double progressMax = 1;

        [ObservableProperty]
        private string progressText = "";

        [ObservableProperty]
        private string connectionCheckMessage = "";

        private bool _keepTargetSectionOpen;

        /// <summary>
        /// Same dry-run preview, but keeps the connection section open so the
        /// verdict is readable right where the credentials were entered.
        /// </summary>
        [RelayCommand]
        private async Task CheckSyncStatusAsync()
        {
            _keepTargetSectionOpen = true;

            try
            {
                await PreviewSyncAsync();
            }
            finally
            {
                _keepTargetSectionOpen = false;
            }
        }

        private void UpdateConnectionCheckMessage(SyncPlan plan)
        {
            TransferPreviewWarning? error = plan.Warnings
                .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error);

            if (error is not null)
            {
                ConnectionCheckMessage = $"Check failed: {error.Message}";
                return;
            }

            bool everythingInSync =
                plan.UploadCount == 0 &&
                plan.DownloadCount == 0 &&
                plan.ConflictCount == 0;

            ConnectionCheckMessage = everythingInSync
                ? plan.InSyncCount > 0
                    ? $"Connected. Everything is in sync: {plan.InSyncCount} run(s) match the sync target."
                    : "Connected. Neither side has any backup runs yet."
                : $"Connected. In sync: {plan.InSyncCount}, to upload: {plan.UploadCount}, to download: {plan.DownloadCount}, conflicts: {plan.ConflictCount}.";
        }

        public ObservableCollection<SyncItemRowViewModel> Items { get; } = new();

        public ObservableCollection<TransferWarningRowViewModel> Warnings { get; } = new();

        public ObservableCollection<SyncItemResultRowViewModel> ExecutionResults { get; } = new();

        public ObservableCollection<SyncLogEntryRowViewModel> SyncLog { get; } = new();

        public IReadOnlyList<SyncProviderOption> ProviderOptions { get; } =
            new[]
            {
                new SyncProviderOption(
                    SyncProviderKind.LocalFolder,
                    "Local or mounted folder"),
                new SyncProviderOption(
                    SyncProviderKind.Sftp,
                    "SFTP server (SSH)")
            };

        public bool IsLocalFolderSelected =>
            SelectedProviderKind == SyncProviderKind.LocalFolder;

        public bool IsSftpSelected =>
            SelectedProviderKind == SyncProviderKind.Sftp;

        public SyncViewModel(
            ISyncProviderFactory syncProviderFactory,
            IFolderPickerService folderPickerService,
            ISyncSettingsStore syncSettingsStore)
        {
            _syncProviderFactory = syncProviderFactory;
            _folderPickerService = folderPickerService;
            _syncSettingsStore = syncSettingsStore;

            SyncUiSettings saved = _syncSettingsStore.Load();
            selectedProviderKind = saved.SelectedProviderKind;
            remoteRootPath = saved.LocalFolderPath;
            sftpHost = saved.SftpHost;
            sftpPort = saved.SftpPort;
            sftpUsername = saved.SftpUsername;
            sftpUsePrivateKey = saved.SftpUsePrivateKey;
            sftpUsePassword = !saved.SftpUsePrivateKey;
            sftpKeyFilePath = saved.SftpKeyFilePath;
            sftpRemotePath = saved.SftpRemotePath;

            string? unavailable = GetUnavailableProviderMessage(selectedProviderKind);

            if (unavailable is not null)
                statusMessage = unavailable;
        }

        partial void OnRemoteRootPathChanged(string value) => InvalidatePlan();

        partial void OnUploadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnDownloadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnSelectedProviderKindChanged(SyncProviderKind value)
        {
            InvalidatePlan();

            StatusMessage = GetUnavailableProviderMessage(value)
                ?? "Sync provider changed. Configure it and build a new sync preview.";
        }

        partial void OnSftpHostChanged(string value) => InvalidatePlan();

        partial void OnSftpPortChanged(string value) => InvalidatePlan();

        partial void OnSftpUsernameChanged(string value) => InvalidatePlan();

        partial void OnSftpPasswordChanged(string value) => InvalidatePlan();

        partial void OnSftpKeyFilePathChanged(string value) => InvalidatePlan();

        partial void OnSftpKeyPassphraseChanged(string value) => InvalidatePlan();

        partial void OnSftpRemotePathChanged(string value) => InvalidatePlan();

        partial void OnSftpTrustNewHostKeyChanged(bool value) => InvalidatePlan();

        partial void OnSftpUsePasswordChanged(bool value)
        {
            if (value)
                SftpUsePrivateKey = false;

            InvalidatePlan();
        }

        partial void OnSftpUsePrivateKeyChanged(bool value)
        {
            if (value)
                SftpUsePassword = false;

            InvalidatePlan();
        }

        // A plan built against different settings must not stay executable,
        // and a provider holding a live connection must be released.
        private void InvalidatePlan()
        {
            if (_lastPlan is null && _lastProvider is null)
                return;

            _lastPlan = null;
            _lastProvider?.Dispose();
            _lastProvider = null;
            ClearPreview();
            StatusMessage = "Sync settings changed. Build a new sync preview.";
        }

        private ISyncProvider CreateConfiguredProvider()
        {
            return SelectedProviderKind switch
            {
                SyncProviderKind.LocalFolder =>
                    _syncProviderFactory.CreateLocalFolderProvider(RemoteRootPath),

                SyncProviderKind.Sftp =>
                    _syncProviderFactory.CreateSftpProvider(BuildSftpSettings()),

                _ => throw new NotSupportedException(
                    GetUnavailableProviderMessage(SelectedProviderKind)
                    ?? "The selected sync provider is unsupported.")
            };
        }

        private SftpConnectionSettings BuildSftpSettings()
        {
            int port = int.Parse(SftpPort.Trim());

            return new SftpConnectionSettings(
                Host: SftpHost.Trim(),
                Port: port,
                Username: SftpUsername.Trim(),
                AuthMethod: SftpUsePrivateKey ? SftpAuthMethod.PrivateKey : SftpAuthMethod.Password,
                Password: string.IsNullOrEmpty(SftpPassword) ? null : SftpPassword,
                PrivateKeyPath: string.IsNullOrWhiteSpace(SftpKeyFilePath) ? null : SftpKeyFilePath.Trim(),
                PrivateKeyPassphrase: string.IsNullOrEmpty(SftpKeyPassphrase) ? null : SftpKeyPassphrase,
                RemotePath: SftpRemotePath.Trim(),
                TrustNewHostKey: SftpTrustNewHostKey);
        }

        private void SaveNonSecretSettings()
        {
            _syncSettingsStore.Save(new SyncUiSettings(
                SchemaVersion: SyncUiSettings.CurrentSchemaVersion,
                SelectedProviderKind: SelectedProviderKind,
                LocalFolderPath: RemoteRootPath,
                SftpHost: SftpHost,
                SftpPort: SftpPort,
                SftpUsername: SftpUsername,
                SftpUsePrivateKey: SftpUsePrivateKey,
                SftpKeyFilePath: SftpKeyFilePath,
                SftpRemotePath: SftpRemotePath));
        }

        private string? ValidateProviderSelection()
        {
            return SelectedProviderKind switch
            {
                SyncProviderKind.LocalFolder when string.IsNullOrWhiteSpace(RemoteRootPath) =>
                    "Choose a local or mounted sync folder first.",

                SyncProviderKind.LocalFolder => null,

                SyncProviderKind.Sftp => ValidateSftpSelection(),

                _ => GetUnavailableProviderMessage(SelectedProviderKind)
            };
        }

        private string? ValidateSftpSelection()
        {
            if (string.IsNullOrWhiteSpace(SftpHost))
                return "Enter the SFTP host first.";

            if (string.IsNullOrWhiteSpace(SftpUsername))
                return "Enter the SFTP username first.";

            if (!int.TryParse(SftpPort.Trim(), out int port) || port is < 1 or > 65535)
                return "Enter an SFTP port between 1 and 65535.";

            if (string.IsNullOrWhiteSpace(SftpRemotePath))
                return "Enter the SFTP remote folder path.";

            if (SftpUsePrivateKey)
            {
                return string.IsNullOrWhiteSpace(SftpKeyFilePath)
                    ? "Choose an SFTP private key file first."
                    : null;
            }

            return string.IsNullOrEmpty(SftpPassword)
                ? "Enter the SFTP password (it remains session-only)."
                : null;
        }

        private static string? GetUnavailableProviderMessage(SyncProviderKind kind)
        {
            return kind switch
            {
                SyncProviderKind.LocalFolder or SyncProviderKind.Sftp => null,
                SyncProviderKind.GoogleDrive => "Google Drive sync is not implemented yet.",
                SyncProviderKind.WebDav => "WebDAV sync is not implemented yet.",
                SyncProviderKind.OneDrive => "OneDrive sync is not implemented yet.",
                _ => $"Sync provider value {(int)kind} is not supported by this version."
            };
        }

        [RelayCommand]
        private async Task ChooseKeyFileAsync()
        {
            try
            {
                string? picked = await _folderPickerService.PickFileAsync(
                    "Select the SSH private key file.",
                    "Private key files",
                    new[] { "*" });

                // Cancel keeps the current key file unchanged.
                if (!string.IsNullOrWhiteSpace(picked))
                    SftpKeyFilePath = picked;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open the file picker: {ex.Message}";
            }
        }

        [RelayCommand]
        private void ForgetSftpHostKey()
        {
            if (string.IsNullOrWhiteSpace(SftpHost))
            {
                StatusMessage = "Enter the SFTP host first.";
                return;
            }

            if (!int.TryParse(SftpPort.Trim(), out int port) || port is < 1 or > 65535)
            {
                StatusMessage = "Enter an SFTP port between 1 and 65535.";
                return;
            }

            _syncProviderFactory.ForgetSftpHostKey(SftpHost.Trim(), port);
            InvalidatePlan();

            StatusMessage = $"Stored host key for {SftpHost.Trim()}:{port} forgotten. The next connection is treated as a first connect.";
        }

        private void ClearPreview()
        {
            Items.Clear();
            Warnings.Clear();
            SummaryDisplay = "";
            SelectedSummaryDisplay = "";
            ConnectionCheckMessage = "";
            CanExecuteSync = false;
        }

        private void UpdateSelectedSummary()
        {
            var selectable = Items.Where(row => row.IsSelectable).ToList();

            if (selectable.Count == 0)
            {
                SelectedSummaryDisplay = "";
                return;
            }

            var selected = selectable.Where(row => row.IncludeInSync).ToList();

            SelectedSummaryDisplay =
                $"Selected for sync: {selected.Count} of {selectable.Count} run(s) " +
                $"({FormatBytes(selected.Sum(row => row.Item.TotalBytes))})";
        }

        [RelayCommand]
        private void SelectAllRuns()
        {
            foreach (SyncItemRowViewModel row in Items.Where(r => r.IsSelectable))
                row.IncludeInSync = true;
        }

        [RelayCommand]
        private void DeselectAllRuns()
        {
            foreach (SyncItemRowViewModel row in Items.Where(r => r.IsSelectable))
                row.IncludeInSync = false;
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
            _lastProvider?.Dispose();
            _lastProvider = null;
            ClearPreview();

            string? providerIssue = ValidateProviderSelection();

            if (providerIssue is not null)
            {
                StatusMessage = providerIssue;
                ConnectionCheckMessage = $"Check failed: {providerIssue}";
                return;
            }

            if (!UploadEnabled && !DownloadEnabled)
            {
                StatusMessage = "Preview blocked: enable upload, download, or both.";
                return;
            }

            SaveNonSecretSettings();

            try
            {
                IsLoading = true;
                StatusMessage = SelectedProviderKind == SyncProviderKind.Sftp
                    ? "Connecting to the SFTP server and building the sync preview (dry run, nothing is copied)..."
                    : "Building sync preview (dry run, nothing is copied)...";

                ISyncProvider provider = CreateConfiguredProvider();
                _lastProvider = provider;

                SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions
                {
                    Upload = UploadEnabled,
                    Download = DownloadEnabled
                });

                _lastPlan = plan;
                ExecutionResults.Clear();
                ExecutionStatusMessage = "No sync executed.";
                ConfirmSync = false;

                foreach (SyncItem item in plan.Items)
                    Items.Add(new SyncItemRowViewModel(item, UpdateSelectedSummary));

                foreach (TransferPreviewWarning warning in plan.Warnings)
                    Warnings.Add(new TransferWarningRowViewModel(warning));

                SummaryDisplay =
                    $"Upload: {plan.UploadCount} run(s) ({FormatBytes(plan.BytesToUpload)})   " +
                    $"Download: {plan.DownloadCount} run(s) ({FormatBytes(plan.BytesToDownload)})   " +
                    $"In sync: {plan.InSyncCount}   Conflicts: {plan.ConflictCount}";

                UpdateSelectedSummary();
                UpdateConnectionCheckMessage(plan);
                CanExecuteSync = plan.CanExecute;

                if (plan.CanExecute && !_keepTargetSectionOpen)
                {
                    // Tuck the connection settings away so the plan gets the
                    // screen; the expander header brings them back anytime.
                    TargetSectionExpanded = false;
                    PlanSectionExpanded = true;
                }

                StatusMessage = plan.CanExecute
                    ? "Sync preview ready. Untick runs you do not want to copy, then confirm and press Sync Now."
                    : plan.ConflictCount > 0 && plan.UploadCount + plan.DownloadCount == 0
                        ? "Only conflicts remain; nothing can be synced automatically."
                        : "Nothing to sync, or the preview has errors. Check the warnings.";

                await RefreshSyncLogAsync(provider);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to build sync preview: {ex.Message}";
                ConnectionCheckMessage = $"Check failed: {ex.Message}";
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

            var selectedRunNames = Items
                .Where(row => row.IsSelectable && row.IncludeInSync)
                .Select(row => row.RunName)
                .ToList();

            if (selectedRunNames.Count == 0 &&
                _lastPlan.UploadCount + _lastPlan.DownloadCount > 0)
            {
                ExecutionStatusMessage = "No runs are selected. Tick at least one run in the sync plan.";
                return;
            }

            try
            {
                IsLoading = true;
                IsSyncRunning = true;
                ResultsSectionExpanded = true;
                ProgressValue = 0;
                ProgressMax = 1;
                ProgressText = "Starting...";
                ExecutionStatusMessage = "Syncing backup runs...";

                // Progress<T> marshals reports back to the UI thread.
                var progress = new Progress<SyncProgress>(p =>
                {
                    ProgressMax = Math.Max(1, p.BytesTotal);
                    ProgressValue = p.BytesDone;
                    ProgressText =
                        $"Run {Math.Min(p.RunsDone + 1, p.RunsTotal)}/{p.RunsTotal}: {p.RunName}  -  {p.CurrentFile}  " +
                        $"({FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)})";
                });

                SyncResult result = await _lastProvider.ExecuteAsync(
                    _lastPlan,
                    new SyncOptions
                    {
                        DryRun = false,
                        ConfirmExecution = ConfirmSync,
                        Upload = UploadEnabled,
                        Download = DownloadEnabled,
                        OnlyRunNames = selectedRunNames,
                        Progress = progress
                    });

                ExecutionResults.Clear();

                foreach (SyncItemResult item in result.Items)
                    ExecutionResults.Add(new SyncItemResultRowViewModel(item));

                TransferPreviewWarning? blocker = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error);

                ExecutionStatusMessage = blocker is not null
                    ? $"Sync blocked: {blocker.Message}"
                    : $"Sync finished. Uploaded {result.Uploaded} run(s), downloaded {result.Downloaded} run(s), skipped {result.Skipped}, copied {FormatBytes(result.BytesCopied)}. Nothing was deleted.";

                ProgressText = blocker is null
                    ? $"Done: {FormatBytes(result.BytesCopied)} copied."
                    : "";

                await RefreshSyncLogAsync(_lastProvider);
            }
            catch (Exception ex)
            {
                ExecutionStatusMessage = $"Sync failed: {ex.Message}";
                ProgressText = "";
            }
            finally
            {
                IsLoading = false;
                IsSyncRunning = false;
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
