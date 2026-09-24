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

                (SyncProviderKind.OneDrive, OneDriveSyncRemoteSettings oneDrive) =>
                    JsonSerializer.Serialize(
                        new OneDriveSettingsDto(
                            oneDrive.SchemaVersion,
                            oneDrive.AccountEmail,
                            oneDrive.RequestedScope,
                            oneDrive.AccountDisplayName,
                            oneDrive.AppRootFolderId),
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
            // Only implemented providers have a persisted settings model; the
            // rest (WebDAV, MEGA, unknown kinds) report the catalog's message.
            if (!_providerCatalog.IsImplemented(providerKind))
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
                    SyncProviderKind.OneDrive => ReadOneDrive(root),
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

            if (!TryReadOptionalString(root, "accountEmail", out string? accountEmail))
                return Corrupted();

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

        private static SyncRemoteProfileSettingsReadResult ReadOneDrive(JsonElement root)
        {
            if (!TryReadString(root, "requestedScope", out string requestedScope))
                return Corrupted();

            try
            {
                requestedScope = OneDriveAuthorizationScopes.ValidateRequestedScope(requestedScope);
            }
            catch (ArgumentException)
            {
                return new SyncRemoteProfileSettingsReadResult(
                    null,
                    "The saved Microsoft OneDrive authorization scope is not supported.");
            }

            if (!TryReadOptionalString(root, "accountEmail", out string? accountEmail) ||
                !TryReadOptionalString(root, "accountDisplayName", out string? accountDisplayName) ||
                !TryReadOptionalString(root, "appRootFolderId", out string? appRootFolderId))
            {
                return Corrupted();
            }

            try
            {
                return new SyncRemoteProfileSettingsReadResult(
                    new OneDriveSyncRemoteSettings(
                        accountEmail,
                        requestedScope,
                        accountDisplayName,
                        appRootFolderId),
                    null);
            }
            catch (ArgumentException)
            {
                return Corrupted();
            }
        }

        private static SyncRemoteProfileSettingsReadResult Corrupted() =>
            new(null, "The saved provider settings are unreadable or corrupted.");

        /// <summary>
        /// Reads an optional string property: absent or null yields null. Returns
        /// false only when the property is present with a non-string type, which
        /// the callers treat as corrupted settings.
        /// </summary>
        private static bool TryReadOptionalString(
            JsonElement root,
            string propertyName,
            out string? value)
        {
            value = null;

            if (!TryGetProperty(root, propertyName, out JsonElement element) ||
                element.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (element.ValueKind != JsonValueKind.String)
                return false;

            value = element.GetString();
            return true;
        }

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

        private sealed record OneDriveSettingsDto(
            int SchemaVersion,
            string? AccountEmail,
            string RequestedScope,
            string? AccountDisplayName,
            string? AppRootFolderId);
    }
}
