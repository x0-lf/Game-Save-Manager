using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameSaves.Core.Sync;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Requests;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal static class GoogleDriveTextReplacementErrorCodes
    {
        public const string InvalidUtf8 =
            "GoogleDriveTextReplacementInvalidUtf8";
        public const string ContentTooLarge =
            "GoogleDriveTextReplacementContentTooLarge";
        public const string InvalidMetadata =
            "GoogleDriveTextReplacementInvalidMetadata";
        public const string Folder = "GoogleDriveTextReplacementFolder";
        public const string Trashed = "GoogleDriveTextReplacementTrashed";
        public const string WorkspaceDocument =
            "GoogleDriveTextReplacementWorkspaceDocument";
        public const string UnsupportedLocation =
            "GoogleDriveTextReplacementUnsupportedLocation";
        public const string InvalidResponse =
            "GoogleDriveTextReplacementInvalidResponse";
        public const string IdentityMismatch =
            "GoogleDriveTextReplacementIdentityMismatch";
    }

    internal sealed class GoogleDriveTextReplacementMetadataRequest
    {
        public GoogleDriveTextReplacementMetadataRequest(string fileId)
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

        public string Fields =>
            GoogleDriveRequestContract.TextReplacementMetadataFields;

        // Authoritative-ID inspection identifies objects moved into a shared
        // drive so this My Drive-only operation can reject them explicitly.
        public bool SupportsAllDrives =>
            GoogleDriveRequestContract.AuthoritativeIdLookupSupportsAllDrives;

        public override string ToString() =>
            "Google Drive text-replacement metadata request";
    }

    internal sealed class GoogleDriveTextReplacementRequest
    {
        public GoogleDriveTextReplacementRequest(
            string fileId,
            int contentLength,
            string mediaType)
        {
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "A Google Drive file ID is required.",
                    nameof(fileId));
            }
            if (contentLength < 0)
                throw new ArgumentOutOfRangeException(nameof(contentLength));
            if (!MediaTypeHeaderValue.TryParse(mediaType, out var parsed) ||
                string.IsNullOrWhiteSpace(parsed.MediaType))
            {
                throw new ArgumentException(
                    "A valid text-content media type is required.",
                    nameof(mediaType));
            }

            FileId = fileId;
            ContentLength = contentLength;
            MediaType = mediaType;
        }

        public string FileId { get; }

        public int ContentLength { get; }

        public string MediaType { get; }

        public string Fields =>
            GoogleDriveRequestContract.TextReplacementResponseFields;

        public bool SupportsAllDrives =>
            GoogleDriveRequestContract.SupportsAllDrives;

        public override string ToString() =>
            "Google Drive bounded text-replacement request";
    }

    internal sealed class GoogleDriveTextReplacementMetadata
    {
        public GoogleDriveTextReplacementMetadata(
            string? id,
            string? mimeType,
            bool? trashed,
            string? driveId)
        {
            Id = id;
            MimeType = mimeType;
            Trashed = trashed;
            DriveId = driveId;
        }

        public string? Id { get; }

        public string? MimeType { get; }

        public bool? Trashed { get; }

        public string? DriveId { get; }

        public bool IsFolder => string.Equals(
            MimeType,
            GoogleDriveApplicationRoot.FolderMimeType,
            StringComparison.Ordinal);

        public bool IsWorkspaceObject => MimeType?.StartsWith(
            "application/vnd.google-apps.",
            StringComparison.Ordinal) == true;

        public override string ToString() =>
            "Google Drive text-replacement metadata: " +
            $"idPresent={!string.IsNullOrWhiteSpace(Id)}; " +
            $"mimeTypePresent={MimeType is not null}; " +
            $"trashedPresent={Trashed is not null}; " +
            $"sharedDrive={DriveId is not null}; folder={IsFolder}; " +
            $"workspace={IsWorkspaceObject}";
    }

    internal sealed class GoogleDriveTextReplacementResponse
    {
        public GoogleDriveTextReplacementResponse(string? id, string? driveId)
        {
            Id = id;
            DriveId = driveId;
        }

        public string? Id { get; }

        public string? DriveId { get; }

        public override string ToString() =>
            "Google Drive text-replacement response: " +
            $"idPresent={!string.IsNullOrWhiteSpace(Id)}; " +
            $"sharedDrive={DriveId is not null}";
    }

    internal sealed class GoogleDriveTextReplacementResult
    {
        public GoogleDriveTextReplacementResult(string fileId)
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

        public override string ToString() =>
            "Google Drive text content replaced";
    }

    internal interface IGoogleDriveTextReplacementClient : IDisposable
    {
        Task<GoogleDriveTextReplacementMetadata> GetMetadataAsync(
            GoogleDriveTextReplacementMetadataRequest request,
            CancellationToken cancellationToken);

        Task<GoogleDriveTextReplacementResponse> UpdateContentAsync(
            GoogleDriveTextReplacementRequest request,
            Stream content,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveTextReplacementClientFactory
    {
        IGoogleDriveTextReplacementClient Create(
            GoogleAuthorizedCredential credential);
    }

    internal interface IGoogleDriveTextReplacementApi
    {
        Task<GoogleDriveTextReplacementResult> ReplaceTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Replaces only the bounded UTF-8 content of one existing My Drive blob
    /// by authoritative file ID. The media-only files.update request carries
    /// no file metadata, so it cannot rename, reparent, move, trash, delete,
    /// or change permissions. It never performs name lookup or create fallback.
    /// </summary>
    internal sealed class GoogleDriveTextReplacementApi
        : IGoogleDriveTextReplacementApi
    {
        public const int MaxTextContentBytes =
            GoogleDriveTextContentApi.MaxTextContentBytes;

        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private readonly IGoogleDriveTextReplacementClientFactory _clientFactory;

        public GoogleDriveTextReplacementApi(
            IGoogleDriveTextReplacementClientFactory clientFactory) =>
            _clientFactory = clientFactory ??
                throw new ArgumentNullException(nameof(clientFactory));

        public async Task<GoogleDriveTextReplacementResult> ReplaceTextContentAsync(
            GoogleAuthorizedCredential credential,
            string fileId,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            var metadataRequest =
                new GoogleDriveTextReplacementMetadataRequest(fileId);
            var replacementRequest = new GoogleDriveTextReplacementRequest(
                fileId,
                contentBytes.Length,
                mediaType);

            if (contentBytes.Length > MaxTextContentBytes)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplace,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.ContentTooLarge,
                    retryable: false);
            }

            try
            {
                _ = StrictUtf8.GetCharCount(contentBytes.Span);
            }
            catch (DecoderFallbackException)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplace,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.InvalidUtf8,
                    retryable: false);
            }

            IGoogleDriveTextReplacementClient client;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                client = _clientFactory.Create(credential) ??
                    throw new InvalidOperationException(
                        "The Google Drive text-replacement client is unavailable.");
            }
            catch (Exception ex)
            {
                throw MapException(
                    ex,
                    GoogleDriveApiOperation.TextContentReplacementMetadataGet);
            }

            using (client)
            {
                GoogleDriveTextReplacementMetadata metadata;
                try
                {
                    metadata = await client.GetMetadataAsync(
                        metadataRequest,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw MapException(
                        ex,
                        GoogleDriveApiOperation.TextContentReplacementMetadataGet);
                }

                ValidateMetadata(metadata, fileId);
                cancellationToken.ThrowIfCancellationRequested();

                using var content = new MemoryStream(
                    contentBytes.ToArray(),
                    writable: false);
                GoogleDriveTextReplacementResponse response;
                try
                {
                    response = await client.UpdateContentAsync(
                        replacementRequest,
                        content,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw MapException(
                        ex,
                        GoogleDriveApiOperation.TextContentReplace);
                }

                return ValidateResponse(response, fileId);
            }
        }

        private static void ValidateMetadata(
            GoogleDriveTextReplacementMetadata metadata,
            string expectedFileId)
        {
            if (metadata is null ||
                !string.Equals(
                    metadata.Id,
                    expectedFileId,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(metadata.MimeType) ||
                metadata.Trashed is null)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplacementMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.InvalidMetadata,
                    retryable: false);
            }

            if (metadata.DriveId is not null)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplacementMetadataGet,
                    GoogleDriveApiFailure.AccessDenied,
                    GoogleDriveTextReplacementErrorCodes.UnsupportedLocation,
                    retryable: false);
            }

            if (metadata.Trashed.Value)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplacementMetadataGet,
                    GoogleDriveApiFailure.NotFound,
                    GoogleDriveTextReplacementErrorCodes.Trashed,
                    retryable: false);
            }

            if (metadata.IsFolder)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplacementMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.Folder,
                    retryable: false);
            }

            if (metadata.IsWorkspaceObject)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplacementMetadataGet,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.WorkspaceDocument,
                    retryable: false);
            }
        }

        private static GoogleDriveTextReplacementResult ValidateResponse(
            GoogleDriveTextReplacementResponse response,
            string expectedFileId)
        {
            if (response is null || string.IsNullOrWhiteSpace(response.Id))
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplace,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.InvalidResponse,
                    retryable: false);
            }

            if (!string.Equals(
                    response.Id,
                    expectedFileId,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplace,
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextReplacementErrorCodes.IdentityMismatch,
                    retryable: false);
            }

            if (response.DriveId is not null)
            {
                throw Failure(
                    GoogleDriveApiOperation.TextContentReplace,
                    GoogleDriveApiFailure.AccessDenied,
                    GoogleDriveTextReplacementErrorCodes.UnsupportedLocation,
                    retryable: false);
            }

            return new GoogleDriveTextReplacementResult(response.Id);
        }

        internal static GoogleDriveApiException MapException(
            Exception exception,
            GoogleDriveApiOperation operation) =>
            GoogleDriveApiFailureMapper.Map(
                exception,
                operation,
                failure => $"GoogleDriveTextReplacement{failure}");

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

    internal sealed class GoogleDriveTextReplacementClientFactory
        : IGoogleDriveTextReplacementClientFactory
    {
        public IGoogleDriveTextReplacementClient Create(
            GoogleAuthorizedCredential credential) =>
            new GoogleDriveTextReplacementClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = GoogleDriveRequestContract.ApplicationName
                }));
    }

    internal sealed class GoogleDriveTextReplacementClient
        : IGoogleDriveTextReplacementClient
    {
        private const int MaxResponseBytes = 256 * 1024;
        private const string MediaUploadEndpoint =
            "https://www.googleapis.com/upload/drive/v3/files/";

        private readonly DriveService _drive;

        public GoogleDriveTextReplacementClient(DriveService drive) =>
            _drive = drive ?? throw new ArgumentNullException(nameof(drive));

        public async Task<GoogleDriveTextReplacementMetadata> GetMetadataAsync(
            GoogleDriveTextReplacementMetadataRequest request,
            CancellationToken cancellationToken)
        {
            FilesResource.GetRequest sdkRequest =
                CreateMetadataRequest(_drive, request);
            DriveFile file = await sdkRequest.ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            return MapMetadata(file);
        }

        public async Task<GoogleDriveTextReplacementResponse> UpdateContentAsync(
            GoogleDriveTextReplacementRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage httpRequest = CreateUpdateHttpRequest(
                request,
                content);
            using HttpResponseMessage response = await _drive.HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await CreateProviderExceptionAsync(
                    response,
                    cancellationToken).ConfigureAwait(false);
            }

            using Stream responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] responseBytes = await ReadBoundedAsync(
                responseStream,
                cancellationToken).ConfigureAwait(false);
            using var serialized = new MemoryStream(responseBytes, writable: false);
            DriveFile? file = _drive.Serializer.Deserialize<DriveFile>(serialized);
            return MapResponse(file);
        }

        public void Dispose() => _drive.Dispose();

        internal static FilesResource.GetRequest CreateMetadataRequest(
            DriveService drive,
            GoogleDriveTextReplacementMetadataRequest request)
        {
            FilesResource.GetRequest sdkRequest = drive.Files.Get(request.FileId);
            sdkRequest.Fields = request.Fields;
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            return sdkRequest;
        }

        internal static HttpRequestMessage CreateUpdateHttpRequest(
            GoogleDriveTextReplacementRequest request,
            Stream content)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(content);

            var mediaContent = new StreamContent(content);
            mediaContent.Headers.ContentType =
                MediaTypeHeaderValue.Parse(request.MediaType);

            string requestUri =
                MediaUploadEndpoint + Uri.EscapeDataString(request.FileId) +
                "?uploadType=media" +
                "&supportsAllDrives=false" +
                "&fields=" + Uri.EscapeDataString(request.Fields);

            return new HttpRequestMessage(HttpMethod.Patch, requestUri)
            {
                Content = mediaContent
            };
        }

        internal static GoogleDriveTextReplacementMetadata MapMetadata(
            DriveFile? file) =>
            new(file?.Id, file?.MimeType, file?.Trashed, file?.DriveId);

        internal static GoogleDriveTextReplacementResponse MapResponse(
            DriveFile? file) =>
            new(file?.Id, file?.DriveId);

        internal static async Task<GoogleApiException> CreateProviderExceptionAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            string? reason = null;
            try
            {
                using Stream responseStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                byte[] bytes = await ReadBoundedAsync(
                    responseStream,
                    cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(bytes);
                reason = ReadReason(document.RootElement);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Provider bodies are untrusted diagnostics. Status-only
                // classification remains safe when the bounded body is invalid.
            }

            var providerException = new GoogleApiException(
                "Drive",
                "The Google Drive text replacement request did not complete.")
            {
                HttpStatusCode = response.StatusCode
            };

            if (!string.IsNullOrWhiteSpace(reason))
            {
                providerException.Error = new RequestError
                {
                    Errors = new List<SingleError>
                    {
                        new() { Reason = reason }
                    }
                };
            }

            return providerException;
        }

        private static string? ReadReason(JsonElement root)
        {
            if (!root.TryGetProperty("error", out JsonElement error) ||
                !error.TryGetProperty("errors", out JsonElement errors) ||
                errors.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement item in errors.EnumerateArray())
            {
                if (item.TryGetProperty("reason", out JsonElement reason) &&
                    reason.ValueKind == JsonValueKind.String)
                {
                    return reason.GetString();
                }
            }

            return null;
        }

        private static async Task<byte[]> ReadBoundedAsync(
            Stream source,
            CancellationToken cancellationToken)
        {
            using var destination = new BoundedMemoryWriteStream(MaxResponseBytes);
            await source.CopyToAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            return destination.ToArray();
        }
    }
}
