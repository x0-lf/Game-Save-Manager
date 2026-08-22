using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Save;
using GameSaves.Infrastructure.Sync;
using Xunit;

namespace GameSaves.Tests
{
    // The Settings page is the live editor of the persisted UI settings:
    // it must open showing what was stored, persist every change it allows,
    // and clamp or reject what it does not. ThemeService applies nothing in
    // tests because no Avalonia Application is running.
    public sealed class SettingsViewModelTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private SettingsViewModel CreateViewModel(
            string path,
            FakeWorkspaceLayoutHost? workspaceHost = null,
            string? syncPath = null)
        {
            syncPath ??= _temp.GetPath("sync-settings.json");
            var store = new UiSettingsStore(path);
            var themeService = new ThemeService();
            var viewModel = new SettingsViewModel(
                store,
                themeService,
                new WindowMaterialService(themeService),
                new InstalledGamesViewModel(
                    new EmptyInstalledGameStatusService(),
                    store),
                new SyncSettingsStore(syncPath),
                new SyncProviderCatalog(),
                "windows",
                @"C:\data\games.db");
            viewModel.WorkspaceHost = workspaceHost;
            return viewModel;
        }

        [Fact]
        public void Initialization_ReflectsStoredSettings()
        {
            string path = _temp.GetPath("init.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    ThemeChoice = AppUiSettings.ThemeDark,
                    AccentTheme = AppUiSettings.AccentTeal,
                    Transparency = new UiTransparencySettings(
                        Window: 0.9,
                        Card: 0.7,
                        Inset: 0.5),
                });

            SettingsViewModel viewModel = CreateViewModel(path);

