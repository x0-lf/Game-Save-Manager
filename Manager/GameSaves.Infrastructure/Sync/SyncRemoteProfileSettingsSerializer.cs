using GameSaves.Core.Sync;
using System.Globalization;
using System.Text.Json;

namespace GameSaves.Infrastructure.Sync
{
    public sealed record SyncRemoteProfileSettingsReadResult(
        SyncRemoteProfileSettings? Settings,
        string? Error);

    /// <summary>
    /// Explicit allowlist serializer for persisted provider configuration.
    /// Secret-bearing connection and view-model types are never serialized.
    /// </summary>
    public sealed class SyncRemoteProfileSettingsSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public string Serialize(
            SyncProviderKind providerKind,
            SyncRemoteProfileSettings settings)
        {
            return (providerKind, settings) switch
            {
                (SyncProviderKind.LocalFolder, LocalFolderSyncRemoteSettings local) =>
                    JsonSerializer.Serialize(
                        new LocalFolderSettingsDto(
                            local.SchemaVersion,
                            local.LocalFolderPath),
                        Options),

                (SyncProviderKind.Sftp, SftpSyncRemoteSettings sftp) =>
                    JsonSerializer.Serialize(
                        new SftpSettingsDto(
                            sftp.SchemaVersion,
                            sftp.Host,
                            sftp.Port,
                            sftp.Username,
                            (int)sftp.AuthenticationMethod,
                            sftp.PrivateKeyFilePath,
                            sftp.RemotePath),
                        Options),

                _ => throw new ArgumentException(
                    "The provider settings do not match an implemented provider.",
                    nameof(settings))
            };
        }

        public SyncRemoteProfileSettingsReadResult Deserialize(
            SyncProviderKind providerKind,
            int providerSettingsVersion,
            string json)
        {
            if (providerKind is not SyncProviderKind.LocalFolder and not SyncProviderKind.Sftp)
            {
                return new SyncRemoteProfileSettingsReadResult(
                    null,
                    GetUnavailableProviderMessage(providerKind));
            }

            if (providerSettingsVersion != 1)
            {
                return new SyncRemoteProfileSettingsReadResult(
                    null,
                    $"Provider settings version {providerSettingsVersion} is not supported.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Corrupted();

                JsonElement root = document.RootElement;

                if (!TryReadInt32(root, "schemaVersion", out int schemaVersion) ||
                    schemaVersion != providerSettingsVersion)
                {
                    return Corrupted();
                }

                return providerKind switch
                {
                    SyncProviderKind.LocalFolder => ReadLocalFolder(root),
                    SyncProviderKind.Sftp => ReadSftp(root),
                    _ => Corrupted()
                };
            }
            catch (JsonException)
            {
                return Corrupted();
            }
        }

        private static SyncRemoteProfileSettingsReadResult ReadLocalFolder(JsonElement root)
        {
            if (!TryReadString(root, "localFolderPath", out string path))
                return Corrupted();

            return new SyncRemoteProfileSettingsReadResult(
                new LocalFolderSyncRemoteSettings(path),
                null);
        }

        private static SyncRemoteProfileSettingsReadResult ReadSftp(JsonElement root)
        {
            if (!TryReadString(root, "host", out string host) ||
                !TryReadInt32(root, "port", out int port) ||
                !TryReadString(root, "username", out string username) ||
                !TryReadInt32(root, "authenticationMethod", out int authenticationMethod) ||
                !TryReadString(root, "remotePath", out string remotePath) ||
                port is < 1 or > 65535 ||
                !Enum.IsDefined(typeof(SftpAuthMethod), authenticationMethod))
            {
                return Corrupted();
            }

            string? privateKeyFilePath = TryGetProperty(
                root,
                "privateKeyFilePath",
                out JsonElement keyPath) &&
                keyPath.ValueKind == JsonValueKind.String
                    ? keyPath.GetString()
                    : null;

            return new SyncRemoteProfileSettingsReadResult(
                new SftpSyncRemoteSettings(
                    host,
                    port,
                    username,
                    (SftpAuthMethod)authenticationMethod,
                    privateKeyFilePath,
                    remotePath),
                null);
        }

        private static SyncRemoteProfileSettingsReadResult Corrupted() =>
            new(null, "The saved provider settings are unreadable or corrupted.");

        private static string GetUnavailableProviderMessage(SyncProviderKind kind) =>
            kind switch
            {
                SyncProviderKind.GoogleDrive => "Google Drive sync is not implemented yet.",
                SyncProviderKind.WebDav => "WebDAV sync is not implemented yet.",
                SyncProviderKind.OneDrive => "OneDrive sync is not implemented yet.",
                _ => $"Sync provider value {(int)kind} is not supported by this version."
            };

        private static bool TryReadString(
            JsonElement root,
            string propertyName,
            out string value)
        {
            if (TryGetProperty(root, propertyName, out JsonElement element) &&
                element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString() ?? string.Empty;
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryReadInt32(
            JsonElement root,
            string propertyName,
            out int value)
        {
            if (TryGetProperty(root, propertyName, out JsonElement element))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
                    return true;

                if (element.ValueKind == JsonValueKind.String &&
                    int.TryParse(
                        element.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
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

        private sealed record LocalFolderSettingsDto(
            int SchemaVersion,
            string LocalFolderPath);

        private sealed record SftpSettingsDto(
            int SchemaVersion,
            string Host,
            int Port,
            string Username,
            int AuthenticationMethod,
            string? PrivateKeyFilePath,
            string RemotePath);
    }
}
