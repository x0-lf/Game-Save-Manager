using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// IRemoteFileSystem over SFTP (SSH.NET). One connection is opened lazily
    /// and reused for the provider's lifetime; the engine's sequential calls
    /// share it. Host keys follow trust-on-first-use: an unknown key is only
    /// accepted when the user explicitly opted in, a stored key must match
    /// exactly, and a changed key always fails loudly.
    /// </summary>
    internal sealed class SftpRemoteFileSystem : IRemoteFileSystem, IDisposable
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

        private readonly SftpConnectionSettings _settings;
        private readonly SftpKnownHostsStore _knownHosts;
        private readonly string _rootPath;

        private SftpClient? _client;
        private TransferPreviewWarning? _hostKeyIssue;

        public SftpRemoteFileSystem(
            SftpConnectionSettings settings,
            SftpKnownHostsStore knownHosts)
        {
            _settings = settings;
            _knownHosts = knownHosts;
            _rootPath = NormalizeRemotePath(settings.RemotePath);
        }

        public string DisplayRoot => _settings.DisplayRoot;

        public string GetDisplayPath(string relativePath)
        {
            return $"{DisplayRoot.TrimEnd('/')}/{relativePath}";
        }

        // ---------------------------------------------------------------
        // Validation and connection
        // ---------------------------------------------------------------

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                TransferPreviewWarning? settingsIssue = ValidateSettings();

                if (settingsIssue is not null)
                    return settingsIssue;

                try
                {
                    EnsureConnected();
                    return null;
                }
                catch (Exception ex)
                {
                    return MapConnectionFailure(ex);
                }
            }, cancellationToken);
        }

        private TransferPreviewWarning? ValidateSettings()
        {
            if (string.IsNullOrWhiteSpace(_settings.Host))
                return SettingsInvalid("Enter the SFTP server host name or IP address.");

            if (_settings.Port is < 1 or > 65535)
                return SettingsInvalid("The SFTP port must be between 1 and 65535.");

            if (string.IsNullOrWhiteSpace(_settings.Username))
                return SettingsInvalid("Enter the SFTP username.");

            if (string.IsNullOrWhiteSpace(_settings.RemotePath))
                return SettingsInvalid("Enter the remote folder path (for example /srv/gamesave-sync).");

            if (_settings.AuthMethod == SftpAuthMethod.Password &&
                string.IsNullOrEmpty(_settings.Password))
            {
                return SettingsInvalid("Enter the SFTP password (it is never stored).");
            }

            if (_settings.AuthMethod == SftpAuthMethod.PrivateKey)
            {
                if (string.IsNullOrWhiteSpace(_settings.PrivateKeyPath))
                    return SettingsInvalid("Choose a private key file.");

                if (!File.Exists(_settings.PrivateKeyPath))
                    return SettingsInvalid($"The private key file does not exist: {_settings.PrivateKeyPath}");
            }

            return null;
        }

        private static TransferPreviewWarning SettingsInvalid(string message)
        {
            return new TransferPreviewWarning(
                "SftpSettingsInvalid",
                message,
                TransferWarningSeverity.Error);
        }

        private TransferPreviewWarning MapConnectionFailure(Exception ex)
        {
            if (_hostKeyIssue is not null)
                return _hostKeyIssue;

            return ex switch
            {
                SshAuthenticationException => new TransferPreviewWarning(
                    "SftpAuthFailed",
                    $"The SFTP server rejected the credentials: {ex.Message}",
                    TransferWarningSeverity.Error),

                SshPassPhraseNullOrEmptyException => new TransferPreviewWarning(
                    "SftpSettingsInvalid",
                    "The private key is passphrase-protected; enter the passphrase (it is never stored).",
                    TransferWarningSeverity.Error),

                SocketException or SshOperationTimeoutException or SshConnectionException => new TransferPreviewWarning(
                    "SftpConnectFailed",
                    $"Could not connect to {_settings.Host}:{_settings.Port}: {ex.Message}",
                    TransferWarningSeverity.Error),

                _ => new TransferPreviewWarning(
                    "SftpConnectFailed",
                    $"Connecting to the SFTP server failed: {ex.Message}",
                    TransferWarningSeverity.Error)
            };
        }

        private SftpClient EnsureConnected()
        {
            if (_client is { IsConnected: true })
                return _client;

            _client?.Dispose();
            _client = null;
            _hostKeyIssue = null;

            var connectionInfo = new ConnectionInfo(
                _settings.Host.Trim(),
                _settings.Port,
                _settings.Username.Trim(),
                BuildAuthenticationMethod())
            {
                Timeout = ConnectTimeout
            };

            var client = new SftpClient(connectionInfo);

            string? fingerprintToStore = null;

            client.HostKeyReceived += (_, e) =>
            {
                string fingerprint = "SHA256:" +
                    Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');

                string? stored = _knownHosts.GetFingerprint(_settings.Host.Trim(), _settings.Port);

                if (stored is null)
                {
                    if (_settings.TrustNewHostKey)
                    {
                        fingerprintToStore = fingerprint;
                        e.CanTrust = true;
                        return;
                    }

                    _hostKeyIssue = new TransferPreviewWarning(
                        "SftpHostKeyUnknown",
                        $"First connection to {_settings.Host}:{_settings.Port}. Its host key fingerprint is {fingerprint}. Verify it against the server, then enable \"Trust this server's host key on first connect\" and try again.",
                        TransferWarningSeverity.Error);

                    e.CanTrust = false;
                    return;
                }

                if (stored.Equals(fingerprint, StringComparison.Ordinal))
                {
                    e.CanTrust = true;
                    return;
                }

                _hostKeyIssue = new TransferPreviewWarning(
                    "SftpHostKeyChanged",
                    $"THE HOST KEY OF {_settings.Host}:{_settings.Port} HAS CHANGED. Stored: {stored}. Presented: {fingerprint}. This can mean a server reinstall - or a man-in-the-middle attack. If the change is expected, use \"Forget Stored Host Key\" and connect again.",
                    TransferWarningSeverity.Error);

                e.CanTrust = false;
            };

            client.Connect();

            if (fingerprintToStore is not null)
                _knownHosts.SaveFingerprint(_settings.Host.Trim(), _settings.Port, fingerprintToStore);

            _client = client;
            return client;
        }

        private AuthenticationMethod BuildAuthenticationMethod()
        {
            if (_settings.AuthMethod == SftpAuthMethod.PrivateKey)
            {
                PrivateKeyFile keyFile = string.IsNullOrEmpty(_settings.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(_settings.PrivateKeyPath!)
                    : new PrivateKeyFile(_settings.PrivateKeyPath!, _settings.PrivateKeyPassphrase);

                return new PrivateKeyAuthenticationMethod(
                    _settings.Username.Trim(),
                    keyFile);
            }

            return new PasswordAuthenticationMethod(
                _settings.Username.Trim(),
                _settings.Password ?? string.Empty);
        }

        // ---------------------------------------------------------------
        // Primitives
        // ---------------------------------------------------------------

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => EnsureConnected().Exists(_rootPath), cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                SftpClient client = EnsureConnected();

                if (!client.Exists(_rootPath))
                    return Array.Empty<string>();

                return client.ListDirectory(_rootPath)
                    .Where(entry => entry.IsDirectory &&
                                    entry.Name != "." &&
                                    entry.Name != "..")
                    .Select(entry => entry.Name)
                    .ToList();
            }, cancellationToken);
        }

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => EnsureConnected().Exists(ToRemotePath(relativeFolder)),
                cancellationToken);
        }

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<string?>(() =>
            {
                SftpClient client = EnsureConnected();
                string path = ToRemotePath(relativePath);

                return client.Exists(path) ? client.ReadAllText(path) : null;
            }, cancellationToken);
        }

        public Task WriteTextFileAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                SftpClient client = EnsureConnected();
                string path = ToRemotePath(relativePath);

                EnsureRemoteDirectories(client, GetRemoteParent(path));
                client.WriteAllText(path, content);
            }, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                SftpClient client = EnsureConnected();
                string folder = ToRemotePath(relativeFolder);

                if (!client.Exists(folder))
                    return Array.Empty<string>();

                var files = new List<string>();
                CollectFiles(client, folder, prefix: "", files);
                return files;
            }, cancellationToken);
        }

        private static void CollectFiles(
            SftpClient client,
            string folder,
            string prefix,
            List<string> files)
        {
            foreach (var entry in client.ListDirectory(folder))
            {
                if (entry.Name is "." or "..")
                    continue;

                string relative = string.IsNullOrEmpty(prefix)
                    ? entry.Name
                    : $"{prefix}/{entry.Name}";

                if (entry.IsDirectory)
                    CollectFiles(client, $"{folder}/{entry.Name}", relative, files);
                else
                    files.Add(relative);
            }
        }

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                SftpClient client = EnsureConnected();
                string remotePath = ToRemotePath(relativeRemotePath);

                if (client.Exists(remotePath))
                    throw new IOException($"The remote file already exists and is never overwritten: {remotePath}");

                EnsureRemoteDirectories(client, GetRemoteParent(remotePath));

                using FileStream stream = File.OpenRead(localFilePath);
                client.UploadFile(stream, remotePath);

                return new FileInfo(localFilePath).Length;
            }, cancellationToken);
        }

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                SftpClient client = EnsureConnected();
                string remotePath = ToRemotePath(relativeRemotePath);

                string? localDirectory = Path.GetDirectoryName(localFilePath);

                if (!string.IsNullOrWhiteSpace(localDirectory))
                    Directory.CreateDirectory(localDirectory);

                using (FileStream stream = File.Create(localFilePath))
                {
                    client.DownloadFile(remotePath, stream);
                }

                return new FileInfo(localFilePath).Length;
            }, cancellationToken);
        }

        // ---------------------------------------------------------------
        // Path helpers
        // ---------------------------------------------------------------

        private string ToRemotePath(string relativePath)
        {
            return _rootPath == "/"
                ? $"/{relativePath}"
                : $"{_rootPath}/{relativePath}";
        }

        private static string GetRemoteParent(string remotePath)
        {
            int lastSlash = remotePath.LastIndexOf('/');

            return lastSlash <= 0 ? "/" : remotePath[..lastSlash];
        }

        private static void EnsureRemoteDirectories(SftpClient client, string remoteFolder)
        {
            if (remoteFolder == "/" || client.Exists(remoteFolder))
                return;

            string current = "";

            foreach (string segment in remoteFolder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + segment;

                if (!client.Exists(current))
                    client.CreateDirectory(current);
            }
        }

        private static string NormalizeRemotePath(string remotePath)
        {
            string trimmed = remotePath.Trim().Replace('\\', '/').TrimEnd('/');

            if (string.IsNullOrEmpty(trimmed))
                return "/";

            return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        }

        public void Dispose()
        {
            try
            {
                if (_client is { IsConnected: true })
                    _client.Disconnect();
            }
            catch
            {
                // Disconnect failures never matter during disposal.
            }

            _client?.Dispose();
            _client = null;
        }
    }
}