            Assert.Equal(AppUiSettings.ThemeDark, viewModel.ThemeChoice);
            Assert.Equal(AppUiSettings.AccentTeal, viewModel.AccentTheme);
            Assert.Equal(0.9, viewModel.WindowOpacity);
            Assert.Equal(0.7, viewModel.CardOpacity);
            Assert.Equal(0.5, viewModel.InsetOpacity);
            Assert.Equal("windows", viewModel.Platform);
            Assert.Equal(@"C:\data\games.db", viewModel.DatabasePath);
        }

        [Fact]
        public void ChangingThemeChoice_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("theme.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.ThemeChoice = AppUiSettings.ThemeLight;

            AppUiSettings loaded = new UiSettingsStore(path).Load();
            Assert.Equal(AppUiSettings.ThemeLight, loaded.ThemeChoice);
            Assert.Equal(
                AppUiSettings.ThemeLight,
                CreateViewModel(path).ThemeChoice);
        }

        [Fact]
        public void AnUnknownThemeChoice_IsIgnored()
        {
            string path = _temp.GetPath("theme-invalid.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.ThemeChoice = "neon";

            Assert.Equal(
                AppUiSettings.ThemeSystem,
                new UiSettingsStore(path).Load().ThemeChoice);
        }

        [Fact]
        public void ChangingAccentTheme_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("accent.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.AccentTheme = AppUiSettings.AccentRose;

            AppUiSettings loaded = new UiSettingsStore(path).Load();
            Assert.Equal(AppUiSettings.AccentRose, loaded.AccentTheme);
            Assert.Equal(
                AppUiSettings.AccentRose,
                CreateViewModel(path).AccentTheme);
        }

        [Fact]
        public void AnUnknownAccentTheme_IsIgnored()
        {
            string path = _temp.GetPath("accent-invalid.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.AccentTheme = "chartreuse";

            Assert.Equal(
                AppUiSettings.AccentIndigo,
                new UiSettingsStore(path).Load().AccentTheme);
        }

        [Fact]
        public void ChangingTransparencyLevels_PersistAndSurviveRestart()
        {
            string path = _temp.GetPath("transparency.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.WindowOpacity = 0.85;
            viewModel.CardOpacity = 0.6;
            viewModel.InsetOpacity = 0.4;

            UiTransparencySettings loaded =
                new UiSettingsStore(path).Load().Transparency;
            Assert.Equal(0.85, loaded.Window);
            Assert.Equal(0.6, loaded.Card);
            Assert.Equal(0.4, loaded.Inset);

            SettingsViewModel restarted = CreateViewModel(path);
            Assert.Equal(0.85, restarted.WindowOpacity);
            Assert.Equal(0.6, restarted.CardOpacity);
            Assert.Equal(0.4, restarted.InsetOpacity);
        }

        [Fact]
        public void OutOfRangeTransparency_IsClampedOnPersist()
        {
            string path = _temp.GetPath("transparency-clamp.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.WindowOpacity = 1.7;
            viewModel.CardOpacity = -0.3;
            viewModel.InsetOpacity = 0.2;

            UiTransparencySettings loaded =
                new UiSettingsStore(path).Load().Transparency;
            Assert.Equal(1.0, loaded.Window);
            Assert.Equal(0.0, loaded.Card);
            Assert.Equal(0.2, loaded.Inset);
        }

        [Fact]
        public void Initialization_ReflectsStoredAccessibilitySettings()
        {
            string path = _temp.GetPath("accessibility-init.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    Accessibility = new UiAccessibilitySettings(
                        TextScale: 1.2,
                        ReduceMotion: true,
                        HighContrast: true),
                });

            SettingsViewModel viewModel = CreateViewModel(path);

            Assert.Equal(1.2, viewModel.TextScale);
            Assert.True(viewModel.ReduceMotion);
            Assert.True(viewModel.HighContrast);
        }

        [Fact]
        public void ChangingAccessibilitySettings_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("accessibility.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.TextScale = 1.25;
            viewModel.ReduceMotion = true;
            viewModel.HighContrast = true;

            UiAccessibilitySettings loaded =
                new UiSettingsStore(path).Load().Accessibility;
            Assert.Equal(1.25, loaded.TextScale);
            Assert.True(loaded.ReduceMotion);
            Assert.True(loaded.HighContrast);

            SettingsViewModel restarted = CreateViewModel(path);
            Assert.Equal(1.25, restarted.TextScale);
            Assert.True(restarted.ReduceMotion);
            Assert.True(restarted.HighContrast);
        }

        [Fact]
        public void OutOfRangeTextScale_IsClampedOnPersist()
        {
            string path = _temp.GetPath("text-scale-clamp.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.TextScale = 4.0;
            Assert.Equal(1.5, new UiSettingsStore(path).Load().Accessibility.TextScale);

            viewModel.TextScale = 0.1;
            Assert.Equal(0.85, new UiSettingsStore(path).Load().Accessibility.TextScale);
        }

        [Fact]
        public void EnablingHighContrast_KeepsStoredTransparencyForLater()
        {
            string path = _temp.GetPath("high-contrast.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.WindowOpacity = 0.8;
            viewModel.HighContrast = true;

            AppUiSettings loaded = new UiSettingsStore(path).Load();
            Assert.True(loaded.Accessibility.HighContrast);
            Assert.Equal(0.8, loaded.Transparency.Window);
        }

        [Fact]
        public void ChangingOneSetting_PreservesTheOthers()
        {
            string path = _temp.GetPath("preserve.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.ThemeChoice = AppUiSettings.ThemeDark;
            viewModel.AccentTheme = AppUiSettings.AccentViolet;
            viewModel.CardOpacity = 0.75;
            viewModel.ThemeChoice = AppUiSettings.ThemeSystem;

            AppUiSettings loaded = new UiSettingsStore(path).Load();
            Assert.Equal(AppUiSettings.ThemeSystem, loaded.ThemeChoice);
            Assert.Equal(AppUiSettings.AccentViolet, loaded.AccentTheme);
            Assert.Equal(0.75, loaded.Transparency.Card);
        }

        [Fact]
        public void Initialization_ReflectsStoredWindowMaterial()
        {
            string path = _temp.GetPath("material-init.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    WindowMaterial = AppUiSettings.MaterialMica,
                });

            Assert.Equal(
                AppUiSettings.MaterialMica,
                CreateViewModel(path).WindowMaterial);
        }

        [Fact]
        public void ChangingWindowMaterial_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("material.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.WindowMaterial = AppUiSettings.MaterialAcrylic;

            AppUiSettings loaded = new UiSettingsStore(path).Load();
            Assert.Equal(AppUiSettings.MaterialAcrylic, loaded.WindowMaterial);
            Assert.Equal(
                AppUiSettings.MaterialAcrylic,
                CreateViewModel(path).WindowMaterial);
        }

        [Fact]
        public void AnUnknownWindowMaterial_IsIgnored()
        {
            string path = _temp.GetPath("material-invalid.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.WindowMaterial = "frosted-glass";

            Assert.Equal(
                AppUiSettings.MaterialNone,
                new UiSettingsStore(path).Load().WindowMaterial);
        }

        [Fact]
        public void AWindowMaterial_MakesTheWindowOpacitySettingInert()
        {
            string path = _temp.GetPath("material-inert.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            Assert.False(viewModel.IsWindowMaterialSelected);
            Assert.False(viewModel.IsWindowOpacityInert);

            viewModel.WindowMaterial = AppUiSettings.MaterialMica;

            Assert.True(viewModel.IsWindowMaterialSelected);
            Assert.True(viewModel.IsWindowOpacityInert);

            viewModel.WindowMaterial = AppUiSettings.MaterialNone;

            Assert.False(viewModel.IsWindowMaterialSelected);
            Assert.False(viewModel.IsWindowOpacityInert);
        }

        [Fact]
        public void HighContrast_ForcesTheEffectiveMaterialToNone()
        {
            string path = _temp.GetPath("material-high-contrast.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.WindowMaterial = AppUiSettings.MaterialAcrylic;
            viewModel.HighContrast = true;

            // The stored choice survives for later, but the effective
            // material decision is "none" and the Settings hints agree:
            // accessibility beats aesthetics.
            AppUiSettings loaded = new UiSettingsStore(path).Load();
            Assert.Equal(AppUiSettings.MaterialAcrylic, loaded.WindowMaterial);
            Assert.True(loaded.Accessibility.HighContrast);
            Assert.Equal(
                AppUiSettings.MaterialNone,
                WindowMaterialService.EffectiveMaterial(loaded));

            viewModel.HighContrast = false;
            Assert.Equal(
                AppUiSettings.MaterialAcrylic,
                WindowMaterialService.EffectiveMaterial(
                    new UiSettingsStore(path).Load()));
        }

        [Fact]
        public void EffectiveMaterial_RejectsUnknownValues()
        {
            Assert.Equal(
                AppUiSettings.MaterialNone,
                WindowMaterialService.EffectiveMaterial(
                    AppUiSettings.Default with { WindowMaterial = "vapour" }));
            Assert.Equal(
                AppUiSettings.MaterialAcrylic,
                WindowMaterialService.EffectiveMaterial(
                    AppUiSettings.Default with
                    {
                        WindowMaterial = AppUiSettings.MaterialAcrylic,
                    }));
            Assert.Equal(
                AppUiSettings.MaterialMica,
                WindowMaterialService.EffectiveMaterial(
                    AppUiSettings.Default with
                    {
                        WindowMaterial = AppUiSettings.MaterialMica,
                    }));
        }

        [Fact]
        public void LayoutEdits_AreSharedWithTheInstalledGamesViewModel()
        {
            string path = _temp.GetPath("columns.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            Assert.NotNull(viewModel.InstalledGames);
            Assert.Equal(
                viewModel.InstalledGames.ColumnOptions.Select(option => option.Key),
                new InstalledGamesViewModel(
                    new EmptyInstalledGameStatusService(),
                    new UiSettingsStore(path))
                    .ColumnOptions
                    .Select(option => option.Key));
        }

        [Fact]
        public void Initialization_ReflectsStoredRailLayout()
        {
            string path = _temp.GetPath("rail-init.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    RailLayout = new UiRailLayoutSettings(
                        Position: UiRailLayoutSettings.PositionRight,
                        Collapsed: true,
                        TabOrder: new[]
                        {
                            UiRailLayoutSettings.TabHistory,
                            UiRailLayoutSettings.TabDashboard,
                            UiRailLayoutSettings.TabSettings,
                        },
                        HiddenTabs: new[] { UiRailLayoutSettings.TabSync }),
                });

            SettingsViewModel viewModel = CreateViewModel(path);

            Assert.Equal(UiRailLayoutSettings.PositionRight, viewModel.RailPosition);
            Assert.True(viewModel.RailCollapsed);
            Assert.Equal(
                new[]
                {
                    UiRailLayoutSettings.TabHistory,
                    UiRailLayoutSettings.TabDashboard,
                    UiRailLayoutSettings.TabSettings,
                },
                viewModel.RailTabs.Take(3).Select(option => option.Key));
            Assert.Equal(9, viewModel.RailTabs.Count);
            Assert.False(viewModel.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabSync).IsVisible);
            Assert.All(
                viewModel.RailTabs.Where(option =>
                    option.Key is UiRailLayoutSettings.TabDashboard
                        or UiRailLayoutSettings.TabSettings),
                option => Assert.False(option.CanHide));
        }

        [Fact]
        public void ChangingRailPosition_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("rail-position.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.RailPosition = UiRailLayoutSettings.PositionTop;

            Assert.Equal(
                UiRailLayoutSettings.PositionTop,
                new UiSettingsStore(path).Load().RailLayout.Position);
            Assert.Equal(
                UiRailLayoutSettings.PositionTop,
                CreateViewModel(path).RailPosition);
        }

        [Fact]
        public void AnUnknownRailPosition_IsIgnored()
        {
            string path = _temp.GetPath("rail-position-invalid.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.RailPosition = "diagonal";

            Assert.Equal(
                UiRailLayoutSettings.PositionLeft,
                new UiSettingsStore(path).Load().RailLayout.Position);
        }

        [Fact]
        public void CollapsingTheRail_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("rail-collapse.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.RailCollapsed = true;

            Assert.True(new UiSettingsStore(path).Load().RailLayout.Collapsed);
            Assert.True(CreateViewModel(path).RailCollapsed);
        }

        [Fact]
        public void HidingATab_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("rail-hide.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabHistory).IsVisible =
                false;

            Assert.Equal(
                new[] { UiRailLayoutSettings.TabHistory },
                new UiSettingsStore(path).Load().RailLayout.HiddenTabs);

            SettingsViewModel restarted = CreateViewModel(path);
            Assert.False(restarted.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabHistory).IsVisible);
        }

        [Fact]
        public void PinnedTabs_CannotBeHidden()
        {
            string path = _temp.GetPath("rail-pinned.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            // The Settings checkbox is disabled for these; a programmatic
            // write is refused and reverts, and nothing is persisted.
            viewModel.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabDashboard).IsVisible =
                false;
            viewModel.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabSettings).IsVisible =
                false;

            Assert.True(viewModel.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabDashboard).IsVisible);
            Assert.True(viewModel.RailTabs.Single(
                option => option.Key == UiRailLayoutSettings.TabSettings).IsVisible);
            Assert.Empty(new UiSettingsStore(path).Load().RailLayout.HiddenTabs);
        }

        [Fact]
        public void HidingEveryHideableTab_StillLeavesVisibleTabs()
        {
            string path = _temp.GetPath("rail-hide-all.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            foreach (RailTabOption option in viewModel.RailTabs)
                option.IsVisible = false;

            AppUiSettings loaded = new UiSettingsStore(path).Load();

            // The seven hideable tabs hid; the pinned Dashboard and Settings
            // stay visible, so the rail is never empty.
            Assert.Equal(7, loaded.RailLayout.HiddenTabs.Count);
            Assert.Equal(
                new[] { UiRailLayoutSettings.TabDashboard, UiRailLayoutSettings.TabSettings },
                viewModel.RailTabs
                    .Where(option => option.IsVisible)
                    .Select(option => option.Key));
        }

        [Fact]
        public void MovingATabUp_PersistsTheOrderAndSurvivesRestart()
        {
            string path = _temp.GetPath("rail-move.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            // History (canonical slot 8, collection index 7) moves above
            // Sync (index 6).
            viewModel.MoveRailTabUpCommand.Execute(
                UiRailLayoutSettings.TabHistory);

            Assert.Equal(
                UiRailLayoutSettings.TabHistory,
                viewModel.RailTabs[6].Key);
            Assert.Equal(
                UiRailLayoutSettings.TabSync,
                viewModel.RailTabs[7].Key);
            Assert.Equal(
                viewModel.RailTabs.Select(option => option.Key),
                new UiSettingsStore(path).Load().RailLayout.TabOrder);
            Assert.Equal(
                viewModel.RailTabs.Select(option => option.Key),
                CreateViewModel(path).RailTabs.Select(option => option.Key));
        }

        [Fact]
        public void MoveButtons_RespectTheBoundsAndReportFlags()
        {
            string path = _temp.GetPath("rail-move-bounds.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            Assert.False(viewModel.RailTabs[0].CanMoveUp);
            Assert.True(viewModel.RailTabs[0].CanMoveDown);
            Assert.False(viewModel.RailTabs[^1].CanMoveDown);
            Assert.True(viewModel.RailTabs[^1].CanMoveUp);

            IReadOnlyList<string> before =
                viewModel.RailTabs.Select(option => option.Key).ToArray();

            viewModel.MoveRailTabUpCommand.Execute(
                UiRailLayoutSettings.TabDashboard);
            viewModel.MoveRailTabDownCommand.Execute(
                UiRailLayoutSettings.TabSettings);
            viewModel.MoveRailTabDownCommand.Execute("nonsense");

            Assert.Equal(
                before,
                new UiSettingsStore(path).Load().RailLayout.TabOrder);
        }

        // Workspace layouts (A7): the Settings page owns the saved-layout
        // list; the host bridge (real main window in production, a fake
        // here) owns snapshots, applying, and the file exchange. Saved
        // layouts are listed but never auto-applied.
        [Fact]
        public void StoredWorkspaceLayouts_AreListedButNeverAutoApplied()
        {
            string path = _temp.GetPath("workspaces-init.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    WorkspaceLayouts = new[]
                    {
                        new UiWorkspaceLayoutSettings(
                            "Saved",
                            new[]
                            {
                                new UiDetachedWindowSettings(
                                    UiRailLayoutSettings.TabSync,
                                    Left: 10,
                                    Top: 20,
                                    Width: 800,
                                    Height: 600),
                            }),
                    },
                });
            var host = new FakeWorkspaceLayoutHost();

            SettingsViewModel viewModel = CreateViewModel(path, host);

            UiWorkspaceLayoutSettings layout = Assert.Single(viewModel.WorkspaceLayouts);
            Assert.Equal("Saved", layout.Name);
            Assert.Equal(UiRailLayoutSettings.TabSync, layout.Detached.Single().TabKey);
            Assert.True(viewModel.HasWorkspaceLayouts);

            // Documented behavior: construction lists layouts but never
            // applies them.
            Assert.Empty(host.Applied);
            Assert.Equal(0, host.ReattachAllCalls);
        }

        [Fact]
        public void SavingAWorkspaceLayout_CapturesTheSnapshotAndPersistsIt()
        {
            string path = _temp.GetPath("workspaces-save.json");
            var host = new FakeWorkspaceLayoutHost
            {
                Snapshot = new[]
                {
                    new UiDetachedWindowSettings(
                        UiRailLayoutSettings.TabHistory,
                        Left: -1920,
                        Top: 40,
                        Width: 700,
                        Height: 500),
                },
            };
            SettingsViewModel viewModel = CreateViewModel(path, host);
            Assert.False(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));

            viewModel.NewLayoutName = "  Editing night  ";

            Assert.True(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);

            UiWorkspaceLayoutSettings layout = Assert.Single(viewModel.WorkspaceLayouts);
            Assert.Equal("Editing night", layout.Name);
            Assert.Equal(host.Snapshot, layout.Detached);
            Assert.Equal(string.Empty, viewModel.NewLayoutName);

            AppUiSettings loaded = new UiSettingsStore(path).Load();
            layout = Assert.Single(loaded.WorkspaceLayouts);
            Assert.Equal("Editing night", layout.Name);
            Assert.Equal(UiRailLayoutSettings.TabHistory, layout.Detached.Single().TabKey);
        }

        [Fact]
        public void SaveWorkspaceLayout_RejectsEmptyDuplicateAndOverlongNames()
        {
            string path = _temp.GetPath("workspaces-names.json");
            SettingsViewModel viewModel = CreateViewModel(
                path, new FakeWorkspaceLayoutHost());

            // Empty and whitespace-only names never enable the command.
            foreach (string candidate in new[] { "", "   " })
            {
                viewModel.NewLayoutName = candidate;
                Assert.False(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));
            }

            viewModel.NewLayoutName = "Desk";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);
            Assert.Single(viewModel.WorkspaceLayouts);

            // A duplicate name stays disabled; a different one works.
            viewModel.NewLayoutName = "Desk";
            Assert.False(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));
            viewModel.NewLayoutName = "Couch";
            Assert.True(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));

            // Names longer than 40 characters are refused (the TextBox also
            // caps typing at 40).
            viewModel.NewLayoutName = new string('x', 41);
            Assert.False(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));
            Assert.Single(viewModel.WorkspaceLayouts);
        }

        [Fact]
        public void TheSavedLayoutLimit_StopsTheNinthSave()
        {
            string path = _temp.GetPath("workspaces-limit.json");
            SettingsViewModel viewModel = CreateViewModel(
                path, new FakeWorkspaceLayoutHost());

            for (int index = 0; index < UiWorkspaceLayoutSettings.MaxSavedLayouts; index++)
            {
                viewModel.NewLayoutName = $"Layout {index}";
                Assert.True(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));
                viewModel.SaveWorkspaceLayoutCommand.Execute(null);
            }

            Assert.Equal(
                UiWorkspaceLayoutSettings.MaxSavedLayouts,
                viewModel.WorkspaceLayouts.Count);

            viewModel.NewLayoutName = "One too many";
            Assert.False(viewModel.SaveWorkspaceLayoutCommand.CanExecute(null));
        }

        [Fact]
        public void ApplyingALayout_DelegatesOnlyItsDetachedSetToTheHost()
        {
            string path = _temp.GetPath("workspaces-apply.json");
            var detached = new[]
            {
                new UiDetachedWindowSettings(
                    UiRailLayoutSettings.TabProfiles,
                    Left: 100,
                    Top: 100,
                    Width: 900,
                    Height: 600),
                new UiDetachedWindowSettings(
                    UiRailLayoutSettings.TabBackups,
                    Left: 1020,
                    Top: 140,
                    Width: 800,
                    Height: 500),
            };
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    WorkspaceLayouts = new[]
                    {
                        new UiWorkspaceLayoutSettings("Pair", detached),
                    },
                });
            var host = new FakeWorkspaceLayoutHost();
            SettingsViewModel viewModel = CreateViewModel(path, host);

            viewModel.ApplyWorkspaceLayoutCommand.Execute("Pair");

            Assert.Equal(
                new IReadOnlyList<UiDetachedWindowSettings>[] { detached },
                host.Applied);

            // An unknown name does nothing.
            viewModel.ApplyWorkspaceLayoutCommand.Execute("Nope");
            Assert.Single(host.Applied);
        }

        [Fact]
        public void DeletingALayout_RemovesAndPersistsIt()
        {
            string path = _temp.GetPath("workspaces-delete.json");
            var host = new FakeWorkspaceLayoutHost();
            SettingsViewModel viewModel = CreateViewModel(path, host);
            viewModel.NewLayoutName = "Keep";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);
            viewModel.NewLayoutName = "Drop";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);

            viewModel.DeleteWorkspaceLayoutCommand.Execute("Drop");

            UiWorkspaceLayoutSettings remaining = Assert.Single(viewModel.WorkspaceLayouts);
            Assert.Equal("Keep", remaining.Name);
            Assert.True(viewModel.HasWorkspaceLayouts);
            Assert.Equal(
                "Keep",
                Assert.Single(new UiSettingsStore(path).Load().WorkspaceLayouts).Name);
        }

        [Fact]
        public void ResetWorkspace_IsATwoStepConfirmation()
        {
            string path = _temp.GetPath("workspaces-reset.json");
            var host = new FakeWorkspaceLayoutHost
            {
                Snapshot = new[]
                {
                    new UiDetachedWindowSettings(
                        UiRailLayoutSettings.TabSync,
                        Left: 0,
                        Top: 0,
                        Width: 600,
                        Height: 400),
                },
            };
            SettingsViewModel viewModel = CreateViewModel(path, host);
            viewModel.NewLayoutName = "Everything";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);

            // First click arms: nothing has happened yet.
            Assert.Equal("Reset workspace", viewModel.ResetWorkspaceText);
            viewModel.ResetWorkspaceCommand.Execute(null);

            Assert.True(viewModel.IsResetArmed);
            Assert.Equal("Confirm reset", viewModel.ResetWorkspaceText);
            Assert.Equal(0, host.ReattachAllCalls);
            Assert.Single(viewModel.WorkspaceLayouts);

            // Second click executes: windows reattached, layouts cleared and
            // persisted empty, confirmation disarmed.
            viewModel.ResetWorkspaceCommand.Execute(null);

            Assert.False(viewModel.IsResetArmed);
            Assert.Equal("Reset workspace", viewModel.ResetWorkspaceText);
            Assert.Equal(1, host.ReattachAllCalls);
            Assert.Empty(viewModel.WorkspaceLayouts);
            Assert.False(viewModel.HasWorkspaceLayouts);
            Assert.Empty(new UiSettingsStore(path).Load().WorkspaceLayouts);

            // Any other workspace action disarms an armed reset instead of
            // leaving a loaded confirmation behind.
            viewModel.NewLayoutName = "After reset";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);
            viewModel.ResetWorkspaceCommand.Execute(null);
            Assert.True(viewModel.IsResetArmed);
            viewModel.ApplyWorkspaceLayoutCommand.Execute("After reset");
            Assert.False(viewModel.IsResetArmed);
        }

        [Fact]
        public async Task Import_SuffixesCollidingNamesAndSkipsBeyondTheLimit()
        {
            string path = _temp.GetPath("workspaces-import.json");
            var host = new FakeWorkspaceLayoutHost();
            SettingsViewModel viewModel = CreateViewModel(path, host);
            viewModel.NewLayoutName = "Desk";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);

            host.ImportResult = new WorkspaceImportResult(
                WorkspaceFileOutcome.Completed,
                WorkspaceLayoutTransfer.Serialize(new[]
                {
                    new UiWorkspaceLayoutSettings(
                        "Desk",
                        new[]
                        {
                            new UiDetachedWindowSettings(
                                UiRailLayoutSettings.TabSync,
                                10, 10, 600, 400),
                        }),
                    new UiWorkspaceLayoutSettings(
                        "Couch",
                        Array.Empty<UiDetachedWindowSettings>()),
                    // Garbage layouts are dropped during import.
                    new UiWorkspaceLayoutSettings(
                        "   ",
                        Array.Empty<UiDetachedWindowSettings>()),
                }));

            await viewModel.ImportWorkspaceLayoutsCommand.ExecuteAsync(null);

            Assert.Equal(
                new[] { "Desk", "Desk (2)", "Couch" },
                viewModel.WorkspaceLayouts.Select(layout => layout.Name));
            Assert.Equal(
                new[] { "Desk", "Desk (2)", "Couch" },
                new UiSettingsStore(path).Load().WorkspaceLayouts
                    .Select(layout => layout.Name));
        }

        [Fact]
        public async Task Import_FillsTheSavedLayoutLimitAndReportsSkipped()
        {
            string path = _temp.GetPath("workspaces-import-limit.json");
            var host = new FakeWorkspaceLayoutHost();
            SettingsViewModel viewModel = CreateViewModel(path, host);

            for (int index = 0; index < 7; index++)
            {
                viewModel.NewLayoutName = $"Layout {index}";
                viewModel.SaveWorkspaceLayoutCommand.Execute(null);
            }

            host.ImportResult = new WorkspaceImportResult(
                WorkspaceFileOutcome.Completed,
                WorkspaceLayoutTransfer.Serialize(new[]
                {
                    new UiWorkspaceLayoutSettings("A", Array.Empty<UiDetachedWindowSettings>()),
                    new UiWorkspaceLayoutSettings("B", Array.Empty<UiDetachedWindowSettings>()),
                    new UiWorkspaceLayoutSettings("C", Array.Empty<UiDetachedWindowSettings>()),
                }));

            await viewModel.ImportWorkspaceLayoutsCommand.ExecuteAsync(null);

            Assert.Equal(UiWorkspaceLayoutSettings.MaxSavedLayouts, viewModel.WorkspaceLayouts.Count);
            Assert.Equal("Layout 6", viewModel.WorkspaceLayouts[6].Name);
            Assert.Equal("A", viewModel.WorkspaceLayouts[7].Name);
            Assert.Contains("skipped 2", viewModel.WorkspaceStatus);
        }

        [Fact]
        public async Task Import_HandlesFailureGarbageAndCancellation()
        {
            string path = _temp.GetPath("workspaces-import-bad.json");
            var host = new FakeWorkspaceLayoutHost();
            SettingsViewModel viewModel = CreateViewModel(path, host);

            // Cancelled: silent no-op.
            host.ImportResult = new WorkspaceImportResult(
                WorkspaceFileOutcome.Cancelled, null);
            await viewModel.ImportWorkspaceLayoutsCommand.ExecuteAsync(null);
            Assert.Empty(viewModel.WorkspaceLayouts);
            Assert.Equal(string.Empty, viewModel.WorkspaceStatus);

            // IO failure: reported.
            host.ImportResult = new WorkspaceImportResult(
                WorkspaceFileOutcome.Failed, null);
            await viewModel.ImportWorkspaceLayoutsCommand.ExecuteAsync(null);
            Assert.Contains("could not be read", viewModel.WorkspaceStatus);

            // Completed but nothing valid in the payload: reported, nothing
            // saved. (A layout with no valid name is garbage; a layout with
            // no entries is a legal all-attached layout.)
            host.ImportResult = new WorkspaceImportResult(
                WorkspaceFileOutcome.Completed, "[{\"Name\":\"   \"},7]");
            await viewModel.ImportWorkspaceLayoutsCommand.ExecuteAsync(null);
            Assert.Contains("No valid layouts", viewModel.WorkspaceStatus);
            Assert.Empty(viewModel.WorkspaceLayouts);
            Assert.Empty(new UiSettingsStore(path).Load().WorkspaceLayouts);
        }

        [Fact]
        public async Task Export_SendsAPrivatePayloadAndReportsTheOutcome()
        {
            string path = _temp.GetPath("workspaces-export.json");
            var host = new FakeWorkspaceLayoutHost
            {
                Snapshot = new[]
                {
                    new UiDetachedWindowSettings(
                        UiRailLayoutSettings.TabManualBackup,
                        Left: 24,
                        Top: 24,
                        Width: 900,
                        Height: 640),
                },
            };
            SettingsViewModel viewModel = CreateViewModel(path, host);

            // Nothing to export until a layout exists.
            Assert.False(viewModel.ExportWorkspaceLayoutsCommand.CanExecute(null));

            viewModel.NewLayoutName = "Clean";
            viewModel.SaveWorkspaceLayoutCommand.Execute(null);
            Assert.True(viewModel.ExportWorkspaceLayoutsCommand.CanExecute(null));

            await viewModel.ExportWorkspaceLayoutsCommand.ExecuteAsync(null);

            // The payload carries the layout name, the stable tab key, and
            // numbers only — nothing machine-specific can appear in it.
            Assert.NotNull(host.ExportPayload);
            Assert.Contains("Clean", host.ExportPayload);
            Assert.Contains(UiRailLayoutSettings.TabManualBackup, host.ExportPayload);

            foreach (string forbidden in new[]
            {
                @"C:\Users",
                "mike",
                "AppData",
                "AIza",
                "oauth",
                "token",
                "password",
            })
            {
                Assert.DoesNotContain(
                    forbidden,
                    host.ExportPayload,
                    StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal("Layouts exported.", viewModel.WorkspaceStatus);

            host.ExportOutcome = WorkspaceFileOutcome.Failed;
            await viewModel.ExportWorkspaceLayoutsCommand.ExecuteAsync(null);
            Assert.Contains("could not be written", viewModel.WorkspaceStatus);
        }

        // A8 Behaviour: the startup tab choice is a persisted setting like
        // the appearance choices — reflected on open, saved on change,
        // rejected when it is not one of the nine stable tab keys.
        [Fact]
        public void Initialization_ReflectsStoredStartupTab()
        {
            string path = _temp.GetPath("startup-init.json");
            new UiSettingsStore(path).Save(
                AppUiSettings.Default with
                {
                    StartupTabKey = UiRailLayoutSettings.TabManualBackup,
                });

            Assert.Equal(
                UiRailLayoutSettings.TabManualBackup,
                CreateViewModel(path).StartupTabKey);
        }

        [Fact]
        public void ChangingTheStartupTab_PersistsAndSurvivesRestart()
        {
            string path = _temp.GetPath("startup.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.StartupTabKey = UiRailLayoutSettings.TabHistory;

            Assert.Equal(
                UiRailLayoutSettings.TabHistory,
                new UiSettingsStore(path).Load().StartupTabKey);
            Assert.Equal(
                UiRailLayoutSettings.TabHistory,
                CreateViewModel(path).StartupTabKey);
        }

        [Fact]
        public void AnUnknownStartupTab_IsIgnored()
        {
            string path = _temp.GetPath("startup-invalid.json");
            SettingsViewModel viewModel = CreateViewModel(path);

            viewModel.StartupTabKey = "achievements";

            Assert.Equal(
                UiRailLayoutSettings.TabDashboard,
                new UiSettingsStore(path).Load().StartupTabKey);
        }

        // A8 Providers: the rows are the real catalog's own availability
        // state, filtered exactly like the Sync tab's provider picker.
        [Fact]
        public void ProviderStatuses_ListTheCatalogConfigurableProviders()
        {
            SettingsViewModel viewModel = CreateViewModel(
                _temp.GetPath("providers.json"));

            var catalog = new SyncProviderCatalog();

            Assert.Equal(
                catalog.GetAll()
                    .Where(descriptor => descriptor.IsConfigurationAvailable)
                    .Select(descriptor => (
                        descriptor.DisplayName,
                        Status: descriptor.IsImplemented
                            ? "Available"
                            : descriptor.UnavailableMessage ?? "Not implemented")),
                viewModel.ProviderStatuses.Select(row => (row.Name, row.Status)));
            Assert.Equal(3, viewModel.ProviderStatuses.Count);
        }

        // A8 Data locations: every surfaced path is the exact file the
        // owning component uses, never a re-derived copy.
        [Fact]
        public void DataLocations_SurfaceTheExactStorePaths()
        {
            string uiPath = _temp.GetPath("ui-data.json");
            string syncPath = _temp.GetPath("sync-data.json");
            SettingsViewModel viewModel =
                CreateViewModel(uiPath, syncPath: syncPath);

            Assert.Equal(uiPath, viewModel.UiSettingsPath);
            Assert.Equal(syncPath, viewModel.SyncSettingsPath);
            Assert.Equal(@"C:\data\games.db", viewModel.DatabasePath);
        }

        // A8 Diagnostics: the values come from the app assembly and the
        // running environment, not from stored or invented data.
        [Fact]
        public void Diagnostics_SurfaceTheRealEnvironmentAndAssembly()
        {
            SettingsViewModel viewModel = CreateViewModel(
                _temp.GetPath("diagnostics.json"));

            Assert.Equal(
                typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3),
                viewModel.ApplicationVersion);
            Assert.Equal("windows", viewModel.Platform);
            Assert.Equal(
                Environment.OSVersion.VersionString,
                viewModel.OperatingSystemVersion);
            Assert.NotEmpty(viewModel.RuntimeDescription);
            Assert.Contains(".NET", viewModel.RuntimeDescription, StringComparison.Ordinal);
        }

        private sealed class FakeWorkspaceLayoutHost : IWorkspaceLayoutHost
        {
            public IReadOnlyList<UiDetachedWindowSettings> Snapshot { get; set; } =
                Array.Empty<UiDetachedWindowSettings>();

            public List<IReadOnlyList<UiDetachedWindowSettings>> Applied { get; } = new();

            public int ReattachAllCalls { get; private set; }

            public string? ExportPayload { get; private set; }

            public WorkspaceFileOutcome ExportOutcome { get; set; } =
                WorkspaceFileOutcome.Completed;

            public WorkspaceImportResult ImportResult { get; set; } =
                new(WorkspaceFileOutcome.Cancelled, null);

            public IReadOnlyList<UiDetachedWindowSettings> CaptureDetachedTabs() =>
                Snapshot;

            public void ApplyDetachedTabs(IReadOnlyList<UiDetachedWindowSettings> detached) =>
                Applied.Add(detached);

            public void ReattachAllDetachedTabs() => ReattachAllCalls++;

            public Task<WorkspaceFileOutcome> ExportAsync(string payload)
            {
                ExportPayload = payload;
                return Task.FromResult(ExportOutcome);
            }

            public Task<WorkspaceImportResult> ImportAsync() =>
                Task.FromResult(ImportResult);
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
