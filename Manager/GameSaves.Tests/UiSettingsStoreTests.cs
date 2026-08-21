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
            Assert.Equal(
                AppUiSettings.ThemeSystem,
                new UiSettingsStore(malformed).Load().ThemeChoice);

            string unknown = _temp.GetPath("unknown.json");
            File.WriteAllText(unknown, "{\"ThemeChoice\":\"neon\"}");
            Assert.Equal(
                AppUiSettings.ThemeSystem,
                new UiSettingsStore(unknown).Load().ThemeChoice);
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

        private sealed class EmptyInstalledGameStatusService : IInstalledGameSaveStatusService
        {
            public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<InstalledGameSaveStatus>>(
                    Array.Empty<InstalledGameSaveStatus>());
        }
    }
}
