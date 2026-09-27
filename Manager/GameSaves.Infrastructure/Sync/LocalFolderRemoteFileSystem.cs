using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Text;

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

        /// <summary>
        /// Free space of the volume holding the folder, or of its nearest
        /// existing parent, since the folder itself appears only with the first
        /// upload. A folder with no existing parent at all (an unplugged drive,
        /// a missing share) fails the check. Null when the volume cannot be
        /// told truthfully: a network share path, or a mount point, junction,
        /// or link on the way, where Windows would report the drive letter's
        /// volume instead of the one actually written to.
        /// </summary>
        public Task<RemoteCapacity?> GetCapacityAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run<RemoteCapacity?>(() =>
            {
                if (_normalizedRoot is null)
                    return null;

                DirectoryInfo? existing = new(_normalizedRoot);
                while (existing is not null && !existing.Exists)
                    existing = existing.Parent;

                if (existing is null)
                {
                    throw new DirectoryNotFoundException(
                        "The sync folder's drive or network share is not available.");
                }

                for (DirectoryInfo? step = existing; step?.Parent is not null; step = step.Parent)
                {
                    if (step.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return null;
                }

                if (_normalizedRoot.StartsWith(@"\\", StringComparison.Ordinal))
                    return null;

                var drive = new DriveInfo(existing.FullName);
                return new RemoteCapacity(
                    drive.AvailableFreeSpace,
                    drive.TotalSize,
                    drive.TotalSize - drive.TotalFreeSpace);
            }, cancellationToken);
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

        public bool SupportsArchiveContainers => true;

        public Task<IReadOnlyList<string>> ListRunArchiveNamesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                if (!Directory.Exists(_normalizedRoot))
                    return Array.Empty<string>();

                return Directory.EnumerateFiles(_normalizedRoot!)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name) &&
                                   (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
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

        public Task<bool> FileExistsAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(File.Exists(ToLocalPath(relativePath)));
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

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ToLocalPath(relativePath);
                string? directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                WriteUtf8File(path, content, FileMode.CreateNew);
            }, cancellationToken);
        }

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string canonicalPath = RemoteProviderMetadataPath.Validate(relativePath);

            return Task.Run<string?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ToLocalPath(canonicalPath);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }, cancellationToken);
        }

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string canonicalPath = RemoteProviderMetadataPath.Validate(relativePath);

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ToLocalPath(canonicalPath);
                string directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                string temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

                try
                {
                    WriteUtf8File(temporaryPath, content, FileMode.CreateNew);
                    cancellationToken.ThrowIfCancellationRequested();

                    // File.Move with overwrite is an atomic name replacement on
                    // supported local filesystems. Mounted/network filesystems
                    // may provide weaker atomicity guarantees.
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
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

        private static void WriteUtf8File(
            string path,
            string content,
            FileMode mode)
        {
            using var stream = new FileStream(
                path,
                mode,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        private string ToLocalPath(string relativePath)
        {
            return Path.Combine(
                _normalizedRoot ?? _rawRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
