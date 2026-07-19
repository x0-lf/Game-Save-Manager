using System;
using System.IO;
using System.Text.Json;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Non-secret sync settings remembered between sessions. Passwords and
    /// passphrases are deliberately NOT part of this record and are never
    /// written to disk; they live only in the UI for the current session.
    /// </summary>
    public sealed record SyncUiSettings(
        bool UseSftp,
        string LocalFolderPath,
        string SftpHost,
        string SftpPort,
        string SftpUsername,
        bool SftpUsePrivateKey,
        string SftpKeyFilePath,
        string SftpRemotePath)
    {
        public static SyncUiSettings Default { get; } = new(
            UseSftp: false,
            LocalFolderPath: "",
            SftpHost: "",
            SftpPort: "22",
            SftpUsername: "",
            SftpUsePrivateKey: false,
            SftpKeyFilePath: "",
            SftpRemotePath: "/gamesave-sync");
    }

    public static class SyncSettingsStore
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameSave",
            "sync-settings.json");

        public static SyncUiSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return SyncUiSettings.Default;

                return JsonSerializer.Deserialize<SyncUiSettings>(
                           File.ReadAllText(FilePath))
                       ?? SyncUiSettings.Default;
            }
            catch
            {
                return SyncUiSettings.Default;
            }
        }

        public static void Save(SyncUiSettings settings)
        {
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(
                    FilePath,
                    JsonSerializer.Serialize(
                        settings,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Remembering settings is best-effort only.
            }
        }
    }
}
