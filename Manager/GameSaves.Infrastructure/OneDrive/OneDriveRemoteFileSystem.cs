using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;
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
        private readonly Guid _remoteProfileId;
        private readonly IOneDriveApiClient _apiClient;
        private readonly OneDriveOAuthService _oauthService;
        private readonly string? _accountEmail;

        public OneDriveRemoteFileSystem(
            Guid remoteProfileId,
            IOneDriveApiClient apiClient,
            OneDriveOAuthService oauthService,
            string? accountEmail = null)
        {
            _remoteProfileId = remoteProfileId;
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
            _accountEmail = accountEmail;
        }

        public string DisplayRoot =>
            string.IsNullOrWhiteSpace(_accountEmail)
                ? "OneDrive: AppRoot (GameSave Manager)"
                : $"OneDrive: AppRoot ({_accountEmail})";

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

            try
            {
                var appRoot = await _apiClient.GetAppRootAsync(token, cancellationToken);
                if (appRoot == null)
                {
                    return new TransferPreviewWarning(
                        "OneDriveAppRootMissing",
                        "The Microsoft OneDrive application folder (approot) could not be accessed.",
                        TransferWarningSeverity.Error);
                }

                var quota = await _apiClient.GetQuotaAsync(token, cancellationToken);
                if (quota.RemainingBytes <= 0 && quota.TotalBytes > 0)
                {
                    return new TransferPreviewWarning(
                        "OneDriveQuotaExceeded",
                        "Microsoft OneDrive storage quota is exceeded.",
                        TransferWarningSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                return new TransferPreviewWarning(
                    "OneDriveValidationFailed",
                    $"Microsoft OneDrive validation failed: {ex.Message}",
                    TransferWarningSeverity.Error);
            }

            return null;
        }

        public async Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default)
        {
            string? token = await GetTokenOrThrowAsync(cancellationToken);
            var appRoot = await _apiClient.GetAppRootAsync(token, cancellationToken);
            return appRoot != null;
        }

        public async Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            string? token = await GetTokenOrThrowAsync(cancellationToken);
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
            string? token = await GetTokenOrThrowAsync(cancellationToken);
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
            string? token = await GetTokenOrThrowAsync(cancellationToken);
            string clean = relativeFolder.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(clean))
                return await RootExistsAsync(cancellationToken);

            var item = await _apiClient.GetItemAsync(token, clean, cancellationToken);
            return item != null && item.IsFolder;
        }

        public async Task<bool> FileExistsAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string? token = await GetTokenOrThrowAsync(cancellationToken);
            string clean = relativePath.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(clean))
                return false;

            var item = await _apiClient.GetItemAsync(token, clean, cancellationToken);
            return item != null && item.IsFile;
        }

        public async Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string? token = await GetTokenOrThrowAsync(cancellationToken);
            string clean = relativePath.Replace('\\', '/').Trim('/');
            return await _apiClient.ReadTextAsync(token, clean, cancellationToken);
        }

        public async Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string? token = await GetTokenOrThrowAsync(cancellationToken);
            string clean = relativePath.Replace('\\', '/').Trim('/');

            // Strict create-only invariant: Never overwrite existing file content
            if (await FileExistsAsync(clean, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {clean}");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);
            await _apiClient.UploadContentAsync(token, clean, stream, cancellationToken);
        }

        public async Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string clean = relativePath.Replace('\\', '/').Trim('/');
            AssertAllowlistedMetadata(clean);

            string? token = await GetTokenOrThrowAsync(cancellationToken);
            return await _apiClient.ReadTextAsync(token, clean, cancellationToken);
        }

        public async Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string clean = relativePath.Replace('\\', '/').Trim('/');
            AssertAllowlistedMetadata(clean);

            string? token = await GetTokenOrThrowAsync(cancellationToken);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);
            await _apiClient.UploadContentAsync(token, clean, stream, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string? token = await GetTokenOrThrowAsync(cancellationToken);
            string cleanFolder = relativeFolder.Replace('\\', '/').Trim('/');

            var results = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(cleanFolder);

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = queue.Dequeue();

                var children = await _apiClient.ListChildrenAsync(token, current, cancellationToken);
                foreach (var child in children)
                {
                    if (child.IsFolder)
                    {
                        string subFolder = string.IsNullOrEmpty(current)
                            ? child.Name
                            : $"{current}/{child.Name}";
                        queue.Enqueue(subFolder);
                    }
                    else if (child.IsFile)
                    {
                        // The returned path must be relative to relativeFolder!
                        string fullRel = string.IsNullOrEmpty(current)
                            ? child.Name
                            : $"{current}/{child.Name}";

                        string fileRelative = string.IsNullOrEmpty(cleanFolder)
                            ? fullRel
                            : fullRel.Substring(cleanFolder.Length).TrimStart('/');

                        results.Add(fileRelative);
                    }
                }
            }

            return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(localFilePath))
                throw new FileNotFoundException($"Local file to upload not found: {localFilePath}");

            string clean = relativeRemotePath.Replace('\\', '/').Trim('/');

            // Strict create-only invariant: Never overwrite existing file content
            if (await FileExistsAsync(clean, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {clean}");
            }

            string? token = await GetTokenOrThrowAsync(cancellationToken);
            using var fileStream = File.OpenRead(localFilePath);
            var item = await _apiClient.UploadContentAsync(token, clean, fileStream, cancellationToken);
            return new FileInfo(localFilePath).Length;
        }

        public async Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            string clean = relativeRemotePath.Replace('\\', '/').Trim('/');
            string? dir = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string? token = await GetTokenOrThrowAsync(cancellationToken);
            using (var fileStream = File.Create(localFilePath))
            {
                await _apiClient.DownloadContentAsync(token, clean, fileStream, cancellationToken);
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

        private static void AssertAllowlistedMetadata(string cleanPath)
        {
            if (!cleanPath.StartsWith(".gamesave-sync/", StringComparison.OrdinalIgnoreCase) &&
                !cleanPath.Equals(".gamesave-sync", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Provider metadata path must reside under '.gamesave-sync/'. Path: '{cleanPath}'");
            }
        }
    }
}
