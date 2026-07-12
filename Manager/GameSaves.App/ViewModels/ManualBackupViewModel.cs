using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class ManualBackupViewModel : ViewModelBase
    {
        private readonly IManualBackupService _manualBackupService;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly IFolderPickerService _folderPickerService;
        private readonly IManualBackupPresetRepository _presetRepository;
        private readonly ProfilesViewModel _profilesViewModel;
        private readonly InstalledGamesViewModel _installedGamesViewModel;
        private ManualBackupPlan? _lastPlan;
        private bool _presetsLoaded;
        private bool _applyingPreset;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Select a profile, a game, and a destination.";

        [ObservableProperty]
        private SteamProfileRowViewModel? selectedProfile;

        [ObservableProperty]
        private InstalledGameRowViewModel? selectedGame;

        [ObservableProperty]
        private string destinationPath = "";

        [ObservableProperty]
        private bool includeSteamUserDataGameFolder = true;

        [ObservableProperty]
        private bool includeApprovedMappings = true;

        [ObservableProperty]
        private int totalFiles;

        [ObservableProperty]
        private string totalSizeDisplay = "0 B";

        [ObservableProperty]
        private bool canExecuteBackup;

        [ObservableProperty]
        private bool confirmBackup;

        [ObservableProperty]
        private string executionStatusMessage = "No backup executed.";

        [ObservableProperty]
        private int filesBackedUp;

        [ObservableProperty]
        private string bytesBackedUpDisplay = "0 B";

        [ObservableProperty]
        private string backupLocationMessage = "";

        public bool HasAnyBackupSource =>
            IncludeSteamUserDataGameFolder || IncludeApprovedMappings;

        public string DefaultDestination => _backupHistoryService.GetBackupBasePath();

        public ObservableCollection<SteamProfileRowViewModel> Profiles =>
            _profilesViewModel.Profiles;

        public ObservableCollection<InstalledGameRowViewModel> Games =>
            _installedGamesViewModel.Games;

        public ObservableCollection<TransferPreviewItemRowViewModel> Items { get; } = new();

        public ObservableCollection<TransferWarningRowViewModel> Warnings { get; } = new();

        public ObservableCollection<SaveTransferItemResultRowViewModel> ExecutionResults { get; } = new();

        [ObservableProperty]
        private BackupPresetRowViewModel? selectedPreset;

        [ObservableProperty]
        private string presetName = "";

        public ObservableCollection<BackupPresetRowViewModel> Presets { get; } = new();

        public ManualBackupViewModel(
            IManualBackupService manualBackupService,
            IBackupHistoryService backupHistoryService,
            IFolderPickerService folderPickerService,
            IManualBackupPresetRepository presetRepository,
            ProfilesViewModel profilesViewModel,
            InstalledGamesViewModel installedGamesViewModel)
        {
            _manualBackupService = manualBackupService;
            _backupHistoryService = backupHistoryService;
            _folderPickerService = folderPickerService;
            _presetRepository = presetRepository;
            _profilesViewModel = profilesViewModel;
            _installedGamesViewModel = installedGamesViewModel;

            destinationPath = _backupHistoryService.GetBackupBasePath();
        }

        public string PresetStatusDisplay => SelectedPreset is null
            ? "No preset selected - the backup uses the settings exactly as shown."
            : $"Preset \"{SelectedPreset.Name}\" is selected.";

        partial void OnSelectedPresetChanged(BackupPresetRowViewModel? value)
        {
            OnPropertyChanged(nameof(PresetStatusDisplay));

            if (value is null)
                return;

            // Applying a preset must not read as a manual edit, which would
            // immediately deselect it again.
            _applyingPreset = true;

            try
            {
                PresetName = value.Name;
                DestinationPath = value.Preset.DestinationRoot;
                IncludeSteamUserDataGameFolder = value.Preset.IncludeSteamUserDataGameFolder;
                IncludeApprovedMappings = value.Preset.IncludeApprovedMappings;
            }
            finally
            {
                _applyingPreset = false;
            }

            StatusMessage = $"Preset \"{value.Name}\" applied. Build a new backup preview.";

            long presetId = value.Id;

            _ = Task.Run(() =>
            {
                try
                {
                    _presetRepository.MarkUsed(presetId);
                }
                catch
                {
                    // Usage tracking is best-effort only.
                }
            });
        }

        [RelayCommand]
        private void ClearPresetSelection()
        {
            if (SelectedPreset is null)
            {
                StatusMessage = "No preset is selected. The backup uses the current settings.";
                return;
            }

            SelectedPreset = null;
            StatusMessage = "Preset selection cleared. The current settings are kept and the backup runs without a preset.";
        }

        // A manual edit means the settings no longer match the selected preset;
        // deselect it so the UI never claims a preset that will not be used.
        private void DeselectPresetAfterManualEdit()
        {
            if (!_applyingPreset && SelectedPreset is not null)
                SelectedPreset = null;
        }

        private async Task LoadPresetsAsync()
        {
            var presets = await Task.Run(() => _presetRepository.GetAll());

            SelectedPreset = null;
            Presets.Clear();

            foreach (ManualBackupPreset preset in presets)
                Presets.Add(new BackupPresetRowViewModel(preset));

            _presetsLoaded = true;
        }

        [RelayCommand]
        private async Task SavePresetAsync()
        {
            if (IsLoading)
                return;

            if (string.IsNullOrWhiteSpace(PresetName))
            {
                StatusMessage = "Enter a preset name before saving.";
                return;
            }

            if (string.IsNullOrWhiteSpace(DestinationPath))
            {
                StatusMessage = "A preset needs a backup destination.";
                return;
            }

            try
            {
                var toSave = new ManualBackupPreset(
                    Id: 0,
                    Name: PresetName.Trim(),
                    DestinationRoot: DestinationPath,
                    IncludeSteamUserDataGameFolder: IncludeSteamUserDataGameFolder,
                    IncludeApprovedMappings: IncludeApprovedMappings,
                    CreatedUtc: DateTimeOffset.UtcNow,
                    LastUsedUtc: null);

                ManualBackupPreset saved =
                    await Task.Run(() => _presetRepository.Save(toSave));

                await LoadPresetsAsync();

                StatusMessage = $"Preset \"{saved.Name}\" saved.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to save the preset: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task DeletePresetAsync()
        {
            if (IsLoading)
                return;

            if (SelectedPreset is null)
            {
                StatusMessage = "Select a preset to delete.";
                return;
            }

            try
            {
                string name = SelectedPreset.Name;
                long id = SelectedPreset.Id;

                await Task.Run(() => _presetRepository.Delete(id));
                await LoadPresetsAsync();

                StatusMessage = $"Preset \"{name}\" deleted. Saved backups are not affected.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to delete the preset: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ChooseDestinationFolderAsync()
        {
            try
            {
                string? picked = await _folderPickerService.PickFolderAsync(
                    "Select where this backup should be stored.",
                    DestinationPath);

                // Cancel keeps the current destination unchanged.
                if (!string.IsNullOrWhiteSpace(picked))
                    DestinationPath = picked;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open the folder picker: {ex.Message}";
            }
        }

        partial void OnIncludeSteamUserDataGameFolderChanged(bool value)
        {
            OnPropertyChanged(nameof(HasAnyBackupSource));
            DeselectPresetAfterManualEdit();
            InvalidatePlan();
        }

        partial void OnIncludeApprovedMappingsChanged(bool value)
        {
            OnPropertyChanged(nameof(HasAnyBackupSource));
            DeselectPresetAfterManualEdit();
            InvalidatePlan();
        }

        partial void OnDestinationPathChanged(string value)
        {
            DeselectPresetAfterManualEdit();
            InvalidatePlan();
        }

        // A plan built with different inputs must not stay executable.
        private void InvalidatePlan()
        {
            if (_lastPlan is null)
                return;

            _lastPlan = null;
            ClearPreview();
            StatusMessage = "Backup settings changed. Build a new backup preview.";
        }

        [RelayCommand]
        private async Task RefreshInputsAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Refreshing profiles and installed games...";

                if (Profiles.Count == 0)
                    await _profilesViewModel.RefreshProfilesCommand.ExecuteAsync(null);

                if (Games.Count == 0)
                    await _installedGamesViewModel.RefreshCommand.ExecuteAsync(null);

                if (!_presetsLoaded)
                    await LoadPresetsAsync();

                SelectedProfile ??= Profiles.FirstOrDefault();
                SelectedGame ??= Games.FirstOrDefault();

                StatusMessage = "Inputs refreshed.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to refresh inputs: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void UseDefaultDestination()
        {
            DestinationPath = DefaultDestination;
        }

        [RelayCommand]
        private async Task PreviewBackupAsync()
        {
            if (IsLoading)
                return;

            _lastPlan = null;
            ClearPreview();

            if (SelectedProfile is null)
            {
                StatusMessage = "Select a profile.";
                return;
            }

            if (SelectedGame is null)
            {
                StatusMessage = "Select an installed game.";
                return;
            }

            if (!HasAnyBackupSource)
            {
                StatusMessage = "Preview blocked: no backup source is selected. Enable the Steam userdata game folder, approved save-path mappings, or both.";
                return;
            }

            if (string.IsNullOrWhiteSpace(DestinationPath))
            {
                StatusMessage = "Destination is required before previewing a backup. Use \"Choose Folder...\" or type a folder path.";
                return;
            }

            try
            {
                IsLoading = true;
                StatusMessage = "Building backup preview (dry run, nothing is copied)...";

                var options = new ManualBackupOptions
                {
                    IncludeSteamUserDataGameFolder = IncludeSteamUserDataGameFolder,
                    IncludeApprovedMappings = IncludeApprovedMappings
                };

                ManualBackupPlan plan =
                    await _manualBackupService.CreatePreviewAsync(
                        SelectedGame.Game,
                        SelectedProfile.Profile,
                        DestinationPath,
                        options);

                _lastPlan = plan;
                ExecutionResults.Clear();
                ExecutionStatusMessage = "No backup executed.";
                FilesBackedUp = 0;
                BytesBackedUpDisplay = "0 B";
                BackupLocationMessage = "";
                ConfirmBackup = false;

                foreach (TransferPreviewItem item in plan.Items)
                    Items.Add(new TransferPreviewItemRowViewModel(item));

                foreach (TransferPreviewWarning warning in plan.Warnings)
                    Warnings.Add(new TransferWarningRowViewModel(warning));

                TotalFiles = plan.TotalFiles;
                TotalSizeDisplay = FormatBytes(plan.TotalBytes);
                CanExecuteBackup = plan.CanExecute;

                StatusMessage = plan.CanExecute
                    ? $"Backup preview ready for {plan.Game.Name}: {plan.TotalFiles} file(s), {FormatBytes(plan.TotalBytes)}. Nothing was copied."
                    : "Backup preview created, but nothing can be backed up. Check the warnings.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to build backup preview: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task ExecuteBackupAsync()
        {
            if (IsLoading)
                return;

            if (_lastPlan is null)
            {
                ExecutionStatusMessage = "Build a backup preview first.";
                return;
            }

            if (!ConfirmBackup)
            {
                ExecutionStatusMessage = "Backup blocked. Confirm the checkbox first.";
                return;
            }

            try
            {
                IsLoading = true;
                ExecutionStatusMessage = "Backing up files...";

                var options = new ManualBackupExecuteOptions
                {
                    DryRun = false,
                    ConfirmExecution = ConfirmBackup
                };

                ManualBackupResult result =
                    await _manualBackupService.ExecuteAsync(_lastPlan, options);

                ExecutionResults.Clear();

                foreach (SaveTransferItemResult item in result.Items)
                    ExecutionResults.Add(new SaveTransferItemResultRowViewModel(item));

                FilesBackedUp = result.FilesBackedUp;
                BytesBackedUpDisplay = FormatBytes(result.BytesBackedUp);

                bool inDefaultLocation = result.BackupRootPath is not null &&
                    result.BackupRootPath.StartsWith(DefaultDestination, StringComparison.OrdinalIgnoreCase);

                BackupLocationMessage = result.BackupRootPath is null
                    ? ""
                    : inDefaultLocation
                        ? $"Backup written to: {result.BackupRootPath}. Refresh the Backups tab to see and restore it."
                        : $"Backup written to: {result.BackupRootPath}. Custom destinations are not listed in the Backups tab, but the manifest.json makes the run self-contained.";

                TransferPreviewWarning? blocker = result.Warnings
                    .Skip(_lastPlan.Warnings.Count)
                    .FirstOrDefault(warning => warning.Severity == TransferWarningSeverity.Error);

                ExecutionStatusMessage = blocker is not null
                    ? $"Backup blocked: {blocker.Message}"
                    : $"Backup finished. Backed up {FilesBackedUp} file(s). Source files were not changed.";
            }
            catch (Exception ex)
            {
                ExecutionStatusMessage = $"Backup failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ClearPreview()
        {
            Items.Clear();
            Warnings.Clear();
            TotalFiles = 0;
            TotalSizeDisplay = "0 B";
            CanExecuteBackup = false;
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
