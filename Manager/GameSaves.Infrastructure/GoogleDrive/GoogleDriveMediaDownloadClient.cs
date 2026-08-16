using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveMediaDownloadProgressStatus
    {
        NotStarted = 0,
        Downloading = 1,
        Completed = 2,
        Failed = 3
    }

    internal sealed class GoogleDriveMediaDownloadProgress
    {
        public GoogleDriveMediaDownloadProgress(
            GoogleDriveMediaDownloadProgressStatus status,
            long bytesDownloaded)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (bytesDownloaded < 0)
                throw new ArgumentOutOfRangeException(nameof(bytesDownloaded));

            Status = status;
            BytesDownloaded = bytesDownloaded;
        }

        public GoogleDriveMediaDownloadProgressStatus Status { get; }

        public long BytesDownloaded { get; }

        public override string ToString() =>
            $"Google Drive media download progress: status={Status}; " +
            $"bytesDownloaded={BytesDownloaded}";
    }

    /// <summary>
    /// Project-owned snapshot of the only source metadata a backup download
    /// validates. Diagnostic formatting exposes presence and counts only.
    /// </summary>
    internal sealed class GoogleDriveMediaDownloadMetadata
    {
        public GoogleDriveMediaDownloadMetadata(
            string? id,
            string? name,
            string? mimeType,
            bool? trashed,
            IEnumerable<string>? parentIds,
            string? driveId,
            long? size)
        {
            Id = id;
            Name = name;
            MimeType = mimeType;
            Trashed = trashed;
            ParentIds = parentIds?.ToArray();
            DriveId = driveId;
            Size = size;
        }

        public string? Id { get; }

        public string? Name { get; }

        public string? MimeType { get; }

        public bool? Trashed { get; }

        public IReadOnlyList<string>? ParentIds { get; }

        public string? DriveId { get; }

        public long? Size { get; }

        public override string ToString() =>
            "Google Drive media download metadata: " +
            $"idPresent={!string.IsNullOrWhiteSpace(Id)}; " +
            $"namePresent={Name is not null}; " +
            $"mimeTypePresent={MimeType is not null}; " +
            $"trashedPresent={Trashed is not null}; " +
            $"parents={ParentIds?.Count ?? -1}; " +
            $"sharedDrive={DriveId is not null}; sizePresent={Size is not null}";
    }

    internal interface IGoogleDriveMediaDownloadClient : IDisposable
    {
        /// <summary>
        /// Reads only the source metadata a backup download validates.
        /// </summary>
        Task<GoogleDriveMediaDownloadMetadata> GetMetadataAsync(
            string fileId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Streams one Drive file's content into the destination and returns
        /// the number of bytes the provider reported as written.
        /// </summary>
        Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveMediaDownloadClientFactory
    {
        IGoogleDriveMediaDownloadClient Create(
            GoogleAuthorizedCredential credential);
    }

    internal sealed class GoogleDriveMediaDownloadClientFactory
        : IGoogleDriveMediaDownloadClientFactory
    {
        public IGoogleDriveMediaDownloadClient Create(
            GoogleAuthorizedCredential credential)
        {
            ArgumentNullException.ThrowIfNull(credential);

            return new GoogleDriveMediaDownloadClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = GoogleDriveRequestContract.ApplicationName
                }));
        }
    }

    /// <summary>
    /// Read-only media boundary over the installed Google client. It owns one
    /// short-lived <see cref="DriveService"/>, handles exactly one download,
    /// and never requests metadata, export, conversion, or abuse acknowledgement.
    /// </summary>
    internal sealed class GoogleDriveMediaDownloadClient
        : IGoogleDriveMediaDownloadClient
    {
        private DriveService? _drive;
        private int _downloadStarted;

        public GoogleDriveMediaDownloadClient(DriveService drive) =>
            _drive = drive ?? throw new ArgumentNullException(nameof(drive));

        internal bool IsDisposed => _drive is null;

        public async Task<GoogleDriveMediaDownloadMetadata> GetMetadataAsync(
            string fileId,
            CancellationToken cancellationToken)
        {
            DriveService drive = _drive ??
                throw new ObjectDisposedException(GetType().Name);
            cancellationToken.ThrowIfCancellationRequested();

            FilesResource.GetRequest request = CreateSdkMetadataRequest(
                drive,
                fileId);
            DriveFile file = await request.ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Map(file);
        }

        public async Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            DriveService drive = _drive ??
                throw new ObjectDisposedException(GetType().Name);
            ValidateInput(fileId, destination);
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Exchange(ref _downloadStarted, 1) != 0)
            {
                throw new InvalidOperationException(
                    "A media-download client handles exactly one download.");
            }

            FilesResource.GetRequest request = CreateSdkRequest(drive, fileId);
            if (progress is not null)
            {
                request.MediaDownloader.ProgressChanged += sdkProgress =>
                    progress.Report(MapProgress(sdkProgress));
            }

            cancellationToken.ThrowIfCancellationRequested();
            IDownloadProgress completed = await request.DownloadAsync(
                destination,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (completed.Status != DownloadStatus.Completed)
            {
                if (completed.Exception is OperationCanceledException cancelled)
                    throw cancelled;

                throw completed.Exception ?? new IOException(
                    "The Google Drive media download did not complete.");
            }

            return completed.BytesDownloaded;
        }

        public void Dispose()
        {
            DriveService? drive = Interlocked.Exchange(ref _drive, null);
            drive?.Dispose();
        }

        internal static FilesResource.GetRequest CreateSdkRequest(
            DriveService drive,
            string fileId)
        {
            ArgumentNullException.ThrowIfNull(drive);
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "An authoritative file ID is required.",
                    nameof(fileId));
            }

            FilesResource.GetRequest request = drive.Files.Get(fileId);
            request.SupportsAllDrives = GoogleDriveRequestContract.SupportsAllDrives;
            request.AcknowledgeAbuse = false;
            return request;
        }

        internal static FilesResource.GetRequest CreateSdkMetadataRequest(
            DriveService drive,
            string fileId)
        {
            ArgumentNullException.ThrowIfNull(drive);
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "An authoritative file ID is required.",
                    nameof(fileId));
            }

            FilesResource.GetRequest request = drive.Files.Get(fileId);
            request.Fields = GoogleDriveRequestContract.BinaryDownloadMetadataFields;
            request.SupportsAllDrives = GoogleDriveRequestContract.SupportsAllDrives;
            return request;
        }

        internal static GoogleDriveMediaDownloadMetadata Map(DriveFile? file) =>
            new(
                file?.Id,
                file?.Name,
                file?.MimeType,
                file?.Trashed,
                file?.Parents,
                file?.DriveId,
                file?.Size);

        internal static GoogleDriveMediaDownloadProgress MapProgress(
            IDownloadProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);

            GoogleDriveMediaDownloadProgressStatus status = progress.Status switch
            {
                DownloadStatus.NotStarted =>
                    GoogleDriveMediaDownloadProgressStatus.NotStarted,
                DownloadStatus.Downloading =>
                    GoogleDriveMediaDownloadProgressStatus.Downloading,
                DownloadStatus.Completed =>
                    GoogleDriveMediaDownloadProgressStatus.Completed,
                DownloadStatus.Failed =>
                    GoogleDriveMediaDownloadProgressStatus.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(progress))
            };

            return new GoogleDriveMediaDownloadProgress(
                status,
                progress.BytesDownloaded);
        }

        private static void ValidateInput(string fileId, Stream destination)
        {
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "An authoritative file ID is required.",
                    nameof(fileId));
            }

            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
            {
                throw new ArgumentException(
                    "A writable destination stream is required.",
                    nameof(destination));
            }
        }
    }
}
