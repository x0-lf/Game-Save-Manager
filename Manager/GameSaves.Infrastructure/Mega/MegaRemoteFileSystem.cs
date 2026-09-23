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

namespace GameSaves.Infrastructure.Mega
{
    /// <summary>
    /// Implements IRemoteFileSystem targeting a dedicated folder inside the user's
    /// personal MEGA cloud drive (default: "GameSave Manager Backups").
    /// Enforces strict create-only upload semantics and zero-deletion invariants.
    /// </summary>
    internal sealed class MegaRemoteFileSystem : IRemoteFileSystem
    {
        private readonly Guid _remoteProfileId;
        private readonly IMegaApiClient _apiClient;
        private readonly IMegaSessionService _sessionService;
        private readonly string? _accountEmail;
        private readonly string _rootFolderName;

        public MegaRemoteFileSystem(
            Guid remoteProfileId,
            IMegaApiClient apiClient,
            IMegaSessionService sessionService,
            string? accountEmail = null,
            string? rootFolderName = null)
        {
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException("A saved remote profile ID is required.", nameof(remoteProfileId));

            _remoteProfileId = remoteProfileId;
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _accountEmail = accountEmail;
            _rootFolderName = string.IsNullOrWhiteSpace(rootFolderName)
                ? MegaSyncRemoteSettings.DefaultRootFolderName
                : rootFolderName.Trim();
        }

        public string DisplayRoot =>
            string.IsNullOrWhiteSpace(_accountEmail)
                ? $"MEGA: {_rootFolderName}"
                : $"MEGA: {_rootFolderName} ({_accountEmail})";

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
            MegaSessionToken? session = await _sessionService.GetSessionAsync(_remoteProfileId, cancellationToken);
            if (session == null)
            {
                return new TransferPreviewWarning(
                    "MegaAuthRequired",
                    "MEGA authentication is required before syncing.",
                    TransferWarningSeverity.Error);
            }

            try
            {
                MegaQuotaInfo quota = await _apiClient.GetQuotaAsync(session, cancellationToken);
                if (quota.RemainingBytes <= 0 && quota.TotalBytes > 0)
                {
                    return new TransferPreviewWarning(
                        "MegaQuotaExceeded",
                        "MEGA storage quota is exceeded.",
                        TransferWarningSeverity.Error);
                }

                if (quota.TotalBytes > 0 && (double)quota.RemainingBytes / quota.TotalBytes < 0.10)
                {
                    return new TransferPreviewWarning(
                        "MegaLowStorage",
                        "MEGA storage has less than 10% capacity remaining.",
                        TransferWarningSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                return new TransferPreviewWarning(
                    "MegaValidationFailed",
                    $"MEGA validation failed: {ex.Message}",
                    TransferWarningSeverity.Error);
            }

            return null;
        }

        public async Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default)
        {
            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            return rootFolder != null;
        }

        public async Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                return Array.Empty<string>();

            return nodes
                .Where(n => n.ParentId == rootFolder.Id && n.Type == MegaNodeType.Folder)
                .Where(n => !n.Name.StartsWith(".", StringComparison.Ordinal))
                .Select(n => n.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool SupportsArchiveContainers => true;

        public async Task<IReadOnlyList<string>> ListRunArchiveNamesAsync(
            CancellationToken cancellationToken = default)
        {
            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                return Array.Empty<string>();

            return nodes
                .Where(n => n.ParentId == rootFolder.Id && n.Type == MegaNodeType.File)
                .Where(n => n.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                            n.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativeFolder ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(clean))
                return await RootExistsAsync(cancellationToken);

            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                return false;

            return FindDirectoryNode(nodes, rootFolder.Id, clean) != null;
        }

        public async Task<bool> FileExistsAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(clean))
                return false;

            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                return false;

            return FindFileNode(nodes, rootFolder.Id, clean) != null;
        }

