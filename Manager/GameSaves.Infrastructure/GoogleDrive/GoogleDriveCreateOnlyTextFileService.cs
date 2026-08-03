using System.Text;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveCreateOnlyTextFileService
    {
        Task CreateAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default);
    }

    internal static class GoogleDriveCreateOnlyTextFileErrorCodes
    {
        public const string AlreadyExists =
            "GoogleDriveCreateOnlyTextFileAlreadyExists";
        public const string InvalidParentResolution =
            "GoogleDriveCreateOnlyTextFileInvalidParentResolution";
        public const string InvalidTargetResolution =
            "GoogleDriveCreateOnlyTextFileInvalidTargetResolution";
        public const string InvalidCreateResponse =
            "GoogleDriveCreateOnlyTextFileInvalidCreateResponse";
        public const string CacheRejected =
            "GoogleDriveCreateOnlyTextFileCacheRejected";
    }

    /// <summary>
    /// Creates one immutable text blob after two authoritative exact-name
    /// checks coordinated by parent ID and exact file name. Google Drive does
    /// not enforce globally unique names: this lock prevents duplicate creates
    /// only within this process. A cross-process race is surfaced by a later
    /// authoritative lookup as ambiguity and is never resolved by overwriting.
    /// </summary>
    internal sealed class GoogleDriveCreateOnlyTextFileService
        : IGoogleDriveCreateOnlyTextFileService
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveTextCreationApi _textCreationApi;
        private readonly GoogleDriveObjectCreationCoordinator _creationCoordinator;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveCreateOnlyTextFileService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveTextCreationApi textCreationApi,
            GoogleDriveObjectCreationCoordinator creationCoordinator,
            IGoogleDriveObjectIdCache objectIdCache)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _textCreationApi = textCreationApi ??
                throw new ArgumentNullException(nameof(textCreationApi));
            _creationCoordinator = creationCoordinator ??
                throw new ArgumentNullException(nameof(creationCoordinator));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
        }

        public async Task CreateAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            GoogleDriveRelativePath path =
                GoogleDriveRelativePath.Parse(relativePath);
            if (path.IsRoot)
            {
                throw new ArgumentException(
                    "A Google Drive text-file path is required.",
                    nameof(relativePath));
            }

            ArgumentNullException.ThrowIfNull(content);
            byte[] contentBytes = EncodeContent(content);

            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string exactFileName = path.Segments[^1];
            GoogleDriveRelativePath parentPath = ParentPath(path);
            string parentId = parentPath.IsRoot
                ? context.RootFolderId
                : await EnsureParentAsync(
                    context,
                    parentPath,
                    cancellationToken).ConfigureAwait(false);

            var cacheScope = new GoogleDriveObjectCacheScope(
                context.RemoteProfileId,
                context.RootFolderId);

            await RequireMissingAsync(
                context,
                cacheScope,
                parentId,
                exactFileName,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using IDisposable lease = await _creationCoordinator.AcquireAsync(
                parentId,
                exactFileName,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await RequireMissingAsync(
                context,
                cacheScope,
                parentId,
                exactFileName,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveTextCreationResult created;
            try
            {
                created = await _textCreationApi.CreateTextFileAsync(
                    context.Credential,
                    parentId,
                    exactFileName,
                    contentBytes,
                    GoogleDriveTextCreationMediaTypes.Json,
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
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.InvalidCreateResponse,
                    "The Google Drive text file was not created safely.");
            }

            if (created is null || string.IsNullOrWhiteSpace(created.FileId))
            {
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.InvalidCreateResponse,
                    "The Google Drive text file was not created safely.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var metadata = new GoogleDriveObjectMetadata(
                created.FileId,
                exactFileName,
                GoogleDriveTextCreationMediaTypes.Json,
                trashed: false,
                parentIds: [parentId],
                driveId: null);

            bool cached;
            try
            {
                cached = _objectIdCache.TryStoreUniqueValidated(
                    cacheScope,
                    parentId,
                    exactFileName,
                    GoogleDriveObjectKind.File,
                    metadata);
            }
            catch
            {
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.CacheRejected,
                    "The created Google Drive text file could not be recorded safely.");
            }

            if (!cached)
            {
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.CacheRejected,
                    "The created Google Drive text file could not be recorded safely.");
            }
        }

        private async Task<string> EnsureParentAsync(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveRelativePath parentPath,
            CancellationToken cancellationToken)
        {
            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.EnsureFolderPathAsync(
                    context.RootFolderId,
                    parentPath,
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
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.InvalidParentResolution,
                    "The Google Drive parent folder could not be resolved safely.");
            }

            if (resolution is not null &&
                (resolution.Status is GoogleDriveObjectResolutionStatus.Found or
                    GoogleDriveObjectResolutionStatus.Created) &&
                resolution.ObjectKind == GoogleDriveObjectKind.Folder &&
                !string.IsNullOrWhiteSpace(resolution.ObjectId))
            {
                return resolution.ObjectId;
            }

            if (resolution is null)
            {
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.InvalidParentResolution,
                    "The Google Drive parent folder could not be resolved safely.");
            }

            throw ResolutionFailure(resolution);
        }

        private async Task RequireMissingAsync(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            string exactFileName,
            CancellationToken cancellationToken)
        {
            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.FindChildAsync(
                    parentId,
                    exactFileName,
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
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.InvalidTargetResolution,
                    "The Google Drive text-file target could not be resolved safely.");
            }

            if (resolution is null)
            {
                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.InvalidTargetResolution,
                    "The Google Drive text-file target could not be resolved safely.");
            }

            if (resolution.Status == GoogleDriveObjectResolutionStatus.NotFound)
            {
                TryInvalidateConfirmedStale(
                    resolution,
                    cacheScope,
                    parentId,
                    exactFileName);
                return;
            }

            if (resolution.Status == GoogleDriveObjectResolutionStatus.Found)
            {
                if (resolution.Metadata is not null)
                {
                    try
                    {
                        _objectIdCache.TryStoreUniqueValidated(
                            cacheScope,
                            parentId,
                            exactFileName,
                            GoogleDriveObjectKind.File,
                            resolution.Metadata);
                    }
                    catch
                    {
                        // Existing remote state remains authoritative. Cache
                        // maintenance must not weaken the create-only refusal.
                    }
                }

                throw Failure(
                    GoogleDriveCreateOnlyTextFileErrorCodes.AlreadyExists,
                    "A Google Drive object already exists at the immutable text-file path.");
            }

            TryInvalidateConfirmedStale(
                resolution,
                cacheScope,
                parentId,
                exactFileName);

            throw ResolutionFailure(resolution);
        }

        private void TryInvalidateConfirmedStale(
            GoogleDriveObjectResolutionResult resolution,
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            string exactFileName)
        {
            try
            {
                if (resolution.Status ==
                    GoogleDriveObjectResolutionStatus.ReauthenticationRequired)
                {
                    _objectIdCache.InvalidateProfile(
                        cacheScope.RemoteProfileId,
                        GoogleDriveObjectCacheInvalidationReason
                            .AuthorizationRevocation);
                    return;
                }

                if (resolution.Status is
                    GoogleDriveObjectResolutionStatus.NotFound or
                    GoogleDriveObjectResolutionStatus.Ambiguous or
                    GoogleDriveObjectResolutionStatus.TypeMismatch or
                    GoogleDriveObjectResolutionStatus.Trashed or
                    GoogleDriveObjectResolutionStatus.UnsupportedLocation or
                    GoogleDriveObjectResolutionStatus.AccessDenied)
                {
                    _objectIdCache.Remove(
                        cacheScope,
                        parentId,
                        exactFileName,
                        GoogleDriveObjectKind.File);
                }
            }
            catch
            {
                // Remote state remains authoritative. Cache maintenance must
                // neither permit creation nor replace the sanitized failure.
            }
        }

        private static byte[] EncodeContent(string content)
        {
            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(content);
            }
            catch (EncoderFallbackException)
            {
                throw Failure(
                    GoogleDriveTextCreationErrorCodes.InvalidUtf8,
                    "The Google Drive text content is not valid UTF-8.");
            }

            if (bytes.Length > GoogleDriveTextCreationApi.MaxTextContentBytes)
            {
                throw Failure(
                    GoogleDriveTextCreationErrorCodes.ContentTooLarge,
                    "The Google Drive text content is too large.");
            }

            return bytes;
        }

        private static GoogleDriveRelativePath ParentPath(
            GoogleDriveRelativePath path) =>
            path.Segments.Count == 1
                ? GoogleDriveRelativePath.Root
                : GoogleDriveRelativePath.Parse(
                    string.Join('/', path.Segments.Take(path.Segments.Count - 1)));

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
