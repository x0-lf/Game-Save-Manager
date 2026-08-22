using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Save;
using Xunit;

namespace GameSaves.Tests
{
    // The UI store backs restart-survival for theme and table layout: what
    // Save writes, Load returns, and malformed values fall back safely.
    public sealed class UiSettingsStoreTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void AMissingFile_YieldsTheSystemDefault()
        {
            var store = new UiSettingsStore(_temp.GetPath("absent.json"));

            Assert.Equal(AppUiSettings.ThemeSystem, store.Load().ThemeChoice);
            Assert.Equal(
                AppUiSettings.DefaultInstalledGameColumnOrder,
                store.Load().InstalledGameColumnOrder);
            Assert.Empty(store.Load().HiddenInstalledGameColumns);
        }

        [Fact]
        public void ASavedThemeChoice_SurvivesReload()
        {
            string path = _temp.GetPath("ui-settings.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with { ThemeChoice = AppUiSettings.ThemeDark });

            Assert.Equal(
                AppUiSettings.ThemeDark,
                new UiSettingsStore(path).Load().ThemeChoice);
        }

        [Fact]
        public void AMalformedFileOrUnknownTheme_FallsBackToDefaults()
        {
            string malformed = _temp.GetPath("broken.json");
            File.WriteAllText(malformed, "{not json");
            AppUiSettings malformedSettings = new UiSettingsStore(malformed).Load();
            Assert.Equal(AppUiSettings.ThemeSystem, malformedSettings.ThemeChoice);
            Assert.Equal(
                AppUiSettings.AccentIndigo,
                malformedSettings.AccentTheme);

            string unknown = _temp.GetPath("unknown.json");
            File.WriteAllText(unknown, "{\"ThemeChoice\":\"neon\"}");
            Assert.Equal(
                AppUiSettings.ThemeSystem,
                new UiSettingsStore(unknown).Load().ThemeChoice);
        }

        [Fact]
        public void ASchemaV2File_LoadsWithDefaultAccentAndOpaqueTransparency()
        {
            string path = _temp.GetPath("v2.json");
            File.WriteAllText(
                path,
                "{\"SchemaVersion\":2,\"ThemeChoice\":\"dark\"," +
                "\"InstalledGameColumnOrder\":[\"game\",\"appId\"]," +
                "\"HiddenInstalledGameColumns\":[]}");

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
            Assert.Equal(AppUiSettings.AccentIndigo, loaded.AccentTheme);
            Assert.Equal(1.0, loaded.Transparency.Window);
            Assert.Equal(1.0, loaded.Transparency.Card);
            Assert.Equal(1.0, loaded.Transparency.Inset);
        }

        [Fact]
        public void AccentTheme_PersistsAndUnknownValuesFallBackToIndigo()
        {
            string path = _temp.GetPath("accent.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with { AccentTheme = AppUiSettings.AccentTeal });

            Assert.Equal(
                AppUiSettings.AccentTeal,
                new UiSettingsStore(path).Load().AccentTheme);

            string unknown = _temp.GetPath("unknown-accent.json");
            File.WriteAllText(unknown, "{\"AccentTheme\":\"chartreuse\"}");
            Assert.Equal(
                AppUiSettings.AccentIndigo,
                new UiSettingsStore(unknown).Load().AccentTheme);
        }

        [Fact]
        public void Transparency_PersistsAClampedRoundTrip()
        {
            string path = _temp.GetPath("transparency.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    Transparency = new UiTransparencySettings(
                        Window: 0.8,
                        Card: 0.6,
                        Inset: 0.4),
                });

            UiTransparencySettings loaded =
                new UiSettingsStore(path).Load().Transparency;

            Assert.Equal(0.8, loaded.Window);
            Assert.Equal(0.6, loaded.Card);
            Assert.Equal(0.4, loaded.Inset);
        }

