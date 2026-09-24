using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class SyncViewModel : ViewModelBase
    {
        private readonly ISyncProviderFactory _syncProviderFactory;
        private readonly ISyncProviderCatalog _providerCatalog;
        private readonly IFolderPickerService _folderPickerService;
        private readonly ISyncSettingsStore _syncSettingsStore;
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISyncRemoteProfileService _profileService;
        private readonly IGoogleDriveOAuthService _googleDriveOAuthService;
        private readonly IOneDriveOAuthService? _oneDriveOAuthService;
        private readonly IGoogleDriveRootFolderService _googleDriveRootFolderService;
        private readonly IBackupHistoryService? _backupHistoryService;
        private readonly IUtcClock _clock;
        private readonly IRetryBackoffNotifier? _retryBackoffNotifier;
        private readonly IGoogleDriveDesktopDetector? _googleDriveDesktopDetector;
        private readonly SynchronizationContext? _uiContext;
        private CancellationTokenSource? _countdownCancellation;
        private SyncPlan? _lastPlan;
        private ISyncProvider? _lastProvider;

        // The completed run, kept so revalidation can be retried and so a
        // verification failure never erases what the transfer actually did.
        // _verifiedProvider pins the provider instance the result came from:
        // once the profile or provider changes, the old result must not be
        // revalidated against a different endpoint.
        private SyncResult? _lastResult;
        private ISyncProvider? _verifiedProvider;
        private CancellationTokenSource? _verificationCancellation;
        private bool _applyingProfile;
        private bool _suppressProfileSelection;
        private bool _suppressProfileOptionSelection;
        private CancellationTokenSource? _googleAuthenticationCancellation;
        private long _googleAuthenticationGeneration;
        private bool _googleDriveInteractiveOperation;
        private CancellationTokenSource? _oneDriveAuthenticationCancellation;
        private long _oneDriveAuthenticationGeneration;
        private bool _oneDriveInteractiveOperation;
        private bool _isBulkLoadingItems;
        private CancellationTokenSource? _googleRootFolderCancellation;

        // Owned by ExecuteSyncAsync for the lifetime of one run. Until
        // Milestone Y every layer beneath this view model honoured a
        // cancellation token and this one never created it, so the token they
        // all respected was always default and a running sync could not be
        // stopped at all.
        private CancellationTokenSource? _syncCancellation;
        private long _googleRootFolderGeneration;

        private enum GoogleDriveInteractiveOperation
        {
            Connect,
            Reconnect
        }

        private enum GoogleDriveRootOperation
        {
            Inspect,
            Ensure,
            Recreate
        }

        private sealed record GoogleDriveUiSnapshot(
            GoogleDriveConnectionStatus Status,
            string? AccountDisplayName,
            string? AccountEmail,
            bool HasStoredAuthentication);

        private enum OneDriveInteractiveOperation
        {
            Connect,
            Reconnect
        }

        private sealed record OneDriveUiSnapshot(
            OneDriveConnectionStatus Status,
            string? AccountDisplayName,
            string? AccountEmail,
            bool HasStoredOneDriveAuthentication);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSwitchToGoogleDriveDesktop))]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "No remote profile saved yet.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanPreviewSync))]
        [NotifyPropertyChangedFor(nameof(IsTargetingGoogleDriveDesktop))]
        private string remoteRootPath = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLocalFolderSelected))]
        [NotifyPropertyChangedFor(nameof(IsSftpSelected))]
        [NotifyPropertyChangedFor(nameof(IsGoogleDriveSelected))]
        [NotifyPropertyChangedFor(nameof(IsOneDriveSelected))]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseOneDriveForSync))]
        [NotifyPropertyChangedFor(nameof(SelectedProviderSupportsArchiveContainers))]
        [NotifyPropertyChangedFor(nameof(ArchiveSyncNotice))]
        [NotifyPropertyChangedFor(nameof(ShowArchiveSyncNotice))]
        [NotifyPropertyChangedFor(nameof(ArchiveSyncTooltip))]
        [NotifyPropertyChangedFor(nameof(SelectedProviderDescriptor))]
        [NotifyPropertyChangedFor(nameof(RequiresInteractiveLogin))]
        [NotifyPropertyChangedFor(nameof(RequiresServerCredentials))]
        [NotifyPropertyChangedFor(nameof(SupportsResumableUpload))]
        [NotifyPropertyChangedFor(nameof(SupportsRemoteQuota))]
        [NotifyPropertyChangedFor(nameof(SupportsRemoteFolderSelection))]
        [NotifyPropertyChangedFor(nameof(SupportsPersistentAuthentication))]
        [NotifyPropertyChangedFor(nameof(SupportsConnectionTesting))]
        [NotifyPropertyChangedFor(nameof(SupportsLogout))]
        [NotifyPropertyChangedFor(nameof(SupportsOpenRemoteLocation))]
        [NotifyPropertyChangedFor(nameof(CanSelectRemoteFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckConnection))]
        [NotifyPropertyChangedFor(nameof(CanLogout))]
        [NotifyPropertyChangedFor(nameof(CanOpenRemoteLocation))]
        [NotifyPropertyChangedFor(nameof(CanShowQuota))]
        [NotifyPropertyChangedFor(nameof(ProviderCapabilitySummary))]
        [NotifyPropertyChangedFor(nameof(CanPreviewSync))]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseGoogleDriveForSync))]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanShowRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(IsTargetingGoogleDriveDesktop))]
        [NotifyPropertyChangedFor(nameof(ShowGoogleDriveDesktopPromotion))]
        private SyncProviderKind selectedProviderKind = SyncProviderKind.LocalFolder;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanPreviewSync))]
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
        private bool archiveSync;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExecuteSyncNow))]
        private bool confirmSync;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExecuteSyncNow))]
        private bool canExecuteSync;

        // What the plan actually contains, so the direction actions can be
        // withdrawn when there is nothing for them to do rather than offering
        // a transfer that would move nothing.
        [ObservableProperty]
        private bool planHasUploads;

        [ObservableProperty]
        private bool planHasDownloads;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExecuteSyncNow))]
        private bool hasSelectedRuns;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanVerifyLastSync))]
        [NotifyPropertyChangedFor(nameof(CanCancelVerification))]
        [NotifyPropertyChangedFor(nameof(CanPreviewSync))]
        [NotifyPropertyChangedFor(nameof(CanSwitchToGoogleDriveDesktop))]
        private bool isVerifying;

        [ObservableProperty]
        private string verificationStatusMessage =
            "Nothing has been verified in this session yet.";

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
        [NotifyPropertyChangedFor(nameof(CanCancelSync))]
        private bool isSyncRunning;

        [ObservableProperty]
        private bool isRetrying;

        [ObservableProperty]
        private string retryCountdownText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowGoogleDriveDesktopPromotion))]
        private bool isRateLimited;

        [ObservableProperty]
        private string rateLimitDiagnosticMessage = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCancelSync))]
        private bool isCancellingSync;

        [ObservableProperty]
        private double progressValue;

        [ObservableProperty]
        private double progressMax = 1;

        [ObservableProperty]
        private string progressText = "";

        [ObservableProperty]
        private string connectionCheckMessage = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanShowRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseOneDriveForSync))]
        [NotifyPropertyChangedFor(nameof(CanPreviewSync))]
        private SyncRemoteProfile? selectedRemoteProfile;

        [ObservableProperty]
        private SyncRemoteProfileOption? selectedRemoteProfileOption;

        [ObservableProperty]
        private string remoteProfileDisplayName = "";

        // The pristine state is "no profile selected", not "you changed
        // something": a first run must not claim unsaved changes before the
        // user has touched anything. See ui-critic-round-1 finding 13.
        [ObservableProperty]
        private string remoteProfileState = "Unsaved settings (no profile)";

        [ObservableProperty]
        private bool confirmDeleteRemoteProfile;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseGoogleDriveForSync))]
        private bool hasStoredAuthentication;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanCancelGoogleDriveConnection))]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        private bool isGoogleDriveConnecting;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GoogleDriveAccountDisplayText))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveEmailDisplayText))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveAccountLabel))]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectGoogleDrive))]
        private string? googleDriveAccountDisplayName;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GoogleDriveAccountDisplayText))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveEmailDisplayText))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveAccountLabel))]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectGoogleDrive))]
        private string? googleDriveAccountEmail;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectGoogleDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseGoogleDriveForSync))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveAccountDisplayText))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveEmailDisplayText))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveAccountLabel))]
        [NotifyPropertyChangedFor(nameof(GoogleDriveStatusDisplayText))]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanShowRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        private GoogleDriveConnectionStatus googleDriveConnectionStatus =
            GoogleDriveConnectionStatus.NotConfigured;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanDisconnectGoogleDrive))]
        private bool confirmDisconnectGoogleDrive;

        [ObservableProperty]
        private string googleDriveConnectionMessage =
            "Save a Google Drive profile before connecting.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GoogleDriveRootFolderDisplayText))]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanShowRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanUseGoogleDriveForSync))]
        private string? googleDriveRootFolderDisplayName;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GoogleDriveRootFolderStatusDisplayText))]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanShowRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanUseGoogleDriveForSync))]
        private GoogleDriveRootFolderStatus googleDriveRootFolderStatus =
            GoogleDriveRootFolderStatus.Unconfigured;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSetUpGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCheckGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        [NotifyPropertyChangedFor(nameof(CanCancelGoogleDriveRootFolderOperation))]
        private bool isGoogleDriveRootFolderBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRecreateGoogleDriveRootFolder))]
        private bool confirmRecreateGoogleDriveRootFolder;

        [ObservableProperty]
        private string googleDriveRootFolderMessage =
            "Connect Google Drive before setting up its backup folder.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanCancelOneDriveConnection))]
        private bool isOneDriveConnecting;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OneDriveAccountDisplayText))]
        [NotifyPropertyChangedFor(nameof(OneDriveEmailDisplayText))]
        [NotifyPropertyChangedFor(nameof(OneDriveAccountLabel))]
        [NotifyPropertyChangedFor(nameof(RemoteEndpointDisplay))]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectOneDrive))]
        private string? oneDriveAccountDisplayName;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OneDriveAccountDisplayText))]
        [NotifyPropertyChangedFor(nameof(OneDriveEmailDisplayText))]
        [NotifyPropertyChangedFor(nameof(OneDriveAccountLabel))]
        [NotifyPropertyChangedFor(nameof(RemoteEndpointDisplay))]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectOneDrive))]
        private string? oneDriveAccountEmail;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseOneDriveForSync))]
        [NotifyPropertyChangedFor(nameof(OneDriveAccountDisplayText))]
        [NotifyPropertyChangedFor(nameof(OneDriveEmailDisplayText))]
        [NotifyPropertyChangedFor(nameof(OneDriveAccountLabel))]
        [NotifyPropertyChangedFor(nameof(OneDriveStatusDisplayText))]
        private OneDriveConnectionStatus oneDriveConnectionStatus =
            OneDriveConnectionStatus.NotConfigured;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanDisconnectOneDrive))]
        private bool confirmDisconnectOneDrive;

        [ObservableProperty]
        private string oneDriveConnectionMessage =
            "Save a Microsoft OneDrive profile before connecting.";

        [ObservableProperty]
        private string? oneDriveQuotaSummary;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowConnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowReconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanShowDisconnectOneDrive))]
        [NotifyPropertyChangedFor(nameof(CanUseOneDriveForSync))]
        private bool hasStoredOneDriveAuthentication;

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

        public PaginationController<SyncItemRowViewModel> Pagination { get; } = new()
        {
            ItemName = "run",
            PluralItemName = "runs",
        };

        public ObservableCollection<TransferWarningRowViewModel> Warnings { get; } = new();

        public ObservableCollection<SyncItemResultRowViewModel> ExecutionResults { get; } = new();

        public ObservableCollection<SyncLogEntryRowViewModel> SyncLog { get; } = new();

        public ObservableCollection<SyncRemoteProfile> RemoteProfiles { get; } = new();

        public ObservableCollection<SyncRemoteProfileOption> RemoteProfileOptions { get; } = new();

        public IReadOnlyList<SyncProviderDescriptor> ProviderOptions { get; }

        public Task GoogleAuthenticationInitializationTask { get; private set; } =
            Task.CompletedTask;

        public Task GoogleRootFolderInitializationTask { get; private set; } =
            Task.CompletedTask;

        public Task OneDriveAuthenticationInitializationTask { get; private set; } =
            Task.CompletedTask;

        public SyncProviderDescriptor SelectedProviderDescriptor =>
            _providerCatalog.GetDescriptor(SelectedProviderKind);

        public bool RequiresInteractiveLogin =>
            SelectedProviderDescriptor.Capabilities.RequiresInteractiveLogin;

        public bool RequiresServerCredentials =>
            SelectedProviderDescriptor.Capabilities.RequiresServerCredentials;

        public bool SupportsResumableUpload =>
            SelectedProviderDescriptor.Capabilities.SupportsResumableUpload;

        public bool SupportsRemoteQuota =>
            SelectedProviderDescriptor.Capabilities.SupportsRemoteQuota;

        public bool SupportsRemoteFolderSelection =>
            SelectedProviderDescriptor.Capabilities.SupportsRemoteFolderSelection;

        public bool SupportsPersistentAuthentication =>
            SelectedProviderDescriptor.Capabilities.SupportsPersistentAuthentication;

        public bool SupportsConnectionTesting =>
            SelectedProviderDescriptor.Capabilities.SupportsConnectionTesting;

        public bool SupportsLogout =>
            SelectedProviderDescriptor.Capabilities.SupportsLogout;

        public bool SupportsOpenRemoteLocation =>
            SelectedProviderDescriptor.Capabilities.SupportsOpenRemoteLocation;

        public bool CanSelectRemoteFolder =>
            SelectedProviderDescriptor.IsImplemented && SupportsRemoteFolderSelection;

        public bool CanCheckConnection =>
            SelectedProviderDescriptor.IsImplemented && SupportsConnectionTesting;

        public bool CanLogout =>
            SelectedProviderDescriptor.IsImplemented && SupportsLogout;

        public bool CanOpenRemoteLocation =>
            SelectedProviderDescriptor.IsImplemented && SupportsOpenRemoteLocation;

        public bool CanShowQuota =>
            SelectedProviderDescriptor.IsImplemented && SupportsRemoteQuota;

        // Not while a verification is running: preview disposes the provider that
        // the verification is still reading through.
        public bool CanPreviewSync =>
            SelectedProviderDescriptor.IsImplemented &&
            !IsLoading &&
            !IsVerifying &&
            HasPlausibleSyncTarget;

        // Preview is safe, but a preview against a target the user has not
        // even named can only fail; sibling views gate their previews the
        // same way. Plausibility only - full validation still happens in
        // the preview itself.
        private bool HasPlausibleSyncTarget => SelectedProviderKind switch
        {
            SyncProviderKind.LocalFolder =>
                !string.IsNullOrWhiteSpace(RemoteRootPath),
            SyncProviderKind.Sftp =>
                !string.IsNullOrWhiteSpace(SftpHost),
            SyncProviderKind.GoogleDrive => HasUsableGoogleDriveProfile,
            SyncProviderKind.OneDrive => HasUsableOneDriveProfile,
            _ => false,
        };

        // ---------------------------------------------------------------
        // Endpoints, named before the preview runs
        //
        // Everything below is read from configuration already in memory. No
        // remote call is made to render a static description, and every value
        // is chosen so a secret cannot reach it: no password, passphrase, key
        // file content, token, or Drive object ID appears in any of them.
        // ---------------------------------------------------------------

        /// <summary>Where backup runs are read from and written to locally.</summary>
        public string LocalEndpointDisplay =>
            _backupHistoryService?.GetBackupBasePath() is { Length: > 0 } path
                ? path
                : "The local backup base is not available in this session.";

        /// <summary>
        /// The configured far side, named the way the user configured it.
        /// SFTP shows host, port and path and never the credentials; Google
        /// Drive shows the account and the folder's display name and never the
        /// folder ID.
        /// </summary>
        public string RemoteEndpointDisplay => SelectedProviderKind switch
        {
            SyncProviderKind.LocalFolder =>
                string.IsNullOrWhiteSpace(RemoteRootPath)
                    ? "No sync folder chosen yet."
                    : RemoteRootPath,

            SyncProviderKind.Sftp => string.IsNullOrWhiteSpace(SftpHost)
                ? "No SFTP host configured yet."
                : $"{SftpHost}:{(string.IsNullOrWhiteSpace(SftpPort) ? "22" : SftpPort.Trim())}" +
                  $"{(string.IsNullOrWhiteSpace(SftpRemotePath) ? "" : " " + SftpRemotePath.Trim())}",

            SyncProviderKind.GoogleDrive =>
                $"{GoogleDriveEndpointAccount} — {GoogleDriveEndpointFolder}",

            SyncProviderKind.OneDrive =>
                $"{OneDriveEndpointAccount} — AppRoot (GameSave Manager)",

            _ => "This sync provider is not available in this version."
        };

        private string OneDriveEndpointAccount =>
            OneDriveAccountEmail ??
            OneDriveAccountDisplayName ??
            (SelectedRemoteProfile?.ProviderSettings as OneDriveSyncRemoteSettings)
                ?.AccountEmail ??
            "No Microsoft account connected";

        private string GoogleDriveEndpointAccount =>
            GoogleDriveAccountEmail ??
            GoogleDriveAccountDisplayName ??
            (SelectedRemoteProfile?.ProviderSettings as GoogleDriveSyncRemoteSettings)
                ?.AccountEmail ??
            "No Google account connected";

        private string GoogleDriveEndpointFolder =>
            GoogleDriveRootFolderDisplayName ??
            SelectedRemoteProfile?.RemoteRootDisplayName ??
            GoogleDriveApplicationRoot.DisplayName;

        /// <summary>
        /// True while the endpoints describe settings that exist only in this
        /// session. Shown so unsaved settings are never mistaken for a profile.
        /// </summary>
        public bool IsUsingUnsavedSettings => SelectedRemoteProfile is null;

        public string EndpointProfileStateDisplay => IsUsingUnsavedSettings
            ? "Unsaved settings — not stored in any remote profile."
            : $"Profile \"{SelectedRemoteProfile!.DisplayName}\" — {RemoteProfileState}";

        /// <summary>
        /// What is still missing before a preview can run, stated before the
        /// user presses anything. Null when the configuration is complete.
        /// </summary>
        public string? EndpointIssue => ValidateProviderSelection();

        public bool HasEndpointIssue => EndpointIssue is not null;

        /// <summary>
        /// Raises the endpoint descriptions after any change that could make
        /// them stale: provider, profile, target settings, or Drive state.
        /// </summary>
        private void RefreshEndpoints()
        {
            OnPropertyChanged(nameof(LocalEndpointDisplay));
            OnPropertyChanged(nameof(RemoteEndpointDisplay));
            OnPropertyChanged(nameof(IsUsingUnsavedSettings));
            OnPropertyChanged(nameof(EndpointProfileStateDisplay));
            OnPropertyChanged(nameof(EndpointIssue));
            OnPropertyChanged(nameof(HasEndpointIssue));
            OnPropertyChanged(nameof(CanOpenLocalBackupLocation));
            OnPropertyChanged(nameof(CanOpenRemoteLocation));
        }

        public string ProviderCapabilitySummary
        {
            get
            {
                if (!SelectedProviderDescriptor.IsImplemented)
                    return SelectedProviderDescriptor.IsConfigurationAvailable
                        ? SelectedProviderDescriptor.UnavailableMessage ??
                          "Provider configuration is available, but sync is unavailable."
                        : SelectedProviderDescriptor.UnavailableMessage ?? "Provider unavailable.";

                // Product prose, not a feature-flag dump: one sentence for
                // what the provider needs, one for what it supports.
                var needs = new List<string>();

                if (RequiresServerCredentials)
                    needs.Add("server credentials");
                if (RequiresInteractiveLogin)
                    needs.Add("an interactive sign-in");

                var supports = new List<string>();

                if (SupportsConnectionTesting)
                    supports.Add("connection testing");
                if (SupportsRemoteFolderSelection)
                    supports.Add("remote folder selection");
                if (SupportsPersistentAuthentication)
                    supports.Add("staying signed in");

                var sentences = new List<string>();

                if (needs.Count > 0)
                    sentences.Add($"Needs {JoinNaturally(needs)}.");
                if (supports.Count > 0)
                    sentences.Add($"Supports {JoinNaturally(supports)}.");

                return string.Join(" ", sentences);
            }
        }

        private static string JoinNaturally(IReadOnlyList<string> items) =>
            items.Count switch
            {
                1 => items[0],
                2 => $"{items[0]} and {items[1]}",
                _ => string.Join(", ", items.Take(items.Count - 1)) +
                     $", and {items[^1]}",
            };

        public bool IsLocalFolderSelected =>
            SelectedProviderDescriptor.ConfigurationSurface ==
            SyncProviderConfigurationSurface.LocalFolder;

        public bool IsSftpSelected =>
            SelectedProviderDescriptor.ConfigurationSurface ==
            SyncProviderConfigurationSurface.Sftp;

        public bool IsGoogleDriveSelected =>
            SelectedProviderKind == SyncProviderKind.GoogleDrive &&
            SelectedProviderDescriptor.ConfigurationSurface ==
            SyncProviderConfigurationSurface.InteractiveOAuth;

        public bool SelectedProviderSupportsArchiveContainers =>
            SelectedProviderKind != SyncProviderKind.GoogleDrive;

        public string ArchiveSyncTooltip =>
            SelectedProviderSupportsArchiveContainers
                ? "Transfers whole backup runs as a single compressed .7z archive container, reducing cloud API request overhead and transfer latency."
                : "Google Drive operates on loose-file folder sync. Runs are transferred as folders and verified safely.";

        public string? ArchiveSyncNotice =>
            !SelectedProviderSupportsArchiveContainers && ArchiveSync
                ? "Note: Google Drive operates on loose-file sync. Archive container transfer is automatically converted to folder sync."
                : null;

        public bool ShowArchiveSyncNotice =>
            !SelectedProviderSupportsArchiveContainers && ArchiveSync;

        public bool IsGoogleOAuthClientConfigurationAvailable =>
            _googleDriveOAuthService.GetClientConfigurationState().IsAvailable;

        public string GoogleDriveAccountDisplayText =>
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Disconnected ||
            (string.IsNullOrWhiteSpace(GoogleDriveAccountDisplayName) &&
             string.IsNullOrWhiteSpace(GoogleDriveAccountEmail))
                ? "Not connected"
                : GoogleDriveAccountDisplayName ?? GoogleDriveAccountEmail!;

        public string GoogleDriveEmailDisplayText =>
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Disconnected ||
            string.IsNullOrWhiteSpace(GoogleDriveAccountEmail)
                ? "Not available"
                : GoogleDriveAccountEmail;

        public string GoogleDriveAccountLabel =>
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Connected
                ? "Account"
                : (GoogleDriveConnectionStatus is
                       GoogleDriveConnectionStatus.ReauthenticationRequired or
                       GoogleDriveConnectionStatus.StoredAuthenticationAvailable) &&
                  (GoogleDriveAccountDisplayName is not null || GoogleDriveAccountEmail is not null)
                    ? "Previously connected account"
                    : "Account";

        public string GoogleDriveStatusDisplayText => GoogleDriveConnectionStatus switch
        {
            GoogleDriveConnectionStatus.ReauthenticationRequired =>
                "Authorization expired or revoked",
            GoogleDriveConnectionStatus.StoredAuthenticationAvailable =>
                "Checking stored authentication",
            GoogleDriveConnectionStatus.NotConfigured => "Not configured",
            GoogleDriveConnectionStatus.Disconnected => "Disconnected",
            GoogleDriveConnectionStatus.Connecting => "Connecting",
            GoogleDriveConnectionStatus.Connected => "Connected",
            GoogleDriveConnectionStatus.Unavailable => "Unavailable",
            GoogleDriveConnectionStatus.Failed => "Connection failed",
            _ => "Unknown"
        };

        private bool HasUsableGoogleDriveProfile =>
            SelectedRemoteProfile is
            {
                ProviderKind: SyncProviderKind.GoogleDrive,
                SettingsError: null,
                ProviderSettings: GoogleDriveSyncRemoteSettings
                {
                    SchemaVersion: GoogleDriveSyncRemoteSettings.CurrentSchemaVersion,
                    RequestedScope: GoogleDriveAuthorizationScopes.DriveFile
                }
            };

        public bool CanShowConnectGoogleDrive =>
            IsGoogleDriveSelected &&
            HasUsableGoogleDriveProfile &&
            (GoogleDriveConnectionStatus is GoogleDriveConnectionStatus.Disconnected or
                GoogleDriveConnectionStatus.Failed) &&
            !HasStoredAuthentication &&
            GoogleDriveAccountDisplayName is null &&
            GoogleDriveAccountEmail is null;

        public bool CanConnectGoogleDrive =>
            IsGoogleDriveSelected &&
            HasUsableGoogleDriveProfile &&
            CanShowConnectGoogleDrive &&
            !IsGoogleDriveConnecting &&
            IsGoogleOAuthClientConfigurationAvailable;

        public bool CanShowReconnectGoogleDrive =>
            IsGoogleDriveSelected &&
            HasUsableGoogleDriveProfile &&
            ((GoogleDriveConnectionStatus is GoogleDriveConnectionStatus.Connected or
                  GoogleDriveConnectionStatus.ReauthenticationRequired) ||
             (GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Failed &&
              (HasStoredAuthentication ||
               GoogleDriveAccountDisplayName is not null ||
               GoogleDriveAccountEmail is not null)));

        public bool CanReconnectGoogleDrive =>
            CanShowReconnectGoogleDrive &&
            !IsGoogleDriveConnecting &&
            IsGoogleOAuthClientConfigurationAvailable;

        public bool CanShowDisconnectGoogleDrive =>
            IsGoogleDriveSelected &&
            HasUsableGoogleDriveProfile &&
            (HasStoredAuthentication ||
             GoogleDriveConnectionStatus is
                 GoogleDriveConnectionStatus.Connected or
                 GoogleDriveConnectionStatus.ReauthenticationRequired or
                 GoogleDriveConnectionStatus.StoredAuthenticationAvailable ||
             GoogleDriveAccountDisplayName is not null ||
             GoogleDriveAccountEmail is not null);

        public bool CanDisconnectGoogleDrive =>
            CanShowDisconnectGoogleDrive &&
            !IsGoogleDriveConnecting &&
            ConfirmDisconnectGoogleDrive;

        public bool CanCancelGoogleDriveConnection =>
            IsGoogleDriveSelected &&
            IsGoogleDriveConnecting &&
            _googleDriveInteractiveOperation;

        public bool IsOneDriveSelected =>
            SelectedProviderKind == SyncProviderKind.OneDrive &&
            SelectedProviderDescriptor.ConfigurationSurface ==
            SyncProviderConfigurationSurface.InteractiveOAuth;

        private bool IsOneDriveOAuthClientConfigurationAvailable =>
            _oneDriveOAuthService?.GetClientConfigurationState().IsAvailable ?? false;

        public string OneDriveAccountDisplayText =>
            OneDriveConnectionStatus == OneDriveConnectionStatus.Disconnected ||
            (string.IsNullOrWhiteSpace(OneDriveAccountDisplayName) &&
             string.IsNullOrWhiteSpace(OneDriveAccountEmail))
                ? "Not connected"
                : OneDriveAccountDisplayName ?? OneDriveAccountEmail!;

        public string OneDriveEmailDisplayText =>
            OneDriveConnectionStatus == OneDriveConnectionStatus.Disconnected ||
            string.IsNullOrWhiteSpace(OneDriveAccountEmail)
                ? "Not available"
                : OneDriveAccountEmail;

        public string OneDriveAccountLabel =>
            OneDriveConnectionStatus == OneDriveConnectionStatus.Connected
                ? "Account"
                : (OneDriveConnectionStatus is
                       OneDriveConnectionStatus.ReauthenticationRequired or
                       OneDriveConnectionStatus.StoredAuthenticationAvailable) &&
                  (OneDriveAccountDisplayName is not null || OneDriveAccountEmail is not null)
                    ? "Previously connected account"
                    : "Account";

        public string OneDriveStatusDisplayText => OneDriveConnectionStatus switch
        {
            OneDriveConnectionStatus.ReauthenticationRequired =>
                "Authorization expired or revoked",
            OneDriveConnectionStatus.StoredAuthenticationAvailable =>
                "Checking stored authentication",
            OneDriveConnectionStatus.NotConfigured => "Not configured",
            OneDriveConnectionStatus.Disconnected => "Disconnected",
            OneDriveConnectionStatus.Connecting => "Connecting",
            OneDriveConnectionStatus.Connected => "Connected",
            OneDriveConnectionStatus.Unavailable => "Unavailable",
            OneDriveConnectionStatus.Failed => "Connection failed",
            _ => "Unknown"
        };

        private bool HasUsableOneDriveProfile =>
            SelectedRemoteProfile is
            {
                ProviderKind: SyncProviderKind.OneDrive,
                SettingsError: null,
                ProviderSettings: OneDriveSyncRemoteSettings
                {
                    SchemaVersion: OneDriveSyncRemoteSettings.CurrentSchemaVersion
                }
            };

        public bool CanShowConnectOneDrive =>
            IsOneDriveSelected &&
            HasUsableOneDriveProfile &&
            (OneDriveConnectionStatus is OneDriveConnectionStatus.Disconnected or
                OneDriveConnectionStatus.Failed) &&
            !HasStoredOneDriveAuthentication &&
            OneDriveAccountDisplayName is null &&
            OneDriveAccountEmail is null;

        public bool CanConnectOneDrive =>
            IsOneDriveSelected &&
            HasUsableOneDriveProfile &&
            CanShowConnectOneDrive &&
            !IsOneDriveConnecting &&
            IsOneDriveOAuthClientConfigurationAvailable;

        public bool CanShowReconnectOneDrive =>
            IsOneDriveSelected &&
            HasUsableOneDriveProfile &&
            ((OneDriveConnectionStatus is OneDriveConnectionStatus.Connected or
                  OneDriveConnectionStatus.ReauthenticationRequired) ||
             (OneDriveConnectionStatus == OneDriveConnectionStatus.Failed &&
              (HasStoredOneDriveAuthentication ||
               OneDriveAccountDisplayName is not null ||
               OneDriveAccountEmail is not null)));

        public bool CanReconnectOneDrive =>
            CanShowReconnectOneDrive &&
            !IsOneDriveConnecting &&
            IsOneDriveOAuthClientConfigurationAvailable;

        public bool CanShowDisconnectOneDrive =>
            IsOneDriveSelected &&
            HasUsableOneDriveProfile &&
            (HasStoredOneDriveAuthentication ||
             OneDriveConnectionStatus is
                 OneDriveConnectionStatus.Connected or
                 OneDriveConnectionStatus.ReauthenticationRequired or
                 OneDriveConnectionStatus.StoredAuthenticationAvailable ||
             OneDriveAccountDisplayName is not null ||
             OneDriveAccountEmail is not null);

        public bool CanDisconnectOneDrive =>
            CanShowDisconnectOneDrive &&
            !IsOneDriveConnecting &&
            ConfirmDisconnectOneDrive;

        public bool CanCancelOneDriveConnection =>
            IsOneDriveSelected &&
            IsOneDriveConnecting &&
            _oneDriveInteractiveOperation;

        public bool CanUseOneDriveForSync =>
            IsOneDriveSelected &&
            SelectedProviderDescriptor.IsImplemented &&
            OneDriveConnectionStatus == OneDriveConnectionStatus.Connected &&
            HasStoredOneDriveAuthentication;

        public string GoogleDriveRootFolderDisplayText =>
            GoogleDriveRootFolderDisplayName ?? "Not configured";

        public string GoogleDriveRootFolderStatusDisplayText =>
            GoogleDriveRootFolderStatus switch
            {
                GoogleDriveRootFolderStatus.Unconfigured => "Setup required",
                GoogleDriveRootFolderStatus.Checking => "Checking",
                GoogleDriveRootFolderStatus.Ready => "Ready",
                GoogleDriveRootFolderStatus.Moved =>
                    "Moved in My Drive — still linked by ID",
                GoogleDriveRootFolderStatus.Missing => "Missing",
                GoogleDriveRootFolderStatus.Trashed => "Trashed",
                GoogleDriveRootFolderStatus.WrongType => "Invalid folder identity",
                GoogleDriveRootFolderStatus.UnsupportedLocation =>
                    "Unsupported location",
                GoogleDriveRootFolderStatus.Ambiguous =>
                    "Duplicate folders need attention",
                GoogleDriveRootFolderStatus.RecreationConfirmationRequired =>
                    "Replacement confirmation required",
                GoogleDriveRootFolderStatus.Creating => "Creating",
                GoogleDriveRootFolderStatus.ReauthenticationRequired =>
                    "Reconnect Google Drive first",
                GoogleDriveRootFolderStatus.Unavailable =>
                    "Temporarily unavailable",
                _ => "Failed"
            };

        private bool HasSavedGoogleDriveRootId =>
            !string.IsNullOrWhiteSpace(SelectedRemoteProfile?.RemoteFolderId);

        public bool CanSetUpGoogleDriveRootFolder =>
            IsGoogleDriveSelected &&
            HasUsableGoogleDriveProfile &&
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Connected &&
            !HasSavedGoogleDriveRootId &&
            !IsGoogleDriveConnecting &&
            !IsGoogleDriveRootFolderBusy;

        public bool CanCheckGoogleDriveRootFolder =>
            IsGoogleDriveSelected &&
            HasUsableGoogleDriveProfile &&
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Connected &&
            HasSavedGoogleDriveRootId &&
            !IsGoogleDriveConnecting &&
            !IsGoogleDriveRootFolderBusy;

        public bool CanShowRecreateGoogleDriveRootFolder =>
            IsGoogleDriveSelected &&
            HasSavedGoogleDriveRootId &&
            GoogleDriveRootFolderStatus is
                GoogleDriveRootFolderStatus.Missing or
                GoogleDriveRootFolderStatus.Trashed or
                GoogleDriveRootFolderStatus.WrongType or
                GoogleDriveRootFolderStatus.UnsupportedLocation or
                GoogleDriveRootFolderStatus.RecreationConfirmationRequired;

        public bool CanRecreateGoogleDriveRootFolder =>
            CanShowRecreateGoogleDriveRootFolder &&
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Connected &&
            ConfirmRecreateGoogleDriveRootFolder &&
            !IsGoogleDriveConnecting &&
            !IsGoogleDriveRootFolderBusy;

        public bool CanCancelGoogleDriveRootFolderOperation =>
            IsGoogleDriveSelected && IsGoogleDriveRootFolderBusy;

        public bool CanUseGoogleDriveForSync =>
            IsGoogleDriveSelected &&
            SelectedProviderDescriptor.IsImplemented &&
            GoogleDriveConnectionStatus == GoogleDriveConnectionStatus.Connected &&
            HasStoredAuthentication &&
            HasSavedGoogleDriveRootId &&
            GoogleDriveRootFolderStatus is
                GoogleDriveRootFolderStatus.Ready or
                GoogleDriveRootFolderStatus.Moved;

        // The rate-limit banner is Google-specific text; backoff events are
        // only raised by the Google Drive provider today.
        public bool ShowGoogleDriveDesktopPromotion => IsRateLimited && IsGoogleDriveSelected;

        // Offered only when the detector found a real Drive mount: without
        // one, a "switch" would point backups at a folder nothing uploads.
        public bool CanSwitchToGoogleDriveDesktop =>
            !IsLoading &&
            !IsVerifying &&
            _googleDriveDesktopDetector?.DefaultSyncFolderPath is not null;

        public bool IsTargetingGoogleDriveDesktop =>
            IsLocalFolderSelected &&
            !string.IsNullOrWhiteSpace(RemoteRootPath) &&
            _googleDriveDesktopDetector?.MountedDrivePath is { } mounted &&
            RemoteRootPath.Trim().StartsWith(mounted, StringComparison.OrdinalIgnoreCase);

        [RelayCommand]
        public void SwitchToGoogleDriveDesktop()
        {
            if (_googleDriveDesktopDetector?.DefaultSyncFolderPath is not { } targetFolder)
            {
                StatusMessage =
                    "Google Drive for Desktop was not detected. Install it and sign in, then restart the app.";
                return;
            }

            // Starts an unsaved Local folder setup rather than rewriting the
            // selected profile: saving would otherwise convert a Google Drive
            // profile into a Local folder one and orphan its stored token.
            NewRemoteProfile();
            SelectedProviderKind = SyncProviderKind.LocalFolder;
            RemoteRootPath = targetFolder;
            StatusMessage = $"Switched to Google Drive for Desktop ({targetFolder}). Local files copy directly to the mounted Drive and sync in the background.";
            ClearRateLimitDiagnostics();
        }

        private void ClearRateLimitDiagnostics()
        {
            IsRateLimited = false;
            RateLimitDiagnosticMessage = "";
        }

        private void OnRetryBackoffStarted(object? sender, RetryBackoffEventArgs e)
        {
            // Handlers write bound properties. The notifier is raised from
            // inside the retry wrapper, on whatever thread the transfer runs.
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => OnRetryBackoffStarted(sender, e), null);
                return;
            }

            IsRetrying = true;
            if (e.IsRateLimited)
            {
                IsRateLimited = true;
                RateLimitDiagnosticMessage = FormatRateLimitDiagnostic();
            }

            _countdownCancellation?.Cancel();
            _countdownCancellation?.Dispose();
            _countdownCancellation = new CancellationTokenSource();
            CancellationToken token = _countdownCancellation.Token;

            _ = RunCountdownTimerAsync(e, token);
        }

        private void OnRetryBackoffEnded(object? sender, RetryBackoffEventArgs e)
        {
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => OnRetryBackoffEnded(sender, e), null);
                return;
            }

            _countdownCancellation?.Cancel();
            IsRetrying = false;
            RetryCountdownText = "";
        }

        private async Task RunCountdownTimerAsync(RetryBackoffEventArgs e, CancellationToken token)
        {
            int remainingSeconds = Math.Max(1, (int)Math.Ceiling(e.Delay.TotalSeconds));
            string reason = e.IsRateLimited
                ? "Rate limited by provider"
                : "Temporary transfer error";

            while (remainingSeconds > 0 && !token.IsCancellationRequested)
            {
                RetryCountdownText =
                    $"{reason}. Retrying attempt {e.Attempt + 1}/{e.MaxAttempts} in {remainingSeconds}s...";

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                remainingSeconds--;
            }
        }

        public static string FormatRateLimitDiagnostic()
        {
            return "Google Drive API rate limit reached (HTTP 429 Too Many Requests / rateLimitExceeded). " +
                   "Direct Google Drive API usage is subject to per-minute request limits. " +
                   "For fast, unrestricted bulk transfers, use Google Drive for Desktop with the Local Folder provider. " +
                   "Local copy success confirms files are written to the mounted drive; Google Drive for Desktop manages cloud upload and synchronization.";
        }

        /// <summary>This page's panel arrangement.</summary>
        public GameSaves.App.Services.IWorkspaceLayoutPage Workspace { get; }

        public SyncViewModel(
            ISyncProviderFactory syncProviderFactory,
            ISyncProviderCatalog providerCatalog,
            IFolderPickerService folderPickerService,
            ISyncSettingsStore syncSettingsStore,
            ISyncRemoteProfileRepository profileRepository,
            ISyncRemoteProfileService profileService,
            ISyncRemoteProfileMigrationService profileMigrationService,
            IUtcClock clock,
            IGoogleDriveOAuthService googleDriveOAuthService,
            GameSaves.App.Services.WorkspaceLayoutService workspaceLayout,
            IGoogleDriveRootFolderService? googleDriveRootFolderService = null,
            IBackupHistoryService? backupHistoryService = null,
            IRetryBackoffNotifier? retryBackoffNotifier = null,
            IGoogleDriveDesktopDetector? googleDriveDesktopDetector = null,
            IOneDriveOAuthService? oneDriveOAuthService = null)
        {
            Workspace = workspaceLayout.Page(
                GameSaves.App.Services.UiRailLayoutSettings.TabSync);

            _syncProviderFactory = syncProviderFactory;
            _providerCatalog = providerCatalog;
            _folderPickerService = folderPickerService;
            _syncSettingsStore = syncSettingsStore;
            _profileRepository = profileRepository;
            _profileService = profileService;
            _clock = clock;
            _googleDriveOAuthService = googleDriveOAuthService;
            _oneDriveOAuthService = oneDriveOAuthService;
            // Keep direct construction compatible with the pre-Milestone-L
            // ViewModel contract. Application DI always supplies the real
            // Infrastructure service; the fallback performs no external work.
            _googleDriveRootFolderService =
                googleDriveRootFolderService ??
                UnavailableGoogleDriveRootFolderService.Instance;
            // Only ever read for the local endpoint description and for opening
            // the backup folder. Absent in view-model tests that build this
            // type directly, which then say so rather than inventing a path.
            _backupHistoryService = backupHistoryService;
            _retryBackoffNotifier = retryBackoffNotifier;
            _googleDriveDesktopDetector = googleDriveDesktopDetector;
            _uiContext = SynchronizationContext.Current;

            if (_retryBackoffNotifier is not null)
            {
                _retryBackoffNotifier.BackoffStarted += OnRetryBackoffStarted;
                _retryBackoffNotifier.BackoffEnded += OnRetryBackoffEnded;
            }

            ProviderOptions = _providerCatalog.GetAll()
                .Where(descriptor => descriptor.IsConfigurationAvailable)
                .ToArray();

            SyncUiSettings saved = profileMigrationService.LoadAndMigrate();
            selectedProviderKind = saved.SelectedProviderKind;
            remoteRootPath = saved.LocalFolderPath;
            sftpHost = saved.SftpHost;
            sftpPort = saved.SftpPort;
            sftpUsername = saved.SftpUsername;
            sftpUsePrivateKey = saved.SftpUsePrivateKey;
            sftpUsePassword = !saved.SftpUsePrivateKey;
            sftpKeyFilePath = saved.SftpKeyFilePath;
            sftpRemotePath = saved.SftpRemotePath;

            LoadProfiles(saved.SelectedRemoteProfileId);

            string? unavailable = GetUnavailableProviderMessage(selectedProviderKind);

            if (unavailable is not null)
                statusMessage = unavailable;

            Items.CollectionChanged += (_, _) =>
            {
                if (!_isBulkLoadingItems)
                {
                    Pagination.SetSource(Items);
                }
            };
        }

        partial void OnRemoteRootPathChanged(string value) => OnPersistentSettingChanged();

        partial void OnUploadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnDownloadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnArchiveSyncChanged(bool value)
        {
            OnPropertyChanged(nameof(ArchiveSyncNotice));
            OnPropertyChanged(nameof(ShowArchiveSyncNotice));
            InvalidatePlan();
        }

        partial void OnIsLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanPreviewSync));
            OnPropertyChanged(nameof(CanExecuteSyncNow));
            OnPropertyChanged(nameof(CanVerifyLastSync));
        }

        partial void OnSelectedProviderKindChanged(SyncProviderKind value)
        {
            CancelAllAuthentication();
            ClearRateLimitDiagnostics();

            if (value != SyncProviderKind.Sftp)
                ClearSessionOnlySftpState();

            InvalidatePlan();
            MarkProfileDirty();

            StatusMessage = GetUnavailableProviderMessage(value)
                ?? "Sync provider changed. Configure it and build a new sync preview.";

            if (value == SyncProviderKind.GoogleDrive && SelectedRemoteProfile is null)
            {
                GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.NotConfigured;
                GoogleDriveConnectionMessage =
                    "Save the Google Drive profile before connecting so its authentication can be stored securely.";
            }
            else if (value == SyncProviderKind.OneDrive && SelectedRemoteProfile is null)
            {
                OneDriveConnectionStatus = OneDriveConnectionStatus.NotConfigured;
                OneDriveConnectionMessage =
                    "Save the Microsoft OneDrive profile before connecting so its authentication can be stored securely.";
            }
        }

        partial void OnSftpHostChanged(string value) => OnPersistentSettingChanged();

        partial void OnSftpPortChanged(string value) => OnPersistentSettingChanged();

        partial void OnSftpUsernameChanged(string value) => OnPersistentSettingChanged();

        partial void OnSftpPasswordChanged(string value) => InvalidatePlan();

        partial void OnSftpKeyFilePathChanged(string value) => OnPersistentSettingChanged();

        partial void OnSftpKeyPassphraseChanged(string value) => InvalidatePlan();

        partial void OnSftpRemotePathChanged(string value) => OnPersistentSettingChanged();

        partial void OnSftpTrustNewHostKeyChanged(bool value) => InvalidatePlan();

        partial void OnSftpUsePasswordChanged(bool value)
        {
            if (value)
                SftpUsePrivateKey = false;

            InvalidatePlan();
            MarkProfileDirty();
        }

        partial void OnSftpUsePrivateKeyChanged(bool value)
        {
            if (value)
                SftpUsePassword = false;

            InvalidatePlan();
            MarkProfileDirty();
        }

        partial void OnRemoteProfileDisplayNameChanged(string value) => MarkProfileDirty();

        // The Google Drive endpoint is assembled from these four, and none of
        // them route through InvalidatePlan.
        partial void OnRemoteProfileStateChanged(string value) => RefreshEndpoints();

        partial void OnGoogleDriveAccountEmailChanged(string? value) => RefreshEndpoints();

        partial void OnGoogleDriveAccountDisplayNameChanged(string? value) =>
            RefreshEndpoints();

        partial void OnGoogleDriveRootFolderDisplayNameChanged(string? value) =>
            RefreshEndpoints();

        partial void OnGoogleDriveConnectionStatusChanged(
            GoogleDriveConnectionStatus value) => RefreshEndpoints();

        partial void OnGoogleDriveRootFolderStatusChanged(
            GoogleDriveRootFolderStatus value) => RefreshEndpoints();

        partial void OnSelectedRemoteProfileChanged(SyncRemoteProfile? value)
        {
            ConfirmDisconnectGoogleDrive = false;
            ConfirmRecreateGoogleDriveRootFolder = false;

            if (!_suppressProfileSelection && value is not null)
            {
                SelectProfileOption(value.Id);
                ApplyRemoteProfile(value, persistSelection: true);
            }

            RefreshEndpoints();
        }

        partial void OnSelectedRemoteProfileOptionChanged(SyncRemoteProfileOption? value)
        {
            if (_suppressProfileOptionSelection || value is null)
                return;

            if (value.Profile is null)
            {
                UseWithoutSavedProfile();
                return;
            }

            _suppressProfileSelection = true;
            SelectedRemoteProfile = value.Profile;
            _suppressProfileSelection = false;
            HasStoredAuthentication = false;
            ApplyRemoteProfile(value.Profile, persistSelection: true);
        }

        private void OnPersistentSettingChanged()
        {
            InvalidatePlan();
            MarkProfileDirty();
        }

        private void MarkProfileDirty()
        {
            if (!_applyingProfile && SelectedRemoteProfile is not null)
                RemoteProfileState = "Unsaved changes";
        }

        // A plan built against different settings must not stay executable,
        // and a provider holding a live connection must be released.
        private void InvalidatePlan(bool force = false)
        {
            // Endpoints are re-read even when there is no plan to drop: they
            // describe the settings, not the plan, and a stale endpoint is
            // exactly what makes "Upload" point the wrong way.
            RefreshEndpoints();

            if (!force && _lastPlan is null && _lastProvider is null)
                return;

            _lastPlan = null;
            // A check still reading the old endpoint must not outlive it.
            _verificationCancellation?.Cancel();
            _lastProvider?.Dispose();
            _lastProvider = null;
            // The completed result stays on screen, but it can no longer be
            // revalidated: the endpoint it was produced against is gone.
            _verifiedProvider = null;
            ClearPreview();
            ConfirmSync = false;
            OnPropertyChanged(nameof(CanVerifyLastSync));
            StatusMessage = "Sync settings changed. Build a new sync preview.";
        }

        private void LoadProfiles(Guid? selectedProfileId)
        {
            ReplaceProfileCollections(_profileRepository.GetAll());

            if (selectedProfileId is null)
            {
                SelectNoProfileOption();
                return;
            }

            SyncRemoteProfile? selected = RemoteProfiles
                .FirstOrDefault(profile => profile.Id == selectedProfileId.Value);

            if (selected is not null)
            {
                _suppressProfileSelection = true;
                SelectedRemoteProfile = selected;
                _suppressProfileSelection = false;
                SelectProfileOption(selected.Id);
                ApplyRemoteProfile(selected, persistSelection: false);
            }
            else
            {
                SelectNoProfileOption();
            }
        }

        private void UseWithoutSavedProfile()
        {
            CancelAllAuthentication();
            _suppressProfileSelection = true;
            SelectedRemoteProfile = null;
            _suppressProfileSelection = false;

            _applyingProfile = true;

            try
            {
                RemoteProfileDisplayName = "";
                ClearSessionOnlySftpState();
            }
            finally
            {
                _applyingProfile = false;
            }

            InvalidatePlan(force: true);
            HasStoredAuthentication = false;
            ResetGoogleDriveState();
            ResetOneDriveState();
            ConfirmDeleteRemoteProfile = false;
            RemoteProfileState = "Unsaved settings (no profile)";
            StatusMessage = "Using the current sync settings without a saved remote profile. Build a new preview when ready.";
            SaveNonSecretSettings();
        }

        private void ApplyRemoteProfile(
            SyncRemoteProfile profile,
            bool persistSelection)
        {
            CancelAllAuthentication();
            ClearRateLimitDiagnostics();
            HasStoredAuthentication = false;
            _applyingProfile = true;

            try
            {
                ClearSessionOnlySftpState();
                SelectedProviderKind = profile.ProviderKind;
                RemoteProfileDisplayName = profile.DisplayName;

                // Account state, quota and connection status always belong to
                // one profile. Reset them all once, before the case below loads
                // the new profile's own values, so nothing from the previously
                // selected profile survives the switch.
                ResetGoogleDriveState();
                ResetOneDriveState();

                switch (profile.ProviderSettings)
                {
                    case LocalFolderSyncRemoteSettings local:
                        RemoteRootPath = local.LocalFolderPath;
                        ResetSftpNonSecretFields();
                        break;

                    case SftpSyncRemoteSettings sftp:
                        RemoteRootPath = "";
                        SftpHost = sftp.Host;
                        SftpPort = sftp.Port.ToString();
                        SftpUsername = sftp.Username;
                        SftpUsePrivateKey =
                            sftp.AuthenticationMethod == SftpAuthMethod.PrivateKey;
                        SftpUsePassword = !SftpUsePrivateKey;
                        SftpKeyFilePath = sftp.PrivateKeyFilePath ?? "";
                        SftpRemotePath = sftp.RemotePath;
                        break;

                    case GoogleDriveSyncRemoteSettings googleDrive:
                        RemoteRootPath = "";
                        ResetSftpNonSecretFields();
                        GoogleDriveAccountDisplayName = profile.AccountDisplayName;
                        GoogleDriveAccountEmail = googleDrive.AccountEmail;
                        GoogleDriveConnectionStatus =
                            GoogleDriveConnectionStatus.StoredAuthenticationAvailable;
                        GoogleDriveConnectionMessage =
                            "Checking stored Google Drive authentication…";
                        GoogleDriveRootFolderDisplayName =
                            profile.RemoteRootDisplayName;
                        GoogleDriveRootFolderStatus =
                            string.IsNullOrWhiteSpace(profile.RemoteFolderId)
                                ? GoogleDriveRootFolderStatus.Unconfigured
                                : GoogleDriveRootFolderStatus.Checking;
                        GoogleDriveRootFolderMessage =
                            string.IsNullOrWhiteSpace(profile.RemoteFolderId)
                                ? "No Google Drive backup folder is configured."
                                : "The saved Google Drive backup folder will be validated after authentication.";
                        break;

                    case OneDriveSyncRemoteSettings oneDrive:
                        RemoteRootPath = "";
                        ResetSftpNonSecretFields();
                        OneDriveAccountDisplayName = profile.AccountDisplayName;
                        OneDriveAccountEmail = oneDrive.AccountEmail;
                        OneDriveConnectionStatus =
                            OneDriveConnectionStatus.StoredAuthenticationAvailable;
                        OneDriveConnectionMessage =
                            "Checking stored Microsoft OneDrive authentication…";
                        break;

                    default:
                        RemoteRootPath = "";
                        ResetSftpNonSecretFields();
                        break;
                }
            }
            finally
            {
                _applyingProfile = false;
            }

            InvalidatePlan(force: true);
            ConfirmDeleteRemoteProfile = false;
            RemoteProfileState = profile.SettingsError is null &&
                                 GetUnavailableProviderMessage(profile.ProviderKind) is null
                ? "Saved"
                : "Profile unavailable";
            StatusMessage = profile.SettingsError ??
                            GetUnavailableProviderMessage(profile.ProviderKind) ??
                            profile.ProviderSettings switch
                            {
                                GoogleDriveSyncRemoteSettings =>
                                    "Loaded the Google Drive profile. Checking stored authentication without opening a browser.",
                                OneDriveSyncRemoteSettings =>
                                    "Loaded the Microsoft OneDrive profile. Checking stored authentication without opening a browser.",
                                _ => $"Loaded remote profile '{profile.DisplayName}'. Build a new sync preview when ready."
                            };

            if (persistSelection)
                SaveNonSecretSettings();

            if (profile.ProviderKind == SyncProviderKind.GoogleDrive &&
                profile.ProviderSettings is GoogleDriveSyncRemoteSettings)
            {
                BeginGoogleAuthenticationRestore(profile.Id);
            }
            else if (profile.ProviderKind == SyncProviderKind.OneDrive &&
                profile.ProviderSettings is OneDriveSyncRemoteSettings)
            {
                BeginOneDriveAuthenticationRestore(profile.Id);
            }
            else
            {
                _ = RefreshStoredAuthenticationAsync(profile.Id);
            }
        }

        [RelayCommand]
        private void NewRemoteProfile()
        {
            CancelAllAuthentication();
            _suppressProfileSelection = true;
            SelectedRemoteProfile = null;
            _suppressProfileSelection = false;
            SelectNoProfileOption();

            _applyingProfile = true;

            try
            {
                RemoteProfileDisplayName = "";
                SelectedProviderKind = SyncProviderKind.LocalFolder;
                RemoteRootPath = "";
                ResetSftpNonSecretFields();
                ClearSessionOnlySftpState();
                ResetGoogleDriveState();
                ResetOneDriveState();
            }
            finally
            {
                _applyingProfile = false;
            }

            InvalidatePlan(force: true);
            HasStoredAuthentication = false;
            ConfirmDeleteRemoteProfile = false;
            RemoteProfileState = "Unsaved changes";
            StatusMessage = "New unsaved remote profile. Configure it, enter a name, then choose Save.";
            SaveNonSecretSettings();
        }

        [RelayCommand]
        private void SaveRemoteProfile()
        {
            try
            {
                DateTimeOffset now = _clock.UtcNow;
                bool isNewProfile = SelectedRemoteProfile is null;
                SyncRemoteProfile profile;

                if (isNewProfile)
                {
                    profile = _profileRepository.Create(BuildProfile(
                        Guid.NewGuid(),
                        createdUtc: now,
                        updatedUtc: now,
                        lastUsedUtc: null,
                        lastSuccessfulConnectionUtc: null));
                }
                else
                {
                    SyncRemoteProfile selected = SelectedRemoteProfile!;
                    profile = _profileRepository.Update(BuildProfile(
                        selected.Id,
                        selected.CreatedUtc,
                        now,
                        selected.LastUsedUtc,
                        selected.LastSuccessfulConnectionUtc));
                }

                RefreshProfileList(profile.Id);
                if (profile.ProviderKind == SyncProviderKind.GoogleDrive)
                {
                    GoogleDriveAccountDisplayName = profile.AccountDisplayName;
                    GoogleDriveAccountEmail =
                        (profile.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail;

                    if (isNewProfile)
                    {
                        GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Disconnected;
                        GoogleDriveConnectionMessage =
                            "Profile saved. Connect Google Drive to authorize this account.";
                        HasStoredAuthentication = false;
                        GoogleDriveRootFolderDisplayName = null;
                        GoogleDriveRootFolderStatus =
                            GoogleDriveRootFolderStatus.Unconfigured;
                        GoogleDriveRootFolderMessage =
                            "Connect Google Drive before setting up its backup folder.";
                    }
                }
                else if (profile.ProviderKind == SyncProviderKind.OneDrive)
                {
                    OneDriveAccountDisplayName = profile.AccountDisplayName;
                    OneDriveAccountEmail =
                        (profile.ProviderSettings as OneDriveSyncRemoteSettings)?.AccountEmail;

                    if (isNewProfile)
                    {
                        OneDriveConnectionStatus = OneDriveConnectionStatus.Disconnected;
                        OneDriveConnectionMessage =
                            "Profile saved. Connect Microsoft OneDrive to authorize this account.";
                        HasStoredOneDriveAuthentication = false;
                    }
                }
                RemoteProfileState = "Saved";
                StatusMessage = $"Remote profile '{profile.DisplayName}' saved. No connection was started.";
                SaveNonSecretSettings();
            }
            catch (ArgumentException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (SyncRemoteProfileDuplicateNameException ex)
            {
                StatusMessage = ex.Message;
            }
            catch
            {
                StatusMessage = "The remote profile could not be saved.";
            }
        }

        [RelayCommand]
        private void SaveRemoteProfileAs()
        {
            try
            {
                DateTimeOffset now = _clock.UtcNow;
                SyncRemoteProfile profile = _profileRepository.Create(BuildProfile(
                    Guid.NewGuid(),
                    createdUtc: now,
                    updatedUtc: now,
                    lastUsedUtc: null,
                    lastSuccessfulConnectionUtc: null,
                    includeAccountMetadata: false));

                RefreshProfileList(profile.Id);
                ClearSessionOnlySftpState();
                if (profile.ProviderKind == SyncProviderKind.GoogleDrive)
                {
                    GoogleDriveAccountDisplayName = null;
                    GoogleDriveAccountEmail = null;
                    GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Disconnected;
                    GoogleDriveConnectionMessage =
                        "Profile saved. Connect Google Drive to authorize this account.";
                    HasStoredAuthentication = false;
                    GoogleDriveRootFolderDisplayName = null;
                    GoogleDriveRootFolderStatus =
                        GoogleDriveRootFolderStatus.Unconfigured;
                    GoogleDriveRootFolderMessage =
                        "Connect Google Drive before setting up its backup folder.";
                }
                else if (profile.ProviderKind == SyncProviderKind.OneDrive)
                {
                    OneDriveAccountDisplayName = null;
                    OneDriveAccountEmail = null;
                    OneDriveConnectionStatus = OneDriveConnectionStatus.Disconnected;
                    OneDriveConnectionMessage =
                        "Profile saved. Connect Microsoft OneDrive to authorize this account.";
                    HasStoredOneDriveAuthentication = false;
                }
                InvalidatePlan(force: true);
                RemoteProfileState = "Saved";
                StatusMessage = $"Saved a new remote profile '{profile.DisplayName}'. No connection was started.";
                SaveNonSecretSettings();
            }
            catch (ArgumentException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (SyncRemoteProfileDuplicateNameException ex)
            {
                StatusMessage = ex.Message;
            }
            catch
            {
                StatusMessage = "The new remote profile could not be saved.";
            }
        }

        [RelayCommand]
        private void RenameRemoteProfile()
        {
            if (SelectedRemoteProfile is null)
            {
                StatusMessage = "Select a saved remote profile to rename.";
                return;
            }

            try
            {
                SyncRemoteProfile renamed = _profileRepository.Rename(
                    SelectedRemoteProfile.Id,
                    RemoteProfileDisplayName,
                    _clock.UtcNow);
                RefreshProfileList(renamed.Id);
                RemoteProfileState = "Saved";
                StatusMessage = $"Remote profile renamed to '{renamed.DisplayName}'.";
            }
            catch (ArgumentException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (SyncRemoteProfileDuplicateNameException ex)
            {
                StatusMessage = ex.Message;
            }
            catch
            {
                StatusMessage = "The remote profile could not be renamed.";
            }
        }

        [RelayCommand]
        private async Task DeleteRemoteProfileAsync()
        {
            if (SelectedRemoteProfile is null)
            {
                StatusMessage = "Select a saved remote profile to delete.";
                return;
            }

            if (!ConfirmDeleteRemoteProfile)
            {
                StatusMessage = "Confirm profile deletion first. Backup and remote data will not be deleted.";
                return;
            }

            try
            {
                Guid profileId = SelectedRemoteProfile.Id;
                string deletedName = SelectedRemoteProfile.DisplayName;
                InvalidatePlan(force: true);
                CancelAllAuthentication();
                ClearSessionOnlySftpState();

                SyncRemoteProfileDeleteResult result =
                    await _profileService.DeleteAsync(profileId);

                if (!result.ProfileDeleted)
                {
                    HasStoredAuthentication =
                        await _profileService.HasStoredAuthenticationAsync(profileId);
                    StatusMessage = result.CleanupWarning ??
                        "The remote profile configuration could not be deleted.";
                    return;
                }

                ReplaceProfileCollections(_profileRepository.GetAll());
                NewRemoteProfile();
                ConfirmDeleteRemoteProfile = false;
                HasStoredAuthentication = false;
                StatusMessage = result.CleanupWarning is null
                    ? $"Deleted profile '{deletedName}' and its stored authentication only. No backup, history, known-host, or remote data was removed."
                    : $"Deleted profile '{deletedName}'. {result.CleanupWarning}";
            }
            catch
            {
                StatusMessage = "The remote profile configuration could not be deleted.";
            }
        }

        [RelayCommand]
        private async Task DisconnectAuthenticationAsync()
        {
            if (SelectedRemoteProfile is null)
            {
                StatusMessage = "Select a saved remote profile to disconnect.";
                return;
            }

            Guid profileId = SelectedRemoteProfile.Id;
            InvalidatePlan(force: true);
            ClearSessionOnlySftpState();

            SyncRemoteProfileAuthenticationResult result =
                await _profileService.DisconnectAuthenticationAsync(profileId);

            HasStoredAuthentication =
                await _profileService.HasStoredAuthenticationAsync(profileId);
            StatusMessage = result.Succeeded
                ? "Stored authentication was removed. The saved non-secret profile configuration was kept."
                : result.CleanupWarning ??
                  "Stored authentication could not be removed.";
        }

        private async Task RefreshStoredAuthenticationAsync(Guid profileId)
        {
            try
            {
                bool exists =
                    await _profileService.HasStoredAuthenticationAsync(profileId);

                if (SelectedRemoteProfile?.Id == profileId)
                    HasStoredAuthentication = exists;
            }
            catch
            {
                if (SelectedRemoteProfile?.Id == profileId)
                    HasStoredAuthentication = false;
            }
        }

        [RelayCommand]
        private Task ConnectGoogleDriveAsync() =>
            RunGoogleInteractiveAuthenticationAsync(GoogleDriveInteractiveOperation.Connect);

        [RelayCommand]
        private Task ReconnectGoogleDriveAsync() =>
            RunGoogleInteractiveAuthenticationAsync(GoogleDriveInteractiveOperation.Reconnect);

        private async Task RunGoogleInteractiveAuthenticationAsync(
            GoogleDriveInteractiveOperation operation)
        {
            if (!IsGoogleDriveSelected ||
                SelectedRemoteProfile is not { ProviderKind: SyncProviderKind.GoogleDrive } profile)
            {
                GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.NotConfigured;
                GoogleDriveConnectionMessage =
                    "Save the Google Drive profile before connecting so its authentication can be stored securely.";
                StatusMessage = GoogleDriveConnectionMessage;
                return;
            }

            GoogleDriveOAuthClientConfigurationState configuration =
                _googleDriveOAuthService.GetClientConfigurationState();

            if (!configuration.IsAvailable)
            {
                GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Unavailable;
                GoogleDriveConnectionMessage = configuration.Message ??
                    "Google Drive OAuth client configuration is unavailable.";
                StatusMessage = GoogleDriveConnectionMessage;
                return;
            }

            if (IsGoogleDriveConnecting)
                return;

            var previousState = new GoogleDriveUiSnapshot(
                GoogleDriveConnectionStatus,
                GoogleDriveAccountDisplayName,
                GoogleDriveAccountEmail,
                HasStoredAuthentication);

            CancelGoogleAuthentication();
            long generation = ++_googleAuthenticationGeneration;
            var cancellation = new CancellationTokenSource();
            _googleAuthenticationCancellation = cancellation;
            _googleDriveInteractiveOperation = true;
            IsGoogleDriveConnecting = true;
            OnPropertyChanged(nameof(CanCancelGoogleDriveConnection));
            GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Connecting;
            GoogleDriveConnectionMessage =
                operation == GoogleDriveInteractiveOperation.Reconnect
                    ? "Waiting for Google Drive reauthorization in the system browser…"
                    : "Waiting for Google Drive authorization in the system browser…";
            StatusMessage = GoogleDriveConnectionMessage;

            try
            {
                GoogleDriveAuthenticationResult result =
                    operation == GoogleDriveInteractiveOperation.Reconnect
                        ? await _googleDriveOAuthService.ReconnectAsync(
                            profile.Id,
                            cancellation.Token)
                        : await _googleDriveOAuthService.ConnectAsync(
                            profile.Id,
                            cancellation.Token);
                ApplyGoogleAuthenticationResult(
                    profile.Id,
                    generation,
                    result,
                    operation,
                    previousState);
            }
            catch (OperationCanceledException)
            {
                ApplyGoogleAuthenticationResult(
                    profile.Id,
                    generation,
                    new GoogleDriveAuthenticationResult(
                        GoogleDriveAuthenticationStatus.Cancelled,
                        ErrorCode: GoogleDriveOAuthErrorCodes.Cancelled,
                        Message: "Google Drive sign-in was cancelled. No backup data was changed."),
                    operation,
                    previousState);
            }
            catch
            {
                ApplyGoogleAuthenticationResult(
                    profile.Id,
                    generation,
                    new GoogleDriveAuthenticationResult(
                        GoogleDriveAuthenticationStatus.Failed,
                        ErrorCode: GoogleDriveOAuthErrorCodes.Failed,
                        Message: "Google Drive sign-in failed. Review the developer OAuth configuration and try again."),
                    operation,
                    previousState);
            }
            finally
            {
                if (generation == _googleAuthenticationGeneration)
                {
                    IsGoogleDriveConnecting = false;
                    _googleDriveInteractiveOperation = false;
                    OnPropertyChanged(nameof(CanCancelGoogleDriveConnection));
                    _googleAuthenticationCancellation?.Dispose();
                    _googleAuthenticationCancellation = null;

                    if (GoogleDriveConnectionStatus ==
                        GoogleDriveConnectionStatus.Connected)
                    {
                        BeginGoogleDriveRootFolderInspection(profile.Id);
                    }
                }
            }
        }

        /// <summary>
        /// Visible only while a sync is running, so the control cannot be
        /// pressed when there is nothing to stop.
        /// </summary>
        public bool CanCancelSync => IsSyncRunning && !IsCancellingSync;

        [RelayCommand]
        private void CancelSync()
        {
            if (!CanCancelSync)
                return;

            IsCancellingSync = true;
            _syncCancellation?.Cancel();
            ExecutionStatusMessage =
                "Cancelling the sync. Files already copied are kept; nothing is deleted.";
        }

        [RelayCommand]
        private void CancelGoogleDriveConnection()
        {
            if (!IsGoogleDriveConnecting)
                return;

            // Publish the interim message before cancelling: cancelling the
            // token can resume the connect continuation inline, and that
            // continuation writes the final outcome message. Writing after the
            // cancel would overwrite it and strand the surface on "Cancelling".
            ConfirmDisconnectGoogleDrive = false;
            GoogleDriveConnectionMessage =
                "Cancelling Google Drive sign-in…";
            _googleAuthenticationCancellation?.Cancel();
        }

        [RelayCommand]
        private async Task DisconnectGoogleDriveAsync()
        {
            if (!IsGoogleDriveSelected ||
                SelectedRemoteProfile is not { ProviderKind: SyncProviderKind.GoogleDrive } profile)
            {
                StatusMessage = "Select a saved Google Drive profile before disconnecting.";
                return;
            }

            if (!ConfirmDisconnectGoogleDrive)
            {
                StatusMessage =
                    "Confirm removing locally stored Google Drive authentication first. The saved profile, backups, and Drive files will remain.";
                return;
            }

            CancelGoogleAuthentication();
            long generation = ++_googleAuthenticationGeneration;
            var cancellation = new CancellationTokenSource();
            _googleAuthenticationCancellation = cancellation;
            _googleDriveInteractiveOperation = false;
            IsGoogleDriveConnecting = true;
            InvalidatePlan(force: true);
            ClearSessionOnlySftpState();
            GoogleDriveConnectionMessage = "Removing locally stored Google Drive authentication...";
            StatusMessage = GoogleDriveConnectionMessage;

            try
            {
                GoogleDriveDisconnectionResult result =
                    await _googleDriveOAuthService.DisconnectAsync(
                        profile.Id,
                        cancellation.Token);

                if (generation != _googleAuthenticationGeneration ||
                    SelectedRemoteProfile?.Id != profile.Id ||
                    !IsGoogleDriveSelected)
                {
                    return;
                }

                if (result.Succeeded)
                {
                    HasStoredAuthentication = false;
                    GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Disconnected;
                    GoogleDriveAccountDisplayName = null;
                    GoogleDriveAccountEmail = null;
                    ConfirmDisconnectGoogleDrive = false;
                    RefreshProfileList(profile.Id);
                    GoogleDriveRootFolderDisplayName =
                        SelectedRemoteProfile?.RemoteRootDisplayName;
                    GoogleDriveRootFolderStatus =
                        HasSavedGoogleDriveRootId
                            ? GoogleDriveRootFolderStatus.ReauthenticationRequired
                            : GoogleDriveRootFolderStatus.Unconfigured;
                    GoogleDriveRootFolderMessage =
                        HasSavedGoogleDriveRootId
                            ? "The saved folder identity was preserved. Reconnect Google Drive to validate it."
                            : "Connect Google Drive before setting up its backup folder.";
                }
                else
                {
                    if (result.LocalAuthenticationRemoved)
                        HasStoredAuthentication = false;

                    GoogleDriveConnectionStatus = result.Status ==
                        GoogleDriveDisconnectionStatus.SecretStoreUnavailable
                            ? GoogleDriveConnectionStatus.Unavailable
                            : GoogleDriveConnectionStatus.Failed;

                    SyncRemoteProfile? current = _profileRepository.GetById(profile.Id);

                    if (current is not null)
                    {
                        RefreshProfileList(current.Id);
                        GoogleDriveAccountDisplayName = current.AccountDisplayName;
                        GoogleDriveAccountEmail =
                            (current.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail;
                    }
                }

                GoogleDriveConnectionMessage = result.Message ?? result.Status.ToString();
                StatusMessage = GoogleDriveConnectionMessage;
            }
            catch (OperationCanceledException)
            {
                GoogleDriveConnectionMessage =
                    "Google Drive disconnect was cancelled. No backup data was changed.";
                StatusMessage = GoogleDriveConnectionMessage;
                ConfirmDisconnectGoogleDrive = false;
            }
            catch
            {
                GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Failed;
                GoogleDriveConnectionMessage =
                    "Locally stored Google Drive authentication could not be removed.";
                StatusMessage = GoogleDriveConnectionMessage;
            }
            finally
            {
                if (generation == _googleAuthenticationGeneration)
                {
                    IsGoogleDriveConnecting = false;
                    cancellation.Dispose();
                    _googleAuthenticationCancellation = null;
                }
            }
        }

        private void BeginGoogleAuthenticationRestore(Guid profileId)
        {
            CancelGoogleAuthentication();
            long generation = ++_googleAuthenticationGeneration;
            var cancellation = new CancellationTokenSource();
            _googleAuthenticationCancellation = cancellation;
            _googleDriveInteractiveOperation = false;
            IsGoogleDriveConnecting = true;
            GoogleDriveConnectionStatus =
                GoogleDriveConnectionStatus.StoredAuthenticationAvailable;
            GoogleDriveConnectionMessage =
                "Checking stored Google Drive authentication…";
            GoogleAuthenticationInitializationTask = RestoreGoogleAuthenticationAsync(
                profileId,
                generation,
                cancellation);
        }

        private async Task RestoreGoogleAuthenticationAsync(
            Guid profileId,
            long generation,
            CancellationTokenSource cancellation)
        {
            try
            {
                GoogleDriveAuthenticationResult result =
                    await _googleDriveOAuthService.RestoreAsync(
                        profileId,
                        cancellation.Token);
                ApplyGoogleAuthenticationResult(profileId, generation, result);
            }
            catch (OperationCanceledException)
            {
                // A provider/profile change deliberately makes this result stale.
            }
            catch
            {
                ApplyGoogleAuthenticationResult(
                    profileId,
                    generation,
                    new GoogleDriveAuthenticationResult(
                        GoogleDriveAuthenticationStatus.Failed,
                        ErrorCode: GoogleDriveOAuthErrorCodes.Failed,
                        Message: "Stored Google Drive authentication could not be checked."));
            }
            finally
            {
                if (generation == _googleAuthenticationGeneration)
                {
                    IsGoogleDriveConnecting = false;
                    cancellation.Dispose();
                    _googleAuthenticationCancellation = null;

                    if (SelectedRemoteProfile?.Id == profileId &&
                        GoogleDriveConnectionStatus ==
                        GoogleDriveConnectionStatus.Connected)
                    {
                        BeginGoogleDriveRootFolderInspection(profileId);
                    }
                }
            }
        }

        private void ApplyGoogleAuthenticationResult(
            Guid profileId,
            long generation,
            GoogleDriveAuthenticationResult result,
            GoogleDriveInteractiveOperation? operation = null,
            GoogleDriveUiSnapshot? previousState = null)
        {
            if (generation != _googleAuthenticationGeneration ||
                SelectedRemoteProfile?.Id != profileId ||
                !IsGoogleDriveSelected)
            {
                return;
            }

            GoogleDriveConnectionStatus = result.Status switch
            {
                GoogleDriveAuthenticationStatus.Connected =>
                    GoogleDriveConnectionStatus.Connected,
                GoogleDriveAuthenticationStatus.NoStoredAuthentication =>
                    GoogleDriveConnectionStatus.Disconnected,
                GoogleDriveAuthenticationStatus.ReauthenticationRequired or
                GoogleDriveAuthenticationStatus.TokenCorrupted or
                GoogleDriveAuthenticationStatus.AuthorizationRevoked =>
                    GoogleDriveConnectionStatus.ReauthenticationRequired,
                GoogleDriveAuthenticationStatus.ClientConfigurationMissing or
                GoogleDriveAuthenticationStatus.SecretStoreUnavailable or
                GoogleDriveAuthenticationStatus.Unavailable =>
                    GoogleDriveConnectionStatus.Unavailable,
                GoogleDriveAuthenticationStatus.Cancelled or
                GoogleDriveAuthenticationStatus.AuthorizationDenied
                    when operation == GoogleDriveInteractiveOperation.Reconnect &&
                         previousState is not null => previousState.Status,
                GoogleDriveAuthenticationStatus.Cancelled or
                GoogleDriveAuthenticationStatus.AuthorizationDenied =>
                    GoogleDriveConnectionStatus.Disconnected,
                GoogleDriveAuthenticationStatus.Failed or
                GoogleDriveAuthenticationStatus.AccountLookupFailed or
                GoogleDriveAuthenticationStatus.BrowserLaunchFailed or
                GoogleDriveAuthenticationStatus.CallbackFailed
                    when operation == GoogleDriveInteractiveOperation.Reconnect &&
                         previousState is not null => previousState.Status,
                _ => GoogleDriveConnectionStatus.Failed
            };

            if (operation == GoogleDriveInteractiveOperation.Reconnect &&
                previousState is not null &&
                result.Status is (GoogleDriveAuthenticationStatus.Cancelled or
                    GoogleDriveAuthenticationStatus.AuthorizationDenied or
                    GoogleDriveAuthenticationStatus.Failed or
                    GoogleDriveAuthenticationStatus.AccountLookupFailed or
                    GoogleDriveAuthenticationStatus.BrowserLaunchFailed or
                    GoogleDriveAuthenticationStatus.CallbackFailed))
            {
                GoogleDriveAccountDisplayName = previousState.AccountDisplayName;
                GoogleDriveAccountEmail = previousState.AccountEmail;
                HasStoredAuthentication = previousState.HasStoredAuthentication;
            }

            if (result.ConnectionSettings is { } settings)
            {
                GoogleDriveAccountDisplayName = settings.AccountDisplayName;
                GoogleDriveAccountEmail = settings.AccountEmail;
                HasStoredAuthentication = settings.HasStoredToken;

                SyncRemoteProfile? updated = _profileRepository.GetById(profileId);

                if (updated is not null)
                {
                    RefreshProfileList(updated.Id);
                    GoogleDriveAccountDisplayName = updated.AccountDisplayName;
                    GoogleDriveAccountEmail =
                        (updated.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail;
                    GoogleDriveRootFolderDisplayName =
                        updated.RemoteRootDisplayName;
                }
            }
            else if (result.Status == GoogleDriveAuthenticationStatus.NoStoredAuthentication)
            {
                HasStoredAuthentication = false;
            }
            else if (result.Status is
                     GoogleDriveAuthenticationStatus.ReauthenticationRequired or
                     GoogleDriveAuthenticationStatus.TokenCorrupted)
            {
                // Both outcomes originate from an existing protected entry. It
                // remains removable even though it cannot establish a session.
                HasStoredAuthentication = true;
            }
            else if (result.Status == GoogleDriveAuthenticationStatus.AuthorizationRevoked)
            {
                HasStoredAuthentication = false;
            }

            GoogleDriveConnectionMessage = result.Message ?? result.Status.ToString();
            StatusMessage = GoogleDriveConnectionMessage;

            if (result.Status != GoogleDriveAuthenticationStatus.Connected &&
                !(operation == GoogleDriveInteractiveOperation.Reconnect &&
                       previousState is not null &&
                       result.Status is
                           GoogleDriveAuthenticationStatus.Cancelled or
                           GoogleDriveAuthenticationStatus.AuthorizationDenied or
                           GoogleDriveAuthenticationStatus.Failed or
                           GoogleDriveAuthenticationStatus.AccountLookupFailed or
                           GoogleDriveAuthenticationStatus.BrowserLaunchFailed or
                           GoogleDriveAuthenticationStatus.CallbackFailed))
            {
                GoogleDriveRootFolderStatus =
                    HasSavedGoogleDriveRootId
                        ? GoogleDriveRootFolderStatus.ReauthenticationRequired
                        : GoogleDriveRootFolderStatus.Unconfigured;
                GoogleDriveRootFolderMessage =
                    HasSavedGoogleDriveRootId
                        ? "Reconnect Google Drive before checking the saved backup folder."
                        : "Connect Google Drive before setting up its backup folder.";
            }
        }

        [RelayCommand]
        private Task SetUpGoogleDriveRootFolderAsync() =>
            RunGoogleDriveRootFolderOperationAsync(
                GoogleDriveRootOperation.Ensure);

        [RelayCommand]
        private Task CheckGoogleDriveRootFolderAsync() =>
            RunGoogleDriveRootFolderOperationAsync(
                GoogleDriveRootOperation.Inspect);

        [RelayCommand]
        private Task RecreateGoogleDriveRootFolderAsync() =>
            RunGoogleDriveRootFolderOperationAsync(
                GoogleDriveRootOperation.Recreate);

        [RelayCommand]
        private void CancelGoogleDriveRootFolder()
        {
            if (!IsGoogleDriveRootFolderBusy)
                return;

            _googleRootFolderCancellation?.Cancel();
            GoogleDriveRootFolderMessage =
                "Cancelling the Google Drive folder operation…";
        }

        private void BeginGoogleDriveRootFolderInspection(Guid profileId)
        {
            if (SelectedRemoteProfile?.Id != profileId ||
                GoogleDriveConnectionStatus != GoogleDriveConnectionStatus.Connected)
            {
                return;
            }

            GoogleRootFolderInitializationTask =
                RunGoogleDriveRootFolderOperationAsync(
                    GoogleDriveRootOperation.Inspect);
        }

        private async Task RunGoogleDriveRootFolderOperationAsync(
            GoogleDriveRootOperation operation)
        {
            if (!IsGoogleDriveSelected ||
                SelectedRemoteProfile is not
                {
                    ProviderKind: SyncProviderKind.GoogleDrive
                } profile)
            {
                GoogleDriveRootFolderMessage =
                    "Select a saved Google Drive profile first.";
                StatusMessage = GoogleDriveRootFolderMessage;
                return;
            }

            if (GoogleDriveConnectionStatus != GoogleDriveConnectionStatus.Connected)
            {
                GoogleDriveRootFolderStatus =
                    GoogleDriveRootFolderStatus.ReauthenticationRequired;
                GoogleDriveRootFolderMessage =
                    "Connect or reconnect Google Drive before managing its backup folder.";
                StatusMessage = GoogleDriveRootFolderMessage;
                return;
            }

            if (operation == GoogleDriveRootOperation.Recreate &&
                !ConfirmRecreateGoogleDriveRootFolder)
            {
                GoogleDriveRootFolderMessage =
                    "Confirm creating or selecting a replacement Google Drive root folder first.";
                StatusMessage = GoogleDriveRootFolderMessage;
                return;
            }

            if (IsGoogleDriveConnecting || IsGoogleDriveRootFolderBusy)
                return;

            CancelGoogleDriveRootFolderOperation();
            long generation = ++_googleRootFolderGeneration;
            var cancellation = new CancellationTokenSource();
            _googleRootFolderCancellation = cancellation;
            IsGoogleDriveRootFolderBusy = true;
            GoogleDriveRootFolderStatus =
                operation == GoogleDriveRootOperation.Inspect
                    ? GoogleDriveRootFolderStatus.Checking
                    : GoogleDriveRootFolderStatus.Creating;
            GoogleDriveRootFolderMessage = operation switch
            {
                GoogleDriveRootOperation.Inspect =>
                    "Checking the saved Google Drive backup folder…",
                GoogleDriveRootOperation.Ensure =>
                    "Looking for the visible Google Drive backup folder before creating one…",
                _ =>
                    "Looking for a safe replacement Google Drive backup folder…"
            };
            StatusMessage = GoogleDriveRootFolderMessage;

            try
            {
                GoogleDriveRootFolderResult result = operation switch
                {
                    GoogleDriveRootOperation.Inspect =>
                        await _googleDriveRootFolderService.InspectAsync(
                            profile.Id,
                            cancellation.Token),
                    GoogleDriveRootOperation.Ensure =>
                        await _googleDriveRootFolderService.EnsureAsync(
                            profile.Id,
                            cancellation.Token),
                    GoogleDriveRootOperation.Recreate =>
                        await _googleDriveRootFolderService.RecreateAsync(
                            profile.Id,
                            GoogleDriveRootFolderRecreationConfirmation.Confirmed,
                            cancellation.Token),
                    _ => throw new InvalidOperationException()
                };

                ApplyGoogleDriveRootFolderResult(
                    profile.Id,
                    generation,
                    result);
            }
            catch (OperationCanceledException)
            {
                ApplyGoogleDriveRootFolderResult(
                    profile.Id,
                    generation,
                    new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Failed,
                        profile.Id,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.Cancelled,
                        Message: "The Google Drive folder operation was cancelled. No backup data was changed."));
            }
            catch
            {
                ApplyGoogleDriveRootFolderResult(
                    profile.Id,
                    generation,
                    new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Failed,
                        profile.Id,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.Failed,
                        Message: "The Google Drive root folder could not be checked."));
            }
            finally
            {
                if (generation == _googleRootFolderGeneration)
                {
                    IsGoogleDriveRootFolderBusy = false;
                    cancellation.Dispose();
                    _googleRootFolderCancellation = null;
                }
            }
        }

        private void ApplyGoogleDriveRootFolderResult(
            Guid profileId,
            long generation,
            GoogleDriveRootFolderResult result)
        {
            if (generation != _googleRootFolderGeneration ||
                SelectedRemoteProfile?.Id != profileId ||
                !IsGoogleDriveSelected)
            {
                return;
            }

            GoogleDriveRootFolderStatus = result.Status;
            GoogleDriveRootFolderDisplayName =
                result.DisplayName ?? SelectedRemoteProfile.RemoteRootDisplayName;
            GoogleDriveRootFolderMessage = result.Message ?? result.Status.ToString();
            StatusMessage = GoogleDriveRootFolderMessage;

            if (result.Succeeded)
            {
                SyncRemoteProfile? updated = _profileRepository.GetById(profileId);

                if (updated is not null)
                {
                    RefreshProfileList(updated.Id);
                    GoogleDriveRootFolderDisplayName =
                        updated.RemoteRootDisplayName;
                }

                ConfirmRecreateGoogleDriveRootFolder = false;
            }
            else if (result.Status ==
                     GoogleDriveRootFolderStatus.ReauthenticationRequired)
            {
                GoogleDriveConnectionStatus =
                    GoogleDriveConnectionStatus.ReauthenticationRequired;
            }
        }

        private void CancelGoogleDriveRootFolderOperation()
        {
            _googleRootFolderGeneration++;
            _googleRootFolderCancellation?.Cancel();
            _googleRootFolderCancellation?.Dispose();
            _googleRootFolderCancellation = null;
            IsGoogleDriveRootFolderBusy = false;
            GoogleRootFolderInitializationTask = Task.CompletedTask;
        }

        private void CancelGoogleAuthentication()
        {
            CancelGoogleDriveRootFolderOperation();
            _googleAuthenticationGeneration++;
            _googleAuthenticationCancellation?.Cancel();
            _googleAuthenticationCancellation?.Dispose();
            _googleAuthenticationCancellation = null;
            _googleDriveInteractiveOperation = false;
            IsGoogleDriveConnecting = false;
            OnPropertyChanged(nameof(CanCancelGoogleDriveConnection));
            GoogleAuthenticationInitializationTask = Task.CompletedTask;
        }

        private void CancelOneDriveAuthentication()
        {
            _oneDriveAuthenticationGeneration++;
            _oneDriveAuthenticationCancellation?.Cancel();
            _oneDriveAuthenticationCancellation?.Dispose();
            _oneDriveAuthenticationCancellation = null;
            _oneDriveInteractiveOperation = false;
            IsOneDriveConnecting = false;
            OnPropertyChanged(nameof(CanCancelOneDriveConnection));
            OneDriveAuthenticationInitializationTask = Task.CompletedTask;
        }

        // Every sign-in flow and every pending destructive confirmation belongs
        // to the profile and provider that were on screen when it started.
        // Switching provider, loading, creating or deleting a profile abandons
        // them all; a flow left running could otherwise store a token for a
        // profile that is no longer selected, or no longer exists.
        private void CancelAllAuthentication()
        {
            CancelGoogleAuthentication();
            CancelOneDriveAuthentication();
            ConfirmDisconnectGoogleDrive = false;
            ConfirmDisconnectOneDrive = false;
            ConfirmRecreateGoogleDriveRootFolder = false;
        }

        private void ResetOneDriveState()
        {
            OneDriveAccountDisplayName = null;
            OneDriveAccountEmail = null;
            OneDriveConnectionStatus = OneDriveConnectionStatus.NotConfigured;
            OneDriveConnectionMessage =
                "Save a Microsoft OneDrive profile before connecting.";
            OneDriveQuotaSummary = null;
            ConfirmDisconnectOneDrive = false;
            HasStoredOneDriveAuthentication = false;
        }

        [RelayCommand]
        private Task ConnectOneDriveAsync() =>
            RunOneDriveInteractiveAuthenticationAsync(OneDriveInteractiveOperation.Connect);

        [RelayCommand]
        private Task ReconnectOneDriveAsync() =>
            RunOneDriveInteractiveAuthenticationAsync(OneDriveInteractiveOperation.Reconnect);

        [RelayCommand]
        private void CancelOneDriveConnection()
        {
            if (!IsOneDriveConnecting)
                return;

            ConfirmDisconnectOneDrive = false;
            OneDriveConnectionMessage =
                "Cancelling Microsoft OneDrive sign-in…";
            _oneDriveAuthenticationCancellation?.Cancel();
        }

        [RelayCommand]
        private async Task DisconnectOneDriveAsync()
        {
            if (!IsOneDriveSelected ||
                SelectedRemoteProfile is not { ProviderKind: SyncProviderKind.OneDrive } profile)
            {
                StatusMessage = "Select a saved Microsoft OneDrive profile before disconnecting.";
                return;
            }

            if (!ConfirmDisconnectOneDrive)
            {
                StatusMessage =
                    "Confirm removing locally stored Microsoft OneDrive authentication first. The saved profile, backups, and OneDrive files will remain.";
                return;
            }

            if (_oneDriveOAuthService is null)
            {
                StatusMessage = "Microsoft OneDrive service is unavailable.";
                return;
            }

            CancelOneDriveAuthentication();
            long generation = ++_oneDriveAuthenticationGeneration;
            var cancellation = new CancellationTokenSource();
            _oneDriveAuthenticationCancellation = cancellation;
            _oneDriveInteractiveOperation = false;
            IsOneDriveConnecting = true;
            InvalidatePlan(force: true);
            ClearSessionOnlySftpState();
            OneDriveConnectionMessage = "Removing locally stored Microsoft OneDrive authentication...";
            StatusMessage = OneDriveConnectionMessage;

            try
            {
                OneDriveDisconnectionResult result =
                    await _oneDriveOAuthService.DisconnectAsync(
                        profile.Id,
                        cancellation.Token);

                if (generation != _oneDriveAuthenticationGeneration ||
                    SelectedRemoteProfile?.Id != profile.Id ||
                    !IsOneDriveSelected)
                {
                    return;
                }

                if (result.Succeeded)
                {
                    HasStoredOneDriveAuthentication = false;
                    OneDriveConnectionStatus = OneDriveConnectionStatus.Disconnected;
                    OneDriveAccountDisplayName = null;
                    OneDriveAccountEmail = null;
                    OneDriveQuotaSummary = null;
                    ConfirmDisconnectOneDrive = false;
                    RefreshProfileList(profile.Id);
                }
                else
                {
                    if (result.LocalAuthenticationRemoved)
                    {
                        HasStoredOneDriveAuthentication = false;
                    }

                    OneDriveConnectionStatus = result.Status ==
                        OneDriveDisconnectionStatus.SecretStoreUnavailable
                            ? OneDriveConnectionStatus.Unavailable
                            : OneDriveConnectionStatus.Failed;

                    SyncRemoteProfile? current = _profileRepository.GetById(profile.Id);

                    if (current is not null)
                    {
                        RefreshProfileList(current.Id);
                        OneDriveAccountDisplayName = current.AccountDisplayName;
                        OneDriveAccountEmail =
                            (current.ProviderSettings as OneDriveSyncRemoteSettings)?.AccountEmail;
                    }
                }

                OneDriveConnectionMessage = result.Message ?? result.Status.ToString();
                StatusMessage = OneDriveConnectionMessage;
            }
            catch (OperationCanceledException)
            {
                OneDriveConnectionMessage =
                    "Microsoft OneDrive disconnect was cancelled. No backup data was changed.";
                StatusMessage = OneDriveConnectionMessage;
                ConfirmDisconnectOneDrive = false;
            }
            catch
            {
                OneDriveConnectionStatus = OneDriveConnectionStatus.Failed;
                OneDriveConnectionMessage =
                    "Locally stored Microsoft OneDrive authentication could not be removed.";
                StatusMessage = OneDriveConnectionMessage;
            }
            finally
            {
                if (generation == _oneDriveAuthenticationGeneration)
                {
                    IsOneDriveConnecting = false;
                    cancellation.Dispose();
                    _oneDriveAuthenticationCancellation = null;
                }
            }
        }

        private async Task RunOneDriveInteractiveAuthenticationAsync(
            OneDriveInteractiveOperation operation)
        {
            if (!IsOneDriveSelected ||
                SelectedRemoteProfile is not { ProviderKind: SyncProviderKind.OneDrive } profile)
            {
                OneDriveConnectionStatus = OneDriveConnectionStatus.NotConfigured;
                OneDriveConnectionMessage =
                    "Save the Microsoft OneDrive profile before connecting so its authentication can be stored securely.";
                StatusMessage = OneDriveConnectionMessage;
                return;
            }

            if (_oneDriveOAuthService is null)
            {
                OneDriveConnectionStatus = OneDriveConnectionStatus.Unavailable;
                OneDriveConnectionMessage = "Microsoft OneDrive service is unavailable.";
                StatusMessage = OneDriveConnectionMessage;
                return;
            }

            OneDriveOAuthClientConfigurationState configuration =
                _oneDriveOAuthService.GetClientConfigurationState();

            if (!configuration.IsAvailable)
            {
                OneDriveConnectionStatus = OneDriveConnectionStatus.Unavailable;
                OneDriveConnectionMessage = configuration.Message ??
                    "Microsoft OneDrive OAuth client configuration is unavailable.";
                StatusMessage = OneDriveConnectionMessage;
                return;
            }

            if (IsOneDriveConnecting)
                return;

            var previousState = new OneDriveUiSnapshot(
                OneDriveConnectionStatus,
                OneDriveAccountDisplayName,
                OneDriveAccountEmail,
                HasStoredOneDriveAuthentication);

            CancelOneDriveAuthentication();
            long generation = ++_oneDriveAuthenticationGeneration;
            var cancellation = new CancellationTokenSource();
            _oneDriveAuthenticationCancellation = cancellation;
            _oneDriveInteractiveOperation = true;
            IsOneDriveConnecting = true;
            OnPropertyChanged(nameof(CanCancelOneDriveConnection));
            OneDriveConnectionStatus = OneDriveConnectionStatus.Connecting;
            OneDriveConnectionMessage =
                operation == OneDriveInteractiveOperation.Reconnect
                    ? "Waiting for Microsoft OneDrive reauthorization in the system browser…"
                    : "Waiting for Microsoft OneDrive authorization in the system browser…";
            StatusMessage = OneDriveConnectionMessage;

            try
            {
                OneDriveAuthenticationResult result =
                    operation == OneDriveInteractiveOperation.Reconnect
                        ? await _oneDriveOAuthService.ReconnectAsync(
                            profile.Id,
                            cancellation.Token)
                        : await _oneDriveOAuthService.ConnectAsync(
                            profile.Id,
                            cancellation.Token);
                ApplyOneDriveAuthenticationResult(
                    profile.Id,
                    generation,
                    result,
                    operation,
                    previousState);
            }
            catch (OperationCanceledException)
            {
                ApplyOneDriveAuthenticationResult(
                    profile.Id,
                    generation,
                    new OneDriveAuthenticationResult(
                        OneDriveAuthenticationStatus.Cancelled,
                        Succeeded: false,
                        ErrorCode: OneDriveOAuthErrorCodes.Cancelled,
                        Message: "Microsoft OneDrive sign-in was cancelled. No backup data was changed."),
                    operation,
                    previousState);
            }
            catch
            {
                ApplyOneDriveAuthenticationResult(
                    profile.Id,
                    generation,
                    new OneDriveAuthenticationResult(
                        OneDriveAuthenticationStatus.Failed,
                        Succeeded: false,
                        ErrorCode: OneDriveOAuthErrorCodes.Failed,
                        Message: "Microsoft OneDrive sign-in failed. Review the developer OAuth configuration and try again."),
                    operation,
                    previousState);
            }
            finally
            {
                if (generation == _oneDriveAuthenticationGeneration)
                {
                    IsOneDriveConnecting = false;
                    _oneDriveInteractiveOperation = false;
                    OnPropertyChanged(nameof(CanCancelOneDriveConnection));
                    _oneDriveAuthenticationCancellation?.Dispose();
                    _oneDriveAuthenticationCancellation = null;
                }
            }
        }

        private void BeginOneDriveAuthenticationRestore(Guid profileId)
        {
            CancelOneDriveAuthentication();
            long generation = ++_oneDriveAuthenticationGeneration;
            var cancellation = new CancellationTokenSource();
            _oneDriveAuthenticationCancellation = cancellation;
            _oneDriveInteractiveOperation = false;
            IsOneDriveConnecting = true;
            OneDriveConnectionStatus =
                OneDriveConnectionStatus.StoredAuthenticationAvailable;
            OneDriveConnectionMessage =
                "Checking stored Microsoft OneDrive authentication…";
            OneDriveAuthenticationInitializationTask = RestoreOneDriveAuthenticationAsync(
                profileId,
                generation,
                cancellation);
        }

        private async Task RestoreOneDriveAuthenticationAsync(
            Guid profileId,
            long generation,
            CancellationTokenSource cancellation)
        {
            try
            {
                if (_oneDriveOAuthService is null)
                {
                    ApplyOneDriveAuthenticationResult(
                        profileId,
                        generation,
                        new OneDriveAuthenticationResult(
                            OneDriveAuthenticationStatus.Unavailable,
                            Succeeded: false,
                            ErrorCode: OneDriveOAuthErrorCodes.ClientIdMissing,
                            Message: "Microsoft OneDrive service is unavailable."));
                    return;
                }

                OneDriveAuthenticationResult result =
                    await _oneDriveOAuthService.RestoreAsync(
                        profileId,
                        cancellation.Token);
                ApplyOneDriveAuthenticationResult(profileId, generation, result);
            }
            catch (OperationCanceledException)
            {
                // A provider/profile change deliberately makes this result stale.
            }
            catch
            {
                ApplyOneDriveAuthenticationResult(
                    profileId,
                    generation,
                    new OneDriveAuthenticationResult(
                        OneDriveAuthenticationStatus.Failed,
                        Succeeded: false,
                        ErrorCode: OneDriveOAuthErrorCodes.Failed,
                        Message: "Stored Microsoft OneDrive authentication could not be checked."));
            }
            finally
            {
                if (generation == _oneDriveAuthenticationGeneration)
                {
                    IsOneDriveConnecting = false;
                    cancellation.Dispose();
                    _oneDriveAuthenticationCancellation = null;
                }
            }
        }

        private void ApplyOneDriveAuthenticationResult(
            Guid profileId,
            long generation,
            OneDriveAuthenticationResult result,
            OneDriveInteractiveOperation? operation = null,
            OneDriveUiSnapshot? previousState = null)
        {
            if (generation != _oneDriveAuthenticationGeneration ||
                SelectedRemoteProfile?.Id != profileId ||
                !IsOneDriveSelected)
            {
                return;
            }

            OneDriveConnectionStatus = result.Status switch
            {
                OneDriveAuthenticationStatus.Connected =>
                    OneDriveConnectionStatus.Connected,
                OneDriveAuthenticationStatus.NoStoredAuthentication =>
                    OneDriveConnectionStatus.Disconnected,
                OneDriveAuthenticationStatus.ReauthenticationRequired or
                OneDriveAuthenticationStatus.TokenCorrupted or
                OneDriveAuthenticationStatus.AuthorizationRevoked =>
                    OneDriveConnectionStatus.ReauthenticationRequired,
                OneDriveAuthenticationStatus.ClientConfigurationMissing or
                OneDriveAuthenticationStatus.SecretStoreUnavailable or
                OneDriveAuthenticationStatus.Unavailable =>
                    OneDriveConnectionStatus.Unavailable,
                OneDriveAuthenticationStatus.Cancelled or
                OneDriveAuthenticationStatus.AuthorizationDenied
                    when operation == OneDriveInteractiveOperation.Reconnect &&
                         previousState is not null => previousState.Status,
                OneDriveAuthenticationStatus.Cancelled or
                OneDriveAuthenticationStatus.AuthorizationDenied =>
                    OneDriveConnectionStatus.Disconnected,
                OneDriveAuthenticationStatus.Failed or
                OneDriveAuthenticationStatus.AccountLookupFailed or
                OneDriveAuthenticationStatus.BrowserLaunchFailed or
                OneDriveAuthenticationStatus.CallbackFailed
                    when operation == OneDriveInteractiveOperation.Reconnect &&
                         previousState is not null => previousState.Status,
                _ => OneDriveConnectionStatus.Failed
            };

            if (operation == OneDriveInteractiveOperation.Reconnect &&
                previousState is not null &&
                result.Status is (OneDriveAuthenticationStatus.Cancelled or
                    OneDriveAuthenticationStatus.AuthorizationDenied or
                    OneDriveAuthenticationStatus.Failed or
                    OneDriveAuthenticationStatus.AccountLookupFailed or
                    OneDriveAuthenticationStatus.BrowserLaunchFailed or
                    OneDriveAuthenticationStatus.CallbackFailed))
            {
                OneDriveAccountDisplayName = previousState.AccountDisplayName;
                OneDriveAccountEmail = previousState.AccountEmail;
                HasStoredOneDriveAuthentication = previousState.HasStoredOneDriveAuthentication;
            }

            if (result.Succeeded)
            {
                OneDriveAccountDisplayName = result.AccountDisplayName;
                OneDriveAccountEmail = result.AccountEmail;
                HasStoredOneDriveAuthentication = true;

                SyncRemoteProfile? updated = _profileRepository.GetById(profileId);

                if (updated is not null)
                {
                    RefreshProfileList(updated.Id);
                    OneDriveAccountDisplayName = updated.AccountDisplayName;
                    OneDriveAccountEmail =
                        (updated.ProviderSettings as OneDriveSyncRemoteSettings)?.AccountEmail;
                }

                _ = LoadOneDriveQuotaAsync(profileId, generation);
            }
            else if (result.Status == OneDriveAuthenticationStatus.NoStoredAuthentication)
            {
                HasStoredOneDriveAuthentication = false;
                OneDriveQuotaSummary = null;
            }
            else if (result.Status is
                     OneDriveAuthenticationStatus.ReauthenticationRequired or
                     OneDriveAuthenticationStatus.TokenCorrupted)
            {
                HasStoredOneDriveAuthentication = true;
                OneDriveQuotaSummary = null;
            }
            else if (result.Status == OneDriveAuthenticationStatus.AuthorizationRevoked)
            {
                HasStoredOneDriveAuthentication = false;
                OneDriveQuotaSummary = null;
            }
            else
            {
                OneDriveQuotaSummary = null;
            }

            OneDriveConnectionMessage = result.Message ?? result.Status.ToString();
            StatusMessage = OneDriveConnectionMessage;
        }

        private async Task LoadOneDriveQuotaAsync(Guid profileId, long generation)
        {
            if (_oneDriveOAuthService is null)
                return;

            try
            {
                OneDriveQuotaInfo? quota = await _oneDriveOAuthService.GetQuotaAsync(profileId);

                if (generation == _oneDriveAuthenticationGeneration &&
                    SelectedRemoteProfile?.Id == profileId &&
                    IsOneDriveSelected &&
                    quota is not null)
                {
                    OneDriveQuotaSummary =
                        $"{quota.FormattedUsed} of {quota.FormattedTotal} used ({quota.FormattedRemaining} free)";
                }
            }
            catch
            {
                // Quota fetch is best-effort; failure should not break connected status
            }
        }

        private SyncRemoteProfile BuildProfile(
            Guid id,
            DateTimeOffset createdUtc,
            DateTimeOffset updatedUtc,
            DateTimeOffset? lastUsedUtc,
            DateTimeOffset? lastSuccessfulConnectionUtc,
            bool includeAccountMetadata = true)
        {
            string displayName = SyncRemoteProfileValidation.NormalizeDisplayName(
                RemoteProfileDisplayName);
            SyncRemoteProfileSettings settings;
            string? accountDisplayName;
            string? remoteRootDisplayName;
            string? remoteFolderId = null;

            switch (SelectedProviderKind)
            {
                case SyncProviderKind.LocalFolder:
                    if (string.IsNullOrWhiteSpace(RemoteRootPath))
                        throw new ArgumentException("Choose a local or mounted sync folder first.");

                    string localPath = RemoteRootPath.Trim();
                    settings = new LocalFolderSyncRemoteSettings(localPath);
                    accountDisplayName = null;
                    remoteRootDisplayName = localPath;
                    break;

                case SyncProviderKind.Sftp:
                    string? issue = ValidateSftpNonSecretProfileSettings();

                    if (issue is not null)
                        throw new ArgumentException(issue);

                    int port = int.Parse(SftpPort.Trim());
                    string host = SftpHost.Trim();
                    string username = SftpUsername.Trim();
                    string remotePath = SftpRemotePath.Trim();
                    settings = new SftpSyncRemoteSettings(
                        host,
                        port,
                        username,
                        SftpUsePrivateKey
                            ? SftpAuthMethod.PrivateKey
                            : SftpAuthMethod.Password,
                        string.IsNullOrWhiteSpace(SftpKeyFilePath)
                            ? null
                            : SftpKeyFilePath.Trim(),
                        remotePath);
                    accountDisplayName = $"{username}@{host}";
                    remoteRootDisplayName =
                        $"sftp://{username}@{host}:{port}" +
                        (remotePath.StartsWith('/') ? remotePath : "/" + remotePath);
                    break;

                case SyncProviderKind.GoogleDrive:
                    settings = new GoogleDriveSyncRemoteSettings(
                        includeAccountMetadata ? GoogleDriveAccountEmail : null,
                        GoogleDriveAuthorizationScopes.DriveFile);
                    accountDisplayName = includeAccountMetadata
                        ? GoogleDriveAccountDisplayName
                        : null;
                    remoteRootDisplayName =
                        SelectedRemoteProfile?.ProviderKind == SyncProviderKind.GoogleDrive
                            ? SelectedRemoteProfile.RemoteRootDisplayName
                            : null;
                    remoteFolderId =
                        SelectedRemoteProfile?.ProviderKind == SyncProviderKind.GoogleDrive
                            ? SelectedRemoteProfile.RemoteFolderId
                            : null;
                    break;

                case SyncProviderKind.OneDrive:
                    settings = new OneDriveSyncRemoteSettings(
                        includeAccountMetadata ? OneDriveAccountEmail : null,
                        OneDriveAuthorizationScopes.AppFolder,
                        includeAccountMetadata ? OneDriveAccountDisplayName : null);
                    accountDisplayName = includeAccountMetadata
                        ? OneDriveAccountDisplayName
                        : null;
                    remoteRootDisplayName =
                        SelectedRemoteProfile?.ProviderKind == SyncProviderKind.OneDrive
                            ? SelectedRemoteProfile.RemoteRootDisplayName
                            : "OneDrive: AppRoot (GameSave Manager)";
                    break;

                default:
                    throw new ArgumentException(
                        GetUnavailableProviderMessage(SelectedProviderKind) ??
                        "The selected provider is unavailable.");
            }

            return new SyncRemoteProfile(
                id,
                displayName,
                SelectedProviderKind,
                accountDisplayName,
                remoteRootDisplayName,
                settings,
                createdUtc,
                updatedUtc,
                lastUsedUtc,
                lastSuccessfulConnectionUtc,
                RemoteFolderId: remoteFolderId);
        }

        private string? ValidateSftpNonSecretProfileSettings()
        {
            if (string.IsNullOrWhiteSpace(SftpHost))
                return "Enter the SFTP host first.";

            if (string.IsNullOrWhiteSpace(SftpUsername))
                return "Enter the SFTP username first.";

            if (!int.TryParse(SftpPort.Trim(), out int port) || port is < 1 or > 65535)
                return "Enter an SFTP port between 1 and 65535.";

            if (string.IsNullOrWhiteSpace(SftpRemotePath))
                return "Enter the SFTP remote folder path.";

            return SftpUsePrivateKey && string.IsNullOrWhiteSpace(SftpKeyFilePath)
                ? "Choose an SFTP private key file first."
                : null;
        }

        private void ResetSftpNonSecretFields()
        {
            SftpHost = "";
            SftpPort = "22";
            SftpUsername = "";
            SftpUsePrivateKey = false;
            SftpUsePassword = true;
            SftpKeyFilePath = "";
            SftpRemotePath = "/gamesave-sync";
        }

        private void ClearSessionOnlySftpState()
        {
            SftpPassword = "";
            SftpKeyPassphrase = "";
            SftpTrustNewHostKey = false;
        }

        private void ResetGoogleDriveState()
        {
            GoogleDriveAccountDisplayName = null;
            GoogleDriveAccountEmail = null;
            GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.NotConfigured;
            GoogleDriveConnectionMessage =
                "Save a Google Drive profile before connecting.";
            GoogleDriveRootFolderDisplayName = null;
            GoogleDriveRootFolderStatus = GoogleDriveRootFolderStatus.Unconfigured;
            GoogleDriveRootFolderMessage =
                "Connect Google Drive before setting up its backup folder.";
            ConfirmRecreateGoogleDriveRootFolder = false;
        }

        private void RefreshProfileList(Guid selectedId)
        {
            IReadOnlyList<SyncRemoteProfile> profiles = _profileRepository.GetAll();
            ReplaceProfileCollections(profiles);

            SyncRemoteProfile selected = profiles.First(profile => profile.Id == selectedId);
            _suppressProfileSelection = true;
            SelectedRemoteProfile = selected;
            _suppressProfileSelection = false;
            SelectProfileOption(selected.Id);
            _applyingProfile = true;
            RemoteProfileDisplayName = selected.DisplayName;
            _applyingProfile = false;
        }

        private void ReplaceProfileCollections(IReadOnlyList<SyncRemoteProfile> profiles)
        {
            RemoteProfiles.Clear();
            RemoteProfileOptions.Clear();
            RemoteProfileOptions.Add(new SyncRemoteProfileOption(
                Profile: null,
                DisplayName: "No saved profile (use current settings)"));

            foreach (SyncRemoteProfile profile in profiles)
            {
                RemoteProfiles.Add(profile);
                RemoteProfileOptions.Add(new SyncRemoteProfileOption(
                    profile,
                    profile.DisplayName));
            }
        }

        private void SelectNoProfileOption()
        {
            _suppressProfileOptionSelection = true;
            SelectedRemoteProfileOption = RemoteProfileOptions
                .FirstOrDefault(option => option.Profile is null);
            _suppressProfileOptionSelection = false;
        }

        private void SelectProfileOption(Guid profileId)
        {
            _suppressProfileOptionSelection = true;
            SelectedRemoteProfileOption = RemoteProfileOptions
                .FirstOrDefault(option => option.Profile?.Id == profileId);
            _suppressProfileOptionSelection = false;
        }

        private void TryUpdateLastUsed()
        {
            if (SelectedRemoteProfile is null)
                return;

            try
            {
                SyncRemoteProfile updated = _profileRepository.UpdateLastUsed(
                    SelectedRemoteProfile.Id,
                    _clock.UtcNow);
                RefreshProfileList(updated.Id);
            }
            catch
            {
                // Profile metadata is best-effort and never fails sync.
            }
        }

        private void TryUpdateLastSuccessfulConnection()
        {
            if (SelectedRemoteProfile is null)
                return;

            try
            {
                SyncRemoteProfile updated =
                    _profileRepository.UpdateLastSuccessfulConnection(
                        SelectedRemoteProfile.Id,
                        _clock.UtcNow);
                RefreshProfileList(updated.Id);
            }
            catch
            {
                // Profile metadata is best-effort and never fails sync.
            }
        }

        private ISyncProvider CreateConfiguredProvider()
        {
            return SelectedProviderKind switch
            {
                SyncProviderKind.LocalFolder =>
                    _syncProviderFactory.CreateLocalFolderProvider(RemoteRootPath),

                SyncProviderKind.Sftp =>
                    _syncProviderFactory.CreateSftpProvider(BuildSftpSettings()),

                // Google Drive is keyed by the saved profile rather than by
                // connection settings: its credentials live in the profile and
                // its remote file system is assembled from provider-internal
                // services. ValidateProviderSelection guarantees the profile is
                // present and usable before this runs.
                SyncProviderKind.GoogleDrive =>
                    _syncProviderFactory.CreateGoogleDriveProvider(
                        SelectedRemoteProfile!.Id),

                SyncProviderKind.OneDrive =>
                    _syncProviderFactory.CreateOneDriveProvider(
                        SelectedRemoteProfile!.Id),

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
            bool persistUnsavedForm = SelectedRemoteProfile is null;

            _syncSettingsStore.Save(new SyncUiSettings(
                SchemaVersion: SyncUiSettings.CurrentSchemaVersion,
                SelectedProviderKind: SelectedProviderKind,
                LocalFolderPath: persistUnsavedForm ? RemoteRootPath : "",
                SftpHost: persistUnsavedForm ? SftpHost : "",
                SftpPort: persistUnsavedForm ? SftpPort : "22",
                SftpUsername: persistUnsavedForm ? SftpUsername : "",
                SftpUsePrivateKey: persistUnsavedForm && SftpUsePrivateKey,
                SftpKeyFilePath: persistUnsavedForm ? SftpKeyFilePath : "",
                SftpRemotePath: persistUnsavedForm ? SftpRemotePath : "/gamesave-sync",
                SelectedRemoteProfileId: SelectedRemoteProfile?.Id,
                LegacyProfileMigrationCompleted: true));
        }

        private string? ValidateProviderSelection()
        {
            return SelectedProviderKind switch
            {
                SyncProviderKind.LocalFolder when string.IsNullOrWhiteSpace(RemoteRootPath) =>
                    "Choose a local or mounted sync folder first.",

                SyncProviderKind.LocalFolder => null,

                SyncProviderKind.Sftp => ValidateSftpSelection(),

                SyncProviderKind.GoogleDrive => ValidateGoogleDriveSelection(),

                SyncProviderKind.OneDrive => ValidateOneDriveSelection(),

                _ => GetUnavailableProviderMessage(SelectedProviderKind)
            };
        }

        private string? ValidateOneDriveSelection() =>
            ValidateCloudSelection(
                SyncProviderKind.OneDrive,
                "Select a saved Microsoft OneDrive profile first.",
                CanUseOneDriveForSync,
                "Connect Microsoft OneDrive before syncing.");

        private string? ValidateGoogleDriveSelection() =>
            ValidateCloudSelection(
                SyncProviderKind.GoogleDrive,
                "Select a saved Google Drive profile first.",
                CanUseGoogleDriveForSync,
                "Connect Google Drive and set up its backup folder before syncing.");

        /// <summary>
        /// Refuses a cloud selection that <see cref="CreateConfiguredProvider"/>
        /// could not build. The catalog's own "unavailable" answer comes first,
        /// then the saved profile the factory needs; everything else about
        /// readiness already lives in the provider's CanUse*ForSync flag.
        /// </summary>
        private string? ValidateCloudSelection(
            SyncProviderKind kind,
            string noProfileMessage,
            bool ready,
            string notReadyMessage) =>
            GetUnavailableProviderMessage(kind) ??
            (SelectedRemoteProfile?.ProviderKind != kind ? noProfileMessage
                : ready ? null
                : notReadyMessage);

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

        private string? GetUnavailableProviderMessage(SyncProviderKind kind)
        {
            SyncProviderDescriptor descriptor = _providerCatalog.GetDescriptor(kind);

            if (descriptor.IsImplemented)
                return null;

            return descriptor.Kind == SyncProviderKind.Unknown &&
                   kind != SyncProviderKind.Unknown
                ? $"Sync provider value {(int)kind} is not supported by this version."
                : descriptor.UnavailableMessage ?? "The selected sync provider is unavailable.";
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
            _isBulkLoadingItems = true;
            try
            {
                Items.Clear();
            }
            finally
            {
                _isBulkLoadingItems = false;
            }

            Pagination.SetSource(Items);
            Warnings.Clear();
            SummaryDisplay = "";
            SelectedSummaryDisplay = "";
            ConnectionCheckMessage = "";
            CanExecuteSync = false;
            PlanHasUploads = false;
            PlanHasDownloads = false;
            HasSelectedRuns = false;
        }

        private void UpdateSelectedSummary()
        {
            if (_isBulkLoadingItems)
                return;

            var selectable = Items.Where(row => row.IsSelectable).ToList();

            if (selectable.Count == 0)
            {
                SelectedSummaryDisplay = "";
                HasSelectedRuns = false;
                return;
            }

            var selected = selectable.Where(row => row.IncludeInSync).ToList();

            HasSelectedRuns = selected.Count > 0;

            SelectedSummaryDisplay =
                $"Selected for sync: {selected.Count} of {selectable.Count} run(s) " +
                $"({FormatBytes(selected.Sum(row => row.Item.TotalBytes))})";
        }

        [RelayCommand]
        private void SelectAllRuns() => SetAllIncluded(true);

        [RelayCommand]
        private void DeselectAllRuns() => SetAllIncluded(false);

        private void SetAllIncluded(bool include)
        {
            _isBulkLoadingItems = true;
            try
            {
                foreach (SyncItemRowViewModel row in Items.Where(r => r.IsSelectable))
                    row.IncludeInSync = include;
            }
            finally
            {
                _isBulkLoadingItems = false;
            }

            UpdateSelectedSummary();
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

        // ---------------------------------------------------------------
        // Opening locations
        //
        // Both commands only ever hand a location to the shell. Neither reads,
        // writes, copies, or deletes anything, and the provider's capability
        // metadata - not its name - decides whether the remote one is offered.
        // ---------------------------------------------------------------

        /// <summary>
        /// The one place a location actually reaches the operating system.
        /// Tests replace it so they can prove which target a command resolves
        /// without a file manager or browser opening on the test machine.
        /// </summary>
        internal Func<string, bool> LocationLauncher { get; set; } = LaunchWithShell;

        private static bool LaunchWithShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool CanOpenLocalBackupLocation =>
            _backupHistoryService?.GetBackupBasePath() is { Length: > 0 } path &&
            Directory.Exists(path);

        [RelayCommand]
        private void OpenLocalBackupLocation()
        {
            string? path = _backupHistoryService?.GetBackupBasePath();

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                StatusMessage =
                    "The local backup folder does not exist yet. It is created by the " +
                    "first backup or the first download.";
                return;
            }

            if (!LocationLauncher(path))
                StatusMessage = "The local backup folder could not be opened.";
        }

        /// <summary>
        /// The location the remote action would hand to the shell, or null when
        /// there is nothing safe to open. A Drive folder resolves to its own
        /// browser URL; the folder ID inside that URL is never displayed,
        /// logged, or put into a bound property.
        /// </summary>
        internal string? ResolveRemoteLocationTarget()
        {
            switch (SelectedProviderKind)
            {
                case SyncProviderKind.LocalFolder:
                    return !string.IsNullOrWhiteSpace(RemoteRootPath) &&
                           Directory.Exists(RemoteRootPath)
                        ? RemoteRootPath
                        : null;

                case SyncProviderKind.GoogleDrive:
                    string? folderId = SelectedRemoteProfile?.RemoteFolderId;

                    // A root that is missing, trashed, moved out of reach, or
                    // duplicated is not a location to send someone to.
                    return !string.IsNullOrWhiteSpace(folderId) &&
                           GoogleDriveRootFolderStatus is
                               GoogleDriveRootFolderStatus.Ready or
                               GoogleDriveRootFolderStatus.Moved
                        ? $"https://drive.google.com/drive/folders/{folderId}"
                        : null;

                default:
                    return null;
            }
        }

        private string RemoteLocationUnavailableMessage() => SelectedProviderKind switch
        {
            SyncProviderKind.LocalFolder =>
                "Choose an existing local or mounted sync folder first.",

            SyncProviderKind.GoogleDrive => GoogleDriveConnectionStatus switch
            {
                GoogleDriveConnectionStatus.Connected =>
                    "Check the Google Drive backup folder first. A folder that is " +
                    "missing, trashed, moved, or duplicated is not opened.",
                _ =>
                    "Connect the Google Drive account first, then check its backup folder."
            },

            _ => "Opening the selected provider location is unavailable."
        };

        [RelayCommand]
        private void OpenRemoteLocation()
        {
            if (!CanOpenRemoteLocation)
            {
                StatusMessage = "Opening the selected provider location is unavailable.";
                return;
            }

            string? target = ResolveRemoteLocationTarget();

            if (target is null)
            {
                StatusMessage = RemoteLocationUnavailableMessage();
                return;
            }

            if (!LocationLauncher(target))
                StatusMessage = "The sync location could not be opened.";
        }

        // ---------------------------------------------------------------
        // Direction workflows
        //
        // These set the existing direction options and rebuild the preview
        // through the one engine. There is no second transfer path, and none
        // of them execute anything: the confirmation and Sync Now still stand
        // between a direction choice and a byte moving.
        // ---------------------------------------------------------------

        [RelayCommand]
        private Task PreviewUploadAsync() =>
            PreviewDirectionAsync(upload: true, download: false);

        [RelayCommand]
        private Task PreviewDownloadAsync() =>
            PreviewDirectionAsync(upload: false, download: true);

        [RelayCommand]
        private Task PreviewBothDirectionsAsync() =>
            PreviewDirectionAsync(upload: true, download: true);

        private Task PreviewDirectionAsync(bool upload, bool download)
        {
            UploadEnabled = upload;
            DownloadEnabled = download;
            return PreviewSyncAsync();
        }

        /// <summary>
        /// Fills the bound plan state from a plan the provider produced. Shared
        /// by the preview and by revalidation, so the fresh read revalidation
        /// already performed also refreshes the plan instead of costing a
        /// second enumeration.
        /// </summary>
        private void ApplyPlan(SyncPlan plan)
        {
            _lastPlan = plan;

            _isBulkLoadingItems = true;
            try
            {
                Items.Clear();
                Warnings.Clear();
                ConfirmSync = false;

                DateTimeOffset checkedAt = _clock.UtcNow;

                foreach (SyncItem item in plan.Items)
                {
                    Items.Add(new SyncItemRowViewModel(
                        item,
                        plan.ProviderName,
                        checkedAt,
                        UpdateSelectedSummary));
                }
            }
            finally
            {
                _isBulkLoadingItems = false;
            }

            Pagination.SetSource(Items);

            foreach (TransferPreviewWarning warning in plan.Warnings)
                Warnings.Add(new TransferWarningRowViewModel(warning));

            SummaryDisplay =
                $"Upload: {plan.UploadCount} run(s) ({FormatBytes(plan.BytesToUpload)})   " +
                $"Download: {plan.DownloadCount} run(s) ({FormatBytes(plan.BytesToDownload)})   " +
                $"In sync: {plan.InSyncCount}   Conflicts: {plan.ConflictCount}";

            PlanHasUploads = plan.UploadCount > 0;
            PlanHasDownloads = plan.DownloadCount > 0;

            UpdateSelectedSummary();
            UpdateConnectionCheckMessage(plan);
            CanExecuteSync = plan.CanExecute;
        }

        /// <summary>
        /// Sync Now is offered only for a plan that can run, with something
        /// selected, and with the confirmation given. No path executes a
        /// transfer without all three.
        /// </summary>
        public bool CanExecuteSyncNow =>
            CanExecuteSync && ConfirmSync && HasSelectedRuns && !IsLoading;

        [RelayCommand]
        private async Task PreviewSyncAsync()
        {
            // A verification in flight is holding _lastProvider. Disposing it here
            // tears the connection out from under an operation that is still reading.
            if (IsLoading || IsVerifying)
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
            TryUpdateLastUsed();

            try
            {
                IsLoading = true;
                StatusMessage = RequiresServerCredentials
                    ? "Connecting to the configured server and building the sync preview (dry run, nothing is copied)..."
                    : "Building sync preview (dry run, nothing is copied)...";

                ISyncProvider provider = CreateConfiguredProvider();
                _lastProvider = provider;

                SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions
                {
                    Upload = UploadEnabled,
                    Download = DownloadEnabled,
                    ArchiveSync = ArchiveSync
                });

                if (plan.ProviderValidationSucceeded)
                    TryUpdateLastSuccessfulConnection();

                ExecutionResults.Clear();
                ExecutionStatusMessage = "No sync executed.";
                _lastResult = null;
                _verifiedProvider = null;
                VerificationStatusMessage =
                    "Nothing has been verified in this session yet.";

                ApplyPlan(plan);

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

            bool verifyAfterwards = false;
            bool syncExecuting = false;

            try
            {
                IsLoading = true;
                IsSyncRunning = true;
                IsCancellingSync = false;
                ClearRateLimitDiagnostics();
                _syncCancellation?.Dispose();
                _syncCancellation = new CancellationTokenSource();
                TryUpdateLastUsed();
                ResultsSectionExpanded = true;
                ProgressValue = 0;
                ProgressMax = 1;
                ProgressText = "Starting...";
                ExecutionStatusMessage = "Syncing backup runs...";

                syncExecuting = true;
                // Progress<T> marshals reports back to the UI thread.
                var progress = new Progress<SyncProgress>(p =>
                {
                    if (!syncExecuting)
                        return;

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
                        ArchiveSync = ArchiveSync,
                        OnlyRunNames = selectedRunNames,
                        Progress = progress
                    },
                    _syncCancellation.Token);

                syncExecuting = false;
                if (result.BytesCopied > 0)
                {
                    ProgressMax = Math.Max(ProgressMax, result.BytesCopied);
                    ProgressValue = ProgressMax;
                }

                ExecutionResults.Clear();

                string remoteLabel = _lastPlan?.ProviderName ?? "the remote";

                foreach (SyncItemResult item in result.Items)
                    ExecutionResults.Add(new SyncItemResultRowViewModel(item, remoteLabel));

                // Kept before anything else can replace the plan, so the
                // completed run survives the revalidation that follows.
                _lastResult = result;
                _verifiedProvider = _lastProvider;
                OnPropertyChanged(nameof(CanVerifyLastSync));

                TransferPreviewWarning? blocker = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error);

                ExecutionStatusMessage = blocker is not null
                    ? $"Sync blocked: {blocker.Message}"
                    : $"Sync finished. Uploaded {result.Uploaded} run(s), downloaded {result.Downloaded} run(s), skipped {result.Skipped}, copied {FormatBytes(result.BytesCopied)}. Nothing was deleted. Transferred is not yet verified.";

                ProgressText = blocker is null
                    ? $"Done: {FormatBytes(result.BytesCopied)} copied."
                    : "";

                await RefreshSyncLogAsync(_lastProvider);

                // Verification runs after the sync-running state is cleared, so
                // Cancel Sync never appears to still be cancelling a transfer
                // when what is running is a read-only check.
                verifyAfterwards = blocker is null && result.Items.Any(
                    item => item.Status is SyncItemStatus.Uploaded or
                        SyncItemStatus.Downloaded);
            }
            catch (OperationCanceledException)
            {
                // Whatever was already copied stays. Upload is create-only and
                // download never overwrites, so a cancelled run leaves a partial
                // run rather than damage, and nothing is cleaned up.
                ExecutionStatusMessage =
                    "Sync cancelled by you. Files already copied are kept, nothing was " +
                    "deleted or replaced, and running the sync again is safe.";
                ProgressText = "";
            }
            catch (Exception ex)
            {
                // IsRateLimited comes only from the provider's typed backoff
                // signal. Sniffing "429" in the message matched run folders
                // named by timestamp (yyyyMMdd_HHmmss) on any provider.
                ExecutionStatusMessage = IsRateLimited
                    ? $"Sync rate limited by provider: {ex.Message}"
                    : $"Sync failed: {ex.Message}";
                ProgressText = "";
            }
            finally
            {
                syncExecuting = false;
                _countdownCancellation?.Cancel();
                IsRetrying = false;
                RetryCountdownText = "";
                IsLoading = false;
                IsSyncRunning = false;
                IsCancellingSync = false;
                _syncCancellation?.Dispose();
                _syncCancellation = null;
            }

            if (verifyAfterwards)
                await VerifyLastSyncAsync();
        }

        // ---------------------------------------------------------------
        // Revalidation
        //
        // A finished transfer is not proof that both sides now hold the run.
        // This re-reads both sides through the provider's own preview - the
        // same comparison the plan uses, so there is no second manifest
        // engine - and reports what it found per run. It is read-only: a dry
        // run copies, moves, deletes and repairs nothing.
        // ---------------------------------------------------------------

        public bool CanVerifyLastSync =>
            !IsVerifying &&
            !IsLoading &&
            _lastResult is not null &&
            _lastProvider is not null &&
            ReferenceEquals(_verifiedProvider, _lastProvider);

        public bool CanCancelVerification => IsVerifying;

        [RelayCommand]
        private void CancelVerification()
        {
            _verificationCancellation?.Cancel();
            VerificationStatusMessage = "Stopping the check...";
        }

        [RelayCommand]
        private async Task VerifyLastSyncAsync()
        {
            if (_lastResult is null)
            {
                VerificationStatusMessage =
                    "Nothing has been synced in this session, so there is nothing to verify.";
                return;
            }

            // A sync did run; the endpoint it ran against is simply no longer
            // the selected one. Saying "nothing was synced" here would be the
            // one wrong answer.
            if (_lastProvider is null ||
                !ReferenceEquals(_verifiedProvider, _lastProvider))
            {
                VerificationStatusMessage =
                    "The provider or profile changed after that sync ran. Its result stays " +
                    "as recorded; build a new preview to check the current endpoint.";
                return;
            }

            var copied = ExecutionResults.Where(row => row.WasCopied).ToList();

            if (copied.Count == 0)
            {
                VerificationStatusMessage =
                    "No run was copied, so there is nothing to verify.";
                return;
            }

            IsVerifying = true;
            _verificationCancellation?.Dispose();
            _verificationCancellation = new CancellationTokenSource();

            foreach (SyncItemResultRowViewModel row in copied)
                row.Verification = SyncVerificationState.Running;

            VerificationStatusMessage =
                $"Checking {copied.Count} transferred run(s) on both sides. Nothing is copied, moved, or deleted.";

            ISyncProvider provider = _lastProvider;

            try
            {
                SyncPlan plan = await provider.CreatePreviewAsync(
                    new SyncOptions { Upload = true, Download = true, ArchiveSync = ArchiveSync },
                    _verificationCancellation.Token);

                // Settings changed while the check ran: its plan describes an
                // endpoint that is gone and must not become the current plan.
                if (!ReferenceEquals(provider, _lastProvider))
                    throw new OperationCanceledException();

                var byName = plan.Items.ToDictionary(
                    item => item.RunName,
                    StringComparer.OrdinalIgnoreCase);

                foreach (SyncItemResultRowViewModel row in copied)
                    row.Verification = Classify(byName, row.RunName);

                // The check already read both sides; using that same read as
                // the next plan costs no extra enumeration, and the execution
                // results above it are untouched by the refresh.
                ApplyPlan(plan);

                int verified = copied.Count(row => row.IsVerified);
                bool allSidecars = copied.Count > 0 && copied.All(row => row.Verification == SyncVerificationState.SidecarManifestMatch);
                bool anySidecars = copied.Any(row => row.Verification == SyncVerificationState.SidecarManifestMatch);

                VerificationStatusMessage = verified == copied.Count
                    ? (allSidecars
                        ? $"Sidecar manifest match: all {verified} transferred run(s) matched remote sidecar manifests (payload bytes not re-read)."
                        : anySidecars
                            ? $"Manifest match: all {verified} transferred run(s) matched manifests, with some using sidecar descriptors (payload bytes not re-read)."
                            : $"Manifest match: all {verified} transferred run(s) exist on both sides with matching manifests (payload bytes not re-read).")
                    : $"Manifest match: {verified} of {copied.Count} transferred run(s). The rest are listed with what was actually found; nothing was changed.";
            }
            catch (OperationCanceledException)
            {
                MarkUnfinished(copied, SyncVerificationState.Cancelled);
                VerificationStatusMessage =
                    "Verification cancelled. The transfers themselves are unchanged and " +
                    "still recorded; verifying again copies nothing.";
            }
            catch (Exception ex)
            {
                // An endpoint that cannot be read is not a content mismatch,
                // and it is not a failed transfer either.
                MarkUnfinished(copied, SyncVerificationState.EndpointUnavailable);
                VerificationStatusMessage =
                    $"Verification could not read both sides: {ex.Message} The transfers " +
                    "themselves are unchanged. Retry the check when the endpoint is reachable.";
            }
            finally
            {
                IsVerifying = false;
                _verificationCancellation?.Dispose();
                _verificationCancellation = null;
                OnPropertyChanged(nameof(CanVerifyLastSync));
            }
        }

        private static void MarkUnfinished(
            IEnumerable<SyncItemResultRowViewModel> rows,
            SyncVerificationState state)
        {
            foreach (SyncItemResultRowViewModel row in rows)
            {
                if (row.Verification == SyncVerificationState.Running)
                    row.Verification = state;
            }
        }

        /// <summary>
        /// A fresh plan states where each run is now. A matching manifest is the
        /// verdict that means manifest-matched; a run the plan still wants to copy is
        /// a run that is missing on the side it would be copied to.
        /// </summary>
        private static SyncVerificationState Classify(
            IReadOnlyDictionary<string, SyncItem> plan,
            string runName)
        {
            if (!plan.TryGetValue(runName, out SyncItem? item))
                return SyncVerificationState.MissingBothSides;

            return item.Action switch
            {
                SyncItemAction.InSync => item.Verification switch
                {
                    VerificationStrength.SidecarManifestMatch => SyncVerificationState.SidecarManifestMatch,
                    VerificationStrength.PayloadVerified => SyncVerificationState.PayloadVerified,
                    _ => SyncVerificationState.ManifestMatch
                },
                SyncItemAction.Conflict => SyncVerificationState.ContentMismatch,
                SyncItemAction.UploadToRemote => SyncVerificationState.MissingRemotely,
                SyncItemAction.DownloadToLocal => SyncVerificationState.MissingLocally,
                _ => SyncVerificationState.EndpointUnavailable
            };
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

        // Kept as a method: its signature ends the source slice that
        // SyncUiProviderParityTests inspects.
        private static string FormatBytes(long bytes) => ByteSize.Format(bytes);

        private sealed class UnavailableGoogleDriveRootFolderService
            : IGoogleDriveRootFolderService
        {
            public static UnavailableGoogleDriveRootFolderService Instance { get; } =
                new();

            public Task<GoogleDriveRootFolderResult> InspectAsync(
                Guid remoteProfileId,
                CancellationToken cancellationToken = default) =>
                Unavailable(remoteProfileId, cancellationToken);

            public Task<GoogleDriveRootFolderResult> EnsureAsync(
                Guid remoteProfileId,
                CancellationToken cancellationToken = default) =>
                Unavailable(remoteProfileId, cancellationToken);

            public Task<GoogleDriveRootFolderResult> RecreateAsync(
                Guid remoteProfileId,
                GoogleDriveRootFolderRecreationConfirmation confirmation,
                CancellationToken cancellationToken = default) =>
                Unavailable(remoteProfileId, cancellationToken);

            private static Task<GoogleDriveRootFolderResult> Unavailable(
                Guid remoteProfileId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Unavailable,
                    remoteProfileId,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.Unavailable,
                    Message: "Google Drive root-folder services are unavailable."));
            }
        }
    }
}
