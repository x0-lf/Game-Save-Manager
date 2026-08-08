using System.Collections.ObjectModel;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Stable Infrastructure-only outcomes for recursive file discovery beneath
    /// one authoritative Google Drive backup-run folder.
    /// </summary>
    internal enum GoogleDriveRecursiveFileListingStatus
    {
        Completed = 0,
        FolderNotFound = 1,
        InvalidPath = 2,
        Ambiguous = 3,
        CaseCollision = 4,
        TypeCollision = 5,
        UnsupportedObject = 6,
        TrashedObject = 7,
        UnsupportedLocation = 8,
        InvalidMetadata = 9,
        ReauthenticationRequired = 10,
        AccessDenied = 11,
        RateLimited = 12,
        QuotaExceeded = 13,
        Unavailable = 14,
        Cancelled = 15,
        Failed = 16
    }

    internal static class GoogleDriveRecursiveFileListingErrorCodes
    {
        public const string InvalidPath = "GoogleDriveFileListingInvalidPath";
        public const string FolderNotFound =
            "GoogleDriveFileListingFolderNotFound";
        public const string Ambiguous = "GoogleDriveFileListingAmbiguous";
        public const string CaseCollision =
            "GoogleDriveFileListingCaseCollision";
        public const string TypeCollision =
            "GoogleDriveFileListingTypeCollision";
        public const string UnsupportedObject =
            "GoogleDriveFileListingUnsupportedObject";
        public const string Trashed = "GoogleDriveFileListingTrashed";
        public const string UnsupportedLocation =
            "GoogleDriveFileListingUnsupportedLocation";
        public const string InvalidMetadata =
            "GoogleDriveFileListingInvalidMetadata";
        public const string AuthenticationRequired =
            "GoogleDriveFileListingAuthenticationRequired";
        public const string AccessDenied =
            "GoogleDriveFileListingAccessDenied";
        public const string RateLimited =
            "GoogleDriveFileListingRateLimited";
        public const string QuotaExceeded =
            "GoogleDriveFileListingQuotaExceeded";
        public const string Unavailable =
            "GoogleDriveFileListingUnavailable";
        public const string Cancelled = "GoogleDriveFileListingCancelled";
        public const string Failed = "GoogleDriveFileListingFailed";

        public static string ForStatus(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.InvalidPath => InvalidPath,
                GoogleDriveRecursiveFileListingStatus.FolderNotFound => FolderNotFound,
                GoogleDriveRecursiveFileListingStatus.Ambiguous => Ambiguous,
                GoogleDriveRecursiveFileListingStatus.CaseCollision => CaseCollision,
                GoogleDriveRecursiveFileListingStatus.TypeCollision => TypeCollision,
                GoogleDriveRecursiveFileListingStatus.UnsupportedObject =>
                    UnsupportedObject,
                GoogleDriveRecursiveFileListingStatus.TrashedObject => Trashed,
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation =>
                    UnsupportedLocation,
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    InvalidMetadata,
                GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired =>
                    AuthenticationRequired,
                GoogleDriveRecursiveFileListingStatus.AccessDenied => AccessDenied,
                GoogleDriveRecursiveFileListingStatus.RateLimited => RateLimited,
                GoogleDriveRecursiveFileListingStatus.QuotaExceeded => QuotaExceeded,
                GoogleDriveRecursiveFileListingStatus.Unavailable => Unavailable,
                GoogleDriveRecursiveFileListingStatus.Cancelled => Cancelled,
                GoogleDriveRecursiveFileListingStatus.Failed => Failed,
                GoogleDriveRecursiveFileListingStatus.Completed =>
                    throw new ArgumentException(
                        "A completed listing does not have an error code.",
                        nameof(status)),
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
    }

    internal static class GoogleDriveRecursiveFileListingFailureMapper
    {
        private const string ParentMismatchErrorCode =
            "GoogleDriveObjectParentMismatch";

        public static GoogleDriveRecursiveFileListingException FromStatus(
            GoogleDriveRecursiveFileListingStatus status,
            bool retryable = false) =>
            new(new GoogleDriveRecursiveFileListingResult(
                status,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                retryable,
                GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
                SafeUserMessage(status)));

        public static GoogleDriveRecursiveFileListingException FromApiFailure(
            GoogleDriveApiException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            GoogleDriveRecursiveFileListingStatus? metadataStatus =
                exception.Details.SafeErrorCode switch
                {
                    GoogleDriveObjectResolutionErrorCodes.UnsupportedLocation =>
                        GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
                    GoogleDriveObjectResolutionErrorCodes.Trashed =>
                        GoogleDriveRecursiveFileListingStatus.TrashedObject,
                    ParentMismatchErrorCode =>
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
                    GoogleDriveObjectResolutionErrorCodes.TypeMismatch =>
                        GoogleDriveRecursiveFileListingStatus.TypeCollision,
                    _ => null
                };

            return metadataStatus is { } status
                ? FromStatus(status, exception.Details.Retryable)
                : FromRemoteValidation(
                    GoogleDriveRemoteValidationMapper.FromApiFailure(
                        exception.Details));
        }

        public static GoogleDriveRecursiveFileListingException FromResolution(
            GoogleDriveObjectResolutionResult resolution)
        {
            ArgumentNullException.ThrowIfNull(resolution);

            return resolution.Status switch
            {
                GoogleDriveObjectResolutionStatus.InvalidPath =>
                    FromStatus(GoogleDriveRecursiveFileListingStatus.InvalidPath),
                GoogleDriveObjectResolutionStatus.Ambiguous =>
                    FromStatus(GoogleDriveRecursiveFileListingStatus.Ambiguous),
                GoogleDriveObjectResolutionStatus.Failed
                    when string.Equals(
                        resolution.ErrorCode,
                        GoogleDriveObjectResolutionErrorCodes.InvalidMetadata,
                        StringComparison.Ordinal) =>
                    FromStatus(
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata),
                _ => FromRemoteValidation(
                    GoogleDriveRemoteValidationMapper.FromObjectResolution(
                        resolution))
            };
        }

        public static GoogleDriveRecursiveFileListingException FromRemoteValidation(
            GoogleDriveRemoteValidationResult validation)
        {
            ArgumentNullException.ThrowIfNull(validation);

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

            return FromStatus(status, validation.Retryable);
        }

        private static string SafeUserMessage(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.InvalidPath =>
                    "The Google Drive backup-folder path is invalid.",
                GoogleDriveRecursiveFileListingStatus.FolderNotFound =>
                    "The Google Drive backup folder could not be found.",
                GoogleDriveRecursiveFileListingStatus.Ambiguous =>
                    "The Google Drive backup folder contains ambiguous duplicate names.",
                GoogleDriveRecursiveFileListingStatus.CaseCollision =>
                    "The Google Drive backup folder contains names that differ only by case.",
                GoogleDriveRecursiveFileListingStatus.TypeCollision =>
                    "A Google Drive object has an unexpected type.",
                GoogleDriveRecursiveFileListingStatus.UnsupportedObject =>
                    "The Google Drive backup folder contains an unsupported object.",
                GoogleDriveRecursiveFileListingStatus.TrashedObject =>
                    "A trashed Google Drive object blocked file listing.",
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation =>
                    "This Google Drive location is not supported.",
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    "Google Drive returned invalid file metadata.",
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
                GoogleDriveRecursiveFileListingStatus.Failed =>
                    "The Google Drive backup folder could not be listed.",
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
    }

    /// <summary>
    /// One validated ordinary blob file. Authoritative IDs, names, and paths
    /// remain available only to trusted Infrastructure callers and are never
    /// included in diagnostic formatting.
    /// </summary>
    internal sealed class GoogleDriveRecursiveFileEntry
    {
        public GoogleDriveRecursiveFileEntry(
            string fileId,
            string parentFolderId,
            string exactFileName,
            string canonicalRelativePath,
            string mimeType)
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("A file ID is required.", nameof(fileId));
            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                throw new ArgumentException(
                    "A parent-folder ID is required.",
                    nameof(parentFolderId));
            }
            if (string.IsNullOrEmpty(exactFileName))
            {
                throw new ArgumentException(
                    "An exact file name is required.",
                    nameof(exactFileName));
            }
            if (string.IsNullOrWhiteSpace(mimeType))
                throw new ArgumentException("A MIME type is required.", nameof(mimeType));
            if (GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType) !=
                GoogleDriveRecursiveObjectKind.BlobFile)
            {
                throw new ArgumentException(
                    "A recursive file entry must describe an ordinary blob file.",
                    nameof(mimeType));
            }

            GoogleDriveRelativePath relativePath;
            try
            {
                relativePath = GoogleDriveRelativePath.Parse(canonicalRelativePath);
            }
            catch (ArgumentException)
            {
                throw new ArgumentException(
                    "A canonical relative file path is required.",
                    nameof(canonicalRelativePath));
            }

            if (relativePath.IsRoot ||
                !string.Equals(
                    relativePath.Segments[^1],
                    exactFileName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The canonical path must end with the exact file name.",
                    nameof(canonicalRelativePath));
            }

            FileId = fileId;
            ParentFolderId = parentFolderId;
            ExactFileName = exactFileName;
            CanonicalRelativePath = relativePath.Canonical;
            MimeType = mimeType;
        }

        public string FileId { get; }

        public string ParentFolderId { get; }

        public string ExactFileName { get; }

        public string CanonicalRelativePath { get; }

        public string MimeType { get; }

        public override string ToString() => "Google Drive recursive file entry";
    }

    /// <summary>
    /// Immutable recursive-listing result. Failure results never contain a
    /// partial file list, and diagnostic formatting exposes fixed categories
    /// and counts only.
    /// </summary>
    internal sealed class GoogleDriveRecursiveFileListingResult
    {
        public GoogleDriveRecursiveFileListingResult(
            GoogleDriveRecursiveFileListingStatus status,
            IEnumerable<GoogleDriveRecursiveFileEntry> entries,
            bool retryable,
            string? safeErrorCode = null,
            string? safeUserMessage = null)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            ArgumentNullException.ThrowIfNull(entries);

            GoogleDriveRecursiveFileEntry[] snapshot = entries.ToArray();
            if (snapshot.Any(entry => entry is null))
            {
                throw new ArgumentException(
                    "Listing entries cannot contain null values.",
                    nameof(entries));
            }

            if (status == GoogleDriveRecursiveFileListingStatus.Completed)
            {
                if (retryable || safeErrorCode is not null ||
                    safeUserMessage is not null)
                {
                    throw new ArgumentException(
                        "A completed listing cannot contain failure details.",
                        nameof(status));
                }
            }
            else
            {
                if (snapshot.Length != 0)
                {
                    throw new ArgumentException(
                        "A failed listing cannot contain partial entries.",
                        nameof(entries));
                }

                string expectedErrorCode =
                    GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status);
                if (!string.Equals(
                        safeErrorCode,
                        expectedErrorCode,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The safe error code does not match the listing status.",
                        nameof(safeErrorCode));
                }
                if (string.IsNullOrWhiteSpace(safeUserMessage))
                {
                    throw new ArgumentException(
                        "A safe user message is required for a failed listing.",
                        nameof(safeUserMessage));
                }
            }

            Status = status;
            Entries = new ReadOnlyCollection<GoogleDriveRecursiveFileEntry>(snapshot);
            Retryable = retryable;
            SafeErrorCode = safeErrorCode;
            SafeUserMessage = safeUserMessage;
        }

        public GoogleDriveRecursiveFileListingStatus Status { get; }

        public IReadOnlyList<GoogleDriveRecursiveFileEntry> Entries { get; }

        public bool Retryable { get; }

        public string? SafeErrorCode { get; }

        public string? SafeUserMessage { get; }

        public string ToSafeDiagnosticString() =>
            $"Google Drive recursive file listing: status={Status}; " +
            $"entries={Entries.Count}; retryable={Retryable}";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// Safe Infrastructure-only failure boundary for recursive listing helpers.
    /// The embedded result contains no partial entries or Drive object data.
    /// </summary>
    internal sealed class GoogleDriveRecursiveFileListingException : Exception
    {
        public GoogleDriveRecursiveFileListingException(
            GoogleDriveRecursiveFileListingResult result)
            : base("The Google Drive file listing could not be completed.")
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Status == GoogleDriveRecursiveFileListingStatus.Completed)
            {
                throw new ArgumentException(
                    "A listing failure cannot contain a completed result.",
                    nameof(result));
            }

            Result = result;
        }

        public GoogleDriveRecursiveFileListingResult Result { get; }

        public override string ToString() =>
            $"{GetType().Name}: {Result.ToSafeDiagnosticString()}";
    }
}