        [Fact]
        public void Transparency_OutOfRangeOrNonNumericValues_FallBackSafely()
        {
            string path = _temp.GetPath("transparency-invalid.json");
            File.WriteAllText(
                path,
                "{\"Transparency\":{\"Window\":1.7,\"Card\":-0.2," +
                "\"Inset\":\"opaque\"}}");

            UiTransparencySettings loaded =
                new UiSettingsStore(path).Load().Transparency;

            Assert.Equal(1.0, loaded.Window);
            Assert.Equal(0.0, loaded.Card);
            Assert.Equal(1.0, loaded.Inset);

            string nonObject = _temp.GetPath("transparency-malformed.json");
            File.WriteAllText(nonObject, "{\"Transparency\":42}");
            Assert.Equal(
                UiTransparencySettings.Default,
                new UiSettingsStore(nonObject).Load().Transparency);
        }

        [Fact]
        public void ASchemaV3File_MigratesToDefaultAccessibility()
        {
            string path = _temp.GetPath("v3.json");
            File.WriteAllText(
                path,
                "{\"SchemaVersion\":3,\"ThemeChoice\":\"dark\"," +
                "\"AccentTheme\":\"teal\"," +
                "\"Transparency\":{\"Window\":0.9,\"Card\":0.9,\"Inset\":0.9}}");

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
            Assert.Equal(AppUiSettings.AccentTeal, loaded.AccentTheme);
            Assert.Equal(0.9, loaded.Transparency.Window);
            Assert.Equal(UiAccessibilitySettings.Default, loaded.Accessibility);
        }

