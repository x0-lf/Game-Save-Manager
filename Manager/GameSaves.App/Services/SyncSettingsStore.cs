using GameSaves.Core.Sync;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Non-secret sync settings remembered between sessions. Passwords and
    /// passphrases are deliberately absent and remain session-only UI state.
    /// </summary>
    public sealed record SyncUiSettings(
        int SchemaVersion,
        SyncProviderKind SelectedProviderKind,
        string LocalFolderPath,
        string SftpHost,
        string SftpPort,
        string SftpUsername,
        bool SftpUsePrivateKey,
        string SftpKeyFilePath,
        string SftpRemotePath,
        Guid? SelectedRemoteProfileId = null,
        bool LegacyProfileMigrationCompleted = false)
    {
        public const int CurrentSchemaVersion = 2;

        public static SyncUiSettings Default { get; } = new(
            SchemaVersion: CurrentSchemaVersion,
            SelectedProviderKind: SyncProviderKind.LocalFolder,
            LocalFolderPath: "",
            SftpHost: "",
            SftpPort: "22",
            SftpUsername: "",
            SftpUsePrivateKey: false,
            SftpKeyFilePath: "",
            SftpRemotePath: "/gamesave-sync",
            SelectedRemoteProfileId: null,
            LegacyProfileMigrationCompleted: true);
    }

    public interface ISyncSettingsStore
    {
        SyncUiSettings Load();

        void Save(SyncUiSettings settings);
    }

    /// <summary>
    /// JSON persistence for non-secret Sync UI settings. Loading supports the
    /// pre-schema UseSftp Boolean without rewriting the file. A path can be
    /// supplied by callers such as tests; the default remains LocalAppData.
    /// </summary>
    public sealed class SyncSettingsStore : ISyncSettingsStore
    {
        private readonly string _filePath;

        public SyncSettingsStore()
            : this(GetDefaultFilePath())
        {
        }

        public SyncSettingsStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A sync settings path is required.", nameof(filePath));

            _filePath = filePath;
        }

        public SyncUiSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return SyncUiSettings.Default;

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_filePath));

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return SyncUiSettings.Default;

                JsonElement root = document.RootElement;
                SyncProviderKind providerKind = ReadProviderKind(root);

                int schemaVersion = ReadInt32(
                        root,
                        nameof(SyncUiSettings.SchemaVersion),
                        defaultValue: 0);

                return new SyncUiSettings(
                    SchemaVersion: schemaVersion,
                    SelectedProviderKind: providerKind,
                    LocalFolderPath: ReadString(
                        root,
                        nameof(SyncUiSettings.LocalFolderPath),
                        SyncUiSettings.Default.LocalFolderPath),
                    SftpHost: ReadString(
                        root,
                        nameof(SyncUiSettings.SftpHost),
                        SyncUiSettings.Default.SftpHost),
                    SftpPort: ReadString(
                        root,
                        nameof(SyncUiSettings.SftpPort),
                        SyncUiSettings.Default.SftpPort),
                    SftpUsername: ReadString(
                        root,
                        nameof(SyncUiSettings.SftpUsername),
                        SyncUiSettings.Default.SftpUsername),
                    SftpUsePrivateKey: ReadBoolean(
                        root,
                        nameof(SyncUiSettings.SftpUsePrivateKey),
                        SyncUiSettings.Default.SftpUsePrivateKey),
                    SftpKeyFilePath: ReadString(
                        root,
                        nameof(SyncUiSettings.SftpKeyFilePath),
                        SyncUiSettings.Default.SftpKeyFilePath),
                    SftpRemotePath: ReadString(
                        root,
                        nameof(SyncUiSettings.SftpRemotePath),
                        SyncUiSettings.Default.SftpRemotePath),
                    SelectedRemoteProfileId: ReadGuid(
                        root,
                        nameof(SyncUiSettings.SelectedRemoteProfileId)),
                    LegacyProfileMigrationCompleted: ReadBoolean(
                        root,
                        nameof(SyncUiSettings.LegacyProfileMigrationCompleted),
                        defaultValue: schemaVersion >= SyncUiSettings.CurrentSchemaVersion));
            }
            catch
            {
                // Malformed or unreadable settings are non-fatal and are not
                // rewritten merely because the application started.
                return SyncUiSettings.Default;
            }
        }

        public void Save(SyncUiSettings settings)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_filePath);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string temporaryPath = _filePath + ".tmp";
                string json = JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            catch
            {
                // Remembering non-secret settings is best-effort only.
            }
        }

        private static string GetDefaultFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameSave",
                "sync-settings.json");
        }

        private static SyncProviderKind ReadProviderKind(JsonElement root)
        {
            if (TryGetProperty(root, nameof(SyncUiSettings.SelectedProviderKind), out JsonElement selected))
            {
                if (selected.ValueKind == JsonValueKind.Number &&
                    selected.TryGetInt32(out int numeric))
                {
                    // Unknown numeric values are intentionally preserved so an
                    // older build never silently selects a different remote.
                    return (SyncProviderKind)numeric;
                }

                if (selected.ValueKind == JsonValueKind.String)
                {
                    string? value = selected.GetString();

                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                        return (SyncProviderKind)numeric;

                    if (Enum.TryParse(value, ignoreCase: true, out SyncProviderKind parsed))
                        return parsed;

                    return SyncProviderKind.Unknown;
                }

                return SyncProviderKind.Unknown;
            }

            // Legacy schema: false meant local folder; true meant SFTP.
            return ReadBoolean(root, "UseSftp", defaultValue: false)
                ? SyncProviderKind.Sftp
                : SyncProviderKind.LocalFolder;
        }

        private static string ReadString(
            JsonElement root,
            string propertyName,
            string defaultValue)
        {
            if (!TryGetProperty(root, propertyName, out JsonElement value))
                return defaultValue;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? defaultValue,
                JsonValueKind.Number => value.GetRawText(),
                _ => defaultValue
            };
        }

        private static int ReadInt32(
            JsonElement root,
            string propertyName,
            int defaultValue)
        {
            if (!TryGetProperty(root, propertyName, out JsonElement value))
                return defaultValue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;

            return value.ValueKind == JsonValueKind.String &&
                   int.TryParse(
                       value.GetString(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out number)
                ? number
                : defaultValue;
        }

        private static bool ReadBoolean(
            JsonElement root,
            string propertyName,
            bool defaultValue)
        {
            if (!TryGetProperty(root, propertyName, out JsonElement value))
                return defaultValue;

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();

            return value.ValueKind == JsonValueKind.String &&
                   bool.TryParse(value.GetString(), out bool parsed)
                ? parsed
                : defaultValue;
        }

        private static Guid? ReadGuid(JsonElement root, string propertyName)
        {
            if (!TryGetProperty(root, propertyName, out JsonElement value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(value.GetString(), out Guid parsed)
                ? parsed
                : null;
        }

        private static bool TryGetProperty(
            JsonElement root,
            string propertyName,
            out JsonElement value)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
