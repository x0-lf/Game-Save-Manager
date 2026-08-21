using System;
using System.IO;
using System.Text.Json;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Non-secret appearance settings remembered between sessions. Follows
    /// the same forgiving-load pattern as <see cref="SyncSettingsStore"/>:
    /// a missing or malformed file yields defaults rather than an error.
    /// </summary>
    public sealed record AppUiSettings(
        int SchemaVersion,
        string ThemeChoice)
    {
        public const int CurrentSchemaVersion = 1;

        public const string ThemeSystem = "system";
        public const string ThemeLight = "light";
        public const string ThemeDark = "dark";

        public static AppUiSettings Default { get; } = new(
            SchemaVersion: CurrentSchemaVersion,
            ThemeChoice: ThemeSystem);
    }

    public interface IUiSettingsStore
    {
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

                return AppUiSettings.Default with { ThemeChoice = theme };
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

        private static string GetDefaultFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameSave",
                "ui-settings.json");
        }
    }
}
