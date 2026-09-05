using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameSaves.App.Services;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// Every capture this project can produce, as data. Two audiences are kept
    /// apart on purpose (see docs/development.md, "Gallery capture"):
    ///
    ///   * <see cref="Full"/> is the exhaustive QA archive. It exists to make
    ///     regressions visible, not to look good, and it is never published.
    ///   * <see cref="Curated"/> is the website set: a few dozen images chosen
    ///     to show what the application does, each with a caption and alt text.
    ///
    /// A scenario says nothing about how it is captured; the harnesses decide
    /// that from <see cref="GalleryScenario.Engine"/>. Acrylic and Mica are
    /// only ever assigned to the Windows engine, because a headless render
    /// cannot contain a compositor backdrop and must not pretend otherwise.
    /// </summary>
    public static class GalleryPlan
    {
        private const string Indigo = AppUiSettings.AccentIndigo;
        private const string Teal = AppUiSettings.AccentTeal;
        private const string Rose = AppUiSettings.AccentRose;
        private const string Violet = AppUiSettings.AccentViolet;

        private const string Dark = AppUiSettings.ThemeDark;
        private const string Light = AppUiSettings.ThemeLight;

        /// <summary>
        /// The exhaustive archive: nine pages across both themes, the four
        /// gallery accents, the three material requests, and both website
        /// resolutions, plus a High Contrast pass that skips the redundant
        /// material axis because High Contrast forces opaque surfaces anyway.
        /// </summary>
        public static IReadOnlyList<GalleryScenario> Full()
        {
            var scenarios = new List<GalleryScenario>();

            foreach (string page in GalleryScenario.Pages)
            foreach (string theme in new[] { Dark, Light })
            foreach (string accent in GalleryScenario.GalleryAccents)
            foreach (string material in GalleryScenario.Materials)
            foreach ((int width, int height) in GalleryScenario.GallerySizes)
            {
                scenarios.Add(new GalleryScenario
                {
                    Subject = GalleryScenario.PageSlug(page),
                    Page = page,
                    Width = width,
                    Height = height,
                    Theme = theme,
                    Accent = accent,
                    RequestedMaterial = material,
                    Variant = "matrix",
                    // A material is composited by Windows, so only the
                    // interactive engine can render one truthfully. Routing is
                    // the whole reason the matrix is data rather than a loop
                    // inside one harness.
                    Engine = material == GalleryMaterials.None
                        ? GalleryEngines.Headless
                        : GalleryEngines.WindowsScreenReadback,
                    ProviderScenario = page == UiRailLayoutSettings.TabSync
                        ? GalleryProviders.LocalFolder
                        : GalleryProviders.None,
                    Caption = $"{GalleryScenario.PageTitle(page)} - {theme} theme, " +
                        $"{GalleryScenario.AccentTitle(accent)} accent, " +
                        $"{GalleryScenario.MaterialTitle(material)} requested",
                });
            }

            // High Contrast: no material axis. The application forces opaque
            // surfaces there, so three material rows would be three identical
            // pictures and one of them would be a lie.
            foreach (string page in GalleryScenario.Pages)
            foreach (string accent in GalleryScenario.GalleryAccents)
            foreach ((int width, int height) in GalleryScenario.GallerySizes)
            {
                scenarios.Add(new GalleryScenario
                {
                    Subject = GalleryScenario.PageSlug(page),
                    Page = page,
                    Width = width,
                    Height = height,
                    Theme = Dark,
                    Accent = accent,
                    HighContrast = true,
                    RequestedMaterial = GalleryMaterials.None,
                    Variant = "matrix",
                    ProviderScenario = page == UiRailLayoutSettings.TabSync
                        ? GalleryProviders.LocalFolder
                        : GalleryProviders.None,
                    Caption = $"{GalleryScenario.PageTitle(page)} - High Contrast, " +
                        $"{GalleryScenario.AccentTitle(accent)} accent",
                });
            }

            return scenarios;
        }

        /// <summary>
        /// The text-scale sweep. 85, 100 and 125 per cent are gallery-quality
        /// candidates; 150 per cent is regression evidence and is captured
        /// whatever it looks like, so defects are recorded rather than hidden.
        /// </summary>
        public static IReadOnlyList<GalleryScenario> Accessibility()
        {
            var scenarios = new List<GalleryScenario>();

            foreach (string page in GalleryScenario.Pages)
            foreach (double scale in new[] { 0.85, 1.0, 1.25, 1.5 })
            foreach ((int width, int height) in GalleryScenario.GallerySizes)
            {
                string slug = ((int)Math.Round(scale * 100))
                    .ToString(CultureInfo.InvariantCulture);

                scenarios.Add(new GalleryScenario
                {
                    Subject = GalleryScenario.PageSlug(page),
                    Page = page,
                    Width = width,
                    Height = height,
                    Theme = Dark,
                    Accent = Indigo,
                    TextScale = scale,
                    Variant = "text-" + slug,
                    ProviderScenario = page == UiRailLayoutSettings.TabSync
                        ? GalleryProviders.LocalFolder
                        : GalleryProviders.None,
                    Category = GalleryCategories.Accessibility,
                    Caption = $"{GalleryScenario.PageTitle(page)} - text size {slug}%",
                });
            }

            return scenarios;
        }

        /// <summary>
        /// The website set. Roughly fifty images that between them cover every
        /// page in both themes, all four gallery accents, all three materials,
        /// every navigation position, the docking system, the accessibility
        /// surfaces, and each implemented sync provider.
        /// </summary>
        public static IReadOnlyList<GalleryScenario> Curated()
        {
            var scenarios = new List<GalleryScenario>();
            int order = 0;

            void Add(GalleryScenario scenario) =>
                scenarios.Add(scenario with
                {
                    GalleryCandidate = true,
                    GalleryOrder = ++order,
                });

            // ---------------------------------------------------------------
            // Core workflows: every page, both themes, accents spread across
            // the set so accent customization reads as real rather than
            // decorative.
            // ---------------------------------------------------------------
            (string Page, string Accent, string Theme, int Width, int Height, string Provider)[] core =
            {
                (UiRailLayoutSettings.TabDashboard, Violet, Dark, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabInstalledGames, Indigo, Dark, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabProfiles, Rose, Dark, 1280, 720, GalleryProviders.None),
                (UiRailLayoutSettings.TabTransferPreview, Rose, Dark, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabManualBackup, Indigo, Dark, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabBackups, Teal, Dark, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabSync, Teal, Dark, 1336, 768, GalleryProviders.LocalFolder),
                (UiRailLayoutSettings.TabHistory, Violet, Dark, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabSettings, Violet, Dark, 1280, 720, GalleryProviders.None),

                (UiRailLayoutSettings.TabDashboard, Indigo, Light, 1280, 720, GalleryProviders.None),
                (UiRailLayoutSettings.TabInstalledGames, Teal, Light, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabProfiles, Violet, Light, 1280, 720, GalleryProviders.None),
                (UiRailLayoutSettings.TabTransferPreview, Indigo, Light, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabManualBackup, Rose, Light, 1280, 720, GalleryProviders.None),
                (UiRailLayoutSettings.TabBackups, Violet, Light, 1336, 768, GalleryProviders.None),
                (UiRailLayoutSettings.TabSync, Indigo, Light, 1336, 768, GalleryProviders.GoogleDrive),
                (UiRailLayoutSettings.TabHistory, Teal, Light, 1280, 720, GalleryProviders.None),
                (UiRailLayoutSettings.TabSettings, Rose, Light, 1336, 768, GalleryProviders.None),
            };

            foreach ((string page, string accent, string theme, int width, int height, string provider) in core)
            {
                string title = GalleryScenario.PageTitle(page);
                string themeWord = theme == Dark ? "Dark" : "Light";
                string shortcut = GalleryScenario.PageShortcut(page);

                Add(new GalleryScenario
                {
                    Subject = GalleryScenario.PageSlug(page),
                    Page = page,
                    Width = width,
                    Height = height,
                    Theme = theme,
                    Accent = accent,
                    ProviderScenario = provider,
                    Variant = "populated",
                    Category = GalleryCategories.Core,
                    Caption = $"{title} — {themeWord} mode, " +
                        $"{GalleryScenario.AccentTitle(accent)} accent",
                    Alt = $"Game Save Manager {title} page in {themeWord.ToLowerInvariant()} mode " +
                        $"with the {accent} accent; reachable with {shortcut}.",
                });
            }

            // ---------------------------------------------------------------
            // Appearance. The two OS-composited materials only ever come from
            // the Windows engine.
            // ---------------------------------------------------------------
            Add(Appearance("settings-appearance", UiRailLayoutSettings.TabSettings, 0,
                Dark, Violet, GalleryMaterials.Mica, GalleryEngines.WindowsScreenReadback,
                "Appearance — Violet accent with Mica",
                "The Settings Appearance category with the Violet accent selected and the Mica window material composited by Windows."));

            Add(Appearance("dashboard", UiRailLayoutSettings.TabDashboard, null,
                Dark, Teal, GalleryMaterials.Acrylic, GalleryEngines.WindowsScreenReadback,
                "Dashboard — Teal accent with Acrylic",
                "The Dashboard with the Teal accent and the Acrylic window material composited by Windows."));

            Add(Appearance("backups", UiRailLayoutSettings.TabBackups, null,
                Dark, Indigo, GalleryMaterials.Mica, GalleryEngines.WindowsScreenReadback,
                "Backups — Indigo accent with Mica",
                "The Backups page with the Indigo accent and the Mica window material composited by Windows."));

            Add(Appearance("profiles", UiRailLayoutSettings.TabProfiles, null,
                Dark, Rose, GalleryMaterials.Acrylic, GalleryEngines.WindowsScreenReadback,
                "Profiles — Rose accent with Acrylic",
                "The Profiles page with the Rose accent and the Acrylic window material composited by Windows."));

            Add(Appearance("settings-appearance", UiRailLayoutSettings.TabSettings, 0,
                Dark, Teal, GalleryMaterials.None, GalleryEngines.Headless,
                "Appearance — Teal accent, no window material",
                "The Settings Appearance category showing theme, accent and window-material choices with the Teal accent."));

            Add(Appearance("settings-appearance", UiRailLayoutSettings.TabSettings, 0,
                Light, Indigo, GalleryMaterials.None, GalleryEngines.Headless,
                "Appearance — Light mode, Indigo accent",
                "The Settings Appearance category in light mode with the default Indigo accent."));

            Add(Appearance("settings-appearance", UiRailLayoutSettings.TabSettings, 0,
                Dark, Rose, GalleryMaterials.None, GalleryEngines.Headless,
                "Appearance — Rose accent, no window material",
                "The Settings Appearance category with the Rose accent and the window material set to none."));

            // ---------------------------------------------------------------
            // Workspace / docking. Ten scenarios, driven through the real
            // layout API; the three that need more than one window are
            // captured from the composited desktop.
            // ---------------------------------------------------------------
            Add(Workspace("workspace-dashboard", UiRailLayoutSettings.TabDashboard,
                GalleryWorkspaces.Default, Dark, Violet, GalleryMaterials.None,
                GalleryEngines.Headless, 1336, 768,
                "Workspace — Dashboard in its default arrangement",
                "The Dashboard workspace in the arrangement the application ships with."));

            Add(Workspace("workspace-backups", UiRailLayoutSettings.TabBackups,
                GalleryWorkspaces.LeftRightSplit, Dark, Teal, GalleryMaterials.None,
                GalleryEngines.Headless, 1336, 768,
                "Workspace — panels docked to the left and right regions",
                "The Backups page with workspace panels docked into the left and right regions around the centre."));

            Add(Workspace("workspace-manual-backup", UiRailLayoutSettings.TabManualBackup,
                GalleryWorkspaces.TopBottom, Dark, Indigo, GalleryMaterials.None,
                GalleryEngines.Headless, 1336, 768,
                "Workspace — panels docked to the top and bottom regions",
                "The Manual backup page with workspace panels docked above and below the centre region."));

            Add(Workspace("workspace-dashboard", UiRailLayoutSettings.TabDashboard,
                GalleryWorkspaces.FourRegions, Dark, Teal, GalleryMaterials.Acrylic,
                GalleryEngines.WindowsScreenReadback, 1336, 768,
                "Workspace — panels docked across four regions",
                "The Dashboard with panels docked into the left, top, right and bottom regions at once, over an Acrylic window."));

            Add(Workspace("workspace-backups", UiRailLayoutSettings.TabBackups,
                GalleryWorkspaces.Resized, Dark, Violet, GalleryMaterials.None,
                GalleryEngines.Headless, 1336, 768,
                "Workspace — regions resized to unequal widths",
                "The Backups workspace after the docked regions were dragged to unequal widths."));

            Add(Workspace("workspace-backups", UiRailLayoutSettings.TabBackups,
                GalleryWorkspaces.FloatingPanel, Dark, Teal, GalleryMaterials.Mica,
                GalleryEngines.WindowsScreenReadback, 1336, 768,
                "Workspace — a panel floated into its own window",
                "The Backups page with one workspace section floated out into a separate window over the main window."));

            Add(Workspace("workspace-backups", UiRailLayoutSettings.TabBackups,
                GalleryWorkspaces.DetachedTab, Dark, Violet, GalleryMaterials.Mica,
                GalleryEngines.WindowsScreenReadback, 1336, 768,
                "Backups — detached into a separate window",
                "The Backups page detached from the navigation rail into its own window alongside the main window."));

            Add(Workspace("workspace-multi-window", UiRailLayoutSettings.TabSync,
                GalleryWorkspaces.MultipleWindows, Dark, Indigo, GalleryMaterials.Acrylic,
                GalleryEngines.WindowsScreenReadback, 1336, 768,
                "Workspace — main window with two detached pages",
                "The main window with two pages detached into separate windows arranged around it."));

            Add(Workspace("workspace-transfer-profiles", UiRailLayoutSettings.TabTransferPreview,
                GalleryWorkspaces.SavedCustom, Dark, Rose, GalleryMaterials.None,
                GalleryEngines.Headless, 1336, 768,
                "Workspace — a saved custom layout",
                "The Transfer profiles page arranged into a custom workspace layout that can be saved and restored."));

            Add(Workspace("workspace-dashboard", UiRailLayoutSettings.TabDashboard,
                GalleryWorkspaces.Restored, Dark, Indigo, GalleryMaterials.None,
                GalleryEngines.Headless, 1280, 720,
                "Workspace — restored to the default layout",
                "The Dashboard workspace after Reset returned it to the default arrangement."));

            // ---------------------------------------------------------------
            // Accessibility.
            // ---------------------------------------------------------------
            Add(new GalleryScenario
            {
                Subject = "settings-accessibility",
                Page = UiRailLayoutSettings.TabSettings,
                SettingsCategory = 1,
                Width = 1280,
                Height = 720,
                Theme = Dark,
                Accent = Rose,
                HighContrast = true,
                Variant = "high-contrast",
                Category = GalleryCategories.Accessibility,
                Caption = "Accessibility — High Contrast surfaces",
                Alt = "The Settings Accessibility category with High Contrast enabled, showing opaque surfaces and stronger borders.",
            });

            Add(new GalleryScenario
            {
                Subject = "settings-accessibility",
                Page = UiRailLayoutSettings.TabSettings,
                SettingsCategory = 1,
                Width = 1280,
                Height = 720,
                Theme = Dark,
                Accent = Indigo,
                Variant = "controls",
                Category = GalleryCategories.Accessibility,
                Caption = "Accessibility — text size, reduce motion and High Contrast controls",
                Alt = "The Settings Accessibility category showing the text-size slider, the Reduce motion switch and the High Contrast switch.",
            });

            Add(new GalleryScenario
            {
                Subject = "dashboard",
                Page = UiRailLayoutSettings.TabDashboard,
                Width = 1336,
                Height = 768,
                Theme = Dark,
                Accent = Teal,
                HighContrast = true,
                Variant = "high-contrast",
                Category = GalleryCategories.Accessibility,
                Caption = "Dashboard — High Contrast",
                Alt = "The Dashboard rendered with High Contrast enabled, which keeps every surface opaque.",
            });

            // Manual backup rather than a table page: the DataGrid column
            // widths are fixed in device-independent units and do not grow
            // with the text scale, so a table's headers truncate above 100%.
            // That defect is recorded in the archive and the accessibility
            // report; it is not something to publish.
            Add(new GalleryScenario
            {
                Subject = "manual-backup",
                Page = UiRailLayoutSettings.TabManualBackup,
                Width = 1336,
                Height = 768,
                Theme = Dark,
                Accent = Indigo,
                TextScale = 1.25,
                Variant = "text-125",
                Category = GalleryCategories.Accessibility,
                Caption = "Manual backup — text size 125%",
                Alt = "The Manual backup page at 125 per cent text size, with every control and label still fully readable.",
            });

            Add(new GalleryScenario
            {
                Subject = "backups",
                Page = UiRailLayoutSettings.TabBackups,
                Width = 1336,
                Height = 768,
                Theme = Dark,
                Accent = Violet,
                TextScale = 0.85,
                Variant = "text-85",
                Category = GalleryCategories.Accessibility,
                Caption = "Backups — text size 85%",
                Alt = "The Backups page at 85 per cent text size, which fits more rows on screen.",
            });

            // ---------------------------------------------------------------
            // Navigation positions.
            // ---------------------------------------------------------------
            (string Position, bool Collapsed, string Page, string Accent, string Caption, string Alt)[] rails =
            {
                (UiRailLayoutSettings.PositionRight, false, UiRailLayoutSettings.TabProfiles, Teal,
                    "Navigation — right-side rail",
                    "The navigation rail moved to the right-hand side of the window."),
                (UiRailLayoutSettings.PositionRight, true, UiRailLayoutSettings.TabBackups, Violet,
                    "Navigation — collapsed right-side rail",
                    "The right-hand navigation rail collapsed to icons only."),
                // A page with a full-height table, so the horizontal bar is
                // shown against content rather than against empty space.
                (UiRailLayoutSettings.PositionTop, false, UiRailLayoutSettings.TabInstalledGames, Indigo,
                    "Navigation — top navigation",
                    "The navigation moved to the top of the window as a horizontal bar."),
                (UiRailLayoutSettings.PositionLeft, true, UiRailLayoutSettings.TabHistory, Rose,
                    "Navigation — collapsed left rail",
                    "The left navigation rail collapsed to icons only."),
            };

            foreach ((string position, bool collapsed, string page, string accent,
                string caption, string alt) in rails)
            {
                Add(new GalleryScenario
                {
                    Subject = "navigation-" + position + (collapsed ? "-collapsed" : "-expanded"),
                    Page = page,
                    Width = 1336,
                    Height = 768,
                    Theme = Dark,
                    Accent = accent,
                    RailPosition = position,
                    RailCollapsed = collapsed,
                    Variant = "rail",
                    Category = GalleryCategories.Navigation,
                    Caption = caption,
                    Alt = alt,
                });
            }

            // ---------------------------------------------------------------
            // Sync providers. No connection is ever made; each state is the
            // real view model driven by the deterministic fixture.
            // ---------------------------------------------------------------
            (string Slug, string Provider, string Accent, string? Scroll, string Caption, string Alt)[] sync =
            {
                ("sync-local-folder", GalleryProviders.LocalFolder, Teal, null,
                    "Sync — Local Folder target",
                    "The Sync page configured against a local or mounted folder target."),
                ("sync-sftp", GalleryProviders.Sftp, Indigo, null,
                    "Sync — SFTP backup-run synchronization",
                    "The Sync page configured for an SFTP server using an example host and a neutral remote path."),
                ("sync-google-drive", GalleryProviders.GoogleDrive, Violet, "sync.target",
                    "Sync — Google Drive with its managed backup folder",
                    "The Sync page connected to Google Drive, showing the application-managed backup folder."),
                ("sync-preview", GalleryProviders.Preview, Teal, "sync.plan",
                    "Sync — preview of the runs that would be copied",
                    "A Sync preview listing which completed backup runs would be uploaded or downloaded."),
                ("sync-results", GalleryProviders.Results, Rose, "sync.results",
                    "Sync — results of a completed synchronization",
                    "The Sync page after a run, listing each completed backup run and the sync history."),
            };

            foreach ((string slug, string provider, string accent, string? scroll,
                string caption, string alt) in sync)
            {
                Add(new GalleryScenario
                {
                    Subject = slug,
                    Page = UiRailLayoutSettings.TabSync,
                    Width = 1336,
                    Height = 768,
                    Theme = Dark,
                    Accent = accent,
                    ProviderScenario = provider,
                    ScrollToPanel = scroll,
                    Variant = "ready",
                    Category = GalleryCategories.Sync,
                    Caption = caption,
                    Alt = alt,
                });
            }

            // ---------------------------------------------------------------
            // The remaining Settings categories, so the website can show that
            // Settings is more than one screen.
            // ---------------------------------------------------------------
            (int Index, string Slug, string Accent, string Caption, string Alt)[] settings =
            {
                (2, "settings-behaviour", Indigo,
                    "Settings — startup section",
                    "The Settings Behaviour category, where any of the nine sections can be chosen as the startup destination."),
                // The Layout category is taller than the window, and Settings
                // is not a workspace page, so there is no panel to scroll to.
                // The caption names what the image actually shows.
                (3, "settings-layout", Teal,
                    "Settings — table columns and navigation rail",
                    "The Settings Layout category, where the Installed games columns are chosen and the navigation rail is positioned; saved workspace layouts and workspace reset follow below."),
                (4, "settings-providers", Violet,
                    "Settings — sync provider availability",
                    "The Settings Providers category listing each sync provider and whether this build can use it."),
                (5, "settings-data", Rose,
                    "Settings — data locations",
                    "The Settings Data category showing the database, interface settings and sync settings file locations."),
                (6, "settings-diagnostics", Indigo,
                    "Settings — diagnostics",
                    "The Settings Diagnostics category showing the application version, platform, operating system and .NET runtime."),
            };

            foreach ((int index, string slug, string accent, string caption, string alt) in settings)
            {
                Add(new GalleryScenario
                {
                    Subject = slug,
                    Page = UiRailLayoutSettings.TabSettings,
                    SettingsCategory = index,
                    Width = 1280,
                    Height = 720,
                    Theme = Dark,
                    Accent = accent,
                    Variant = "category",
                    Category = GalleryCategories.Core,
                    Caption = caption,
                    Alt = alt,
                });
            }

            return scenarios;
        }

        private static GalleryScenario Appearance(
            string subject,
            string page,
            int? category,
            string theme,
            string accent,
            string material,
            string engine,
            string caption,
            string alt) => new()
            {
                Subject = subject,
                Page = page,
                SettingsCategory = category,
                Width = 1336,
                Height = 768,
                Theme = theme,
                Accent = accent,
                RequestedMaterial = material,
                Engine = engine,
                Variant = material,
                Category = GalleryCategories.Appearance,
                Caption = caption,
                Alt = alt,
            };

        private static GalleryScenario Workspace(
            string subject,
            string page,
            string workspace,
            string theme,
            string accent,
            string material,
            string engine,
            int width,
            int height,
            string caption,
            string alt) => new()
            {
                Subject = subject,
                Page = page,
                Width = width,
                Height = height,
                Theme = theme,
                Accent = accent,
                RequestedMaterial = material,
                Engine = engine,
                Workspace = workspace,
                Variant = workspace,
                Category = GalleryCategories.Workspace,
                Caption = caption,
                Alt = alt,
            };

        /// <summary>Curated scenarios a given engine is responsible for.</summary>
        public static IReadOnlyList<GalleryScenario> CuratedFor(string engine) =>
            Curated()
                .Where(scenario => string.Equals(scenario.Engine, engine, StringComparison.Ordinal))
                .ToArray();

        /// <summary>Archive scenarios a given engine is responsible for.</summary>
        public static IReadOnlyList<GalleryScenario> FullFor(string engine) =>
            Full()
                .Where(scenario => string.Equals(scenario.Engine, engine, StringComparison.Ordinal))
                .ToArray();

        /// <summary>
        /// The interactive material archive, subsampled so a run takes minutes
        /// rather than most of an hour on the machine whose screen it occupies.
        /// Every page and both real materials are still covered in both themes;
        /// the accent and resolution axes are thinned, because an accent
        /// changes the same pixels whatever the material is and both harnesses
        /// already sweep accents elsewhere. Pass the full set explicitly to
        /// capture every cell.
        /// </summary>
        public static IReadOnlyList<GalleryScenario> MaterialArchiveSample() =>
            FullFor(GalleryEngines.WindowsScreenReadback)
                .Where(scenario =>
                    scenario.Width == 1336 &&
                    (scenario.Accent == Indigo || scenario.Accent == Teal))
                .ToArray();
    }
}
