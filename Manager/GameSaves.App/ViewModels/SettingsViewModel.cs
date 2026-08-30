using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.Core.Sync;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    /// <summary>
    /// The Settings page. Owns appearance choices that persist through
    /// <see cref="IUiSettingsStore"/> and apply immediately through
    /// <see cref="ThemeService"/>; layout and about information are surfaced
    /// from the child view models and services that already own them.
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly IUiSettingsStore _uiSettingsStore;
        private readonly ThemeService _themeService;
        private readonly WindowMaterialService _windowMaterialService;
        private readonly WorkspaceLayoutService _workspaceLayout;

        // "system", "light" or "dark".
        [ObservableProperty]
        private string themeChoice;

        // One of the AppUiSettings accent constants.
        [ObservableProperty]
        private string accentTheme;

        // Opacity levels in [0.2, 1.0]; 1.0 is fully opaque. Changes apply
        // live and persist immediately.
        [ObservableProperty]
        private double windowOpacity = UiTransparencySettings.Opaque;

        [ObservableProperty]
        private double cardOpacity = UiTransparencySettings.Opaque;

        [ObservableProperty]
        private double insetOpacity = UiTransparencySettings.Opaque;

        // One of the AppUiSettings material constants ("none", "acrylic" or
        // "mica"). Changes apply live and persist immediately; the OS may
        // still deny the material, in which case the app stays opaque.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWindowMaterialSelected))]
        [NotifyPropertyChangedFor(nameof(IsWindowOpacityInert))]
        private string windowMaterial = AppUiSettings.MaterialNone;

        // Accessibility: text scale in [0.85, 1.5] (1.0 = unchanged), plus
        // the reduce-motion and high-contrast switches. Changes apply live
        // and persist immediately.
        [ObservableProperty]
        private double textScale = UiAccessibilitySettings.DefaultTextScale;

        [ObservableProperty]
        private bool reduceMotion;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWindowMaterialSelected))]
        [NotifyPropertyChangedFor(nameof(IsWindowOpacityInert))]
        private bool highContrast;

        // Navigation rail customization. Position is one of the
        // UiRailLayoutSettings position constants; the collapse switch shows
        // a glyph-only strip. Changes apply live and persist immediately.
        [ObservableProperty]
        private string railPosition = UiRailLayoutSettings.PositionLeft;

        [ObservableProperty]
        private bool railCollapsed;

        // The Scan Steam library action on the navigation rail. Turning it off
        // never removes the ability to scan: the Dashboard always offers it.
        [ObservableProperty]
        private bool showScanInNavigationRail = true;

        // The tab the main window selects once at startup. One of the nine
        // stable rail tab keys; the main window falls back to Dashboard when
        // the saved tab is hidden, detached, or unknown. Changes persist
        // immediately but apply only at the next start.
        [ObservableProperty]
        private string startupTabKey = UiRailLayoutSettings.TabDashboard;

        // Workspace layouts (saved detach/bounds snapshots). The name being
        // typed into the inline "save current layout" box; validation and
        // feedback arrive through WorkspaceStatus. Saved layouts are never
        // applied automatically — applying is an explicit action.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveWorkspaceLayoutCommand))]
        private string newLayoutName = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWorkspaceStatus))]
        private string workspaceStatus = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ResetWorkspaceText))]
        private bool isResetArmed;

        // The live-window bridge (snapshot, apply, reattach, file exchange).
        // Assigned by the main window glue once the window exists; commands
        // treat null as "nothing to snapshot or apply".
        public IWorkspaceLayoutHost? WorkspaceHost { get; set; }

        // The shell's navigation bridge, assigned by the main window alongside
        // WorkspaceHost. Null means "no window yet", and every command that
        // uses it is a no-op in that state rather than a crash.
        public IAppNavigationHost? NavigationHost { get; set; }

        /// <summary>
        /// Takes the user to the provider's existing setup experience on the
        /// Sync page. It navigates and preselects, nothing more: a provider
        /// this build cannot use is never offered the action at all, so the
        /// button can never imply a capability that is not there.
        /// </summary>
        [RelayCommand]
        private void ConfigureProvider(ProviderStatusOption? provider)
        {
            if (provider is not { IsConfigurable: true })
                return;

            NavigationHost?.ShowSyncProviderConfiguration(provider.Kind);
        }

        public SettingsViewModel(
            IUiSettingsStore uiSettingsStore,
            ThemeService themeService,
            WindowMaterialService windowMaterialService,
            InstalledGamesViewModel installedGames,
            ISyncSettingsStore syncSettingsStore,
            ISyncProviderCatalog providerCatalog,
            string platform,
            string databasePath,
            WorkspaceLayoutService workspaceLayout)
        {
            _uiSettingsStore = uiSettingsStore;
            _workspaceLayout = workspaceLayout;
            _themeService = themeService;
            _windowMaterialService = windowMaterialService;
            InstalledGames = installedGames;
            Platform = platform;
            DatabasePath = databasePath;

            // The Data locations rows show exactly the files the stores read
            // and write, taken from the store instances themselves so the
            // paths can never drift from the real locations.
            UiSettingsPath = uiSettingsStore.FilePath;
            SyncSettingsPath = syncSettingsStore.FilePath;

            // The Providers rows are the catalog's own availability state,
            // filtered exactly like the Sync tab's provider picker.
            ProviderStatuses = providerCatalog
                .GetAll()
                .Where(descriptor => descriptor.IsConfigurationAvailable)
                .Select(descriptor => new ProviderStatusOption(
                    descriptor.DisplayName,
                    descriptor.IsImplemented
                        ? "Available"
                        : descriptor.UnavailableMessage ?? "Not implemented",
                    descriptor.Kind,
                    descriptor.IsImplemented))
                .ToArray();

            AppUiSettings settings = uiSettingsStore.Load();

            // Built before the rail property assignments below, because their
            // change handlers persist through SaveRailTabSettings, which
            // reads this collection.
            var hiddenTabs = new HashSet<string>(
                settings.RailLayout.HiddenTabs,
                StringComparer.Ordinal);
            RailTabs = new ObservableCollection<RailTabOption>(
                settings.RailLayout.TabOrder.Select(key => new RailTabOption(
                    key,
                    GetRailTabHeader(key),
                    isVisible: !hiddenTabs.Contains(key),
                    canHide: UiRailLayoutSettings.CanHideTab(key))));
            UpdateRailTabMoveFlags();

            foreach (RailTabOption option in RailTabs)
                option.PropertyChanged += OnRailTabPropertyChanged;

            WorkspaceLayouts = new ObservableCollection<UiWorkspaceLayoutSettings>(
                settings.WorkspaceLayouts);
            WorkspaceLayouts.CollectionChanged += OnWorkspaceLayoutsChanged;

            themeChoice = settings.ThemeChoice;
            accentTheme = settings.AccentTheme;
            windowOpacity = settings.Transparency.Window;
            cardOpacity = settings.Transparency.Card;
            insetOpacity = settings.Transparency.Inset;
            windowMaterial = settings.WindowMaterial;
            textScale = settings.Accessibility.TextScale;
            reduceMotion = settings.Accessibility.ReduceMotion;
            highContrast = settings.Accessibility.HighContrast;
            railPosition = settings.RailLayout.Position;
            railCollapsed = settings.RailLayout.Collapsed;
            startupTabKey = settings.StartupTabKey;
            showScanInNavigationRail = settings.ScanAction.ShowInNavigationRail;

            // Per-page scan visibility. Only the pages that actually offer a
            // scan action of their own get a row, so the list cannot imply a
            // control that does not exist.
            ScanPages = new ObservableCollection<ScanPageOption>(
                UiScanActionSettings.ScannablePages.Select(key => new ScanPageOption(
                    key,
                    GetRailTabHeader(key),
                    isVisible: settings.ScanAction.IsVisibleOn(key))));

            foreach (ScanPageOption option in ScanPages)
                option.PropertyChanged += OnScanPageChanged;

            // Section visibility, grouped by page. The catalog is the source of
            // truth for what sections exist, so this list can never offer a
            // section the page does not have.
            SectionGroups = new ObservableCollection<WorkspaceSectionGroup>(
                WorkspaceLayoutCatalog.Pages.Select(pageKey => new WorkspaceSectionGroup(
                    pageKey,
                    GetRailTabHeader(pageKey),
                    workspaceLayout.Page(pageKey))));
        }

        // Per-page scan rows, in rail order.
        public ObservableCollection<ScanPageOption> ScanPages { get; }

        /// <summary>
        /// Section visibility, one group per page. Settings and Diagnostics
        /// have no movable sections and therefore no group.
        /// </summary>
        public ObservableCollection<WorkspaceSectionGroup> SectionGroups { get; }

        /// <summary>
        /// Whether a page offers its own Scan action. Bound directly by the
        /// pages that have one, so a page keeps its own additional conditions
        /// (a scan button hidden because Steam is missing stays hidden) without
        /// this having to know about them.
        /// </summary>
        public bool ShowScanOnDashboard => IsScanVisibleOn(UiRailLayoutSettings.TabDashboard);

        public bool ShowScanOnInstalledGames =>
            IsScanVisibleOn(UiRailLayoutSettings.TabInstalledGames);

        public bool ShowScanOnProfiles => IsScanVisibleOn(UiRailLayoutSettings.TabProfiles);

        private bool IsScanVisibleOn(string pageKey) =>
            ScanPages.FirstOrDefault(option =>
                string.Equals(option.Key, pageKey, StringComparison.Ordinal))
                ?.IsVisible ?? true;

        private void OnScanPageChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ScanPageOption.IsVisible))
                return;

            OnPropertyChanged(nameof(ShowScanOnDashboard));
            OnPropertyChanged(nameof(ShowScanOnInstalledGames));
            OnPropertyChanged(nameof(ShowScanOnProfiles));

            SaveAndApply(settings => settings with
            {
                ScanAction = settings.ScanAction with
                {
                    HiddenPages = UiScanActionSettings.NormalizeHiddenPages(
                        ScanPages
                            .Where(option => !option.IsVisible)
                            .Select(option => option.Key)),
                },
            });
        }

        /// <summary>
        /// A material other than "none" is selected and not overridden by
        /// high contrast, mirroring <see cref="WindowMaterialService.EffectiveMaterial"/>
        /// for the Settings hints: high contrast always wins.
        /// </summary>
        public bool IsWindowMaterialSelected =>
            !HighContrast &&
            WindowMaterial is AppUiSettings.MaterialAcrylic
                or AppUiSettings.MaterialMica;

        // While a material owns the window surface the window-background
        // opacity setting has nothing to act on (and high contrast forces
        // opaque surfaces anyway), so its slider is disabled.
        public bool IsWindowOpacityInert =>
            HighContrast || IsWindowMaterialSelected;

        /// <summary>Ordered accent choices for the appearance picker.</summary>
        public static IReadOnlyList<string> AccentChoices { get; } = new[]
        {
            AppUiSettings.AccentIndigo,
            AppUiSettings.AccentTeal,
            AppUiSettings.AccentRose,
            AppUiSettings.AccentAmber,
            AppUiSettings.AccentViolet,
        };

        // Owned by InstalledGamesViewModel so the table and the Settings
        // page edit the same live options.
        public InstalledGamesViewModel InstalledGames { get; }

        // The rail's per-tab rows in display order; one row per tab key.
        public ObservableCollection<RailTabOption> RailTabs { get; }

        // The saved workspace layouts in saved order; each row is a layout
        // record, so Apply and Delete carry the exact persisted snapshot.
        public ObservableCollection<UiWorkspaceLayoutSettings> WorkspaceLayouts { get; }

        public bool HasWorkspaceLayouts => WorkspaceLayouts.Count > 0;

        public bool HasWorkspaceStatus => WorkspaceStatus.Length > 0;

        public string ResetWorkspaceText =>
            IsResetArmed ? "Confirm reset" : "Reset workspace";

        public string Platform { get; }

        // Diagnostics paints the platform value in its own identity colour, so
        // the row is scannable at a glance. These are the only three the app
        // ships on; anything else keeps the neutral ink.
        public bool IsWindowsPlatform =>
            string.Equals(Platform, "windows", StringComparison.OrdinalIgnoreCase);

        public bool IsLinuxPlatform =>
            string.Equals(Platform, "linux", StringComparison.OrdinalIgnoreCase);

        public string DatabasePath { get; }

        // Read-only surfaces added by the Settings categories work. Every
        // value comes from the component that owns it: paths from the stores
        // and the database path provider input, provider rows from the
        // provider catalog, diagnostics from this assembly and the runtime.

        // Settings > Data locations: the real files this application reads
        // and writes. Read-only; nothing here browses or edits them.
        public string UiSettingsPath { get; }

        public string SyncSettingsPath { get; }

        // Settings > Providers: the provider catalog's current availability
        // state, one row per configurable provider.
        public IReadOnlyList<ProviderStatusOption> ProviderStatuses { get; }

        // Settings > Diagnostics: the application's own assembly version,
        // the running OS, and the .NET runtime description.
        public string ApplicationVersion =>
            typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)
                ?? string.Empty;

        public string OperatingSystemVersion => Environment.OSVersion.VersionString;

        public string RuntimeDescription => RuntimeInformation.FrameworkDescription;

        partial void OnThemeChoiceChanged(string value)
        {
            if (value is not (AppUiSettings.ThemeSystem
                or AppUiSettings.ThemeLight
                or AppUiSettings.ThemeDark))
            {
                return;
            }

            SaveAndApply(settings => settings with { ThemeChoice = value });
        }

        partial void OnAccentThemeChanged(string value)
        {
            if (!AppUiSettings.IsAccentTheme(value))
                return;

            SaveAndApply(settings => settings with { AccentTheme = value });
        }

        partial void OnWindowOpacityChanged(double value) =>
            SaveAndApply(settings => settings with
            {
                Transparency = settings.Transparency with
                {
                    Window = UiTransparencySettings.NormalizeOpacity(value),
                },
            });

        partial void OnCardOpacityChanged(double value) =>
            SaveAndApply(settings => settings with
            {
                Transparency = settings.Transparency with
                {
                    Card = UiTransparencySettings.NormalizeOpacity(value),
                },
            });

        partial void OnInsetOpacityChanged(double value) =>
            SaveAndApply(settings => settings with
            {
                Transparency = settings.Transparency with
                {
                    Inset = UiTransparencySettings.NormalizeOpacity(value),
                },
            });

        partial void OnWindowMaterialChanged(string value)
        {
            if (!AppUiSettings.IsWindowMaterial(value))
                return;

            SaveAndApply(settings => settings with { WindowMaterial = value });
        }

        partial void OnTextScaleChanged(double value) =>
            SaveAndApply(settings => settings with
            {
                Accessibility = settings.Accessibility with
                {
                    TextScale = UiAccessibilitySettings.ClampTextScale(value),
                },
            });

        partial void OnReduceMotionChanged(bool value) =>
            SaveAndApply(settings => settings with
            {
                Accessibility = settings.Accessibility with
                {
                    ReduceMotion = value,
                },
            });

        partial void OnHighContrastChanged(bool value) =>
            SaveAndApply(settings => settings with
            {
                Accessibility = settings.Accessibility with
                {
                    HighContrast = value,
                },
            });

        partial void OnRailPositionChanged(string value)
        {
            if (!UiRailLayoutSettings.IsRailPosition(value))
                return;

            SaveAndApply(settings => settings with
            {
                RailLayout = settings.RailLayout with { Position = value },
            });
        }

        partial void OnShowScanInNavigationRailChanged(bool value) =>
            SaveAndApply(settings => settings with
            {
                ScanAction = settings.ScanAction with { ShowInNavigationRail = value },
            });

        partial void OnRailCollapsedChanged(bool value) =>
            SaveAndApply(settings => settings with
            {
                RailLayout = settings.RailLayout with { Collapsed = value },
            });

        partial void OnStartupTabKeyChanged(string value)
        {
            if (!UiRailLayoutSettings.IsTabKey(value))
                return;

            SaveAndApply(settings => settings with { StartupTabKey = value });
        }

        [RelayCommand]
        private void MoveRailTabUp(string? key) => MoveRailTab(key, -1);

        [RelayCommand]
        private void MoveRailTabDown(string? key) => MoveRailTab(key, 1);

        private void MoveRailTab(string? key, int offset)
        {
            if (key is null)
                return;

            int index = IndexOfRailTab(key);
            int target = index + offset;

            if (index < 0 || target < 0 || target >= RailTabs.Count)
                return;

            RailTabs.Move(index, target);
            UpdateRailTabMoveFlags();
            SaveRailTabSettings();
        }

        private void OnRailTabPropertyChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RailTabOption.IsVisible))
                return;

            if (sender is not RailTabOption option)
                return;

            if (!option.CanHide)
            {
                // Dashboard and Settings are pinned. The Settings checkbox is
                // disabled, so this only guards programmatic writes, and the
                // revert never loops because the value is already true when
                // the handler re-enters.
                option.IsVisible = true;
                return;
            }

            SaveRailTabSettings();
        }

        private int IndexOfRailTab(string key)
        {
            for (int index = 0; index < RailTabs.Count; index++)
            {
                if (string.Equals(RailTabs[index].Key, key, StringComparison.Ordinal))
                    return index;
            }

            return -1;
        }

        private void UpdateRailTabMoveFlags()
        {
            for (int index = 0; index < RailTabs.Count; index++)
            {
                RailTabs[index].CanMoveUp = index > 0;
                RailTabs[index].CanMoveDown = index < RailTabs.Count - 1;
            }
        }

        private void SaveRailTabSettings()
        {
            SaveAndApply(settings => settings with
            {
                RailLayout = settings.RailLayout with
                {
                    TabOrder = RailTabs.Select(option => option.Key).ToArray(),
                    HiddenTabs = UiRailLayoutSettings.NormalizeHiddenTabs(
                        RailTabs
                            .Where(option => !option.IsVisible)
                            .Select(option => option.Key)),
                },
            });
        }

        private static string GetRailTabHeader(string key) => key switch
        {
            UiRailLayoutSettings.TabDashboard => "Dashboard",
            UiRailLayoutSettings.TabInstalledGames => "Installed games",
            UiRailLayoutSettings.TabProfiles => "Profiles",
            UiRailLayoutSettings.TabTransferPreview => "Transfer preview",
            UiRailLayoutSettings.TabManualBackup => "Manual backup",
            UiRailLayoutSettings.TabBackups => "Backups",
            UiRailLayoutSettings.TabSync => "Sync",
            UiRailLayoutSettings.TabHistory => "History",
            UiRailLayoutSettings.TabSettings => "Settings",
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        // Workspace layouts: save a snapshot of the current detach state,
        // apply/delete per row, exchange layouts with a JSON file, and reset.
        // Every mutation disarms the two-step reset first so an armed
        // "Confirm reset" can never survive into an unrelated action.

        private bool CanSaveWorkspaceLayout() =>
            HasWorkspaceLayoutCapacity &&
            IsDistinctWorkspaceLayoutName(NewLayoutName);

        private bool HasWorkspaceLayoutCapacity =>
            WorkspaceLayouts.Count < UiWorkspaceLayoutSettings.MaxSavedLayouts;

        private bool IsDistinctWorkspaceLayoutName(string? candidate)
        {
            string name = (candidate ?? string.Empty).Trim();

            return name.Length > 0 &&
                name.Length <= UiWorkspaceLayoutSettings.MaxNameLength &&
                FindWorkspaceLayout(name) is null;
        }

        [RelayCommand(CanExecute = nameof(CanSaveWorkspaceLayout))]
        private void SaveWorkspaceLayout()
        {
            DisarmReset();

            string name = NewLayoutName.Trim();

            if (name.Length == 0)
            {
                WorkspaceStatus = "Enter a name for the layout.";
                return;
            }

            if (name.Length > UiWorkspaceLayoutSettings.MaxNameLength)
            {
                WorkspaceStatus = "Layout names are at most 40 characters.";
                return;
            }

            if (!HasWorkspaceLayoutCapacity)
            {
                WorkspaceStatus = $"Up to {UiWorkspaceLayoutSettings.MaxSavedLayouts} layouts can be saved.";
                return;
            }

            if (FindWorkspaceLayout(name) is not null)
            {
                WorkspaceStatus = "A layout with that name already exists.";
                return;
            }

            IReadOnlyList<UiDetachedWindowSettings> detached =
                WorkspaceHost?.CaptureDetachedTabs()
                    ?? Array.Empty<UiDetachedWindowSettings>();

            // A named layout captures the whole workspace: which sections sit
            // where on every page, and which tabs are floating. Saving only the
            // floating windows would make "apply" a half-restore.
            if (UiWorkspaceLayoutSettings.TryCreate(
                    name, detached, _workspaceLayout.Capture()) is not { } layout)
            {
                WorkspaceStatus = "The layout could not be saved.";
                return;
            }

            var layouts = new List<UiWorkspaceLayoutSettings>(WorkspaceLayouts)
            {
                layout,
            };

            ReplaceWorkspaceLayouts(UiWorkspaceLayoutSettings.NormalizeList(layouts));
            NewLayoutName = string.Empty;
            WorkspaceStatus = $"Saved layout '{layout.Name}'.";
        }

        [RelayCommand]
        private void ApplyWorkspaceLayout(string? name)
        {
            if (FindWorkspaceLayout(name) is not { } layout)
                return;

            DisarmReset();

            // Pages first, then windows: applying the page arrangements settles
            // what each section looks like, and the detach step then places the
            // floating windows around that.
            _workspaceLayout.Apply(layout.Pages);
            WorkspaceHost?.ApplyDetachedTabs(layout.Detached);
            WorkspaceStatus = $"Applied layout '{layout.Name}'.";
        }

        [RelayCommand]
        private void DeleteWorkspaceLayout(string? name)
        {
            if (FindWorkspaceLayout(name) is not { } layout)
                return;

            DisarmReset();
            var remaining = WorkspaceLayouts
                .Where(candidate => !ReferenceEquals(candidate, layout))
                .ToArray();
            ReplaceWorkspaceLayouts(remaining);
            WorkspaceStatus = $"Deleted layout '{layout.Name}'.";
        }

        private bool CanExportWorkspaceLayouts() =>
            WorkspaceLayouts.Count > 0;

        [RelayCommand(CanExecute = nameof(CanExportWorkspaceLayouts))]
        private async Task ExportWorkspaceLayoutsAsync()
        {
            DisarmReset();

            if (WorkspaceHost is null)
                return;

            string payload = WorkspaceLayoutTransfer.Serialize(WorkspaceLayouts);

            switch (await WorkspaceHost.ExportAsync(payload))
            {
                case WorkspaceFileOutcome.Completed:
                    WorkspaceStatus = "Layouts exported.";
                    break;
                case WorkspaceFileOutcome.Failed:
                    WorkspaceStatus = "The layout file could not be written.";
                    break;
            }
        }

        [RelayCommand]
        private async Task ImportWorkspaceLayoutsAsync()
        {
            DisarmReset();

            if (WorkspaceHost is null)
                return;

            WorkspaceImportResult import = await WorkspaceHost.ImportAsync();

            if (import.Outcome == WorkspaceFileOutcome.Cancelled)
                return;

            if (import.Outcome == WorkspaceFileOutcome.Failed || import.Text is null)
            {
                WorkspaceStatus = "The layout file could not be read.";
                return;
            }

            IReadOnlyList<UiWorkspaceLayoutSettings> incoming =
                WorkspaceLayoutTransfer.Deserialize(import.Text);

            if (incoming.Count == 0)
            {
                WorkspaceStatus = "No valid layouts were found in that file.";
                return;
            }

            var merged = new List<UiWorkspaceLayoutSettings>(WorkspaceLayouts);
            int imported = 0;
            int skipped = 0;

            foreach (UiWorkspaceLayoutSettings layout in incoming)
            {
                if (merged.Count >= UiWorkspaceLayoutSettings.MaxSavedLayouts)
                {
                    skipped++;
                    continue;
                }

                merged.Add(layout with
                {
                    Name = FindAvailableLayoutName(layout.Name, merged),
                });
                imported++;
            }

            ReplaceWorkspaceLayouts(merged);

            WorkspaceStatus = skipped > 0
                ? $"Imported {imported} layouts; the saved-layout limit skipped {skipped}."
                : $"Imported {imported} {(imported == 1 ? "layout" : "layouts")}.";
        }

        // Two-step reset: the first click arms ("Confirm reset"), the second
        // executes. Reattaches everything and clears the saved layouts; the
        // rail layout, columns, and appearance settings are untouched.
        [RelayCommand]
        private void ResetWorkspace()
        {
            if (!IsResetArmed)
            {
                IsResetArmed = true;
                WorkspaceStatus = "Click again to reset the workspace.";
                return;
            }

            DisarmReset();

            // The full reset: every section back where the catalog puts it,
            // every window back in the rail, and the saved layouts cleared. The
            // rail layout, table columns and appearance settings are
            // deliberately untouched — this resets the workspace, not the app.
            _workspaceLayout.ResetAll();
            WorkspaceHost?.ReattachAllDetachedTabs();
            ReplaceWorkspaceLayouts(Array.Empty<UiWorkspaceLayoutSettings>());
            WorkspaceStatus = "Workspace reset: every section and window is back to the default layout and the saved layouts are cleared.";
        }

        private UiWorkspaceLayoutSettings? FindWorkspaceLayout(string? name)
        {
            return WorkspaceLayouts.FirstOrDefault(layout =>
                string.Equals(layout.Name, name, StringComparison.Ordinal));
        }

        // An imported name that collides keeps its meaning by taking the
        // next free " (n)" suffix, trimmed to fit the length limit.
        private static string FindAvailableLayoutName(
            string desired,
            IReadOnlyList<UiWorkspaceLayoutSettings> existing)
        {
            if (!existing.Any(layout =>
                    string.Equals(layout.Name, desired, StringComparison.Ordinal)))
            {
                return desired;
            }

            for (int attempt = 2; attempt < 100; attempt++)
            {
                string suffix = $" ({attempt})";
                string candidate = desired.Length + suffix.Length >
                        UiWorkspaceLayoutSettings.MaxNameLength
                    ? desired[..(UiWorkspaceLayoutSettings.MaxNameLength - suffix.Length)] +
                        suffix
                    : desired + suffix;

                if (!existing.Any(layout =>
                        string.Equals(layout.Name, candidate, StringComparison.Ordinal)))
                {
                    return candidate;
                }
            }

            return desired;
        }

        private void ReplaceWorkspaceLayouts(
            IReadOnlyList<UiWorkspaceLayoutSettings> layouts)
        {
            WorkspaceLayouts.Clear();

            foreach (UiWorkspaceLayoutSettings layout in layouts)
                WorkspaceLayouts.Add(layout);

            SaveAndApply(settings => settings with { WorkspaceLayouts = layouts });
        }

        private void OnWorkspaceLayoutsChanged(
            object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasWorkspaceLayouts));
            SaveWorkspaceLayoutCommand.NotifyCanExecuteChanged();
            ExportWorkspaceLayoutsCommand.NotifyCanExecuteChanged();
        }

        private void DisarmReset()
        {
            if (IsResetArmed)
                IsResetArmed = false;
        }

        // Every change is persisted as a full settings record and then
        // reapplied, so the stored and live states never drift apart. The
        // variant is reapplied first because the accent and transparency
        // overrides are computed against the active variant; the window
        // material is applied last because its confirmation state feeds
        // back into the transparency overrides.
        private void SaveAndApply(Func<AppUiSettings, AppUiSettings> change)
        {
            AppUiSettings settings = change(_uiSettingsStore.Load());
            _uiSettingsStore.Save(settings);
            _themeService.ApplyThemeVariant(settings.ThemeChoice);
            _themeService.Apply(settings);
            _windowMaterialService.Apply(settings);
        }
    }
}
