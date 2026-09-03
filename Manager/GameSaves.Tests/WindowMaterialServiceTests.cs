using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia.Controls;
using GameSaves.App.Services;
using Xunit;

namespace GameSaves.Tests
{
    // Real transparency behaviour is OS-dependent and cannot be faked in
    // unit tests, so these tests pin the pure decision logic (material ->
    // transparency level mapping) and the wiring that carries the decision
    // to the real windows: startup application, main-window attachment,
    // detached-window self-registration, and the Settings surface.
    public sealed class WindowMaterialServiceTests
    {
        [Fact]
        public void Materials_MapToAvaloniaTransparencyLevels()
        {
            // WindowTransparencyLevel values are static struct properties,
            // not enum constants, so the cases are spelled out.
            Assert.Equal(
                WindowTransparencyLevel.None,
                WindowMaterialService.TransparencyLevelForMaterial(
                    AppUiSettings.MaterialNone));
            Assert.Equal(
                WindowTransparencyLevel.AcrylicBlur,
                WindowMaterialService.TransparencyLevelForMaterial(
                    AppUiSettings.MaterialAcrylic));
            Assert.Equal(
                WindowTransparencyLevel.Mica,
                WindowMaterialService.TransparencyLevelForMaterial(
                    AppUiSettings.MaterialMica));
            Assert.Equal(
                WindowTransparencyLevel.None,
                WindowMaterialService.TransparencyLevelForMaterial("frosted-glass"));
            Assert.Equal(
                WindowTransparencyLevel.None,
                WindowMaterialService.TransparencyLevelForMaterial(string.Empty));
        }

        [Fact]
        public void AShownWindow_MustReportTheExactRequestedLevel()
        {
            Assert.True(WindowMaterialService.IsRequestedLevelActive(
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.AcrylicBlur,
                hasPlatformHandle: true));
            Assert.True(WindowMaterialService.IsRequestedLevelActive(
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.Mica,
                hasPlatformHandle: true));

            Assert.False(WindowMaterialService.IsRequestedLevelActive(
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                hasPlatformHandle: true));
            Assert.False(WindowMaterialService.IsRequestedLevelActive(
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.None,
                hasPlatformHandle: true));
            Assert.False(WindowMaterialService.IsRequestedLevelActive(
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.AcrylicBlur,
                hasPlatformHandle: false));
            Assert.False(WindowMaterialService.IsRequestedLevelActive(
                WindowTransparencyLevel.None,
                WindowTransparencyLevel.None,
                hasPlatformHandle: true));
        }

        [Theory]
        [InlineData(AppUiSettings.MaterialAcrylic, false, AppUiSettings.MaterialAcrylic)]
        [InlineData(AppUiSettings.MaterialMica, false, AppUiSettings.MaterialMica)]
        [InlineData(AppUiSettings.MaterialAcrylic, true, AppUiSettings.MaterialNone)]
        [InlineData(AppUiSettings.MaterialMica, true, AppUiSettings.MaterialNone)]
        [InlineData(AppUiSettings.MaterialNone, false, AppUiSettings.MaterialNone)]
        public void HighContrast_ForcesTheEffectiveMaterialToNone(
            string stored, bool highContrast, string expected)
        {
            Assert.Equal(
                expected,
                WindowMaterialService.EffectiveMaterial(
                    AppUiSettings.Default with
                    {
                        WindowMaterial = stored,
                        Accessibility = new UiAccessibilitySettings(
                            TextScale: 1.0,
                            ReduceMotion: false,
                            HighContrast: highContrast),
                    }));
        }

        [Fact]
        public void Startup_AppliesTheMaterialServiceAndAttachesTheMainWindow()
        {
            string source = File.ReadAllText(FindAppFile("App.axaml.cs"));

            // The settings are stored and handed to the service before the
            // window exists, and the created main window is attached so its
            // hint reaches the platform before it is shown.
            Assert.Contains(
                "GetRequiredService<WindowMaterialService>()",
                source);
            Assert.Contains("_windowMaterial.Apply(_uiSettings);", source);
            Assert.Contains("_windowMaterial.Attach(desktop.MainWindow);", source);
        }

        [Fact]
        public void DetachedWindows_RegisterWithTheMaterialServiceOnCreation()
        {
            string detachedTab = File.ReadAllText(
                FindAppFile(Path.Combine("Views", "DetachedWindow.axaml.cs")));
            string floatingPanel = File.ReadAllText(
                FindAppFile(Path.Combine(
                    "Views", "Workspace", "WorkspaceFloatingWindow.axaml.cs")));

            Assert.Contains("App.CurrentWindowMaterial?.Attach(this);", detachedTab);
            Assert.Contains("App.CurrentWindowMaterial?.Attach(this);", floatingPanel);
        }

        [Fact]
        public void SettingsChanges_ReachTheMaterialService()
        {
            string source = File.ReadAllText(
                FindAppFile(
                    Path.Combine("ViewModels", "SettingsViewModel.cs")));

            Assert.Contains("_windowMaterialService.Apply(settings);", source);
        }

        [Fact]
        public void TheAppearanceTab_ExposesTheMaterialChoiceAndInterlocksTheSlider()
        {
            XDocument view = XDocument.Load(
                FindAppFile(Path.Combine("Views", "SettingsView.axaml")));

            string[] groupNames = view
                .Descendants()
                .Where(element => element.Name.LocalName == "RadioButton")
                .Select(element => (string?)element.Attribute("GroupName"))
                .OfType<string>()
                .ToArray();

            Assert.Contains("SettingsWindowMaterial", groupNames);

            // Every material radio is disabled under high contrast, and the
            // window slider gives way to the material choice.
            var materialRadios = view
                .Descendants()
                .Where(element => element.Name.LocalName == "RadioButton" &&
                    (string?)element.Attribute("GroupName") == "SettingsWindowMaterial")
                .ToArray();

            Assert.Equal(3, materialRadios.Length);
            foreach (XElement radio in materialRadios)
                Assert.Equal(
                    "{Binding !HighContrast}",
                    (string?)radio.Attribute("IsEnabled"));

            XElement windowSlider = view
                .Descendants()
                .Single(element => element.Name.LocalName == "Slider" &&
                    (string?)element.Attribute("AutomationProperties.Name") ==
                        "Window transparency");

            Assert.Equal(
                "{Binding !IsWindowOpacityInert}",
                (string?)windowSlider.Attribute("IsEnabled"));
        }

        private static string FindAppFile(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                    return Path.Combine(directory.FullName, "GameSaves.App", relativePath);

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Manager.sln was not found.");
        }
    }
}
