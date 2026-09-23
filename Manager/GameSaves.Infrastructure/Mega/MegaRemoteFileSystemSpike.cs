using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Mega
{
    /// <summary>
    /// Architectural spike proving the integration boundary, safety invariants,
    /// create-only upload ordering, manifest-last placement, and quota inspection
    /// for MEGA cloud synchronization (OBS-012).
    /// </summary>
    public class MegaRemoteFileSystemSpike
    {
        private readonly IMegaApiClient _apiClient;
        private readonly IMegaSessionService _sessionService;
        private readonly Guid _profileId;
        private readonly string _rootFolderName;

        public MegaRemoteFileSystemSpike(
            IMegaApiClient apiClient,
            IMegaSessionService sessionService,
            Guid profileId,
            string? rootFolderName = null)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _profileId = profileId == Guid.Empty ? throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId)) : profileId;
            _rootFolderName = string.IsNullOrWhiteSpace(rootFolderName) ? MegaSyncRemoteSettings.DefaultRootFolderName : rootFolderName.Trim();
        }

        public bool SupportsArchiveContainers => true;

        public string RootFolderName => _rootFolderName;

        /// <summary>
        /// Ensures the application's root backup folder exists in the user's MEGA cloud drive.
        /// </summary>
        public async Task<MegaNode> EnsureRootFolderAsync(CancellationToken cancellationToken = default)
        {
            MegaSessionToken session = await RequireSessionAsync(cancellationToken);
            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);

            MegaNode? rootDrive = nodes.FirstOrDefault(n => n.Type == MegaNodeType.Root);
            string parentId = rootDrive?.Id ?? "root";

            MegaNode? existing = nodes.FirstOrDefault(n => n.ParentId == parentId && n.Name.Equals(_rootFolderName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing;

            return await _apiClient.CreateFolderAsync(session, parentId, _rootFolderName, cancellationToken);
        }

        /// <summary>
        /// Discovers all existing backup runs (both folders and archive containers) inside the root folder.
        /// </summary>
        public async Task<IReadOnlyList<MegaNode>> ListRunsAsync(CancellationToken cancellationToken = default)
        {
            MegaSessionToken session = await RequireSessionAsync(cancellationToken);
            MegaNode root = await EnsureRootFolderAsync(cancellationToken);

            IReadOnlyList<MegaNode> nodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            return nodes.Where(n => n.ParentId == root.Id).ToList();
        }

        /// <summary>
        /// Uploads a backup run in strict adherence to GSM safety invariants:
        /// 1. Create-only check: fails if a run with the same name already exists remotely.
        /// 2. Manifest-last placement: uploads all payload files first, and uploads manifest.json strictly last.
        /// </summary>
        public async Task<MegaNode> UploadRunAsync(
            string runName,
            IReadOnlyDictionary<string, byte[]> files,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runName);
            ArgumentNullException.ThrowIfNull(files);

            MegaSessionToken session = await RequireSessionAsync(cancellationToken);
            MegaNode root = await EnsureRootFolderAsync(cancellationToken);

            // Invariant 1: Create-only guard. Existing runs must never be overwritten.
            IReadOnlyList<MegaNode> existingRuns = await ListRunsAsync(cancellationToken);
            if (existingRuns.Any(r => r.Name.Equals(runName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Remote backup run '{runName}' already exists. Overwriting existing runs is prohibited.");
            }

            // Create remote run folder
            MegaNode runFolder = await _apiClient.CreateFolderAsync(session, root.Id, runName, cancellationToken);

            // Invariant 2: Manifest-last ordering.
            // All payload files must be uploaded before manifest.json.
            var payloadFiles = files.Where(kvp => !kvp.Key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToList();
            var manifestFile = files.FirstOrDefault(kvp => kvp.Key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));

            // Upload payload files
            foreach (var (relPath, content) in payloadFiles)
            {
                using var stream = new MemoryStream(content);
                await _apiClient.UploadFileChunkedAsync(session, runFolder.Id, relPath, stream, progress, cancellationToken);
            }

            // Upload manifest.json strictly as the final file
            if (!string.IsNullOrEmpty(manifestFile.Key))
            {
                using var stream = new MemoryStream(manifestFile.Value);
                await _apiClient.UploadFileChunkedAsync(session, runFolder.Id, "manifest.json", stream, progress, cancellationToken);
            }

            return runFolder;
        }

        /// <summary>
        /// Downloads a synthetic or executed backup run from MEGA.
        /// </summary>
        public async Task<Dictionary<string, byte[]>> DownloadRunAsync(
            string runName,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runName);

            MegaSessionToken session = await RequireSessionAsync(cancellationToken);
            MegaNode root = await EnsureRootFolderAsync(cancellationToken);

            IReadOnlyList<MegaNode> allNodes = await _apiClient.GetNodesAsync(session, cancellationToken);
            MegaNode? runFolder = allNodes.FirstOrDefault(n => n.ParentId == root.Id && n.Name.Equals(runName, StringComparison.OrdinalIgnoreCase));
            if (runFolder is null)
                throw new DirectoryNotFoundException($"Remote run folder '{runName}' was not found.");

            var fileNodes = allNodes.Where(n => n.ParentId == runFolder.Id && n.Type == MegaNodeType.File).ToList();
            var downloaded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (MegaNode fileNode in fileNodes)
            {
                using Stream stream = await _apiClient.DownloadFileAsync(session, fileNode.Id, cancellationToken);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                downloaded[fileNode.Name] = ms.ToArray();
            }

            return downloaded;
        }

        /// <summary>
        /// Inspects live storage quota on MEGA.
        /// </summary>
        public async Task<MegaQuotaInfo> GetQuotaAsync(CancellationToken cancellationToken = default)
        {
            MegaSessionToken session = await RequireSessionAsync(cancellationToken);
            return await _apiClient.GetQuotaAsync(session, cancellationToken);
        }

        private async Task<MegaSessionToken> RequireSessionAsync(CancellationToken ct)
        {
            MegaSessionToken? session = await _sessionService.GetSessionAsync(_profileId, ct);
            if (session is null)
                throw new InvalidOperationException("Active MEGA session required. Connect account first.");

            return session;
        }
    }
}
