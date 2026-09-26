using System.Net.Http.Headers;
using System.Text;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.WebDav
{
    /// <summary>
    /// IRemoteFileSystem over one folder of a WebDAV server. Run content is
    /// create-only (If-None-Match: *, plus an existence check for the manifest
    /// and sidecar files that give a run its identity); only
    /// .gamesave-sync/sync-log.json is ever replaced; nothing is deleted.
    /// </summary>
    internal sealed class WebDavRemoteFileSystem : IRemoteFileSystem
    {
        private const int MaxListingDepth = 64;

        private static readonly MediaTypeHeaderValue OctetStream = new("application/octet-stream");
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly WebDavClient _client;
        private readonly string _folder;

        // ponytail: collections this instance created or found, never
        // invalidated. Sync never deletes one, and one provider serves one
        // preview and its sync; a folder removed by hand meanwhile fails the
        // PUT with 409 rather than being recreated.
        private readonly HashSet<string> _knownCollections = new(StringComparer.Ordinal);

        public WebDavRemoteFileSystem(
            WebDavClient client,
            string remoteFolder,
            string displayRoot)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _folder = remoteFolder;
            DisplayRoot = displayRoot;
        }

        public string DisplayRoot { get; }

        public string GetDisplayPath(string relativePath) =>
            string.IsNullOrEmpty(relativePath)
                ? DisplayRoot
                : $"{DisplayRoot}/{relativePath}";

        public bool SupportsArchiveContainers => true;

        public async Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                WebDavEntry? server = await _client.StatAsync("", collection: true, cancellationToken);

                return server is { IsCollection: true }
                    ? null
                    : new TransferPreviewWarning(
                        "WebDavServerUrlInvalid",
                        "The WebDAV server URL does not point to a WebDAV folder. For Nextcloud use https://HOST/remote.php/dav/files/USER/.",
                        TransferWarningSeverity.Error);
            }
            catch (WebDavException exception) when (
                exception.StatusCode is 401 or 403 or (>= 300 and < 400))
            {
                return new TransferPreviewWarning(
                    exception.StatusCode is 401 ? "WebDavAuthenticationFailed" : "WebDavAccessRefused",
                    exception.Message,
                    TransferWarningSeverity.Error);
            }
        }

        public async Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            (await _client.StatAsync(_folder, collection: true, cancellationToken))?.IsCollection == true;

        public async Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default) =>
            ((await _client.ListAsync(_folder, cancellationToken)) ?? [])
                .Where(entry => entry.IsCollection)
                .Select(entry => entry.Name)
                .ToList();

        public async Task<IReadOnlyList<string>> ListRunArchiveNamesAsync(
            CancellationToken cancellationToken = default) =>
            ((await _client.ListAsync(_folder, cancellationToken)) ?? [])
                .Where(entry => !entry.IsCollection &&
                                (entry.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                                 entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.Name)
                .ToList();

        public async Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            (await _client.StatAsync(Full(relativeFolder), collection: true, cancellationToken))?.IsCollection == true;

        public async Task<bool> FileExistsAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            await _client.StatAsync(Full(relativePath), collection: false, cancellationToken) is not null;

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            _client.GetStringAsync(Full(relativePath), cancellationToken);

        public async Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            // These are the manifest and sidecar files that make a run a run,
            // so they are checked here as well as refused by the server: a
            // server that ignores If-None-Match still cannot replace one.
            if (await _client.StatAsync(Full(relativePath), collection: false, cancellationToken) is not null)
            {
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {relativePath}");
            }

            await EnsureParentCollectionsAsync(relativePath, cancellationToken);
            await _client.PutAsync(
                Full(relativePath),
                new StringContent(content, Utf8NoBom, "application/json"),
                createOnly: true,
                transfer: false,
                cancellationToken);
        }

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            _client.GetStringAsync(
                Full(RemoteProviderMetadataPath.Validate(relativePath)),
                cancellationToken);

        public async Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string path = RemoteProviderMetadataPath.Validate(relativePath);

            await EnsureParentCollectionsAsync(path, cancellationToken);
            await _client.PutAsync(
                Full(path),
                new StringContent(content, Utf8NoBom, "application/json"),
                createOnly: false,
                transfer: false,
                cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            var files = new List<string>();
            await CollectFilesAsync(Full(relativeFolder), "", depth: 0, files, cancellationToken);
            return files;
        }

        public async Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            await EnsureParentCollectionsAsync(relativeRemotePath, cancellationToken);

            await using var source = new FileStream(
                localFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            long length = source.Length;

            // Streamed with a known length: the file is never buffered in memory.
            using var content = new StreamContent(source);
            content.Headers.ContentLength = length;
            content.Headers.ContentType = OctetStream;

            await _client.PutAsync(
                Full(relativeRemotePath),
                content,
                createOnly: true,
                transfer: true,
                cancellationToken);

            return length;
        }

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default) =>
            _client.DownloadAsync(Full(relativeRemotePath), localFilePath, cancellationToken);

        private string Full(string relativePath) =>
            string.IsNullOrEmpty(relativePath) ? _folder : $"{_folder}/{relativePath}";

        private async Task CollectFilesAsync(
            string folder,
            string prefix,
            int depth,
            List<string> files,
            CancellationToken cancellationToken)
        {
            if (depth > MaxListingDepth)
            {
                throw new InvalidDataException(
                    "A WebDAV backup run is nested deeper than any backup run can be.");
            }

            IReadOnlyList<WebDavEntry>? entries = await _client.ListAsync(folder, cancellationToken);

            if (entries is null)
                return;

            foreach (WebDavEntry entry in entries)
            {
                string relative = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";

                if (entry.IsCollection)
                {
                    await CollectFilesAsync(
                        $"{folder}/{entry.Name}",
                        relative,
                        depth + 1,
                        files,
                        cancellationToken);
                }
                else
                {
                    files.Add(relative);
                }
            }
        }

        /// <summary>
        /// MKCOL for the configured folder and every parent of the target, in
        /// order. Creating a collection never replaces anything.
        /// </summary>
        private async Task EnsureParentCollectionsAsync(
            string relativePath,
            CancellationToken cancellationToken)
        {
            string[] segments = Full(relativePath).Split('/');

            for (int count = 1; count < segments.Length; count++)
            {
                string collection = string.Join('/', segments, 0, count);

                if (_knownCollections.Contains(collection))
                    continue;

                await _client.MakeCollectionAsync(collection, cancellationToken);
                _knownCollections.Add(collection);
            }
        }
    }
}
