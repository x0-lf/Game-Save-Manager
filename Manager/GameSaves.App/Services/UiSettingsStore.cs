using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Non-secret UI settings remembered between sessions. Follows
    /// the same forgiving-load pattern as <see cref="SyncSettingsStore"/>:
    /// a missing or malformed file yields defaults rather than an error.
    /// </summary>
    /// <summary>
    /// Per-component opacity levels (1.0 = fully opaque). Values are stored
    /// as doubles in [0,1]; anything non-finite or out of range normalizes
    /// to opaque on load.
    /// </summary>
    public sealed record UiTransparencySettings(
        double Window,
        double Card,
        double Inset)
    {
        public const double Opaque = 1.0;

        public static UiTransparencySettings Default { get; } = new(
            Window: Opaque,
            Card: Opaque,
            Inset: Opaque);

        public UiTransparencySettings Normalized() => new(
            Window: NormalizeOpacity(Window),
            Card: NormalizeOpacity(Card),
            Inset: NormalizeOpacity(Inset));

        public static double NormalizeOpacity(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : Opaque;
    }

    public sealed record AppUiSettings(
        int SchemaVersion,
        string ThemeChoice,
        string AccentTheme,
        UiTransparencySettings Transparency,
        string WindowMaterial,
        UiAccessibilitySettings Accessibility,
        IReadOnlyList<string> InstalledGameColumnOrder,
        IReadOnlyList<string> HiddenInstalledGameColumns,
        UiRailLayoutSettings RailLayout,
        string StartupTabKey,
        IReadOnlyList<UiWorkspaceLayoutSettings> WorkspaceLayouts)
    {
        public const int CurrentSchemaVersion = 9;

        /// <summary>
        /// The live per-page panel arrangement (schema v9). An init property
        /// rather than a positional parameter so every existing construction
        /// of this record keeps compiling and simply gets the default layout.
        /// Unlike <see cref="WorkspaceLayouts"/>, which are named snapshots the
        /// user applies deliberately, this is restored automatically at start.
        /// </summary>
        public IReadOnlyList<UiPageLayout> WorkspacePages { get; init; } =
            Array.Empty<UiPageLayout>();

        /// <summary>
        /// Where the Scan Steam library action is offered (schema v9). An init
        /// property for the same reason as <see cref="WorkspacePages"/>.
        /// </summary>
        public UiScanActionSettings ScanAction { get; init; } =
            UiScanActionSettings.Default;

        public const string ThemeSystem = "system";
        public const string ThemeLight = "light";
        public const string ThemeDark = "dark";

        public const string AccentIndigo = "indigo";
        public const string AccentTeal = "teal";
        public const string AccentRose = "rose";
        public const string AccentAmber = "amber";
        public const string AccentViolet = "violet";

        public static string DefaultAccentTheme => AccentIndigo;

        public static bool IsAccentTheme(string value) => value is
            AccentIndigo or
            AccentTeal or
            AccentRose or
            AccentAmber or
            AccentViolet;

        // Window materials replace the window surface with an OS-composited
        // backdrop (Avalonia transparency levels). "none" is the shipped
        // opaque window; older schema files load as "none".
        public const string MaterialNone = "none";
        public const string MaterialAcrylic = "acrylic";
        public const string MaterialMica = "mica";

        public static bool IsWindowMaterial(string value) => value is
            MaterialNone or
            MaterialAcrylic or
            MaterialMica;

        public const string GameColumn = "game";
        public const string AppIdColumn = "appId";
        public const string InstallPathColumn = "installPath";
        public const string LibraryColumn = "library";
        public const string ApprovedColumn = "approved";
        public const string PendingColumn = "pending";
        public const string NeedsFixColumn = "needsFix";
        public const string ExistsColumn = "exists";
        public const string FilesColumn = "files";
        public const string StatusColumn = "status";

        public static IReadOnlyList<string> DefaultInstalledGameColumnOrder { get; } =
            new[]
            {
                GameColumn,
                AppIdColumn,
                InstallPathColumn,
                LibraryColumn,
                ApprovedColumn,
                PendingColumn,
                NeedsFixColumn,
                ExistsColumn,
                FilesColumn,
                StatusColumn,
            };

        public static AppUiSettings Default { get; } = new(
            SchemaVersion: CurrentSchemaVersion,
            ThemeChoice: ThemeSystem,
            AccentTheme: DefaultAccentTheme,
            Transparency: UiTransparencySettings.Default,
            WindowMaterial: MaterialNone,
            Accessibility: UiAccessibilitySettings.Default,
            InstalledGameColumnOrder: DefaultInstalledGameColumnOrder,
            HiddenInstalledGameColumns: Array.Empty<string>(),
            RailLayout: UiRailLayoutSettings.Default,
            StartupTabKey: UiRailLayoutSettings.TabDashboard,
            WorkspaceLayouts: Array.Empty<UiWorkspaceLayoutSettings>());

        public static IReadOnlyList<string> NormalizeInstalledGameColumnOrder(
            IEnumerable<string> columnKeys)
        {
            var normalized = NormalizeInstalledGameColumns(columnKeys);

            foreach (string key in DefaultInstalledGameColumnOrder)
            {
                if (!normalized.Contains(key))
                    normalized.Add(key);
            }

            return normalized;
        }

        public static IReadOnlyList<string> NormalizeHiddenInstalledGameColumns(
            IEnumerable<string> columnKeys) =>
            NormalizeInstalledGameColumns(columnKeys);

        private static List<string> NormalizeInstalledGameColumns(
            IEnumerable<string> columnKeys)
        {
            var normalized = new List<string>();

            foreach (string key in columnKeys)
            {
                if (IsInstalledGameColumn(key) && !normalized.Contains(key))
                    normalized.Add(key);
            }

            return normalized;
        }

        private static bool IsInstalledGameColumn(string key) => key is
            GameColumn or
            AppIdColumn or
            InstallPathColumn or
            LibraryColumn or
            ApprovedColumn or
            PendingColumn or
            NeedsFixColumn or
            ExistsColumn or
            FilesColumn or
            StatusColumn;
    }

    /// <summary>
    /// Where the "Scan Steam library" action is offered. It is the app's one
    /// recurring primary verb, so it appears on the navigation rail and on
    /// several pages; a user who does not need it on a given page can turn it
    /// off there without losing it everywhere.
    ///
    /// <see cref="HiddenPages"/> holds stable rail tab keys. Hiding the action
    /// never disables scanning: the rail action and the Dashboard's guided
    /// first-run path always remain, so the app can never be left with no way
    /// to scan.
    /// </summary>
    public sealed record UiScanActionSettings(
        bool ShowInNavigationRail,
        IReadOnlyList<string> HiddenPages)
    {
        public static UiScanActionSettings Default { get; } = new(
            ShowInNavigationRail: true,
            HiddenPages: Array.Empty<string>());

        /// <summary>The pages that offer a scan action of their own.</summary>
        public static IReadOnlyList<string> ScannablePages { get; } = new[]
        {
            UiRailLayoutSettings.TabDashboard,
            UiRailLayoutSettings.TabInstalledGames,
            UiRailLayoutSettings.TabProfiles,
        };

        public static IReadOnlyList<string> NormalizeHiddenPages(
            IEnumerable<string> pageKeys)
        {
            var normalized = new List<string>();

            foreach (string key in pageKeys)
            {
                if (ScannablePages.Contains(key) && !normalized.Contains(key))
                    normalized.Add(key);
            }

            return normalized;
        }

        public UiScanActionSettings Normalized() => new(
            ShowInNavigationRail,
            NormalizeHiddenPages(HiddenPages));

        public bool IsVisibleOn(string pageKey) => !HiddenPages.Contains(pageKey);
    }

    /// <summary>
    /// Accessibility choices remembered between sessions. <see cref="TextScale"/>
    /// is a multiplier in [0.85, 1.5]; anything non-finite or out of range
    /// normalizes to 1.0 on load.
    /// </summary>
    public sealed record UiAccessibilitySettings(
        double TextScale,
        bool ReduceMotion,
        bool HighContrast)
    {
        public const double MinTextScale = 0.85;
        public const double MaxTextScale = 1.5;
        public const double DefaultTextScale = 1.0;

        public static UiAccessibilitySettings Default { get; } = new(
            TextScale: DefaultTextScale,
            ReduceMotion: false,
            HighContrast: false);

        public static double ClampTextScale(double value) =>
            double.IsFinite(value)
                ? Math.Clamp(value, MinTextScale, MaxTextScale)
                : DefaultTextScale;

        public UiAccessibilitySettings Normalized() => new(
            TextScale: ClampTextScale(TextScale),
            ReduceMotion: ReduceMotion,
            HighContrast: HighContrast);
    }

    /// <summary>
    /// Navigation rail customization remembered between sessions.
    /// <see cref="Position"/> is whitelisted ("left", "right", "top");
    /// <see cref="Collapsed"/> switches the rail to a glyph-only strip;
    /// <see cref="TabOrder"/> and <see cref="HiddenTabs"/> persist per-tab
    /// customization. Dashboard and Settings can never be hidden (the
    /// operational home and the customization surface itself), which also
    /// guarantees at least one visible tab by construction.
    /// </summary>
    public sealed record UiRailLayoutSettings(
        string Position,
        bool Collapsed,
        IReadOnlyList<string> TabOrder,
        IReadOnlyList<string> HiddenTabs)
    {
        public const string PositionLeft = "left";
        public const string PositionRight = "right";
        public const string PositionTop = "top";

        public static bool IsRailPosition(string value) => value is
            PositionLeft or
            PositionRight or
            PositionTop;

        // Stable keys for the nine rail tabs, in canonical creation order.
        // The Ctrl+1..9 shortcuts address these canonical slots regardless of
        // the persisted display order, and a hidden tab's slot is a no-op.
        public const string TabDashboard = "dashboard";
        public const string TabInstalledGames = "installedGames";
        public const string TabProfiles = "profiles";
        public const string TabTransferPreview = "transferPreview";
        public const string TabManualBackup = "manualBackup";
        public const string TabBackups = "backups";
        public const string TabSync = "sync";
        public const string TabHistory = "history";
        public const string TabSettings = "settings";

        public static IReadOnlyList<string> CanonicalTabOrder { get; } = new[]
        {
            TabDashboard,
            TabInstalledGames,
            TabProfiles,
            TabTransferPreview,
            TabManualBackup,
            TabBackups,
            TabSync,
            TabHistory,
            TabSettings,
        };

        public static UiRailLayoutSettings Default { get; } = new(
            Position: PositionLeft,
            Collapsed: false,
            TabOrder: CanonicalTabOrder,
            HiddenTabs: Array.Empty<string>());

        // Dashboard is the operational home and Settings owns this
        // customization surface itself, so both are pinned visible; the pin
        // also satisfies the "never zero visible tabs" rule by construction.
        public static bool CanHideTab(string key) => key is not
            (TabDashboard or TabSettings);

        public static IReadOnlyList<string> NormalizeTabOrder(
            IEnumerable<string> tabKeys)
        {
            var normalized = new List<string>();

            foreach (string key in tabKeys)
            {
                if (IsTabKey(key) && !normalized.Contains(key))
                    normalized.Add(key);
            }

            // Missing keys append in canonical order, so every normalized
            // order contains all nine tabs exactly once.
            foreach (string key in CanonicalTabOrder)
            {
                if (!normalized.Contains(key))
                    normalized.Add(key);
            }

            return normalized;
        }

        public static IReadOnlyList<string> NormalizeHiddenTabs(
            IEnumerable<string> tabKeys)
        {
            var normalized = new List<string>();

            foreach (string key in tabKeys)
            {
                if (IsTabKey(key) && CanHideTab(key) && !normalized.Contains(key))
                    normalized.Add(key);
            }

            return normalized;
        }

        public static bool IsTabKey(string? key) => key is
            TabDashboard or
            TabInstalledGames or
            TabProfiles or
            TabTransferPreview or
            TabManualBackup or
            TabBackups or
            TabSync or
            TabHistory or
            TabSettings;
    }

    /// <summary>
    /// One detached window's placement inside a saved workspace layout.
    /// <see cref="TabKey"/> is one of the nine stable rail tab keys. Width and
    /// height clamp to [300, 4096]; positions clamp to [-8192, 16384]; an
    /// unknown key or any non-finite coordinate rejects the whole entry.
    /// </summary>
    public sealed record UiDetachedWindowSettings(
        string TabKey,
        double Left,
        double Top,
        double Width,
        double Height)
    {
        public const double MinWindowExtent = 300;
        public const double MaxWindowExtent = 4096;
        public const double MinPosition = -8192;
        public const double MaxPosition = 16384;
        public const int MaxPerLayout = 8;

        // Null when the entry cannot be salvaged; otherwise a copy clamped
        // into the sane ranges. This is the single normalization path for
        // saved, imported, and captured entries alike.
        public static UiDetachedWindowSettings? TryCreate(
            string? tabKey,
            double left,
            double top,
            double width,
            double height)
        {
            if (tabKey is null || !UiRailLayoutSettings.IsTabKey(tabKey))
                return null;

            if (!double.IsFinite(left) || !double.IsFinite(top) ||
                !double.IsFinite(width) || !double.IsFinite(height))
            {
                return null;
            }

            return new UiDetachedWindowSettings(
                tabKey,
                Math.Clamp(left, MinPosition, MaxPosition),
                Math.Clamp(top, MinPosition, MaxPosition),
                Math.Clamp(width, MinWindowExtent, MaxWindowExtent),
                Math.Clamp(height, MinWindowExtent, MaxWindowExtent));
        }
    }

    /// <summary>
    /// A named snapshot of where the workspace's detached windows sit. Names
    /// are trimmed and at most <see cref="MaxNameLength"/> characters; entries
    /// are unique by tab key (first wins) and capped at
    /// <see cref="UiDetachedWindowSettings.MaxPerLayout"/>. Saved layouts are
    /// never applied automatically at startup; applying is an explicit action.
    /// </summary>
    public sealed record UiWorkspaceLayoutSettings(
        string Name,
        IReadOnlyList<UiDetachedWindowSettings> Detached)
    {
        public const int MaxNameLength = 40;
        public const int MaxSavedLayouts = 8;

        /// <summary>
        /// The per-page panel arrangement this layout restores, alongside its
        /// detached windows. An init property rather than a positional
        /// parameter so a layout saved before panels existed still loads, with
        /// no pages and therefore the catalog defaults.
        /// </summary>
        public IReadOnlyList<UiPageLayout> Pages { get; init; } =
            Array.Empty<UiPageLayout>();

        // Null when the name is empty after trimming; otherwise the
        // normalized layout (trimmed, truncated name; deduplicated,
        // capped entries).
        public static UiWorkspaceLayoutSettings? TryCreate(
            string? name,
            IEnumerable<UiDetachedWindowSettings> detached,
            IEnumerable<UiPageLayout>? pages = null)
        {
            string trimmed = (name ?? string.Empty).Trim();

            if (trimmed.Length == 0)
                return null;

            if (trimmed.Length > MaxNameLength)
                trimmed = trimmed[..MaxNameLength];

            var entries = new List<UiDetachedWindowSettings>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiDetachedWindowSettings entry in detached)
            {
                if (!seenKeys.Add(entry.TabKey))
                    continue;

                if (entries.Count >= UiDetachedWindowSettings.MaxPerLayout)
                    break;

                entries.Add(entry);
            }

            return new UiWorkspaceLayoutSettings(trimmed, entries)
            {
                Pages = UiPageLayout.NormalizeList(pages ?? Array.Empty<UiPageLayout>()),
            };
        }

        public UiWorkspaceLayoutSettings? Normalized() => TryCreate(Name, Detached, Pages);

        // Normalizes a whole list: per-layout normalization, unique names
        // (first wins), and the saved-layout cap. Garbage is dropped rather
        // than defaulted.
        public static IReadOnlyList<UiWorkspaceLayoutSettings> NormalizeList(
            IEnumerable<UiWorkspaceLayoutSettings> layouts)
        {
            var normalized = new List<UiWorkspaceLayoutSettings>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiWorkspaceLayoutSettings layout in layouts)
            {
                if (layout.Normalized() is not { } candidate)
                    continue;

                if (!seenNames.Add(candidate.Name))
                    continue;

                if (normalized.Count >= MaxSavedLayouts)
                    break;

                normalized.Add(candidate);
            }

            return normalized;
        }
    }

    public interface IUiSettingsStore
    {
        // The exact file this store reads and writes, so surfaces such as
        // Settings > Data locations never re-derive the path.
        string FilePath { get; }

        AppUiSettings Load();

        void Save(AppUiSettings settings);
    }

    public sealed class UiSettingsStore : IUiSettingsStore
    {
        private readonly string _filePath;

        public UiSettingsStore()
            : this(GetDefaultFilePath())
        {
        }

        public UiSettingsStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A UI settings path is required.", nameof(filePath));

            _filePath = filePath;
        }

        public string FilePath => _filePath;

        public AppUiSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return AppUiSettings.Default;

                using JsonDocument document =
                    JsonDocument.Parse(File.ReadAllText(_filePath));

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return AppUiSettings.Default;

                string theme = AppUiSettings.Default.ThemeChoice;

                if (document.RootElement.TryGetProperty(
                        nameof(AppUiSettings.ThemeChoice), out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    string candidate = value.GetString() ?? theme;

                    if (candidate is AppUiSettings.ThemeSystem
                        or AppUiSettings.ThemeLight
                        or AppUiSettings.ThemeDark)
                    {
                        theme = candidate;
                    }
                }

                string accent = AppUiSettings.Default.AccentTheme;

                if (document.RootElement.TryGetProperty(
                        nameof(AppUiSettings.AccentTheme), out JsonElement accentValue) &&
                    accentValue.ValueKind == JsonValueKind.String)
                {
                    string candidate = accentValue.GetString() ?? accent;

                    if (AppUiSettings.IsAccentTheme(candidate))
                        accent = candidate;
                }

                UiTransparencySettings transparency =
                    ReadTransparencySettings(document.RootElement);

                string windowMaterial = ReadWindowMaterial(document.RootElement);

                UiAccessibilitySettings accessibility =
                    ReadAccessibilitySettings(document.RootElement);

                IReadOnlyList<string> order = ReadInstalledGameColumns(
                    document.RootElement,
                    nameof(AppUiSettings.InstalledGameColumnOrder),
                    appendMissing: true);
                IReadOnlyList<string> hidden = ReadInstalledGameColumns(
                    document.RootElement,
                    nameof(AppUiSettings.HiddenInstalledGameColumns),
                    appendMissing: false);

                UiRailLayoutSettings railLayout =
                    ReadRailLayout(document.RootElement);

                // The startup tab key is whitelisted against the nine stable
                // rail tab keys; unknown or missing values (including every
                // pre-v8 file) load as the Dashboard default.
                string startupTabKey = UiRailLayoutSettings.TabDashboard;

                if (document.RootElement.TryGetProperty(
                        nameof(AppUiSettings.StartupTabKey), out JsonElement startupValue) &&
                    startupValue.ValueKind == JsonValueKind.String)
                {
                    string candidate = startupValue.GetString() ?? startupTabKey;

                    if (UiRailLayoutSettings.IsTabKey(candidate))
                        startupTabKey = candidate;
                }

                IReadOnlyList<UiWorkspaceLayoutSettings> workspaceLayouts =
                    ReadWorkspaceLayouts(document.RootElement);

                return new AppUiSettings(
                    SchemaVersion: AppUiSettings.CurrentSchemaVersion,
                    ThemeChoice: theme,
                    AccentTheme: accent,
                    Transparency: transparency,
                    WindowMaterial: windowMaterial,
                    Accessibility: accessibility,
                    InstalledGameColumnOrder: order,
                    HiddenInstalledGameColumns: hidden,
                    RailLayout: railLayout,
                    StartupTabKey: startupTabKey,
                    WorkspaceLayouts: workspaceLayouts)
                {
                    WorkspacePages = ReadWorkspacePages(document.RootElement),
                    ScanAction = ReadScanAction(document.RootElement),
                };
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return AppUiSettings.Default;
            }
        }

        public void Save(AppUiSettings settings)
        {
            string? directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        // The window material vocabulary is whitelisted like the theme and
        // accent choices; unknown or malformed values load as "none", which
        // is also what pre-v5 files without the property get.
        private static string ReadWindowMaterial(JsonElement root)
        {
            if (root.TryGetProperty(
                    nameof(AppUiSettings.WindowMaterial), out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                string candidate = value.GetString() ?? AppUiSettings.MaterialNone;

                if (AppUiSettings.IsWindowMaterial(candidate))
                    return candidate;
            }

            return AppUiSettings.MaterialNone;
        }

        private static UiAccessibilitySettings ReadAccessibilitySettings(JsonElement root)
        {
            if (!root.TryGetProperty(
                    nameof(AppUiSettings.Accessibility), out JsonElement property) ||
                property.ValueKind != JsonValueKind.Object)
            {
                return UiAccessibilitySettings.Default;
            }

            return new UiAccessibilitySettings(
                TextScale: ReadBoundedDouble(
                    property,
                    nameof(UiAccessibilitySettings.TextScale),
                    UiAccessibilitySettings.ClampTextScale),
                ReduceMotion: ReadBoolean(
                    property,
                    nameof(UiAccessibilitySettings.ReduceMotion)),
                HighContrast: ReadBoolean(
                    property,
                    nameof(UiAccessibilitySettings.HighContrast)));
        }

        // The rail layout vocabulary is whitelisted and normalized like the
        // table columns: unknown positions load as "left", and the order and
        // hidden lists are normalized so all nine tabs appear exactly once
        // with the pinned tabs never hidden. Pre-v6 files without the
        // property get the shipped default (left, expanded, canonical order).
        private static UiRailLayoutSettings ReadRailLayout(JsonElement root)
        {
            if (!root.TryGetProperty(
                    nameof(AppUiSettings.RailLayout), out JsonElement property) ||
                property.ValueKind != JsonValueKind.Object)
            {
                return UiRailLayoutSettings.Default;
            }

            string position = UiRailLayoutSettings.PositionLeft;

            if (property.TryGetProperty(
                    nameof(UiRailLayoutSettings.Position), out JsonElement positionValue) &&
                positionValue.ValueKind == JsonValueKind.String)
            {
                string candidate = positionValue.GetString() ?? position;

                if (UiRailLayoutSettings.IsRailPosition(candidate))
                    position = candidate;
            }

            return new UiRailLayoutSettings(
                Position: position,
                Collapsed: ReadBoolean(
                    property,
                    nameof(UiRailLayoutSettings.Collapsed)),
                TabOrder: UiRailLayoutSettings.NormalizeTabOrder(
                    ReadStringArray(property, nameof(UiRailLayoutSettings.TabOrder))),
                HiddenTabs: UiRailLayoutSettings.NormalizeHiddenTabs(
                    ReadStringArray(property, nameof(UiRailLayoutSettings.HiddenTabs))));
        }

        // The saved workspace layouts list (schema v7). Pre-v6 files without
        // the property load as an empty list, and nothing is ever applied
        // automatically at startup.
        private static IReadOnlyList<UiWorkspaceLayoutSettings> ReadWorkspaceLayouts(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                    nameof(AppUiSettings.WorkspaceLayouts), out JsonElement property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<UiWorkspaceLayoutSettings>();
            }

            return ParseWorkspaceLayouts(property);
        }

        // Shared with the export/import payload path: parses one JSON array
        // of layouts, forgiving everything that is malformed, unknown, or
        // beyond the limits rather than defaulting it.
        internal static IReadOnlyList<UiWorkspaceLayoutSettings> ParseWorkspaceLayouts(
            JsonElement array)
        {
            var layouts = new List<UiWorkspaceLayoutSettings>();

            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string? name = null;

                if (item.TryGetProperty(
                        nameof(UiWorkspaceLayoutSettings.Name), out JsonElement nameValue) &&
                    nameValue.ValueKind == JsonValueKind.String)
                {
                    name = nameValue.GetString();
                }

                var detached = new List<UiDetachedWindowSettings>();

                if (item.TryGetProperty(
                        nameof(UiWorkspaceLayoutSettings.Detached),
                        out JsonElement detachedValue) &&
                    detachedValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in detachedValue.EnumerateArray())
                    {
                        if (ReadDetachedEntry(entry) is { } parsed)
                            detached.Add(parsed);
                    }
                }

                IReadOnlyList<UiPageLayout> pages =
                    item.TryGetProperty(
                        nameof(UiWorkspaceLayoutSettings.Pages),
                        out JsonElement pagesValue) &&
                    pagesValue.ValueKind == JsonValueKind.Array
                        ? ParseWorkspacePages(pagesValue)
                        : Array.Empty<UiPageLayout>();

                if (UiWorkspaceLayoutSettings.TryCreate(name, detached, pages) is { } layout)
                    layouts.Add(layout);
            }

            return UiWorkspaceLayoutSettings.NormalizeList(layouts);
        }

        // Where the scan action is offered (schema v9). A pre-v9 file has no
        // such property and loads as "everywhere", which is what those users
        // already had.
        private static UiScanActionSettings ReadScanAction(JsonElement root)
        {
            if (!root.TryGetProperty(
                    nameof(AppUiSettings.ScanAction), out JsonElement property) ||
                property.ValueKind != JsonValueKind.Object)
            {
                return UiScanActionSettings.Default;
            }

            bool showInRail = UiScanActionSettings.Default.ShowInNavigationRail;

            if (property.TryGetProperty(
                    nameof(UiScanActionSettings.ShowInNavigationRail),
                    out JsonElement railValue))
            {
                if (railValue.ValueKind == JsonValueKind.False)
                    showInRail = false;
                else if (railValue.ValueKind == JsonValueKind.True)
                    showInRail = true;
            }

            return new UiScanActionSettings(
                showInRail,
                UiScanActionSettings.NormalizeHiddenPages(
                    ReadStringArray(property, nameof(UiScanActionSettings.HiddenPages))));
        }

        // The current per-page panel arrangement (schema v9). Every earlier
        // file loads as an empty list, which resolves to the catalog default —
        // so an upgrade always opens on the shipped layout, never on nothing.
        private static IReadOnlyList<UiPageLayout> ReadWorkspacePages(JsonElement root)
        {
            if (!root.TryGetProperty(
                    nameof(AppUiSettings.WorkspacePages), out JsonElement property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<UiPageLayout>();
            }

            return ParseWorkspacePages(property);
        }

        // Shared with the export/import payload path. Forgiving in exactly one
        // direction: a malformed page, panel, or region is dropped, never
        // defaulted into something the user did not ask for. What survives is
        // re-resolved against the catalog before it reaches the screen.
        internal static IReadOnlyList<UiPageLayout> ParseWorkspacePages(JsonElement array)
        {
            var pages = new List<UiPageLayout>();

            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string? pageKey = null;

                if (item.TryGetProperty(
                        nameof(UiPageLayout.PageKey), out JsonElement keyValue) &&
                    keyValue.ValueKind == JsonValueKind.String)
                {
                    pageKey = keyValue.GetString();
                }

                var panels = new List<UiPanelPlacement>();

                if (item.TryGetProperty(
                        nameof(UiPageLayout.Panels), out JsonElement panelsValue) &&
                    panelsValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in panelsValue.EnumerateArray())
                    {
                        if (ReadPanelPlacement(entry) is { } placement)
                            panels.Add(placement);
                    }
                }

                var regions = new List<UiRegionSize>();

                if (item.TryGetProperty(
                        nameof(UiPageLayout.Regions), out JsonElement regionsValue) &&
                    regionsValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in regionsValue.EnumerateArray())
                    {
                        if (ReadRegionSize(entry) is { } region)
                            regions.Add(region);
                    }
                }

                if (UiPageLayout.TryCreate(pageKey, panels, regions) is { } page)
                    pages.Add(page);
            }

            return UiPageLayout.NormalizeList(pages);
        }

        private static UiPanelPlacement? ReadPanelPlacement(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                return null;

            return UiPanelPlacement.TryCreate(
                ReadString(entry, nameof(UiPanelPlacement.Key)),
                ReadString(entry, nameof(UiPanelPlacement.Region)),
                ReadInt32(entry, nameof(UiPanelPlacement.Order)),
                ReadFiniteDouble(entry, nameof(UiPanelPlacement.Size)),
                ReadBoolean(entry, nameof(UiPanelPlacement.Collapsed)),
                ReadBoolean(entry, nameof(UiPanelPlacement.Hidden)),
                ReadFiniteDouble(entry, nameof(UiPanelPlacement.Left)),
                ReadFiniteDouble(entry, nameof(UiPanelPlacement.Top)),
                ReadFiniteDouble(entry, nameof(UiPanelPlacement.Width)),
                ReadFiniteDouble(entry, nameof(UiPanelPlacement.Height))) is { } placement
                ? placement with
                {
                    DockedRegion = ReadDockedRegion(entry),
                }
                : null;
        }

        // The region a floating panel came from. Anything that is not a docked
        // region reads as "no memory", which falls back to the catalog home.
        private static string? ReadDockedRegion(JsonElement entry)
        {
            string? value = ReadString(entry, nameof(UiPanelPlacement.DockedRegion));

            return value is not null &&
                UiPanelRegion.IsRegion(value) &&
                value != UiPanelRegion.Float
                    ? value
                    : null;
        }

        private static UiRegionSize? ReadRegionSize(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                return null;

            return UiRegionSize.TryCreate(
                ReadString(entry, nameof(UiRegionSize.Region)),
                ReadFiniteDouble(entry, nameof(UiRegionSize.Size)));
        }

        private static string? ReadString(JsonElement container, string propertyName) =>
            container.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // A non-integer or out-of-range order is not salvageable as a number;
        // the placement's own clamp turns it into a valid slot.
        private static int ReadInt32(JsonElement container, string propertyName) =>
            container.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number)
                ? number
                : 0;

        // An entry needs a known tab key and all four finite coordinates; a
        // missing or malformed coordinate drops the entry instead of
        // defaulting it (NaN fails the finite check in TryCreate).
        private static UiDetachedWindowSettings? ReadDetachedEntry(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                return null;

            if (!entry.TryGetProperty(
                    nameof(UiDetachedWindowSettings.TabKey), out JsonElement keyValue) ||
                keyValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return UiDetachedWindowSettings.TryCreate(
                keyValue.GetString(),
                ReadFiniteDouble(entry, nameof(UiDetachedWindowSettings.Left)),
                ReadFiniteDouble(entry, nameof(UiDetachedWindowSettings.Top)),
                ReadFiniteDouble(entry, nameof(UiDetachedWindowSettings.Width)),
                ReadFiniteDouble(entry, nameof(UiDetachedWindowSettings.Height)));
        }

        private static double ReadFiniteDouble(JsonElement container, string propertyName)
        {
            if (container.TryGetProperty(propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double number))
            {
                return number;
            }

            return double.NaN;
        }

        private static double ReadBoundedDouble(
            JsonElement container,
            string propertyName,
            Func<double, double> normalize)
        {
            if (container.TryGetProperty(propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double number))
            {
                return normalize(number);
            }

            return normalize(double.NaN);
        }

        private static bool ReadBoolean(JsonElement container, string propertyName)
        {
            if (container.TryGetProperty(propertyName, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.True)
                    return true;

                if (value.ValueKind == JsonValueKind.False)
                    return false;
            }

            return false;
        }

        private static UiTransparencySettings ReadTransparencySettings(JsonElement root)
        {
            if (!root.TryGetProperty(
                    nameof(AppUiSettings.Transparency), out JsonElement property) ||
                property.ValueKind != JsonValueKind.Object)
            {
                return UiTransparencySettings.Default;
            }

            return new UiTransparencySettings(
                Window: ReadOpacity(property, nameof(UiTransparencySettings.Window)),
                Card: ReadOpacity(property, nameof(UiTransparencySettings.Card)),
                Inset: ReadOpacity(property, nameof(UiTransparencySettings.Inset)))
                .Normalized();
        }

        private static double ReadOpacity(JsonElement transparency, string propertyName)
        {
            if (transparency.TryGetProperty(propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double opacity))
            {
                return UiTransparencySettings.NormalizeOpacity(opacity);
            }

            return UiTransparencySettings.Opaque;
        }

        private static IReadOnlyList<string> ReadInstalledGameColumns(
            JsonElement root,
            string propertyName,
            bool appendMissing)
        {
            List<string> values = ReadStringArray(root, propertyName);

            return appendMissing
                ? AppUiSettings.NormalizeInstalledGameColumnOrder(values)
                : AppUiSettings.NormalizeHiddenInstalledGameColumns(values);
        }

        private static List<string> ReadStringArray(
            JsonElement container,
            string propertyName)
        {
            var values = new List<string>();

            if (container.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is string value)
                        values.Add(value);
                }
            }

            return values;
        }

        private static string GetDefaultFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameSave",
                "ui-settings.json");
        }
    }
}
