using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// IRemoteFileSystem over a local or mounted folder (NAS share, USB drive,
    /// cloud-synced folder). Slow operations run on the thread pool so callers
    /// can await them from the UI thread.
    /// </summary>
    internal sealed class LocalFolderRemoteFileSystem : IRemoteFileSystem
    {
        private readonly string _rawRoot;
        private readonly string? _normalizedRoot;
        private readonly string _localBackupBasePath;

        public LocalFolderRemoteFileSystem(string remoteRoot, string localBackupBasePath)
        {
            _rawRoot = remoteRoot;
            _normalizedRoot = TransferPathGuard.TryNormalize(remoteRoot);
            _localBackupBasePath = localBackupBasePath;
        }

        public string DisplayRoot => _normalizedRoot ?? _rawRoot;

        public string GetDisplayPath(string relativePath)
        {
            return ToLocalPath(relativePath);
        }

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            if (_normalizedRoot is null)
            {
                return Task.FromResult<TransferPreviewWarning?>(new TransferPreviewWarning(
                    "RemoteInvalid",
                    "The sync folder is empty or not a valid folder path.",
                    TransferWarningSeverity.Error));
            }

            if (TransferPathGuard.IsUnderRoot(_normalizedRoot, _localBackupBasePath) ||
                TransferPathGuard.IsUnderRoot(_localBackupBasePath, _normalizedRoot))
            {
                return Task.FromResult<TransferPreviewWarning?>(new TransferPreviewWarning(
                    "RemoteOverlapsLocal",
                    "The sync folder overlaps the local backup base. Choose a folder outside it.",
                    TransferWarningSeverity.Error));
            }

            return Task.FromResult<TransferPreviewWarning?>(null);
        }

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Directory.Exists(_normalizedRoot));
        }

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                if (!Directory.Exists(_normalizedRoot))
                    return Array.Empty<string>();

                return Directory.EnumerateDirectories(_normalizedRoot!)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .ToList();
            }, cancellationToken);
        }

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Directory.Exists(ToLocalPath(relativeFolder)));
        }

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<string?>(() =>
            {
                string path = ToLocalPath(relativePath);

                return File.Exists(path) ? File.ReadAllText(path) : null;
            }, cancellationToken);
        }

        public Task WriteTextFileAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                string path = ToLocalPath(relativePath);
                string? directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(path, content);
            }, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                string folder = ToLocalPath(relativeFolder);

                if (!Directory.Exists(folder))
                    return Array.Empty<string>();

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                return Directory.EnumerateFiles(folder, "*", options)
                    .Select(file => Path.GetRelativePath(folder, file)
                        .Replace(Path.DirectorySeparatorChar, '/'))
                    .ToList();
            }, cancellationToken);
        }

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => CopyFile(localFilePath, ToLocalPath(relativeRemotePath)),
                cancellationToken);
        }

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => CopyFile(ToLocalPath(relativeRemotePath), localFilePath),
                cancellationToken);
        }

        private static long CopyFile(string sourceFile, string targetFile)
        {
            string? targetDirectory = Path.GetDirectoryName(targetFile);

            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            File.Copy(sourceFile, targetFile, overwrite: false);

            File.SetCreationTimeUtc(targetFile, File.GetCreationTimeUtc(sourceFile));
            File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(sourceFile));

            return new FileInfo(targetFile).Length;
        }

        private string ToLocalPath(string relativePath)
        {
            return Path.Combine(
                _normalizedRoot ?? _rawRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
