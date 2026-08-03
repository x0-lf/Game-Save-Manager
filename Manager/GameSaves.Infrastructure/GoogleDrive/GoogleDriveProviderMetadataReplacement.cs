using System.Text;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveProviderMetadataReplacementService
    {
        Task ReplaceAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default);
    }

    internal static class GoogleDriveProviderMetadataReplacementErrorCodes
    {
        public const string InvalidParentResolution =
            "GoogleDriveProviderMetadataReplacementInvalidParentResolution";
        public const string InvalidTargetResolution =
            "GoogleDriveProviderMetadataReplacementInvalidTargetResolution";
        public const string InvalidCreateResponse =
            "GoogleDriveProviderMetadataReplacementInvalidCreateResponse";
        public const string InvalidReplaceResponse =
            "GoogleDriveProviderMetadataReplacementInvalidReplaceResponse";
        public const string CacheRejected =
            "GoogleDriveProviderMetadataReplacementCacheRejected";
    }

    /// <summary>
    /// Serializes the single allowlisted provider-metadata path per saved
    /// profile. Entries exist only while a caller holds or awaits a lease.
    /// This is in-process coordination, not a cross-process Drive lock.
    /// </summary>
    internal sealed class GoogleDriveProviderMetadataReplacementCoordinator
    {
        private readonly object _gate = new();
        private readonly Dictionary<OperationKey, Entry> _entries = new();

        public async ValueTask<IDisposable> AcquireAsync(
            Guid remoteProfileId,
            string exactMetadataPath,
            CancellationToken cancellationToken)
        {
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            if (string.IsNullOrWhiteSpace(exactMetadataPath))
                throw new ArgumentException(
                    "An exact provider-metadata path is required.",
                    nameof(exactMetadataPath));

            var key = new OperationKey(remoteProfileId, exactMetadataPath);
            Entry entry;

            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry();
                    _entries.Add(key, entry);
                }

                entry.References++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new Lease(this, key, entry);
            }
            catch
            {
                RemoveReference(key, entry);
                throw;
            }
        }

        private void Release(OperationKey key, Entry entry)
        {
            entry.Semaphore.Release();
            RemoveReference(key, entry);
        }

        private void RemoveReference(OperationKey key, Entry entry)
        {
            lock (_gate)
            {
                entry.References--;
                if (entry.References != 0)
                    return;

                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }

        private readonly record struct OperationKey(
            Guid RemoteProfileId,
            string ExactMetadataPath);

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int References { get; set; }
        }

        private sealed class Lease : IDisposable
        {
            private GoogleDriveProviderMetadataReplacementCoordinator? _owner;
            private readonly OperationKey _key;
            private readonly Entry _entry;

            public Lease(
                GoogleDriveProviderMetadataReplacementCoordinator owner,
                OperationKey key,
                Entry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                GoogleDriveProviderMetadataReplacementCoordinator? owner =
                    Interlocked.Exchange(ref _owner, null);
                owner?.Release(_key, _entry);
            }
        }
    }

    /// <summary>
    /// Creates or replaces only .gamesave-sync/sync-log.json. The parent is
    /// ensured before a profile/path-scoped lease is acquired; inside that
    /// lease, an exact-name lookup chooses between one create and one exact-ID
    /// content update. No duplicate is selected or repaired, and no temporary
    /// Drive object, rename, move, trash, delete, or permission mutation exists.
    /// </summary>
    internal sealed class GoogleDriveProviderMetadataReplacementService
        : IGoogleDriveProviderMetadataReplacementService
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private static readonly GoogleDriveRelativePath ParentPath =
            GoogleDriveRelativePath.Parse(".gamesave-sync");

        private const string ExactFileName = "sync-log.json";

        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveTextCreationApi _textCreationApi;
        private readonly IGoogleDriveTextReplacementApi _textReplacementApi;
        private readonly GoogleDriveProviderMetadataReplacementCoordinator
            _coordinator;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveProviderMetadataReplacementService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveTextCreationApi textCreationApi,
            IGoogleDriveTextReplacementApi textReplacementApi,
            GoogleDriveProviderMetadataReplacementCoordinator coordinator,
            IGoogleDriveObjectIdCache objectIdCache)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _textCreationApi = textCreationApi ??
                throw new ArgumentNullException(nameof(textCreationApi));
            _textReplacementApi = textReplacementApi ??
                throw new ArgumentNullException(nameof(textReplacementApi));
            _coordinator = coordinator ??
                throw new ArgumentNullException(nameof(coordinator));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
        }

        public async Task ReplaceAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            string validatedPath =
                RemoteProviderMetadataPath.Validate(relativePath);
            ArgumentNullException.ThrowIfNull(content);
            byte[] contentBytes = EncodeContent(content);

            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string parentId = await EnsureParentAsync(
                context,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using IDisposable lease = await _coordinator.AcquireAsync(
                context.RemoteProfileId,
                validatedPath,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveObjectResolutionResult target = await FindTargetAsync(
                context,
                parentId,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var cacheScope = new GoogleDriveObjectCacheScope(
                context.RemoteProfileId,
                context.RootFolderId);

            if (target.Status == GoogleDriveObjectResolutionStatus.NotFound)
            {
                TryInvalidateConfirmedStale(
                    target,
                    cacheScope,
                    parentId,
                    ExactFileName);
                await CreateAsync(
                    context,
                    cacheScope,
                    parentId,
                    contentBytes,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (target.Status != GoogleDriveObjectResolutionStatus.Found ||
                target.ObjectKind != GoogleDriveObjectKind.File ||
                string.IsNullOrWhiteSpace(target.ObjectId) ||
                target.Metadata is null)
            {
                TryInvalidateConfirmedStale(
                    target,
                    cacheScope,
                    parentId,
                    ExactFileName);
                throw ResolutionFailure(target);
            }

            StoreValidated(
                cacheScope,
                parentId,
                target.Metadata,
                beforeMutation: true);

            await UpdateAsync(
                context,
                cacheScope,
                parentId,
                target.ObjectId,
                contentBytes,
                cancellationToken).ConfigureAwait(false);
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
                    GoogleDriveTextReplacementErrorCodes.InvalidUtf8,
                    "The Google Drive provider metadata is not valid UTF-8.");
            }

            if (bytes.Length > GoogleDriveTextReplacementApi.MaxTextContentBytes)
            {
                throw Failure(
                    GoogleDriveTextReplacementErrorCodes.ContentTooLarge,
                    "The Google Drive provider metadata is too large.");
            }

            return bytes;
        }

        private static async Task<string> EnsureParentAsync(
            GoogleDriveRemoteOperationContext context,
            CancellationToken cancellationToken)
        {
            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.EnsureFolderPathAsync(
                    context.RootFolderId,
                    ParentPath,
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
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidParentResolution,
                    "The Google Drive provider-metadata folder could not be resolved safely.");
            }

            if (resolution is not null &&
                resolution.Status is GoogleDriveObjectResolutionStatus.Found or
                    GoogleDriveObjectResolutionStatus.Created &&
                resolution.ObjectKind == GoogleDriveObjectKind.Folder &&
                !string.IsNullOrWhiteSpace(resolution.ObjectId))
            {
                return resolution.ObjectId;
            }

            if (resolution is null)
            {
                throw Failure(
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidParentResolution,
                    "The Google Drive provider-metadata folder could not be resolved safely.");
            }

            throw ResolutionFailure(resolution);
        }

        private static async Task<GoogleDriveObjectResolutionResult> FindTargetAsync(
            GoogleDriveRemoteOperationContext context,
            string parentId,
            CancellationToken cancellationToken)
        {
            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.FindChildAsync(
                    parentId,
                    ExactFileName,
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
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidTargetResolution,
                    "The Google Drive provider metadata could not be resolved safely.");
            }

            return resolution ?? throw Failure(
                GoogleDriveProviderMetadataReplacementErrorCodes
                    .InvalidTargetResolution,
                "The Google Drive provider metadata could not be resolved safely.");
        }

        private async Task CreateAsync(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            byte[] contentBytes,
            CancellationToken cancellationToken)
        {
            GoogleDriveTextCreationResult created;
            try
            {
                created = await _textCreationApi.CreateTextFileAsync(
                    context.Credential,
                    parentId,
                    ExactFileName,
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
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidCreateResponse,
                    "The Google Drive provider metadata was not created safely.");
            }

            if (created is null || string.IsNullOrWhiteSpace(created.FileId))
            {
                throw Failure(
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidCreateResponse,
                    "The Google Drive provider metadata was not created safely.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            StoreValidated(
                cacheScope,
                parentId,
                new GoogleDriveObjectMetadata(
                    created.FileId,
                    ExactFileName,
                    GoogleDriveTextCreationMediaTypes.Json,
                    trashed: false,
                    parentIds: [parentId],
                    driveId: null),
                beforeMutation: false);
        }

        private async Task UpdateAsync(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            string authoritativeFileId,
            byte[] contentBytes,
            CancellationToken cancellationToken)
        {
            GoogleDriveTextReplacementResult replaced;
            try
            {
                replaced = await _textReplacementApi.ReplaceTextContentAsync(
                    context.Credential,
                    authoritativeFileId,
                    contentBytes,
                    GoogleDriveTextCreationMediaTypes.Json,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                if (IsConfirmedStale(ex))
                {
                    _objectIdCache.Remove(
                        cacheScope,
                        parentId,
                        ExactFileName,
                        GoogleDriveObjectKind.File);
                }

                throw ApiFailure(ex);
            }
            catch (GoogleDriveRemoteOperationException)
            {
                throw;
            }
            catch
            {
                throw Failure(
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidReplaceResponse,
                    "The Google Drive provider metadata was not replaced safely.");
            }

            if (replaced is null ||
                !string.Equals(
                    replaced.FileId,
                    authoritativeFileId,
                    StringComparison.Ordinal))
            {
                _objectIdCache.Remove(
                    cacheScope,
                    parentId,
                    ExactFileName,
                    GoogleDriveObjectKind.File);
                throw Failure(
                    GoogleDriveProviderMetadataReplacementErrorCodes
                        .InvalidReplaceResponse,
                    "The Google Drive provider metadata identity changed unexpectedly.");
            }
        }

        private void StoreValidated(
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            GoogleDriveObjectMetadata metadata,
            bool beforeMutation)
        {
            bool cached;
            try
            {
                cached = _objectIdCache.TryStoreUniqueValidated(
                    cacheScope,
                    parentId,
                    ExactFileName,
                    GoogleDriveObjectKind.File,
                    metadata);
            }
            catch
            {
                cached = false;
            }

            if (!cached)
            {
                throw Failure(
                    GoogleDriveProviderMetadataReplacementErrorCodes.CacheRejected,
                    beforeMutation
                        ? "The Google Drive provider metadata identity could not be validated safely."
                        : "The created Google Drive provider metadata could not be recorded safely.");
            }
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
                // neither permit mutation nor replace the sanitized failure.
            }
        }

        private static bool IsConfirmedStale(GoogleDriveApiException exception) =>
            exception.Failure is GoogleDriveApiFailure.NotFound or
                GoogleDriveApiFailure.AccessDenied or
                GoogleDriveApiFailure.AuthorizationRevoked ||
            exception.Details.SafeErrorCode is
                GoogleDriveTextReplacementErrorCodes.InvalidMetadata or
                GoogleDriveTextReplacementErrorCodes.Folder or
                GoogleDriveTextReplacementErrorCodes.Trashed or
                GoogleDriveTextReplacementErrorCodes.WorkspaceDocument or
                GoogleDriveTextReplacementErrorCodes.UnsupportedLocation or
                GoogleDriveTextReplacementErrorCodes.IdentityMismatch;

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
