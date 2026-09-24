using GameSaves.Core.Sync;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal static class GoogleDriveTextContentErrorCodes
    {
        public const string Folder = "GoogleDriveTextContentFolder";
        public const string Trashed = "GoogleDriveTextContentTrashed";
        public const string UnsupportedLocation =
            "GoogleDriveTextContentUnsupportedLocation";
        public const string WorkspaceDocument =
            "GoogleDriveTextContentWorkspaceDocument";
        public const string DownloadNotAllowed =
            "GoogleDriveTextContentDownloadNotAllowed";
        public const string DeclaredSizeMissing =
            "GoogleDriveTextContentDeclaredSizeMissing";
        public const string DeclaredSizeTooLarge =
            "GoogleDriveTextContentDeclaredSizeTooLarge";
        public const string StreamedSizeTooLarge =
            "GoogleDriveTextContentStreamedSizeTooLarge";
        public const string Truncated = "GoogleDriveTextContentTruncated";
        public const string InvalidMetadata =
            "GoogleDriveTextContentInvalidMetadata";
    }

    internal sealed class GoogleDriveTextContentMetadataRequest
    {
        public GoogleDriveTextContentMetadataRequest(string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "A Google Drive file ID is required.",
                    nameof(fileId));
            }

            FileId = fileId;
        }

        public string FileId { get; }

        public string Fields => GoogleDriveRequestContract.TextContentMetadataFields;

        // Authoritative-ID inspection must identify shared-drive objects so
        // the API can reject them explicitly rather than treating them as My Drive.
        public bool SupportsAllDrives =>
            GoogleDriveRequestContract.AuthoritativeIdLookupSupportsAllDrives;

        public override string ToString() =>
            "Google Drive text-content metadata request";
    }

    internal sealed class GoogleDriveTextContentMediaRequest
    {
        public GoogleDriveTextContentMediaRequest(string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "A Google Drive file ID is required.",
                    nameof(fileId));
            }

            FileId = fileId;
        }

        public string FileId { get; }

        public bool SupportsAllDrives => GoogleDriveRequestContract.SupportsAllDrives;

        public bool AcknowledgeAbuse => false;

        public override string ToString() =>
            "Google Drive bounded text-content media request";
    }

    internal sealed class GoogleDriveTextContentMetadata
    {
        public GoogleDriveTextContentMetadata(
            string? id,
            string? mimeType,
            bool trashed,
            string? driveId,
            long? declaredSize,
            bool canDownload)
        {
            Id = string.IsNullOrWhiteSpace(id) ? null : id;
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType;
            Trashed = trashed;
            DriveId = string.IsNullOrWhiteSpace(driveId) ? null : driveId;
            DeclaredSize = declaredSize;
            CanDownload = canDownload;
        }

        public string? Id { get; }

        public string? MimeType { get; }

        public bool Trashed { get; }

        public string? DriveId { get; }

        public long? DeclaredSize { get; }

        public bool CanDownload { get; }

        public bool IsFolder => string.Equals(
            MimeType,
            GoogleDriveApplicationRoot.FolderMimeType,
            StringComparison.Ordinal);

        public bool IsWorkspaceObject => MimeType?.StartsWith(
            "application/vnd.google-apps.",
            StringComparison.Ordinal) == true;

        public override string ToString() =>
            "Google Drive text-content metadata: " +
            $"trashed={Trashed}; sharedDrive={DriveId is not null}; " +
            $"folder={IsFolder}; workspace={IsWorkspaceObject}; " +
            $"canDownload={CanDownload}; sizePresent={DeclaredSize is not null}";
    }

    internal sealed class GoogleDriveTextContentResult
    {
        private readonly byte[] _content;

        public GoogleDriveTextContentResult(ReadOnlySpan<byte> content) =>
            _content = content.ToArray();

        public int Length => _content.Length;

        public byte[] ToArray() => _content.ToArray();

        public override string ToString() =>
            "Google Drive bounded text-content result";
    }

    internal interface IGoogleDriveTextContentClient : IDisposable
    {
        Task<GoogleDriveTextContentMetadata> GetMetadataAsync(
            GoogleDriveTextContentMetadataRequest request,
            CancellationToken cancellationToken);

        Task DownloadAsync(
            GoogleDriveTextContentMediaRequest request,
            Stream destination,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveTextContentClientFactory
    {
        IGoogleDriveTextContentClient Create(
            GoogleAuthorizedCredential credential);
    }

    internal interface IGoogleDriveTextContentApi
    {
        Task<GoogleDriveTextContentResult> DownloadTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Downloads only small Drive blob content by authoritative file ID.
    /// One mebibyte is intentionally sufficient for application manifests
    /// and provider metadata while keeping all streamed memory strictly bounded.
    /// UTF-8 decoding is deliberately owned by a later, separate boundary.
    /// </summary>
    internal sealed class GoogleDriveTextContentApi : IGoogleDriveTextContentApi
    {
        public const int MaxTextContentBytes = 1024 * 1024;

        private readonly IGoogleDriveTextContentClientFactory _clientFactory;

        public GoogleDriveTextContentApi(
            IGoogleDriveTextContentClientFactory clientFactory) =>
            _clientFactory = clientFactory ??
                throw new ArgumentNullException(nameof(clientFactory));

        public async Task<GoogleDriveTextContentResult> DownloadTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            var metadataRequest = new GoogleDriveTextContentMetadataRequest(fileId);

            IGoogleDriveTextContentClient client;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                client = _clientFactory.Create(credential) ??
                    throw new InvalidOperationException(
                        "The Google Drive text-content client is unavailable.");
            }
            catch (Exception ex)
            {
                throw MapException(
                    ex,
                    GoogleDriveApiOperation.TextContentMetadataGet);
            }

            using (client)
            {
                return await DownloadValidatedAsync(
                    client,
                    metadataRequest,
                    fileId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<GoogleDriveTextContentResult> DownloadValidatedAsync(
            IGoogleDriveTextContentClient client,
            GoogleDriveTextContentMetadataRequest metadataRequest,
            string fileId,
            CancellationToken cancellationToken)
        {
            GoogleDriveTextContentMetadata metadata;
            try
            {
                metadata = await client.GetMetadataAsync(
                    metadataRequest,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                throw MapException(
                    ex,
                    GoogleDriveApiOperation.TextContentMetadataGet);
            }

            ValidateMetadata(metadata, fileId);

            using var destination = new BoundedMemoryWriteStream(
                MaxTextContentBytes);
            try
            {
                await client.DownloadAsync(
                    new GoogleDriveTextContentMediaRequest(fileId),
                    destination,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (GoogleDriveTextContentLimitExceededException)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentDownload,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.StreamedSizeTooLarge,
                    retryable: false);
            }
            catch (Exception ex)
            {
                throw MapException(
                    ex,
                    GoogleDriveApiOperation.TextContentDownload);
            }

            long declaredSize = metadata.DeclaredSize!.Value;
            if (destination.Length < declaredSize)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentDownload,
                    GoogleDriveApiFailure.Unavailable,
                    GoogleDriveTextContentErrorCodes.Truncated,
                    retryable: true);
            }

            if (destination.Length != declaredSize)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentDownload,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.InvalidMetadata,
                    retryable: false);
            }

            return new GoogleDriveTextContentResult(destination.ToArray());
        }

        private static void ValidateMetadata(
            GoogleDriveTextContentMetadata metadata,
            string expectedFileId)
        {
            if (metadata is null ||
                !string.Equals(metadata.Id, expectedFileId, StringComparison.Ordinal) ||
                metadata.MimeType is null)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.InvalidMetadata,
                    retryable: false);
            }

            if (metadata.DriveId is not null)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.AccessDenied,
                    GoogleDriveTextContentErrorCodes.UnsupportedLocation,
                    retryable: false);
            }

            if (metadata.Trashed)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.NotFound,
                    GoogleDriveTextContentErrorCodes.Trashed,
                    retryable: false);
            }

            if (metadata.IsFolder)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.Folder,
                    retryable: false);
            }

            if (metadata.IsWorkspaceObject)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.WorkspaceDocument,
                    retryable: false);
            }

            if (!metadata.CanDownload)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.AccessDenied,
                    GoogleDriveTextContentErrorCodes.DownloadNotAllowed,
                    retryable: false);
            }

            if (metadata.DeclaredSize is null || metadata.DeclaredSize < 0)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.DeclaredSizeMissing,
                    retryable: false);
            }

            if (metadata.DeclaredSize > MaxTextContentBytes)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextContentErrorCodes.DeclaredSizeTooLarge,
                    retryable: false);
            }
        }

        internal static GoogleDriveApiException MapException(
            Exception exception,
            GoogleDriveApiOperation operation) =>
            GoogleDriveApiFailureMapper.Map(
                exception,
                operation,
                failure => $"GoogleDriveTextContent{failure}");

        private static GoogleDriveApiException Failure(
            GoogleDriveApiOperation operation,
            GoogleDriveApiFailure failure,
            string errorCode,
            bool retryable) =>
            GoogleDriveApiFailureMapper.Create(
                operation,
                failure,
                errorCode,
                retryable);
    }

    internal sealed class GoogleDriveTextContentClientFactory
        : IGoogleDriveTextContentClientFactory
    {
        public IGoogleDriveTextContentClient Create(
            GoogleAuthorizedCredential credential) =>
            new GoogleDriveTextContentClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = GoogleDriveRequestContract.ApplicationName,

                    // Retry lives in exactly one place, RetryingRemoteFileSystem. The
                    // library would otherwise apply a backoff policy this codebase never
                    // states, so a failing call could wait for the decorator bound plus
                    // however long the library chose. See D-032 and D-033.
                    DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None
                }));
    }

    internal sealed class GoogleDriveTextContentClient
        : IGoogleDriveTextContentClient
    {
        private readonly DriveService _drive;

        public GoogleDriveTextContentClient(DriveService drive) =>
            _drive = drive;

        public async Task<GoogleDriveTextContentMetadata> GetMetadataAsync(
            GoogleDriveTextContentMetadataRequest request,
            CancellationToken cancellationToken)
        {
            FilesResource.GetRequest sdkRequest =
                CreateMetadataRequest(_drive, request);
            DriveFile file = await sdkRequest.ExecuteAsync(cancellationToken);
            return Map(file);
        }

        public async Task DownloadAsync(
            GoogleDriveTextContentMediaRequest request,
            Stream destination,
            CancellationToken cancellationToken)
        {
            FilesResource.GetRequest sdkRequest =
                CreateMediaRequest(_drive, request);
            IDownloadProgress progress = await sdkRequest.DownloadAsync(
                destination,
                cancellationToken).ConfigureAwait(false);

            if (progress.Status == DownloadStatus.Completed)
                return;

            if (progress.Exception is OperationCanceledException cancellation)
                throw cancellation;

            throw progress.Exception ?? new IOException(
                "The Google Drive media download did not complete.");
        }

        public void Dispose() => _drive.Dispose();

        internal static FilesResource.GetRequest CreateMetadataRequest(
            DriveService drive,
            GoogleDriveTextContentMetadataRequest request)
        {
            FilesResource.GetRequest sdkRequest = drive.Files.Get(request.FileId);
            sdkRequest.Fields = request.Fields;
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            return sdkRequest;
        }

        internal static FilesResource.GetRequest CreateMediaRequest(
            DriveService drive,
            GoogleDriveTextContentMediaRequest request)
        {
            FilesResource.GetRequest sdkRequest = drive.Files.Get(request.FileId);
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            sdkRequest.AcknowledgeAbuse = request.AcknowledgeAbuse;
            return sdkRequest;
        }

        internal static GoogleDriveTextContentMetadata Map(DriveFile file) =>
            new(
                file.Id,
                file.MimeType,
                file.Trashed ?? false,
                file.DriveId,
                file.Size,
                file.Capabilities?.CanDownload ?? false);
    }

    internal sealed class GoogleDriveTextContentLimitExceededException
        : IOException
    {
        public GoogleDriveTextContentLimitExceededException()
            : base("The Google Drive text content exceeded the allowed size.")
        {
        }
    }

    internal sealed class BoundedMemoryWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly long _maximumLength;

        public BoundedMemoryWriteStream(long maximumLength)
        {
            if (maximumLength < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumLength));

            _maximumLength = maximumLength;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _inner.WriteByte(value);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }

        private void EnsureCapacity(int incomingCount)
        {
            if (incomingCount < 0 || incomingCount > _maximumLength - _inner.Length)
                throw new GoogleDriveTextContentLimitExceededException();
        }
    }
}