        public async Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(clean))
                return null;

            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                return null;

            MegaNode? fileNode = FindFileNode(nodes, rootFolder.Id, clean);
            if (fileNode == null)
                return null;

            return await _apiClient.ReadTextFileAsync(session, fileNode.Id, cancellationToken);
        }

        public async Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');

            // Strict create-only invariant: Never overwrite existing file content
            if (await FileExistsAsync(clean, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {clean}");
            }

            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            MegaNode rootFolder = await EnsureRootFolderAsync(session, cancellationToken);
            string parentFolderId = await EnsureParentDirectoriesAsync(session, rootFolder.Id, clean, cancellationToken);
            string fileName = Path.GetFileName(clean);

            byte[] bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);
            await _apiClient.UploadFileChunkedAsync(session, parentFolderId, fileName, stream, null, cancellationToken);
        }

        public async Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            AssertAllowlistedMetadata(clean);

            return await ReadTextFileAsync(clean, cancellationToken);
        }

        public async Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            AssertAllowlistedMetadata(clean);

            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode rootFolder = await EnsureRootFolderAsync(session, cancellationToken);

            MegaNode? existingFile = FindFileNode(nodes, rootFolder.Id, clean);
            if (existingFile != null)
            {
                await _apiClient.DeleteNodeAsync(session, existingFile.Id, cancellationToken);
            }

            string parentFolderId = await EnsureParentDirectoriesAsync(session, rootFolder.Id, clean, cancellationToken);
            string fileName = Path.GetFileName(clean);

            byte[] bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);
            await _apiClient.UploadFileChunkedAsync(session, parentFolderId, fileName, stream, null, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativeFolder ?? string.Empty).Replace('\\', '/').Trim('/');
            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                return Array.Empty<string>();

            MegaNode? targetFolder = string.IsNullOrEmpty(clean)
                ? rootFolder
                : FindDirectoryNode(nodes, rootFolder.Id, clean);

            if (targetFolder == null)
                return Array.Empty<string>();

            var results = new List<string>();
            var queue = new Queue<(string NodeId, string Prefix)>();
            queue.Enqueue((targetFolder.Id, string.Empty));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (currentId, prefix) = queue.Dequeue();

                foreach (MegaNode child in nodes.Where(n => n.ParentId == currentId))
                {
                    if (child.Type == MegaNodeType.Folder)
                    {
                        string subPrefix = string.IsNullOrEmpty(prefix)
                            ? child.Name
                            : $"{prefix}/{child.Name}";
                        queue.Enqueue((child.Id, subPrefix));
                    }
                    else if (child.Type == MegaNodeType.File)
                    {
                        string filePath = string.IsNullOrEmpty(prefix)
                            ? child.Name
                            : $"{prefix}/{child.Name}";
                        results.Add(filePath);
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

            string clean = (relativeRemotePath ?? string.Empty).Replace('\\', '/').Trim('/');

            // Strict create-only invariant: Never overwrite existing file content
            if (await FileExistsAsync(clean, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {clean}");
            }

            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            MegaNode rootFolder = await EnsureRootFolderAsync(session, cancellationToken);
            string parentFolderId = await EnsureParentDirectoriesAsync(session, rootFolder.Id, clean, cancellationToken);
            string fileName = Path.GetFileName(clean);

            using var fileStream = File.OpenRead(localFilePath);
            await _apiClient.UploadFileChunkedAsync(session, parentFolderId, fileName, fileStream, null, cancellationToken);
            return new FileInfo(localFilePath).Length;
        }

        public async Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            string clean = (relativeRemotePath ?? string.Empty).Replace('\\', '/').Trim('/');
            MegaSessionToken session = await GetSessionOrThrowAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootFolder = FindRootFolderNode(nodes);
            if (rootFolder == null)
                throw new FileNotFoundException($"Remote file '{clean}' was not found.");

            MegaNode? fileNode = FindFileNode(nodes, rootFolder.Id, clean);
            if (fileNode == null)
                throw new FileNotFoundException($"Remote file '{clean}' was not found.");

            string? dir = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using Stream stream = await _apiClient.DownloadFileAsync(session, fileNode.Id, cancellationToken);
            using (var fileStream = File.Create(localFilePath))
            {
                await stream.CopyToAsync(fileStream, cancellationToken);
            }

            return new FileInfo(localFilePath).Length;
        }

        private async Task<MegaSessionToken> GetSessionOrThrowAsync(CancellationToken cancellationToken)
        {
            MegaSessionToken? session = await _sessionService.GetSessionAsync(_remoteProfileId, cancellationToken);
            if (session == null)
            {
                throw new InvalidOperationException(
                    "MEGA authentication session is missing or expired. Please re-authenticate the profile.");
            }
            return session;
        }

        private MegaNode? FindRootFolderNode(IReadOnlyList<MegaNode> nodes)
        {
            MegaNode? rootDrive = nodes.FirstOrDefault(n => n.Type == MegaNodeType.Root);
            string parentId = rootDrive?.Id ?? "root";

            return nodes.FirstOrDefault(n =>
                (n.ParentId == parentId || n.ParentId == null) &&
                n.Name.Equals(_rootFolderName, StringComparison.OrdinalIgnoreCase) &&
                n.Type == MegaNodeType.Folder);
        }

        private static MegaNode? FindDirectoryNode(
            IReadOnlyList<MegaNode> nodes,
            string currentParentId,
            string relativeDirectory)
        {
            string[] segments = relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string currentId = currentParentId;
            MegaNode? current = null;

            foreach (string segment in segments)
            {
                current = nodes.FirstOrDefault(n =>
                    n.ParentId == currentId &&
                    n.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) &&
                    n.Type == MegaNodeType.Folder);

                if (current == null)
                    return null;

                currentId = current.Id;
            }

            return current;
        }

        private static MegaNode? FindFileNode(
            IReadOnlyList<MegaNode> nodes,
            string rootFolderId,
            string relativeFilePath)
        {
            string clean = relativeFilePath.Replace('\\', '/').Trim('/');
            string? dir = Path.GetDirectoryName(clean)?.Replace('\\', '/').Trim('/');
            string fileName = Path.GetFileName(clean);

            string parentId = rootFolderId;
            if (!string.IsNullOrEmpty(dir))
            {
                MegaNode? dirNode = FindDirectoryNode(nodes, rootFolderId, dir);
                if (dirNode == null)
                    return null;
                parentId = dirNode.Id;
            }

            return nodes.FirstOrDefault(n =>
                n.ParentId == parentId &&
                n.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                n.Type == MegaNodeType.File);
        }

        private async Task<MegaNode> EnsureRootFolderAsync(
            MegaSessionToken session,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? rootDrive = nodes.FirstOrDefault(n => n.Type == MegaNodeType.Root);
            string parentId = rootDrive?.Id ?? "root";

            MegaNode? existing = nodes.FirstOrDefault(n =>
                (n.ParentId == parentId || n.ParentId == null) &&
                n.Name.Equals(_rootFolderName, StringComparison.OrdinalIgnoreCase) &&
                n.Type == MegaNodeType.Folder);

            if (existing != null)
                return existing;

            return await _apiClient.CreateFolderAsync(session, parentId, _rootFolderName, cancellationToken);
        }

        private async Task<string> EnsureParentDirectoriesAsync(
            MegaSessionToken session,
            string rootFolderId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            string? dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(dir))
                return rootFolderId;

            string[] segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string currentParentId = rootFolderId;

            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            var workingList = new List<MegaNode>(nodes);

            foreach (string segment in segments)
            {
                MegaNode? child = workingList.FirstOrDefault(n =>
                    n.ParentId == currentParentId &&
                    n.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) &&
                    n.Type == MegaNodeType.Folder);

                if (child == null)
                {
                    child = await _apiClient.CreateFolderAsync(session, currentParentId, segment, cancellationToken);
                    workingList.Add(child);
                }

                currentParentId = child.Id;
            }

            return currentParentId;
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
