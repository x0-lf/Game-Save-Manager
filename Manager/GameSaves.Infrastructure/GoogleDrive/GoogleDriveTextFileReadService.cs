using System.Text;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveTextFileReadService
    {
        Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default);
    }

    internal static class GoogleDriveTextFileReadErrorCodes
    {
        public const string InvalidUtf8 = "GoogleDriveTextFileInvalidUtf8";
        public const string InvalidResolution =
            "GoogleDriveTextFileInvalidResolution";
    }

    /// <summary>
    /// Resolves one Drive-relative file beneath the authoritative application
    /// root and decodes its bounded blob content as strict UTF-8. This service
    /// is read-only and never creates missing parents or content.
    /// </summary>
    internal sealed class GoogleDriveTextFileReadService
        : IGoogleDriveTextFileReadService
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveTextContentApi _textContentApi;

        public GoogleDriveTextFileReadService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveTextContentApi textContentApi)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _textContentApi = textContentApi ??
                throw new ArgumentNullException(nameof(textContentApi));
        }

        public async Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            GoogleDriveRelativePath path =
                GoogleDriveRelativePath.Parse(relativePath);

            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);

            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.ResolveAsync(
                    context.RootFolderId,
                    path,
                    GoogleDriveObjectKind.File,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveRemoteOperationException)
            {
                throw;
            }
            catch
            {
                throw InvalidResolution();
            }

            if (resolution is null)
                throw InvalidResolution();

            if (resolution.Status == GoogleDriveObjectResolutionStatus.NotFound)
                return null;

            if (resolution.Status != GoogleDriveObjectResolutionStatus.Found)
            {
                throw ResolutionFailure(resolution);
            }

            if (resolution.ObjectKind != GoogleDriveObjectKind.File ||
                string.IsNullOrWhiteSpace(resolution.ObjectId))
                throw InvalidResolution();

            GoogleDriveTextContentResult content;
            try
            {
                content = await _textContentApi.DownloadTextContentAsync(
                    context.Credential,
                    resolution.ObjectId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                throw ApiFailure(ex);
            }
            catch (GoogleDriveRemoteOperationException)
            {
                throw;
            }
            catch
            {
                throw InvalidResolution();
            }

            if (content is null)
                throw InvalidResolution();

            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = content.ToArray();
            int offset = HasUtf8Preamble(bytes) ? 3 : 0;

            try
            {
                return StrictUtf8.GetString(
                    bytes,
                    offset,
                    bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                throw Failure(
                    GoogleDriveTextFileReadErrorCodes.InvalidUtf8,
                    "The Google Drive text file is not valid UTF-8.");
            }
        }

        private static bool HasUtf8Preamble(byte[] bytes) =>
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        private static GoogleDriveRemoteOperationException ApiFailure(
            GoogleDriveApiException exception)
        {
            GoogleDriveRemoteValidationResult mapped =
                GoogleDriveRemoteValidationMapper.FromApiFailure(
                    exception.Details);

            return new GoogleDriveRemoteOperationException(
                new GoogleDriveRemoteValidationResult(
                    mapped.Status,
                    exception.Details.SafeErrorCode,
                    mapped.UserMessage,
                    mapped.Retryable,
                    rootDisplayName: null,
                    wasAuthenticationRefreshed: false,
                    cacheInvalidated: false));
        }

        private static GoogleDriveRemoteOperationException ResolutionFailure(
            GoogleDriveObjectResolutionResult resolution)
        {
            GoogleDriveRemoteValidationResult mapped =
                GoogleDriveRemoteValidationMapper.FromObjectResolution(
                    resolution);

            return new GoogleDriveRemoteOperationException(
                new GoogleDriveRemoteValidationResult(
                    mapped.Status,
                    resolution.ErrorCode ?? mapped.ErrorCode,
                    mapped.UserMessage,
                    mapped.Retryable,
                    rootDisplayName: null,
                    wasAuthenticationRefreshed: false,
                    cacheInvalidated: false));
        }

        private static GoogleDriveRemoteOperationException InvalidResolution() =>
            Failure(
                GoogleDriveTextFileReadErrorCodes.InvalidResolution,
                "The Google Drive text file could not be resolved safely.");

        private static GoogleDriveRemoteOperationException Failure(
            string errorCode,
            string userMessage) =>
            new(new GoogleDriveRemoteValidationResult(
                GoogleDriveRemoteValidationStatus.Failed,
                errorCode,
                userMessage,
                retryable: false,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));
    }
}
