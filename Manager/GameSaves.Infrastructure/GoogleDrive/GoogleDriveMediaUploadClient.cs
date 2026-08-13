using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveMediaUploadProgressStatus
    {
        NotStarted = 0,
        Starting = 1,
        Uploading = 2,
        Completed = 3,
        Failed = 4
    }

    internal sealed class GoogleDriveMediaUploadProgress
    {
        public GoogleDriveMediaUploadProgress(
            GoogleDriveMediaUploadProgressStatus status,
            long bytesSent)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (bytesSent < 0)
                throw new ArgumentOutOfRangeException(nameof(bytesSent));

            Status = status;
            BytesSent = bytesSent;
        }

        public GoogleDriveMediaUploadProgressStatus Status { get; }

        public long BytesSent { get; }

        public override string ToString() =>
            $"Google Drive media upload progress: status={Status}; " +
            $"bytesSent={BytesSent}";
    }

    internal sealed class GoogleDriveMediaUploadMetadata
    {
        public GoogleDriveMediaUploadMetadata(
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
            "Google Drive media upload metadata: " +
            $"idPresent={!string.IsNullOrWhiteSpace(Id)}; " +
            $"namePresent={Name is not null}; " +
            $"mimeTypePresent={MimeType is not null}; " +
            $"trashedPresent={Trashed is not null}; " +
            $"parents={ParentIds?.Count ?? -1}; " +
            $"sharedDrive={DriveId is not null}; sizePresent={Size is not null}";
    }

    internal interface IGoogleDriveMediaUploadClient : IDisposable
    {
        Task<GoogleDriveMediaUploadMetadata> UploadAsync(
            string parentFolderId,
            string exactFileName,
            Stream source,
            long expectedLength,
            string mediaType,
            IProgress<GoogleDriveMediaUploadProgress>? progress,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveMediaUploadClientFactory
    {
        IGoogleDriveMediaUploadClient Create(
            GoogleAuthorizedCredential credential);
    }

    internal sealed class GoogleDriveMediaUploadClientFactory
        : IGoogleDriveMediaUploadClientFactory
    {
        public IGoogleDriveMediaUploadClient Create(
            GoogleAuthorizedCredential credential)
        {
            ArgumentNullException.ThrowIfNull(credential);

            return new GoogleDriveMediaUploadClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = GoogleDriveRequestContract.ApplicationName
                }));
        }
    }

    internal sealed class GoogleDriveMediaUploadClient
        : IGoogleDriveMediaUploadClient
    {
        internal const string OpaqueMediaType = "application/octet-stream";
        internal const string ResponseFields =
            "id,name,mimeType,trashed,parents,driveId,size";

        private DriveService? _drive;
        private int _uploadStarted;

        public GoogleDriveMediaUploadClient(DriveService drive) =>
            _drive = drive ?? throw new ArgumentNullException(nameof(drive));

        internal bool IsDisposed => _drive is null;

        public async Task<GoogleDriveMediaUploadMetadata> UploadAsync(
            string parentFolderId,
            string exactFileName,
            Stream source,
            long expectedLength,
            string mediaType,
            IProgress<GoogleDriveMediaUploadProgress>? progress,
            CancellationToken cancellationToken)
        {
            DriveService drive = _drive ??
                throw new ObjectDisposedException(GetType().Name);
            ValidateInput(
                parentFolderId,
                exactFileName,
                source,
                expectedLength,
                mediaType);
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Exchange(ref _uploadStarted, 1) != 0)
            {
                throw new InvalidOperationException(
                    "A media-upload client handles exactly one upload.");
            }

            FilesResource.CreateMediaUpload upload = CreateSdkUpload(
                drive,
                parentFolderId,
                exactFileName,
                source,
                mediaType);
            if (progress is not null)
            {
                upload.ProgressChanged += sdkProgress =>
                    progress.Report(MapProgress(sdkProgress));
            }

            IUploadProgress completed = await upload.UploadAsync(
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (completed.Status != UploadStatus.Completed)
            {
                completed.ThrowOnFailure();
                throw new IOException(
                    "The Google Drive media upload did not complete.");
            }

            return Map(upload.ResponseBody);
        }

        public void Dispose()
        {
            DriveService? drive = Interlocked.Exchange(ref _drive, null);
            drive?.Dispose();
        }

        internal static FilesResource.CreateMediaUpload CreateSdkUpload(
            DriveService drive,
            string parentFolderId,
            string exactFileName,
            Stream source,
            string mediaType)
        {
            ArgumentNullException.ThrowIfNull(drive);
            if (!string.Equals(
                    mediaType,
                    OpaqueMediaType,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The opaque upload media type is required.",
                    nameof(mediaType));
            }

            FilesResource.CreateMediaUpload upload = drive.Files.Create(
                CreateMetadata(parentFolderId, exactFileName),
                source,
                OpaqueMediaType);
            upload.Fields = ResponseFields;
            upload.SupportsAllDrives =
                GoogleDriveRequestContract.SupportsAllDrives;
            return upload;
        }

        internal static DriveFile CreateMetadata(
            string parentFolderId,
            string exactFileName) =>
            new()
            {
                Name = exactFileName,
                MimeType = OpaqueMediaType,
                Parents = [parentFolderId]
            };

        internal static GoogleDriveMediaUploadMetadata Map(DriveFile? file) =>
            new(
                file?.Id,
                file?.Name,
                file?.MimeType,
                file?.Trashed,
                file?.Parents,
                file?.DriveId,
                file?.Size);

        internal static GoogleDriveMediaUploadProgress MapProgress(
            IUploadProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);

            GoogleDriveMediaUploadProgressStatus status = progress.Status switch
            {
                UploadStatus.NotStarted =>
                    GoogleDriveMediaUploadProgressStatus.NotStarted,
                UploadStatus.Starting =>
                    GoogleDriveMediaUploadProgressStatus.Starting,
                UploadStatus.Uploading =>
                    GoogleDriveMediaUploadProgressStatus.Uploading,
                UploadStatus.Completed =>
                    GoogleDriveMediaUploadProgressStatus.Completed,
                UploadStatus.Failed =>
                    GoogleDriveMediaUploadProgressStatus.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(progress))
            };

            return new GoogleDriveMediaUploadProgress(
                status,
                progress.BytesSent);
        }

        private static void ValidateInput(
            string parentFolderId,
            string exactFileName,
            Stream source,
            long expectedLength,
            string mediaType)
        {
            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                throw new ArgumentException(
                    "An authoritative parent ID is required.",
                    nameof(parentFolderId));
            }
            if (string.IsNullOrEmpty(exactFileName))
            {
                throw new ArgumentException(
                    "An exact target name is required.",
                    nameof(exactFileName));
            }

            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead)
            {
                throw new ArgumentException(
                    "A readable source stream is required.",
                    nameof(source));
            }
            if (expectedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedLength));
            if (!string.Equals(
                    mediaType,
                    OpaqueMediaType,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The opaque upload media type is required.",
                    nameof(mediaType));
            }
        }
    }
}
