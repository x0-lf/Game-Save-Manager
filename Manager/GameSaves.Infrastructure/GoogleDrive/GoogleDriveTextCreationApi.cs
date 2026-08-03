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
    internal static class GoogleDriveTextCreationMediaTypes
    {
        public const string Json = "application/json";
    }

    internal static class GoogleDriveTextCreationErrorCodes
    {
        public const string InvalidUtf8 = "GoogleDriveTextCreationInvalidUtf8";
        public const string ContentTooLarge =
            "GoogleDriveTextCreationContentTooLarge";
        public const string InvalidResponse =
            "GoogleDriveTextCreationInvalidResponse";
        public const string NameMismatch =
            "GoogleDriveTextCreationNameMismatch";
        public const string MimeTypeMismatch =
            "GoogleDriveTextCreationMimeTypeMismatch";
        public const string ParentMismatch =
            "GoogleDriveTextCreationParentMismatch";
        public const string Trashed = "GoogleDriveTextCreationTrashed";
        public const string UnsupportedLocation =
            "GoogleDriveTextCreationUnsupportedLocation";
    }

    internal sealed class GoogleDriveTextCreateRequest
    {
        public GoogleDriveTextCreateRequest(
            string parentFolderId,
            string exactFileName,
            int contentLength,
            string mediaType)
        {
            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                throw new ArgumentException(
                    "A Google Drive parent folder ID is required.",
                    nameof(parentFolderId));
            }
            if (string.IsNullOrEmpty(exactFileName))
            {
                throw new ArgumentException(
                    "An exact Google Drive file name is required.",
                    nameof(exactFileName));
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

            ParentFolderId = parentFolderId;
            ParentIds = Array.AsReadOnly(new[] { parentFolderId });
            ExactFileName = exactFileName;
            ContentLength = contentLength;
            MediaType = mediaType;
        }

        public string ParentFolderId { get; }

        public IReadOnlyList<string> ParentIds { get; }

        public string ExactFileName { get; }

        public int ContentLength { get; }

        public string MediaType { get; }

        public string Fields => GoogleDriveRequestContract.TextCreationResponseFields;

        public bool SupportsAllDrives => GoogleDriveRequestContract.SupportsAllDrives;

        public override string ToString() =>
            "Google Drive bounded text-creation request";
    }

    internal sealed class GoogleDriveTextCreationResponse
    {
        public GoogleDriveTextCreationResponse(
            string? id,
            string? name,
            string? mimeType,
            bool? trashed,
            IEnumerable<string>? parentIds,
            string? driveId)
        {
            Id = id;
            Name = name;
            MimeType = mimeType;
            Trashed = trashed;
            ParentIds = parentIds?.ToArray();
            DriveId = driveId;
        }

        public string? Id { get; }

        public string? Name { get; }

        public string? MimeType { get; }

        public bool? Trashed { get; }

        public IReadOnlyList<string>? ParentIds { get; }

        public string? DriveId { get; }

        public override string ToString() =>
            "Google Drive text-creation response: " +
            $"idPresent={!string.IsNullOrWhiteSpace(Id)}; " +
            $"namePresent={Name is not null}; mimeTypePresent={MimeType is not null}; " +
            $"trashedPresent={Trashed is not null}; parents={ParentIds?.Count ?? -1}; " +
            $"sharedDrive={DriveId is not null}";
    }

    internal sealed class GoogleDriveTextCreationResult
    {
        public GoogleDriveTextCreationResult(string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "A created Google Drive file ID is required.",
                    nameof(fileId));
            }

            FileId = fileId;
        }

        public string FileId { get; }

        public override string ToString() =>
            "Google Drive text file created";
    }

    internal interface IGoogleDriveTextCreationClient : IDisposable
    {
        Task<GoogleDriveTextCreationResponse> CreateAsync(
            GoogleDriveTextCreateRequest request,
            Stream content,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveTextCreationClientFactory
    {
        IGoogleDriveTextCreationClient Create(
            GoogleAuthorizedCredential credential);
    }

    internal interface IGoogleDriveTextCreationApi
    {
        Task<GoogleDriveTextCreationResult> CreateTextFileAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            string exactFileName,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Creates one bounded UTF-8 blob with one authoritative parent. This
    /// low-level API deliberately performs no existence lookup and exposes no
    /// update, rename, move, trash, or delete operation. Higher layers remain
    /// responsible for create-only name coordination.
    /// </summary>
    internal sealed class GoogleDriveTextCreationApi
        : IGoogleDriveTextCreationApi
    {
        public const int MaxTextContentBytes =
            GoogleDriveTextContentApi.MaxTextContentBytes;

        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private readonly IGoogleDriveTextCreationClientFactory _clientFactory;

        public GoogleDriveTextCreationApi(
            IGoogleDriveTextCreationClientFactory clientFactory) =>
            _clientFactory = clientFactory ??
                throw new ArgumentNullException(nameof(clientFactory));

        public async Task<GoogleDriveTextCreationResult> CreateTextFileAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            string exactFileName,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);

            var request = new GoogleDriveTextCreateRequest(
                parentFolderId,
                exactFileName,
                contentBytes.Length,
                mediaType);

            if (contentBytes.Length > MaxTextContentBytes)
            {
                throw Failure(
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextCreationErrorCodes.ContentTooLarge,
                    retryable: false);
            }

            try
            {
                _ = StrictUtf8.GetCharCount(contentBytes.Span);
            }
            catch (DecoderFallbackException)
            {
                throw Failure(
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextCreationErrorCodes.InvalidUtf8,
                    retryable: false);
            }

            IGoogleDriveTextCreationClient client;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                client = _clientFactory.Create(credential) ??
                    throw new InvalidOperationException(
                        "The Google Drive text-creation client is unavailable.");
            }
            catch (Exception ex)
            {
                throw MapException(ex);
            }

            using (client)
            using (var content = new MemoryStream(
                contentBytes.ToArray(),
                writable: false))
            {
                GoogleDriveTextCreationResponse response;
                try
                {
                    response = await client.CreateAsync(
                        request,
                        content,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception ex)
                {
                    throw MapException(ex);
                }

                return ValidateResponse(response, request);
            }
        }

        private static GoogleDriveTextCreationResult ValidateResponse(
            GoogleDriveTextCreationResponse response,
            GoogleDriveTextCreateRequest request)
        {
            if (response is null ||
                string.IsNullOrWhiteSpace(response.Id) ||
                response.Name is null ||
                response.MimeType is null ||
                response.Trashed is null ||
                response.ParentIds is null)
            {
                throw Failure(
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextCreationErrorCodes.InvalidResponse,
                    retryable: false);
            }

            if (!string.Equals(
                    response.Name,
                    request.ExactFileName,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextCreationErrorCodes.NameMismatch,
                    retryable: false);
            }

            if (!string.Equals(
                    response.MimeType,
                    request.MediaType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Failure(
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextCreationErrorCodes.MimeTypeMismatch,
                    retryable: false);
            }

            if (response.Trashed.Value)
            {
                throw Failure(
                    GoogleDriveApiFailure.NotFound,
                    GoogleDriveTextCreationErrorCodes.Trashed,
                    retryable: false);
            }

            if (response.DriveId is not null)
            {
                throw Failure(
                    GoogleDriveApiFailure.AccessDenied,
                    GoogleDriveTextCreationErrorCodes.UnsupportedLocation,
                    retryable: false);
            }

            if (response.ParentIds.Count != 1 ||
                !string.Equals(
                    response.ParentIds[0],
                    request.ParentFolderId,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveApiFailure.Failed,
                    GoogleDriveTextCreationErrorCodes.ParentMismatch,
                    retryable: false);
            }

            return new GoogleDriveTextCreationResult(response.Id);
        }

        internal static GoogleDriveApiException MapException(
            Exception exception) =>
            GoogleDriveApiFailureMapper.Map(
                exception,
                GoogleDriveApiOperation.TextContentCreate,
                failure => $"GoogleDriveTextCreation{failure}");

        private static GoogleDriveApiException Failure(
            GoogleDriveApiFailure failure,
            string errorCode,
            bool retryable) =>
            GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.TextContentCreate,
                failure,
                errorCode,
                retryable);
    }

    internal sealed class GoogleDriveTextCreationClientFactory
        : IGoogleDriveTextCreationClientFactory
    {
        public IGoogleDriveTextCreationClient Create(
            GoogleAuthorizedCredential credential) =>
            new GoogleDriveTextCreationClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = GoogleDriveRequestContract.ApplicationName
                }));
    }

    internal sealed class GoogleDriveTextCreationClient
        : IGoogleDriveTextCreationClient
    {
        private const int MaxResponseBytes = 256 * 1024;
        private const string MultipartUploadEndpoint =
            "https://www.googleapis.com/upload/drive/v3/files";

        private readonly DriveService _drive;

        public GoogleDriveTextCreationClient(DriveService drive) =>
            _drive = drive ?? throw new ArgumentNullException(nameof(drive));

        public async Task<GoogleDriveTextCreationResponse> CreateAsync(
            GoogleDriveTextCreateRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage httpRequest = CreateHttpRequest(
                _drive,
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
            return Map(file);
        }

        public void Dispose() => _drive.Dispose();

        internal static HttpRequestMessage CreateHttpRequest(
            DriveService drive,
            GoogleDriveTextCreateRequest request,
            Stream content)
        {
            ArgumentNullException.ThrowIfNull(drive);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(content);

            var metadata = new DriveFile
            {
                Name = request.ExactFileName,
                MimeType = request.MediaType,
                Parents = request.ParentIds.ToArray()
            };
            string metadataJson = drive.Serializer.Serialize(metadata);
            var metadataContent = new StringContent(
                metadataJson,
                Encoding.UTF8,
                GoogleDriveTextCreationMediaTypes.Json);
            var mediaContent = new StreamContent(content);
            mediaContent.Headers.ContentType =
                MediaTypeHeaderValue.Parse(request.MediaType);
            var multipart = new MultipartContent("related")
            {
                metadataContent,
                mediaContent
            };

            string requestUri =
                MultipartUploadEndpoint +
                "?uploadType=multipart" +
                "&supportsAllDrives=false" +
                "&fields=" + Uri.EscapeDataString(request.Fields);

            return new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = multipart
            };
        }

        internal static GoogleDriveTextCreationResponse Map(DriveFile? file) =>
            new(
                file?.Id,
                file?.Name,
                file?.MimeType,
                file?.Trashed,
                file?.Parents,
                file?.DriveId);

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
                "The Google Drive text creation request did not complete.")
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
