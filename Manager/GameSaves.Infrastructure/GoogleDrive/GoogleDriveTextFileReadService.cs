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
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveTextFileReadService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveTextContentApi textContentApi,
            IGoogleDriveObjectIdCache objectIdCache)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _textContentApi = textContentApi ??
                throw new ArgumentNullException(nameof(textContentApi));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
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
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.ResolveAsync(
                    context.RootFolderId,
                    path,
                    GoogleDriveObjectKind.File,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                bool invalidated = TryInvalidateConfirmedStale(
                    context,
                    resolution,
                    ex);
                throw ApiFailure(ex, invalidated);
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

        private bool TryInvalidateConfirmedStale(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveObjectResolutionResult resolution,
            GoogleDriveApiException exception)
        {
            bool profileInvalidation =
                exception.Failure == GoogleDriveApiFailure.AuthorizationRevoked;
            bool scopeInvalidation =
                exception.Failure is GoogleDriveApiFailure.NotFound or
                    GoogleDriveApiFailure.AccessDenied or
                    GoogleDriveApiFailure.InsufficientScope or
                    GoogleDriveApiFailure.ApiNotEnabled ||
                exception.Details.SafeErrorCode is
                    GoogleDriveTextContentErrorCodes.InvalidMetadata or
                    GoogleDriveTextContentErrorCodes.Folder or
                    GoogleDriveTextContentErrorCodes.Trashed or
                    GoogleDriveTextContentErrorCodes.WorkspaceDocument or
                    GoogleDriveTextContentErrorCodes.UnsupportedLocation or
                    GoogleDriveTextContentErrorCodes.DownloadNotAllowed;

            if (!profileInvalidation && !scopeInvalidation)
                return false;

            try
            {
                if (profileInvalidation)
                {
                    _objectIdCache.InvalidateProfile(
                        context.RemoteProfileId,
                        GoogleDriveObjectCacheInvalidationReason
                            .AuthorizationRevocation);
                }
                else
                {
                    GoogleDriveObjectCacheScope scope = new(
                        context.RemoteProfileId,
                        context.RootFolderId);
                    string? parentId = resolution.Metadata?.ParentIds.Count == 1
                        ? resolution.Metadata.ParentIds[0]
                        : null;
                    string? exactName = resolution.Path?.Segments.Count > 0
                        ? resolution.Path.Segments[^1]
                        : null;
                    if (parentId is not null && exactName is not null)
                    {
                        _objectIdCache.Remove(
                            scope,
                            parentId,
                            exactName,
                            GoogleDriveObjectKind.File);
                    }
                    else
                    {
                        _objectIdCache.ClearScope(scope);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static GoogleDriveRemoteOperationException ApiFailure(
            GoogleDriveApiException exception,
            bool cacheInvalidated)
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
                    cacheInvalidated));
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