        [Fact]
        public void Accessibility_PersistsARoundTrip()
        {
            string path = _temp.GetPath("accessibility.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    Accessibility = new UiAccessibilitySettings(
                        TextScale: 1.25,
                        ReduceMotion: true,
                        HighContrast: true),
                });

            UiAccessibilitySettings loaded =
                new UiSettingsStore(path).Load().Accessibility;

            Assert.Equal(1.25, loaded.TextScale);
            Assert.True(loaded.ReduceMotion);
            Assert.True(loaded.HighContrast);
        }

        [Fact]
        public void Accessibility_OutOfRangeOrMalformedValues_FallBackSafely()
        {
            string path = _temp.GetPath("accessibility-invalid.json");
            File.WriteAllText(
                path,
                "{\"Accessibility\":{\"TextScale\":9,\"ReduceMotion\":\"yes\"," +
                "\"HighContrast\":1}}");

            UiAccessibilitySettings loaded =
                new UiSettingsStore(path).Load().Accessibility;

            Assert.Equal(1.5, loaded.TextScale);
            Assert.False(loaded.ReduceMotion);
            Assert.False(loaded.HighContrast);

            string nonNumeric = _temp.GetPath("accessibility-text.json");
            File.WriteAllText(
                nonNumeric,
                "{\"Accessibility\":{\"TextScale\":\"big\",\"HighContrast\":true}}");

            UiAccessibilitySettings textual =
                new UiSettingsStore(nonNumeric).Load().Accessibility;

            Assert.Equal(1.0, textual.TextScale);
            Assert.True(textual.HighContrast);

            string nonObject = _temp.GetPath("accessibility-malformed.json");
            File.WriteAllText(nonObject, "{\"Accessibility\":42}");
            Assert.Equal(
                UiAccessibilitySettings.Default,
                new UiSettingsStore(nonObject).Load().Accessibility);
        }

        [Fact]
        public void ASchemaV4File_MigratesToTheNoneWindowMaterial()
        {
            string path = _temp.GetPath("v4.json");
            File.WriteAllText(
                path,
                "{\"SchemaVersion\":4,\"ThemeChoice\":\"dark\"," +
                "\"AccentTheme\":\"teal\"," +
                "\"Transparency\":{\"Window\":0.9,\"Card\":0.9,\"Inset\":0.9}," +
                "\"Accessibility\":{\"TextScale\":1.1,\"ReduceMotion\":true," +
                "\"HighContrast\":false}}");

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(AppUiSettings.MaterialNone, loaded.WindowMaterial);
            Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
            Assert.Equal(0.9, loaded.Transparency.Window);
            Assert.True(loaded.Accessibility.ReduceMotion);
        }

        [Fact]
        public void WindowMaterial_PersistsARoundTrip()
        {
            string path = _temp.GetPath("material.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    WindowMaterial = AppUiSettings.MaterialMica,
                });

            Assert.Equal(
                AppUiSettings.MaterialMica,
                new UiSettingsStore(path).Load().WindowMaterial);
        }

        [Fact]
        public void AnUnknownOrMalformedWindowMaterial_FallsBackToNone()
        {
            string unknown = _temp.GetPath("material-unknown.json");
            File.WriteAllText(unknown, "{\"WindowMaterial\":\"frosted\"}");
            Assert.Equal(
                AppUiSettings.MaterialNone,
                new UiSettingsStore(unknown).Load().WindowMaterial);

            string malformed = _temp.GetPath("material-malformed.json");
            File.WriteAllText(malformed, "{\"WindowMaterial\":7}");
            Assert.Equal(
                AppUiSettings.MaterialNone,
                new UiSettingsStore(malformed).Load().WindowMaterial);
        }

        [Fact]
        public void InstalledGameColumnPreferences_AreNormalizedAndPersisted()
        {
            string path = _temp.GetPath("columns.json");
            var store = new UiSettingsStore(path);
            store.Save(AppUiSettings.Default with
            {
                InstalledGameColumnOrder = new[]
                {
                    AppUiSettings.StatusColumn,
                    "unknown",
                    AppUiSettings.GameColumn,
                    AppUiSettings.StatusColumn,
                },
                HiddenInstalledGameColumns = new[]
                {
                    AppUiSettings.LibraryColumn,
                    "unknown",
                    AppUiSettings.LibraryColumn,
                },
            });

            AppUiSettings loaded = store.Load();

            Assert.Equal(AppUiSettings.StatusColumn, loaded.InstalledGameColumnOrder[0]);
            Assert.Equal(AppUiSettings.GameColumn, loaded.InstalledGameColumnOrder[1]);
            Assert.Equal(
                AppUiSettings.DefaultInstalledGameColumnOrder.Count,
                loaded.InstalledGameColumnOrder.Count);
            Assert.Equal(
                new[] { AppUiSettings.LibraryColumn },
                loaded.HiddenInstalledGameColumns);
        }

        [Fact]
        public void InstalledGameColumnChanges_SurviveViewModelRestart()
        {
            string path = _temp.GetPath("view-model-columns.json");
            var store = new UiSettingsStore(path);
            var viewModel = new InstalledGamesViewModel(
                new EmptyInstalledGameStatusService(),
                store);

            viewModel.ColumnOptions.Single(
                option => option.Key == AppUiSettings.NeedsFixColumn).IsVisible = false;
            viewModel.SetColumnOrder(new[]
            {
                AppUiSettings.AppIdColumn,
                AppUiSettings.GameColumn,
            });

            var restarted = new InstalledGamesViewModel(
                new EmptyInstalledGameStatusService(),
                new UiSettingsStore(path));

            Assert.Equal(AppUiSettings.AppIdColumn, restarted.ColumnOptions[0].Key);
            Assert.Equal(AppUiSettings.GameColumn, restarted.ColumnOptions[1].Key);
            Assert.False(restarted.ColumnOptions.Single(
                option => option.Key == AppUiSettings.NeedsFixColumn).IsVisible);
        }

        [Fact]
        public void AMissingFile_YieldsTheDefaultRailLayout()
        {
            var store = new UiSettingsStore(_temp.GetPath("rail-absent.json"));

            UiRailLayoutSettings rail = store.Load().RailLayout;

            Assert.Equal(UiRailLayoutSettings.PositionLeft, rail.Position);
            Assert.False(rail.Collapsed);
            Assert.Equal(UiRailLayoutSettings.CanonicalTabOrder, rail.TabOrder);
            Assert.Empty(rail.HiddenTabs);
        }

        [Fact]
        public void ASchemaV5File_MigratesToTheDefaultRailLayout()
        {
            string path = _temp.GetPath("v5.json");
            File.WriteAllText(
                path,
                "{\"SchemaVersion\":5,\"ThemeChoice\":\"dark\"," +
                "\"AccentTheme\":\"teal\"," +
                "\"Transparency\":{\"Window\":0.9,\"Card\":0.9,\"Inset\":0.9}," +
                "\"Accessibility\":{\"TextScale\":1.1,\"ReduceMotion\":true," +
                "\"HighContrast\":false},\"WindowMaterial\":\"none\"," +
                "\"InstalledGameColumnOrder\":[],\"HiddenInstalledGameColumns\":[]}");

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
            Assert.Equal(UiRailLayoutSettings.Default.Position, loaded.RailLayout.Position);
            Assert.Equal(UiRailLayoutSettings.Default.Collapsed, loaded.RailLayout.Collapsed);
            Assert.Equal(UiRailLayoutSettings.Default.TabOrder, loaded.RailLayout.TabOrder);
            Assert.Equal(UiRailLayoutSettings.Default.HiddenTabs, loaded.RailLayout.HiddenTabs);
        }

        [Fact]
        public void RailLayout_PersistsARoundTrip()
        {
            string path = _temp.GetPath("rail.json");
            UiRailLayoutSettings custom = new(
                Position: UiRailLayoutSettings.PositionTop,
                Collapsed: true,
                TabOrder: new[]
                {
                    UiRailLayoutSettings.TabSettings,
                    UiRailLayoutSettings.TabDashboard,
                    UiRailLayoutSettings.TabHistory,
                    UiRailLayoutSettings.TabSync,
                    UiRailLayoutSettings.TabBackups,
                    UiRailLayoutSettings.TabManualBackup,
                    UiRailLayoutSettings.TabTransferPreview,
                    UiRailLayoutSettings.TabProfiles,
                    UiRailLayoutSettings.TabInstalledGames,
                },
                HiddenTabs: new[] { UiRailLayoutSettings.TabSync });
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with { RailLayout = custom });

            UiRailLayoutSettings loaded = new UiSettingsStore(path).Load().RailLayout;

            Assert.Equal(custom.Position, loaded.Position);
            Assert.Equal(custom.Collapsed, loaded.Collapsed);
            Assert.Equal(custom.TabOrder, loaded.TabOrder);
            Assert.Equal(custom.HiddenTabs, loaded.HiddenTabs);
        }

        [Fact]
        public void AnUnknownRailPosition_FallsBackToLeft()
        {
            string path = _temp.GetPath("rail-position-invalid.json");
            File.WriteAllText(
                path,
                "{\"RailLayout\":{\"Position\":\"bottom\",\"Collapsed\":\"yes\"}}");

            UiRailLayoutSettings rail = new UiSettingsStore(path).Load().RailLayout;

            Assert.Equal(UiRailLayoutSettings.PositionLeft, rail.Position);
            Assert.False(rail.Collapsed);
        }

        [Fact]
        public void ARailLayout_GarbageOrderAndHiddenListsNormalizeSafely()
        {
            string path = _temp.GetPath("rail-garbage.json");
            File.WriteAllText(
                path,
                "{\"RailLayout\":{\"TabOrder\":[\"sync\",\"nope\",\"sync\"," +
                "\"dashboard\",\"dashboard\"]," +
                "\"HiddenTabs\":[\"settings\",\"dashboard\",\"history\"," +
                "\"history\",\"nope\"]}}");

            UiRailLayoutSettings rail = new UiSettingsStore(path).Load().RailLayout;

            // Every key exactly once, persisted entries first, missing keys
            // appended in canonical order.
            Assert.Equal(
                new[]
                {
                    UiRailLayoutSettings.TabSync,
                    UiRailLayoutSettings.TabDashboard,
                    UiRailLayoutSettings.TabInstalledGames,
                    UiRailLayoutSettings.TabProfiles,
                    UiRailLayoutSettings.TabTransferPreview,
                    UiRailLayoutSettings.TabManualBackup,
                    UiRailLayoutSettings.TabBackups,
                    UiRailLayoutSettings.TabHistory,
                    UiRailLayoutSettings.TabSettings,
                },
                rail.TabOrder);

            // Unknown and duplicate keys drop out, and the pinned Dashboard
            // and Settings tabs are never hidden.
            Assert.Equal(
                new[] { UiRailLayoutSettings.TabHistory },
                rail.HiddenTabs);
        }

        [Fact]
        public void AMalformedRailLayout_FallsBackToDefaults()
        {
            string path = _temp.GetPath("rail-malformed.json");
            File.WriteAllText(path, "{\"RailLayout\":42}");

            UiRailLayoutSettings rail = new UiSettingsStore(path).Load().RailLayout;

            Assert.Equal(UiRailLayoutSettings.Default.Position, rail.Position);
            Assert.Equal(UiRailLayoutSettings.Default.Collapsed, rail.Collapsed);
            Assert.Equal(UiRailLayoutSettings.Default.TabOrder, rail.TabOrder);
            Assert.Equal(UiRailLayoutSettings.Default.HiddenTabs, rail.HiddenTabs);
        }

        [Fact]
        public void Normalization_KeepsAllNineTabsAndNeverHidesPinnedTabs()
        {
            Assert.Equal(
                UiRailLayoutSettings.CanonicalTabOrder,
                UiRailLayoutSettings.NormalizeTabOrder(
                    new[] { "nonsense", string.Empty }));

            // Even a hidden list that names every tab drops the pinned
            // Dashboard and Settings.
            Assert.Equal(
                UiRailLayoutSettings.CanonicalTabOrder.Where(
                    key => UiRailLayoutSettings.CanHideTab(key)),
                UiRailLayoutSettings.NormalizeHiddenTabs(
                    UiRailLayoutSettings.CanonicalTabOrder));

            Assert.False(UiRailLayoutSettings.CanHideTab(
                UiRailLayoutSettings.TabDashboard));
            Assert.False(UiRailLayoutSettings.CanHideTab(
                UiRailLayoutSettings.TabSettings));
            Assert.True(UiRailLayoutSettings.CanHideTab(
                UiRailLayoutSettings.TabHistory));
        }

        [Fact]
        public void ASchemaV6File_MigratesToAnEmptyWorkspaceLayoutList()
        {
            string path = _temp.GetPath("v6.json");
            File.WriteAllText(
                path,
                "{\"SchemaVersion\":6,\"ThemeChoice\":\"dark\"," +
                "\"RailLayout\":{\"Position\":\"top\",\"Collapsed\":true," +
                "\"TabOrder\":[],\"HiddenTabs\":[]}}");

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
            Assert.Equal(UiRailLayoutSettings.PositionTop, loaded.RailLayout.Position);
            Assert.True(loaded.RailLayout.Collapsed);
            Assert.Empty(loaded.WorkspaceLayouts);
        }

        [Fact]
        public void AMissingFile_YieldsNoWorkspaceLayouts()
        {
            Assert.Empty(
                new UiSettingsStore(_temp.GetPath("workspaces-absent.json"))
                    .Load()
                    .WorkspaceLayouts);
        }

        [Fact]
        public void ASchemaV7File_MigratesToTheDashboardStartupTab()
        {
            string path = _temp.GetPath("v7.json");
            File.WriteAllText(
                path,
                "{\"SchemaVersion\":7,\"ThemeChoice\":\"dark\"," +
                "\"RailLayout\":{\"Position\":\"top\",\"Collapsed\":false," +
                "\"TabOrder\":[],\"HiddenTabs\":[]}," +
                "\"WorkspaceLayouts\":[{\"Name\":\"Desk\",\"Detached\":[]}]}");

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(AppUiSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(8, AppUiSettings.CurrentSchemaVersion);
            Assert.Equal(AppUiSettings.ThemeDark, loaded.ThemeChoice);
            Assert.Equal(UiRailLayoutSettings.TabDashboard, loaded.StartupTabKey);
            Assert.Equal("Desk", Assert.Single(loaded.WorkspaceLayouts).Name);
        }

        [Fact]
        public void AMissingFile_StartsOnTheDashboardTab()
        {
            Assert.Equal(
                UiRailLayoutSettings.TabDashboard,
                new UiSettingsStore(_temp.GetPath("startup-absent.json"))
                    .Load()
                    .StartupTabKey);
        }

        [Fact]
        public void TheStartupTab_PersistsARoundTrip()
        {
            string path = _temp.GetPath("startup.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    StartupTabKey = UiRailLayoutSettings.TabSync,
                });

            Assert.Equal(
                UiRailLayoutSettings.TabSync,
                new UiSettingsStore(path).Load().StartupTabKey);
        }

        [Fact]
        public void AnUnknownStartupTab_FallsBackToDashboard()
        {
            string path = _temp.GetPath("startup-invalid.json");
            File.WriteAllText(path, "{\"StartupTabKey\":\"achievements\"}");

            Assert.Equal(
                UiRailLayoutSettings.TabDashboard,
                new UiSettingsStore(path).Load().StartupTabKey);

            File.WriteAllText(path, "{\"StartupTabKey\":7}");
            Assert.Equal(
                UiRailLayoutSettings.TabDashboard,
                new UiSettingsStore(path).Load().StartupTabKey);
        }

        [Fact]
        public void TheStore_ExposesTheExactFilePathItUses()
        {
            string path = _temp.GetPath("file-path.json");

            Assert.Equal(path, new UiSettingsStore(path).FilePath);
            Assert.Equal(path, new SyncSettingsStore(path).FilePath);
        }

        [Fact]
        public void WorkspaceLayouts_PersistARoundTrip()
        {
            string path = _temp.GetPath("workspaces.json");
            UiDetachedWindowSettings first = new(
                TabKey: UiRailLayoutSettings.TabSync,
                Left: 96,
                Top: 48,
                Width: 980,
                Height: 680);
            UiDetachedWindowSettings second = new(
                TabKey: UiRailLayoutSettings.TabHistory,
                Left: -1920,
                Top: 120,
                Width: 480,
                Height: 360);
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    WorkspaceLayouts = new[]
                    {
                        new UiWorkspaceLayoutSettings(
                            "Two screens",
                            new[] { first, second }),
                    },
                });

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            UiWorkspaceLayoutSettings layout = Assert.Single(loaded.WorkspaceLayouts);
            Assert.Equal("Two screens", layout.Name);
            Assert.Equal(new[] { first, second }, layout.Detached);
        }

        [Fact]
        public void WorkspaceLayouts_DropGarbageEntriesAndClampValues()
        {
            string path = _temp.GetPath("workspaces-garbage.json");
            File.WriteAllText(
                path,
                "{\"WorkspaceLayouts\":[{" +
                "\"Name\":\"Messy\"," +
                "\"Detached\":[" +
                // Unknown tab key: dropped.
                "{\"TabKey\":\"C:\\\\Users\\\\someone\\\\Documents\",\"Left\":1," +
                "\"Top\":1,\"Width\":600,\"Height\":400}," +
                // Missing a coordinate: dropped whole.
                "{\"TabKey\":\"sync\",\"Left\":1,\"Top\":1,\"Width\":600}," +
                // Valid, clamped extents and positions.
                "{\"TabKey\":\"history\",\"Left\":-99999,\"Top\":99999," +
                "\"Width\":10,\"Height\":99999}," +
                // Duplicate tab key: first (above) wins.
                "{\"TabKey\":\"history\",\"Left\":5,\"Top\":5," +
                "\"Width\":650,\"Height\":450}," +
                // Not an object: dropped.
                "42," +
                // Fully valid entry.
                "{\"TabKey\":\"backups\",\"Left\":64,\"Top\":32," +
                "\"Width\":700,\"Height\":500}]}," +
                // Non-object layout: dropped.
                "7," +
                // Empty name: dropped.
                "{\"Name\":\"   \",\"Detached\":[]}]}");

            IReadOnlyList<UiWorkspaceLayoutSettings> loaded =
                new UiSettingsStore(path).Load().WorkspaceLayouts;

            UiWorkspaceLayoutSettings layout = Assert.Single(loaded);
            Assert.Equal("Messy", layout.Name);

            Assert.Equal(
                new[]
                {
                    new UiDetachedWindowSettings(
                        UiRailLayoutSettings.TabHistory,
                        Left: UiDetachedWindowSettings.MinPosition,
                        Top: UiDetachedWindowSettings.MaxPosition,
                        Width: UiDetachedWindowSettings.MinWindowExtent,
                        Height: UiDetachedWindowSettings.MaxWindowExtent),
                    new UiDetachedWindowSettings(
                        UiRailLayoutSettings.TabBackups,
                        Left: 64,
                        Top: 32,
                        Width: 700,
                        Height: 500),
                },
                layout.Detached);
        }

        [Fact]
        public void WorkspaceLayouts_EnforceTheLayoutAndEntryLimits()
        {
            string path = _temp.GetPath("workspaces-limits.json");
            var layouts = new List<UiWorkspaceLayoutSettings>();

            for (int index = 0; index < 12; index++)
            {
                var entries = new List<UiDetachedWindowSettings>();

                // All nine stable tab keys, so normalization's 8-entry cap
                // drops exactly the ninth.
                for (int entry = 0; entry < 9; entry++)
                {
                    entries.Add(new UiDetachedWindowSettings(
                        UiRailLayoutSettings.CanonicalTabOrder[entry],
                        Left: entry * 10,
                        Top: entry * 10,
                        Width: 600,
                        Height: 400));
                }

                layouts.Add(new UiWorkspaceLayoutSettings($"Layout {index}", entries));
            }

            new UiSettingsStore(path).Save(
                AppUiSettings.Default with { WorkspaceLayouts = layouts });

            IReadOnlyList<UiWorkspaceLayoutSettings> loaded =
                new UiSettingsStore(path).Load().WorkspaceLayouts;

            // At most 8 layouts, the first 8 in order.
            Assert.Equal(8, loaded.Count);
            Assert.Equal("Layout 0", loaded[0].Name);
            Assert.Equal("Layout 7", loaded[^1].Name);

            // At most 8 entries per layout, the first 8 in order.
            Assert.Equal(8, loaded[0].Detached.Count);
            Assert.Equal(
                UiRailLayoutSettings.CanonicalTabOrder.Take(8),
                loaded[0].Detached.Select(entry => entry.TabKey));
        }

        [Fact]
        public void WorkspaceLayoutNames_AreTrimmedTruncatedAndUnique()
        {
            string path = _temp.GetPath("workspaces-names.json");
            string longName = new string('n', 60);
            File.WriteAllText(
                path,
                "{\"WorkspaceLayouts\":[" +
                "{\"Name\":\"  Trimmed  \",\"Detached\":[]}," +
                "{\"Name\":\"" + longName + "\",\"Detached\":[]}," +
                "{\"Name\":\"Trimmed\",\"Detached\":[]}]}");

            IReadOnlyList<UiWorkspaceLayoutSettings> loaded =
                new UiSettingsStore(path).Load().WorkspaceLayouts;

            // Duplicate names keep the first occurrence; names are trimmed
            // and truncated to the 40-character limit.
            Assert.Equal(2, loaded.Count);
            Assert.Equal("Trimmed", loaded[0].Name);
            Assert.Equal(
                UiWorkspaceLayoutSettings.MaxNameLength,
                loaded[1].Name.Length);
            Assert.Equal(longName[..UiWorkspaceLayoutSettings.MaxNameLength], loaded[1].Name);
        }

        [Fact]
        public void WorkspaceLayoutTransfer_RoundTripsAndRejectsGarbage()
        {
            UiWorkspaceLayoutSettings layout = new(
                "Exported",
                new[]
                {
                    new UiDetachedWindowSettings(
                        UiRailLayoutSettings.TabSync,
                        Left: 12,
                        Top: 34,
                        Width: 560,
                        Height: 420),
                });

            string payload = WorkspaceLayoutTransfer.Serialize(new[] { layout });

            IReadOnlyList<UiWorkspaceLayoutSettings> roundTripped =
                WorkspaceLayoutTransfer.Deserialize(payload);

            UiWorkspaceLayoutSettings restored = Assert.Single(roundTripped);
            Assert.Equal(layout.Name, restored.Name);
            Assert.Equal(layout.Detached, restored.Detached);

            // Malformed JSON and a non-array root normalize to an empty
            // list rather than failing.
            Assert.Empty(WorkspaceLayoutTransfer.Deserialize("{not json"));
            Assert.Empty(WorkspaceLayoutTransfer.Deserialize("{\"Layouts\":[]}"));

            // An unknown tab key drops its ENTRY; a layout of only unknown
            // entries survives as a legal all-attached layout.
            UiWorkspaceLayoutSettings sanitized = Assert.Single(
                WorkspaceLayoutTransfer.Deserialize(
                    "[{\"Name\":\"x\",\"Detached\":[" +
                    "{\"TabKey\":\"nonsense\",\"Left\":1,\"Top\":1," +
                    "\"Width\":1,\"Height\":1}]}]"));
            Assert.Equal("x", sanitized.Name);
            Assert.Empty(sanitized.Detached);
        }

        [Fact]
        public void WorkspaceLayoutTransfer_CarriesNoMachineSpecificValues()
        {
            // A snapshot fed through fakes carries machine-looking values in
            // its tab keys and numbers. Unknown keys are dropped by
            // normalization, so the serialized export can carry only layout
            // names, the nine stable tab keys, and plain numbers.
            UiDetachedWindowSettings? machineKeyed =
                UiDetachedWindowSettings.TryCreate(
                    @"C:\Users\mike\AppData\Roaming\GameSave",
                    left: 1,
                    top: 1,
                    width: 400,
                    height: 300);

            Assert.Null(machineKeyed);

            UiWorkspaceLayoutSettings layout = new(
                "Clean",
                UiDetachedWindowSettings.TryCreate(
                    UiRailLayoutSettings.TabBackups,
                    100,
                    100,
                    800,
                    600) is { } entry
                    ? new[] { entry }
                    : Array.Empty<UiDetachedWindowSettings>());

            string payload = WorkspaceLayoutTransfer.Serialize(new[] { layout });

            Assert.Contains("Clean", payload);
            Assert.Contains(UiRailLayoutSettings.TabBackups, payload);

            foreach (string forbidden in new[]
            {
                @"C:\Users",
                "mike",
                "AppData",
                "@",
                "AIza",
                "oauth",
                "token",
                "password",
            })
            {
                Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);
            }

            // The same boundary guards the import direction: a payload with
            // a machine-looking tab key cannot import that key. The layout
            // name survives, but only as an all-attached layout.
            UiWorkspaceLayoutSettings imported = Assert.Single(
                WorkspaceLayoutTransfer.Deserialize(
                    "[{\"Name\":\"x\",\"Detached\":[{\"TabKey\":\"C:\\\\Users\\\\mike\"," +
                    "\"Left\":1,\"Top\":1,\"Width\":1,\"Height\":1}]}]"));
            Assert.Equal("x", imported.Name);
            Assert.Empty(imported.Detached);
        }

        private sealed class EmptyInstalledGameStatusService : IInstalledGameSaveStatusService
        {
            public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<InstalledGameSaveStatus>>(
                    Array.Empty<InstalledGameSaveStatus>());
        }
    }
}
