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
            if (string.Equals(
                    mimeType,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.Ordinal) ||
                mimeType.StartsWith(
                    "application/vnd.google-apps.",
                    StringComparison.Ordinal))
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
}
