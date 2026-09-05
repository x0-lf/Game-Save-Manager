using System;
using System.Collections.Generic;
using System.Globalization;
using GameSaves.App.Services;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// The window materials a scenario can request. Only "none" can be
    /// rendered truthfully by the headless engine; the other two are
    /// composited by Windows and therefore belong to the interactive harness.
    /// </summary>
    public static class GalleryMaterials
    {
        public const string None = AppUiSettings.MaterialNone;
        public const string Acrylic = AppUiSettings.MaterialAcrylic;
        public const string Mica = AppUiSettings.MaterialMica;
    }

    /// <summary>The engine that produced a capture.</summary>
    public static class GalleryEngines
    {
        /// <summary>
        /// Avalonia's own render of the window. Deterministic, but it never
        /// contains the Windows compositor's Acrylic/Mica backdrop.
        /// </summary>
        public const string Headless = "avalonia-headless";

        /// <summary>
        /// A GDI read-back of the composited desktop. The only engine whose
        /// pixels can prove a real window material or a multi-window layout.
        /// </summary>
        public const string WindowsScreenReadback = "windows-screen-readback";
    }

    /// <summary>Curated-gallery sections, in presentation order.</summary>
    public static class GalleryCategories
    {
        public const string Core = "core-workflows";
        public const string Appearance = "appearance";
        public const string Workspace = "workspace";
        public const string Accessibility = "accessibility";
        public const string Navigation = "navigation";
        public const string Sync = "sync";
    }

    /// <summary>
    /// The workspace arrangements a scenario can ask for. Every one of them is
    /// produced by driving the real <see cref="IWorkspaceLayoutPage"/> API, so
    /// a capture is evidence that the product can reach that arrangement.
    /// </summary>
    public static class GalleryWorkspaces
    {
        public const string Default = "default";
        public const string LeftRightSplit = "left-right-split";
        public const string TopBottom = "top-bottom";
        public const string FourRegions = "four-regions";
        public const string Resized = "resized-regions";
        public const string Collapsed = "collapsed-section";
        public const string Hidden = "hidden-section";
        public const string SavedCustom = "saved-custom";
        public const string Restored = "restored-default";

        /// <summary>A panel floated into its own window (interactive only).</summary>
        public const string FloatingPanel = "floating-panel";

        /// <summary>A whole page detached into its own window (interactive only).</summary>
        public const string DetachedTab = "detached-tab";

        /// <summary>Main window plus more than one separate surface.</summary>
        public const string MultipleWindows = "multiple-windows";
    }

    /// <summary>Which deterministic fixture the scenario runs against.</summary>
    public static class GalleryData
    {
        /// <summary>The privacy-first empty state the regression harness owns.</summary>
        public const string Empty = "empty";

        /// <summary>The populated, deterministic showcase fixture.</summary>
        public const string Showcase = "showcase";
    }

    /// <summary>Which sync configuration the Sync page should present.</summary>
    public static class GalleryProviders
    {
        public const string None = "none";
        public const string LocalFolder = "local-folder";
        public const string Sftp = "sftp";
        public const string GoogleDrive = "google-drive";
        public const string Preview = "preview";
        public const string Results = "results";
    }

    /// <summary>
    /// One capture request: everything a harness needs to reach a state, plus
    /// everything the website needs to describe the resulting image. Scenarios
    /// are pure data so they can be enumerated, asserted on, and diffed
    /// without a display.
    /// </summary>
    public sealed record GalleryScenario
    {
        /// <summary>
        /// The subject slug the file name starts with, e.g. "dashboard",
        /// "settings-appearance", "workspace-backups".
        /// </summary>
        public required string Subject { get; init; }

        /// <summary>One of the nine canonical rail tab keys.</summary>
        public required string Page { get; init; }

        /// <summary>A Settings category index, or null for every other page.</summary>
        public int? SettingsCategory { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        /// <summary>"light" or "dark"; High Contrast is carried separately.</summary>
        public required string Theme { get; init; }

        public required string Accent { get; init; }

        public string RequestedMaterial { get; init; } = GalleryMaterials.None;

        public bool HighContrast { get; init; }

        public double TextScale { get; init; } = UiAccessibilitySettings.DefaultTextScale;

        public bool ReduceMotion { get; init; }

        public string RailPosition { get; init; } = UiRailLayoutSettings.PositionLeft;

        public bool RailCollapsed { get; init; }

        public string Workspace { get; init; } = GalleryWorkspaces.Default;

        public string DataScenario { get; init; } = GalleryData.Showcase;

        public string ProviderScenario { get; init; } = GalleryProviders.None;

        /// <summary>A short suffix that distinguishes otherwise equal file names.</summary>
        public string Variant { get; init; } = "default";

        /// <summary>
        /// Scrolls the page so this workspace panel sits at the top before the
        /// capture. Named rather than measured in pixels, so the scenario keeps
        /// pointing at the same section when the page above it changes height.
        /// </summary>
        public string? ScrollToPanel { get; init; }

        /// <summary>The engine that must produce this capture.</summary>
        public string Engine { get; init; } = GalleryEngines.Headless;

        /// <summary>True when the image is proposed for the public website.</summary>
        public bool GalleryCandidate { get; init; }

        public string Category { get; init; } = GalleryCategories.Core;

        /// <summary>Presentation order inside the curated gallery.</summary>
        public int GalleryOrder { get; init; }

        /// <summary>Factual one-line caption; no marketing language.</summary>
        public string Caption { get; init; } = string.Empty;

        /// <summary>Alt text for the website's img element.</summary>
        public string Alt { get; init; } = string.Empty;

        /// <summary>
        /// The material a truthful capture can actually demonstrate. The
        /// headless engine composites nothing, and High Contrast disables
        /// transparency by design, so both collapse to "none".
        /// </summary>
        public string ExpectedEffectiveMaterial =>
            HighContrast || Engine == GalleryEngines.Headless
                ? GalleryMaterials.None
                : RequestedMaterial;

        public string ThemeSlug => HighContrast ? "high-contrast" : Theme;

        /// <summary>
        /// Deterministic, descriptive file name. Never opaque: every dimension
        /// that changed the pixels is in the name.
        /// </summary>
        public string FileName =>
            string.Create(CultureInfo.InvariantCulture,
                $"{Subject}_{Width}x{Height}_{ThemeSlug}_{Accent}_{RequestedMaterial}_{Variant}.png");

        public static IReadOnlyList<string> Pages => UiRailLayoutSettings.CanonicalTabOrder;

        /// <summary>The website's two output sizes.</summary>
        public static IReadOnlyList<(int Width, int Height)> GallerySizes { get; } = new[]
        {
            (1280, 720),
            (1336, 768),
        };

        /// <summary>
        /// The accents the website gallery distributes across. Amber stays in
        /// the Settings selector and the existing regression sweeps; it does
        /// not get a marketing sweep of its own.
        /// </summary>
        public static IReadOnlyList<string> GalleryAccents { get; } = new[]
        {
            AppUiSettings.AccentIndigo,
            AppUiSettings.AccentTeal,
            AppUiSettings.AccentRose,
            AppUiSettings.AccentViolet,
        };

        public static IReadOnlyList<string> Materials { get; } = new[]
        {
            GalleryMaterials.None,
            GalleryMaterials.Acrylic,
            GalleryMaterials.Mica,
        };

        /// <summary>The human-readable page name used in captions.</summary>
        public static string PageTitle(string pageKey) => pageKey switch
        {
            UiRailLayoutSettings.TabDashboard => "Dashboard",
            UiRailLayoutSettings.TabInstalledGames => "Installed games",
            UiRailLayoutSettings.TabProfiles => "Profiles",
            UiRailLayoutSettings.TabTransferPreview => "Transfer profiles",
            UiRailLayoutSettings.TabManualBackup => "Manual backup",
            UiRailLayoutSettings.TabBackups => "Backups",
            UiRailLayoutSettings.TabSync => "Sync",
            UiRailLayoutSettings.TabHistory => "History",
            UiRailLayoutSettings.TabSettings => "Settings",
            _ => pageKey,
        };

        /// <summary>The file-name slug for a page key.</summary>
        public static string PageSlug(string pageKey) => pageKey switch
        {
            UiRailLayoutSettings.TabInstalledGames => "installed-games",
            UiRailLayoutSettings.TabTransferPreview => "transfer-profiles",
            UiRailLayoutSettings.TabManualBackup => "manual-backup",
            _ => pageKey,
        };

        /// <summary>The Ctrl+1..9 shortcut a page answers to.</summary>
        public static string PageShortcut(string pageKey)
        {
            int index = 0;

            for (int candidate = 0; candidate < Pages.Count; candidate++)
            {
                if (string.Equals(Pages[candidate], pageKey, StringComparison.Ordinal))
                {
                    index = candidate + 1;
                    break;
                }
            }

            return index == 0 ? string.Empty : "Ctrl+" + index.ToString(CultureInfo.InvariantCulture);
        }

        public static string AccentTitle(string accent) =>
            accent.Length == 0
                ? accent
                : char.ToUpperInvariant(accent[0]) + accent[1..];

        public static string MaterialTitle(string material) => material switch
        {
            GalleryMaterials.Acrylic => "Acrylic",
            GalleryMaterials.Mica => "Mica",
            _ => "no window material",
        };

        /// <summary>The seven Settings categories, in the order the strip shows them.</summary>
        public static IReadOnlyList<string> SettingsCategories { get; } = new[]
        {
            "Appearance",
            "Accessibility",
            "Behaviour",
            "Layout",
            "Providers",
            "Data",
            "Diagnostics",
        };
    }
}
