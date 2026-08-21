using GameSaves.App.Services;
using Xunit;

namespace GameSaves.Tests
{
    // The appearance store backs the restart-survival requirement for theme
    // choice: what Save writes, Load returns, and anything missing or
    // malformed falls back to defaults instead of failing the app at start.
    public sealed class UiSettingsStoreTests : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void AMissingFile_YieldsTheSystemDefault()
        {
            var store = new UiSettingsStore(_temp.GetPath("absent.json"));

            Assert.Equal(AppUiSettings.ThemeSystem, store.Load().ThemeChoice);
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
    }
}
