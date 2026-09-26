namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Non-secret settings for a WebDAV remote (Nextcloud, ownCloud, Apache or
    /// nginx WebDAV, NAS WebDAV servers). The password or app password lives
    /// only in the protected secret store under
    /// <c>SecretKey(profileId, SecretNames.WebDavPassword)</c>, never here.
    /// </summary>
    public sealed record WebDavSyncRemoteSettings : SyncRemoteProfileSettings
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Validates and normalizes every field. Only https:// is accepted: the
        /// password travels in every request, so a plain-HTTP server would
        /// expose it to anyone on the path. The messages are shown to the user.
        /// </summary>
        public WebDavSyncRemoteSettings(
            string serverUrl,
            string username,
            string remoteFolder)
            : base(CurrentSchemaVersion)
        {
            ServerUrl = NormalizeServerUrl(serverUrl);
            Username = NormalizeUsername(username);
            RemoteFolder = NormalizeRemoteFolder(remoteFolder);
        }

        /// <summary>The server's WebDAV base URL, https, ending in '/'.</summary>
        public string ServerUrl { get; }

        public string Username { get; }

        /// <summary>
        /// The folder under <see cref="ServerUrl"/> that holds the backup runs,
        /// '/'-separated, without leading or trailing '/'.
        /// </summary>
        public string RemoteFolder { get; }

        /// <summary>
        /// The root shown in plans and stored in transfer history: the URL
        /// and folder as configured, never a credential.
        /// </summary>
        public string DisplayRoot => ServerUrl + RemoteFolder;

        public static string NormalizeServerUrl(string? serverUrl)
        {
            string trimmed = serverUrl?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
                throw new ArgumentException("Enter the WebDAV server URL first.", nameof(serverUrl));

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
                string.IsNullOrEmpty(uri.Host))
            {
                throw new ArgumentException(
                    "The WebDAV server URL is not a valid address, e.g. https://cloud.example.com/remote.php/dav/files/alice/.",
                    nameof(serverUrl));
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException(
                    "The WebDAV server URL must start with https://. Plain http would send the password unencrypted.",
                    nameof(serverUrl));
            }

            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new ArgumentException(
                    "The WebDAV server URL must not contain a user name, password, query, or fragment. Enter the user name separately.",
                    nameof(serverUrl));
            }

            string url = uri.GetLeftPart(UriPartial.Path);
            return url.EndsWith('/') ? url : url + "/";
        }

        public static string NormalizeUsername(string? username)
        {
            string trimmed = username?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
                throw new ArgumentException("Enter the WebDAV user name first.", nameof(username));

            // Basic authentication separates the user name from the password
            // with the first colon (RFC 7617), so a colon cannot be represented.
            if (trimmed.Contains(':') || trimmed.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "The WebDAV user name cannot contain a colon or control characters.",
                    nameof(username));
            }

            return trimmed;
        }

        public static string NormalizeRemoteFolder(string? remoteFolder)
        {
            string trimmed = (remoteFolder ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .Trim('/');

            if (trimmed.Length == 0)
            {
                throw new ArgumentException(
                    "Enter the WebDAV folder for the backups, e.g. GameSave Manager Backups.",
                    nameof(remoteFolder));
            }

            string[] segments = trimmed.Split('/');

            if (segments.Any(segment =>
                    segment.Trim().Length == 0 ||
                    segment is "." or ".." ||
                    segment.Any(char.IsControl)))
            {
                throw new ArgumentException(
                    "The WebDAV folder must be a plain relative path without empty, '.' or '..' parts.",
                    nameof(remoteFolder));
            }

            return string.Join('/', segments);
        }
    }
}
