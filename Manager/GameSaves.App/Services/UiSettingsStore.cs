using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Non-secret UI settings remembered between sessions. Follows
    /// the same forgiving-load pattern as <see cref="SyncSettingsStore"/>:
    /// a missing or malformed file yields defaults rather than an error.
    /// </summary>
    public sealed record AppUiSettings(
        int SchemaVersion,
        string ThemeChoice,
        IReadOnlyList<string> InstalledGameColumnOrder,
        IReadOnlyList<string> HiddenInstalledGameColumns)
    {
        public const int CurrentSchemaVersion = 2;

        public const string ThemeSystem = "system";
        public const string ThemeLight = "light";
        public const string ThemeDark = "dark";

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
            InstalledGameColumnOrder: DefaultInstalledGameColumnOrder,
            HiddenInstalledGameColumns: Array.Empty<string>());

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

                IReadOnlyList<string> order = ReadInstalledGameColumns(
                    document.RootElement,
                    nameof(AppUiSettings.InstalledGameColumnOrder),
                    appendMissing: true);
                IReadOnlyList<string> hidden = ReadInstalledGameColumns(
                    document.RootElement,
                    nameof(AppUiSettings.HiddenInstalledGameColumns),
                    appendMissing: false);

                return new AppUiSettings(
                    SchemaVersion: AppUiSettings.CurrentSchemaVersion,
                    ThemeChoice: theme,
                    InstalledGameColumnOrder: order,
                    HiddenInstalledGameColumns: hidden);
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

        private static IReadOnlyList<string> ReadInstalledGameColumns(
            JsonElement root,
            string propertyName,
            bool appendMissing)
        {
            var values = new List<string>();

            if (root.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is string value)
                        values.Add(value);
                }
            }

            return appendMissing
                ? AppUiSettings.NormalizeInstalledGameColumnOrder(values)
                : AppUiSettings.NormalizeHiddenInstalledGameColumns(values);
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
