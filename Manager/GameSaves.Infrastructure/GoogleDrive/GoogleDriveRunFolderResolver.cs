namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRunFolderResolver
    {
        Task<GoogleDriveResolvedRunFolder> ResolveAsync(
            GoogleDriveRecursiveFileListingRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Transfers ownership of one authenticated operation context together
    /// with the authoritative backup-run folder identity. The eventual
    /// traversal owner must dispose this value deterministically.
    /// </summary>
    internal sealed class GoogleDriveResolvedRunFolder : IDisposable
    {
        private GoogleDriveRemoteOperationContext? _operationContext;

        public GoogleDriveResolvedRunFolder(
            string folderId,
            GoogleDriveRemoteOperationContext operationContext)
        {
            if (string.IsNullOrWhiteSpace(folderId))
            {
                throw new ArgumentException(
                    "An authoritative backup-run folder ID is required.",
                    nameof(folderId));
            }

            ArgumentNullException.ThrowIfNull(operationContext);
            if (operationContext.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(operationContext));
            }

            FolderId = folderId;
            _operationContext = operationContext;
        }

        public string FolderId { get; }

        public GoogleDriveRemoteOperationContext OperationContext
        {
            get
            {
                ObjectDisposedException.ThrowIf(_operationContext is null, this);
                return _operationContext;
            }
        }

        internal bool IsDisposed => _operationContext is null;

        public void Dispose()
        {
            GoogleDriveRemoteOperationContext? context = Interlocked.Exchange(
                ref _operationContext,
                null);
            context?.Dispose();
        }

        public override string ToString() =>
            $"Google Drive resolved run folder (disposed={IsDisposed})";
    }

    /// <summary>
    /// Restores one saved profile silently and resolves one requested folder
    /// beneath that profile's authoritative application-root ID. Success
    /// transfers context ownership; every other path disposes it here.
    /// </summary>
    internal sealed class GoogleDriveRunFolderResolver
        : IGoogleDriveRunFolderResolver
    {
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;

        public GoogleDriveRunFolderResolver(
            IGoogleDriveRemoteOperationContextFactory contextFactory) =>
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));

        public async Task<GoogleDriveResolvedRunFolder> ResolveAsync(
            GoogleDriveRecursiveFileListingRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveRemoteOperationContext? context = null;
            try
            {
                context = await _contextFactory.CreateAsync(
                    request.RemoteProfileId,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (context.RemoteProfileId != request.RemoteProfileId)
                {
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.Failed);
                }

                GoogleDriveObjectResolutionResult resolution =
                    await context.Resolver.ResolveAsync(
                        context.RootFolderId,
                        request.FolderPath,
                        GoogleDriveObjectKind.Folder,
                        cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveResolvedRunFolder resolved = ValidateResolution(
                    resolution,
                    request,
                    context);
                context = null;
                return resolved;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveRecursiveFileListingException)
            {
                throw;
            }
            catch (GoogleDriveRemoteOperationException exception)
            {
                throw Failure(exception.Result);
            }
            catch (GoogleDriveApiException exception)
            {
                throw Failure(
                    GoogleDriveRemoteValidationMapper.FromApiFailure(
                        exception.Details));
            }
            catch
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.Failed);
            }
            finally
            {
                context?.Dispose();
            }
        }

        private static void ValidateRequest(
            GoogleDriveRecursiveFileListingRequest? request)
        {
            if (request is null ||
                request.RemoteProfileId == Guid.Empty ||
                request.FolderPath is null ||
                request.FolderPath.IsRoot)
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.InvalidPath);
            }
        }

        private static GoogleDriveResolvedRunFolder ValidateResolution(
            GoogleDriveObjectResolutionResult? resolution,
            GoogleDriveRecursiveFileListingRequest request,
            GoogleDriveRemoteOperationContext context)
        {
            if (resolution is null)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
            }

            if (resolution.Status != GoogleDriveObjectResolutionStatus.Found)
                throw Failure(resolution);

            if (resolution.ObjectKind != GoogleDriveObjectKind.Folder)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.TypeCollision);
            }

            GoogleDriveObjectMetadata? metadata = resolution.Metadata;
            if (metadata is null ||
                string.IsNullOrWhiteSpace(resolution.ObjectId) ||
                resolution.Path is null ||
                !string.Equals(
                    resolution.Path.Canonical,
                    request.CanonicalFolderPath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    metadata.Name,
                    request.FolderPath.Segments[^1],
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
            }

            if (metadata.Kind != GoogleDriveObjectKind.Folder)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.TypeCollision);
            }

            if (metadata.Trashed)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.TrashedObject);
            }

            if (!string.IsNullOrWhiteSpace(metadata.DriveId))
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.UnsupportedLocation);
            }

            return new GoogleDriveResolvedRunFolder(
                resolution.ObjectId,
                context);
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveObjectResolutionResult resolution)
        {
            GoogleDriveRecursiveFileListingStatus status =
                resolution.Status switch
                {
                    GoogleDriveObjectResolutionStatus.NotFound =>
                        GoogleDriveRecursiveFileListingStatus.FolderNotFound,
                    GoogleDriveObjectResolutionStatus.InvalidPath =>
                        GoogleDriveRecursiveFileListingStatus.InvalidPath,
                    GoogleDriveObjectResolutionStatus.Ambiguous =>
                        GoogleDriveRecursiveFileListingStatus.Ambiguous,
                    GoogleDriveObjectResolutionStatus.TypeMismatch =>
                        GoogleDriveRecursiveFileListingStatus.TypeCollision,
                    GoogleDriveObjectResolutionStatus.Trashed =>
                        GoogleDriveRecursiveFileListingStatus.TrashedObject,
                    GoogleDriveObjectResolutionStatus.UnsupportedLocation =>
                        GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
                    GoogleDriveObjectResolutionStatus.ReauthenticationRequired =>
                        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
                    GoogleDriveObjectResolutionStatus.AccessDenied =>
                        GoogleDriveRecursiveFileListingStatus.AccessDenied,
                    GoogleDriveObjectResolutionStatus.RateLimited =>
                        GoogleDriveRecursiveFileListingStatus.RateLimited,
                    GoogleDriveObjectResolutionStatus.QuotaExceeded =>
                        GoogleDriveRecursiveFileListingStatus.QuotaExceeded,
                    GoogleDriveObjectResolutionStatus.Unavailable =>
                        GoogleDriveRecursiveFileListingStatus.Unavailable,
                    GoogleDriveObjectResolutionStatus.Failed
                        when string.Equals(
                            resolution.ErrorCode,
                            GoogleDriveObjectResolutionErrorCodes.InvalidMetadata,
                            StringComparison.Ordinal) =>
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
                    _ => GoogleDriveRecursiveFileListingStatus.Failed
                };

            bool retryable = resolution.Status is
                GoogleDriveObjectResolutionStatus.RateLimited or
                GoogleDriveObjectResolutionStatus.Unavailable;
            return Failure(status, retryable);
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveRemoteValidationResult validation)
        {
            GoogleDriveRecursiveFileListingStatus status =
                validation.Status switch
                {
                    GoogleDriveRemoteValidationStatus.UnsupportedScope or
                    GoogleDriveRemoteValidationStatus.NotConnected or
                    GoogleDriveRemoteValidationStatus.AuthenticationCorrupted or
                    GoogleDriveRemoteValidationStatus.AuthorizationRevoked or
                    GoogleDriveRemoteValidationStatus.ReauthenticationRequired =>
                        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
                    GoogleDriveRemoteValidationStatus.RootMissing =>
                        GoogleDriveRecursiveFileListingStatus.FolderNotFound,
                    GoogleDriveRemoteValidationStatus.RootTrashed =>
                        GoogleDriveRecursiveFileListingStatus.TrashedObject,
                    GoogleDriveRemoteValidationStatus.RootWrongType =>
                        GoogleDriveRecursiveFileListingStatus.TypeCollision,
                    GoogleDriveRemoteValidationStatus.RootUnsupportedLocation =>
                        GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
                    GoogleDriveRemoteValidationStatus.RootInaccessible or
                    GoogleDriveRemoteValidationStatus.RootCannotListChildren or
                    GoogleDriveRemoteValidationStatus.RootCannotAddChildren =>
                        GoogleDriveRecursiveFileListingStatus.AccessDenied,
                    GoogleDriveRemoteValidationStatus.RateLimited =>
                        GoogleDriveRecursiveFileListingStatus.RateLimited,
                    GoogleDriveRemoteValidationStatus.QuotaExceeded =>
                        GoogleDriveRecursiveFileListingStatus.QuotaExceeded,
                    GoogleDriveRemoteValidationStatus.AuthenticationUnavailable or
                    GoogleDriveRemoteValidationStatus.Unavailable =>
                        GoogleDriveRecursiveFileListingStatus.Unavailable,
                    GoogleDriveRemoteValidationStatus.Cancelled =>
                        GoogleDriveRecursiveFileListingStatus.Cancelled,
                    _ => GoogleDriveRecursiveFileListingStatus.Failed
                };

            return Failure(status, validation.Retryable);
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveRecursiveFileListingStatus status,
            bool retryable = false) =>
            new(new GoogleDriveRecursiveFileListingResult(
                status,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                retryable,
                GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
                SafeUserMessage(status)));

        private static string SafeUserMessage(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.InvalidPath =>
                    "The Google Drive backup-folder path is invalid.",
                GoogleDriveRecursiveFileListingStatus.FolderNotFound =>
                    "The Google Drive backup folder could not be found.",
                GoogleDriveRecursiveFileListingStatus.Ambiguous =>
                    "The Google Drive backup folder is ambiguous.",
                GoogleDriveRecursiveFileListingStatus.TypeCollision =>
                    "The requested Google Drive object is not a folder.",
                GoogleDriveRecursiveFileListingStatus.TrashedObject =>
                    "The Google Drive backup folder is in the trash.",
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation =>
                    "This Google Drive location is not supported.",
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    "Google Drive returned invalid folder metadata.",
                GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired =>
                    "Google Drive must be connected again.",
                GoogleDriveRecursiveFileListingStatus.AccessDenied =>
                    "Google Drive did not allow access to the backup folder.",
                GoogleDriveRecursiveFileListingStatus.RateLimited =>
                    "Google Drive is receiving too many requests. Try again later.",
                GoogleDriveRecursiveFileListingStatus.QuotaExceeded =>
                    "Google Drive quota prevents listing the backup folder.",
                GoogleDriveRecursiveFileListingStatus.Unavailable =>
                    "Google Drive is temporarily unavailable.",
                GoogleDriveRecursiveFileListingStatus.Cancelled =>
                    "Google Drive file listing was cancelled.",
                _ => "The Google Drive backup folder could not be resolved."
            };
    }
}
