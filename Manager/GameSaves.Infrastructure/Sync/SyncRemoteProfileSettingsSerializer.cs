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
        private readonly ISyncProviderCatalog _providerCatalog;

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public SyncRemoteProfileSettingsSerializer()
            : this(new SyncProviderCatalog())
        {
        }

        public SyncRemoteProfileSettingsSerializer(ISyncProviderCatalog providerCatalog)
        {
            _providerCatalog = providerCatalog;
        }

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

                (SyncProviderKind.GoogleDrive, GoogleDriveSyncRemoteSettings googleDrive) =>
                    JsonSerializer.Serialize(
                        new GoogleDriveSettingsDto(
                            googleDrive.SchemaVersion,
                            googleDrive.AccountEmail,
                            googleDrive.RequestedScope),
                        Options),

                _ => throw new ArgumentException(
                    "The provider settings do not match a supported persisted settings model.",
                    nameof(settings))
            };
        }

        public SyncRemoteProfileSettingsReadResult Deserialize(
            SyncProviderKind providerKind,
            int providerSettingsVersion,
            string json)
        {
            if (providerKind is not SyncProviderKind.LocalFolder and
                not SyncProviderKind.Sftp and
                not SyncProviderKind.GoogleDrive)
            {
                return new SyncRemoteProfileSettingsReadResult(
                    null,
                    _providerCatalog.GetDescriptor(providerKind).UnavailableMessage ??
                    "The saved sync provider is unavailable.");
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
                    SyncProviderKind.GoogleDrive => ReadGoogleDrive(root),
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

        private static SyncRemoteProfileSettingsReadResult ReadGoogleDrive(JsonElement root)
        {
            if (!TryReadString(root, "requestedScope", out string requestedScope))
                return Corrupted();

            if (!string.Equals(
                    requestedScope,
                    GoogleDriveAuthorizationScopes.DriveFile,
                    StringComparison.Ordinal))
            {
                return new SyncRemoteProfileSettingsReadResult(
                    null,
                    "The saved Google Drive authorization scope is not supported.");
            }

            string? accountEmail = null;

            if (TryGetProperty(root, "accountEmail", out JsonElement emailElement))
            {
                if (emailElement.ValueKind == JsonValueKind.String)
                    accountEmail = emailElement.GetString();
                else if (emailElement.ValueKind != JsonValueKind.Null)
                    return Corrupted();
            }

            try
            {
                return new SyncRemoteProfileSettingsReadResult(
                    new GoogleDriveSyncRemoteSettings(accountEmail, requestedScope),
                    null);
            }
            catch (ArgumentException)
            {
                return Corrupted();
            }
        }

        private static SyncRemoteProfileSettingsReadResult Corrupted() =>
            new(null, "The saved provider settings are unreadable or corrupted.");

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

        private sealed record GoogleDriveSettingsDto(
            int SchemaVersion,
            string? AccountEmail,
            string RequestedScope);
    }
}
