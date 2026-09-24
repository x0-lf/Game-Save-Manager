using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    /// <summary>
    /// Implements IRemoteFileSystem targeting Microsoft OneDrive's dedicated
    /// application folder (drive/special/approot).
    /// Enforces strict create-only upload semantics and zero-deletion invariants.
    /// </summary>
    internal sealed class OneDriveRemoteFileSystem : IRemoteFileSystem
    {
        /// <summary>
        /// Deliberately account-free: the display root becomes the engine's remote
        /// root, which is persisted in plain transfer history.
        /// </summary>
        internal const string DisplayRootName = "OneDrive: AppRoot (GameSave Manager)";

        private readonly Guid _remoteProfileId;
        private readonly IOneDriveApiClient _apiClient;
        private readonly OneDriveOAuthService _oauthService;

        public OneDriveRemoteFileSystem(
            Guid remoteProfileId,
            IOneDriveApiClient apiClient,
            OneDriveOAuthService oauthService)
        {
            _remoteProfileId = remoteProfileId;
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
        }

        public string DisplayRoot => DisplayRootName;

        public string GetDisplayPath(string relativePath)
        {
            string clean = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            return string.IsNullOrEmpty(clean)
                ? DisplayRoot
                : $"{DisplayRoot}/{clean}";
        }

        public async Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            string? token = await _oauthService.GetValidAccessTokenAsync(_remoteProfileId, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                return new TransferPreviewWarning(
                    "OneDriveAuthRequired",
                    "Microsoft OneDrive authentication is required before syncing.",
                    TransferWarningSeverity.Error);
            }

            // No quota check: a full drive must not block download-only restores.
            // Uploads to a full drive fail per run instead.
            try
            {
                if (await _apiClient.GetItemAsync(token, "", cancellationToken) == null)
                {
                    return new TransferPreviewWarning(
                        "OneDriveAppRootMissing",
                        "The Microsoft OneDrive application folder (approot) could not be accessed.",
                        TransferWarningSeverity.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new TransferPreviewWarning(
                    "OneDriveValidationFailed",
                    "Microsoft OneDrive could not be validated. Check the connection and try again.",
                    TransferWarningSeverity.Error);
            }

            return null;
        }

        public async Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            return await _apiClient.GetItemAsync(token, "", cancellationToken) != null;
        }

        public async Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            var children = await _apiClient.ListChildrenAsync(token, "", cancellationToken);

            return children
                .Where(c => c.IsFolder)
                .Where(c => !c.Name.StartsWith(".", StringComparison.Ordinal))
                .Select(c => c.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool SupportsArchiveContainers => true;

        public async Task<IReadOnlyList<string>> ListRunArchiveNamesAsync(
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            var children = await _apiClient.ListChildrenAsync(token, "", cancellationToken);

            return children
                .Where(c => c.IsFile)
                .Where(c => c.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                            c.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            var item = await _apiClient.GetItemAsync(token, relativeFolder, cancellationToken);
            return item is { IsFolder: true };
        }

        public async Task<bool> FileExistsAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            var item = await _apiClient.GetItemAsync(token, relativePath, cancellationToken);
            return item is { IsFile: true };
        }

        public async Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            return await _apiClient.ReadTextAsync(token, relativePath, cancellationToken);
        }

        public async Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            // Create-only is enforced by Graph (conflictBehavior=fail), not by a racy existence check.
            await _apiClient.UploadContentAsync(token, relativePath, stream, createOnly: true, cancellationToken);
        }

        public async Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string path = RemoteProviderMetadataPath.Validate(relativePath);
            string token = await GetTokenOrThrowAsync(cancellationToken);
            return await _apiClient.ReadTextAsync(token, path, cancellationToken);
        }

        public async Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string path = RemoteProviderMetadataPath.Validate(relativePath);
            string token = await GetTokenOrThrowAsync(cancellationToken);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await _apiClient.UploadContentAsync(token, path, stream, createOnly: false, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);

            var results = new List<string>();
            var queue = new Queue<(string RemotePath, string RelativePath)>();
            queue.Enqueue((relativeFolder, ""));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string remotePath, string relativePath) = queue.Dequeue();

                var children = await _apiClient.ListChildrenAsync(token, remotePath, cancellationToken);
                foreach (var child in children)
                {
                    // Returned paths are relative to relativeFolder.
                    string childRelative = relativePath.Length == 0
                        ? child.Name
                        : $"{relativePath}/{child.Name}";

                    if (child.IsFolder)
                        queue.Enqueue(($"{remotePath}/{child.Name}", childRelative));
                    else if (child.IsFile)
                        results.Add(childRelative);
                }
            }

            return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            string token = await GetTokenOrThrowAsync(cancellationToken);
            using var fileStream = File.OpenRead(localFilePath);
            long length = fileStream.Length;

            // Create-only is enforced by Graph (conflictBehavior=fail), not by a racy existence check.
            await _apiClient.UploadContentAsync(token, relativeRemotePath, fileStream, createOnly: true, cancellationToken);
            return length;
        }

        public async Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            string? dir = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string token = await GetTokenOrThrowAsync(cancellationToken);

            // CreateNew: an existing local file is never truncated.
            var fileStream = new FileStream(localFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                await using (fileStream)
                {
                    await _apiClient.DownloadContentAsync(token, relativeRemotePath, fileStream, cancellationToken);
                }
            }
            catch
            {
                // Only the partial file this call created is removed, so a retry can create it again.
                File.Delete(localFilePath);
                throw;
            }

            return new FileInfo(localFilePath).Length;
        }

        private async Task<string> GetTokenOrThrowAsync(CancellationToken cancellationToken)
        {
            string? token = await _oauthService.GetValidAccessTokenAsync(_remoteProfileId, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    "OneDrive authentication token is missing or expired. Please re-authenticate the profile.");
            }
            return token;
        }
    }
}
