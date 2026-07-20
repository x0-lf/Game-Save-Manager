using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        private readonly IUtcClock _clock;
        private SyncPlan? _lastPlan;
        private ISyncProvider? _lastProvider;
        private bool _applyingProfile;
        private bool _suppressProfileSelection;
        private bool _suppressProfileOptionSelection;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Choose a sync folder (NAS share, USB drive, cloud-synced folder) and preview the sync.";

        [ObservableProperty]
        private string remoteRootPath = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLocalFolderSelected))]
        [NotifyPropertyChangedFor(nameof(IsSftpSelected))]
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

        [ObservableProperty]
        private SyncRemoteProfile? selectedRemoteProfile;

        [ObservableProperty]
        private SyncRemoteProfileOption? selectedRemoteProfileOption;

        [ObservableProperty]
        private string remoteProfileDisplayName = "";

        [ObservableProperty]
        private string remoteProfileState = "Unsaved changes";

        [ObservableProperty]
        private bool confirmDeleteRemoteProfile;

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

        public ObservableCollection<SyncRemoteProfile> RemoteProfiles { get; } = new();

        public ObservableCollection<SyncRemoteProfileOption> RemoteProfileOptions { get; } = new();

        public IReadOnlyList<SyncProviderDescriptor> ProviderOptions { get; }

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

        public string ProviderCapabilitySummary
        {
            get
            {
                if (!SelectedProviderDescriptor.IsImplemented)
                    return SelectedProviderDescriptor.UnavailableMessage ?? "Provider unavailable.";

                var capabilities = new List<string>();

                if (RequiresServerCredentials)
                    capabilities.Add("server credentials required");
                if (RequiresInteractiveLogin)
                    capabilities.Add("interactive login required");
                if (SupportsConnectionTesting)
                    capabilities.Add("connection testing supported");
                if (SupportsRemoteFolderSelection)
                    capabilities.Add("remote folder selection supported");
                if (SupportsPersistentAuthentication)
                    capabilities.Add("persistent authentication supported");

                return string.Join("; ", capabilities) + ".";
            }
        }

        public bool IsLocalFolderSelected =>
            SelectedProviderDescriptor.ConfigurationSurface ==
            SyncProviderConfigurationSurface.LocalFolder;

        public bool IsSftpSelected =>
            SelectedProviderDescriptor.ConfigurationSurface ==
            SyncProviderConfigurationSurface.Sftp;

        public SyncViewModel(
            ISyncProviderFactory syncProviderFactory,
            ISyncProviderCatalog providerCatalog,
            IFolderPickerService folderPickerService,
            ISyncSettingsStore syncSettingsStore,
            ISyncRemoteProfileRepository profileRepository,
            ISyncRemoteProfileMigrationService profileMigrationService,
            IUtcClock clock)
        {
            _syncProviderFactory = syncProviderFactory;
            _providerCatalog = providerCatalog;
            _folderPickerService = folderPickerService;
            _syncSettingsStore = syncSettingsStore;
            _profileRepository = profileRepository;
            _clock = clock;
            ProviderOptions = _providerCatalog.GetAll()
                .Where(descriptor => descriptor.IsImplemented)
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
        }

        partial void OnRemoteRootPathChanged(string value) => OnPersistentSettingChanged();

        partial void OnUploadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnDownloadEnabledChanged(bool value) => InvalidatePlan();

        partial void OnSelectedProviderKindChanged(SyncProviderKind value)
        {
            InvalidatePlan();
            MarkProfileDirty();

            StatusMessage = GetUnavailableProviderMessage(value)
                ?? "Sync provider changed. Configure it and build a new sync preview.";
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

        partial void OnSelectedRemoteProfileChanged(SyncRemoteProfile? value)
        {
            if (!_suppressProfileSelection && value is not null)
            {
                SelectProfileOption(value.Id);
                ApplyRemoteProfile(value, persistSelection: true);
            }
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
            if (!force && _lastPlan is null && _lastProvider is null)
                return;

            _lastPlan = null;
            _lastProvider?.Dispose();
            _lastProvider = null;
            ClearPreview();
            ConfirmSync = false;
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
            ConfirmDeleteRemoteProfile = false;
            RemoteProfileState = "Unsaved settings (no profile)";
            StatusMessage = "Using the current sync settings without a saved remote profile. Build a new preview when ready.";
            SaveNonSecretSettings();
        }

        private void ApplyRemoteProfile(
            SyncRemoteProfile profile,
            bool persistSelection)
        {
            _applyingProfile = true;

            try
            {
                ClearSessionOnlySftpState();
                SelectedProviderKind = profile.ProviderKind;
                RemoteProfileDisplayName = profile.DisplayName;

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
                $"Loaded remote profile '{profile.DisplayName}'. Build a new sync preview when ready.";

            if (persistSelection)
                SaveNonSecretSettings();
        }

        [RelayCommand]
        private void NewRemoteProfile()
        {
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
            }
            finally
            {
                _applyingProfile = false;
            }

            InvalidatePlan(force: true);
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
                SyncRemoteProfile profile;

                if (SelectedRemoteProfile is null)
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
                    profile = _profileRepository.Update(BuildProfile(
                        SelectedRemoteProfile.Id,
                        SelectedRemoteProfile.CreatedUtc,
                        now,
                        SelectedRemoteProfile.LastUsedUtc,
                        SelectedRemoteProfile.LastSuccessfulConnectionUtc));
                }

                RefreshProfileList(profile.Id);
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
                    lastSuccessfulConnectionUtc: null));

                RefreshProfileList(profile.Id);
                ClearSessionOnlySftpState();
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
        private void DeleteRemoteProfile()
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
                string deletedName = SelectedRemoteProfile.DisplayName;
                _profileRepository.Delete(SelectedRemoteProfile.Id);
                ReplaceProfileCollections(_profileRepository.GetAll());
                NewRemoteProfile();
                ConfirmDeleteRemoteProfile = false;
                StatusMessage = $"Deleted profile '{deletedName}' only. No backup, history, known-host, or remote data was removed.";
            }
            catch
            {
                StatusMessage = "The remote profile configuration could not be deleted.";
            }
        }

        private SyncRemoteProfile BuildProfile(
            Guid id,
            DateTimeOffset createdUtc,
            DateTimeOffset updatedUtc,
            DateTimeOffset? lastUsedUtc,
            DateTimeOffset? lastSuccessfulConnectionUtc)
        {
            string displayName = SyncRemoteProfileValidation.NormalizeDisplayName(
                RemoteProfileDisplayName);
            SyncRemoteProfileSettings settings;
            string? accountDisplayName;
            string remoteRootDisplayName;

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
                RemoteFolderId: null);
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
        private void OpenRemoteLocation()
        {
            if (!CanOpenRemoteLocation || !IsLocalFolderSelected)
            {
                StatusMessage = "Opening the selected provider location is unavailable.";
                return;
            }

            if (string.IsNullOrWhiteSpace(RemoteRootPath) ||
                !Directory.Exists(RemoteRootPath))
            {
                StatusMessage = "Choose an existing local or mounted sync folder first.";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = RemoteRootPath,
                    UseShellExecute = true
                });
            }
            catch
            {
                StatusMessage = "The sync folder could not be opened.";
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
                    Download = DownloadEnabled
                });

                _lastPlan = plan;

                if (plan.ProviderValidationSucceeded)
                    TryUpdateLastSuccessfulConnection();
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
                TryUpdateLastUsed();
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
