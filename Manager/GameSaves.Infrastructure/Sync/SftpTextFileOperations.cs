using Renci.SshNet;
using Renci.SshNet.Common;
using System.Text;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Narrow testable boundary around the SSH.NET file operations required by
    /// Milestone M. It deliberately excludes directory traversal and backup
    /// deletion operations.
    /// </summary>
    internal interface ISftpTextFileClient
    {
        bool Exists(string path);

        Stream Open(string path, FileMode mode, FileAccess access);

        string ReadAllText(string path);

        void WriteAllText(string path, string content);

        void RenameFile(string oldPath, string newPath, bool isPosix);

        void DeleteFile(string path);
    }

    internal sealed class SftpTextFileClient : ISftpTextFileClient
    {
        private readonly SftpClient _client;

        public SftpTextFileClient(SftpClient client) => _client = client;

        public bool Exists(string path) => _client.Exists(path);

        public Stream Open(string path, FileMode mode, FileAccess access) =>
            _client.Open(path, mode, access);

        public string ReadAllText(string path) => _client.ReadAllText(path);

        public void WriteAllText(string path, string content) =>
            _client.WriteAllText(
                path,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        public void RenameFile(string oldPath, string newPath, bool isPosix) =>
            _client.RenameFile(oldPath, newPath, isPosix);

        public void DeleteFile(string path) => _client.DeleteFile(path);
    }

    internal sealed class SftpTextFileOperations
    {
        private readonly ISftpTextFileClient _client;

        public SftpTextFileOperations(ISftpTextFileClient client) => _client = client;

        public void CreateTextFileIfMissing(
            string remotePath,
            string content,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // SSH.NET maps FileMode.CreateNew to SSH_FXF_CREAT | SSH_FXF_EXCL,
            // providing the strongest exclusive-create behavior supported by
            // the installed library and server.
            using Stream stream = _client.Open(
                remotePath,
                FileMode.CreateNew,
                FileAccess.Write);
            WriteUtf8(stream, content);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public string? ReadProviderMetadata(
            string remotePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _client.Exists(remotePath)
                ? _client.ReadAllText(remotePath)
                : null;
        }

        public void ReplaceProviderMetadata(
            string remotePath,
            string content,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string parent = remotePath[..remotePath.LastIndexOf('/')];
            string temporaryPath =
                $"{parent}/.sync-log.json.tmp-{Guid.NewGuid():N}";

            try
            {
                using (Stream stream = _client.Open(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write))
                {
                    WriteUtf8(stream, content);
                }

                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // The OpenSSH posix-rename extension replaces the final
                    // metadata name atomically when the server supports it.
                    _client.RenameFile(temporaryPath, remotePath, isPosix: true);
                }
                catch (SshException)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Some SFTP servers do not implement POSIX replacement.
                    // The explicit fallback may be non-atomic, but it is
                    // restricted to validated provider metadata and never
                    // targets immutable backup-run content.
                    _client.WriteAllText(remotePath, content);
                }
            }
            finally
            {
                if (_client.Exists(temporaryPath))
                    _client.DeleteFile(temporaryPath);
            }
        }

        private static void WriteUtf8(Stream stream, string content)
        {
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);
            writer.Write(content);
            writer.Flush();
            stream.Flush();
        }
    }
}
