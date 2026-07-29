using System.Collections.ObjectModel;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveObjectKind
    {
        File = 0,
        Folder = 1
    }

    internal enum GoogleDriveObjectResolutionStatus
    {
        Found = 0,
        // Reserved for the later Milestone N create-missing-folders slice.
        Created = 1,
        NotFound = 2,
        InvalidPath = 3,
        Ambiguous = 4,
        TypeMismatch = 5,
        Trashed = 6,
        UnsupportedLocation = 7,
        ReauthenticationRequired = 8,
        AccessDenied = 9,
        RateLimited = 10,
        QuotaExceeded = 11,
        Unavailable = 12,
        Failed = 13
    }
    internal static class GoogleDriveObjectResolutionErrorCodes
    {
        public const string InvalidPath = "GoogleDriveObjectInvalidPath";
        public const string NotFound = "GoogleDriveObjectNotFound";
        public const string Ambiguous = "GoogleDriveObjectAmbiguous";
        public const string TypeMismatch = "GoogleDriveObjectTypeMismatch";
        public const string Trashed = "GoogleDriveObjectTrashed";
        public const string UnsupportedLocation = "GoogleDriveObjectUnsupportedLocation";
        public const string AuthenticationRequired = "GoogleDriveObjectAuthenticationRequired";
        public const string AccessDenied = "GoogleDriveObjectAccessDenied";
        public const string RateLimited = "GoogleDriveObjectRateLimited";
        public const string QuotaExceeded = "GoogleDriveObjectQuotaExceeded";
        public const string Unavailable = "GoogleDriveObjectUnavailable";
        public const string InvalidCreateResponse =
            "GoogleDriveObjectInvalidCreateResponse";
        public const string InvalidMetadata = "GoogleDriveObjectInvalidMetadata";
        public const string Failed = "GoogleDriveObjectFailed";
    }

    internal sealed class GoogleDriveObjectMetadata
    {
        public GoogleDriveObjectMetadata(
            string id,
            string name,
            string mimeType,
            bool trashed,
            IEnumerable<string>? parentIds,
            string? driveId)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A Google Drive object ID is required.", nameof(id));
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(mimeType))
                throw new ArgumentException("A Google Drive MIME type is required.", nameof(mimeType));

            Id = id;
            Name = name;
            MimeType = mimeType;
            Kind = string.Equals(
                mimeType,
                GoogleDriveApplicationRoot.FolderMimeType,
                StringComparison.Ordinal)
                ? GoogleDriveObjectKind.Folder
                : GoogleDriveObjectKind.File;
            Trashed = trashed;
            ParentIds = new ReadOnlyCollection<string>(
                parentIds?.ToArray() ?? Array.Empty<string>());
            DriveId = string.IsNullOrWhiteSpace(driveId) ? null : driveId;
        }

        public string Id { get; }

        public string Name { get; }

        public string MimeType { get; }

        public GoogleDriveObjectKind Kind { get; }

        public bool Trashed { get; }

        public IReadOnlyList<string> ParentIds { get; }

        public string? DriveId { get; }

        public override string ToString() =>
            $"Google Drive object metadata (trashed={Trashed}, parents={ParentIds.Count})";
    }

    internal sealed class GoogleDriveObjectResolutionResult
    {
        public GoogleDriveObjectResolutionResult(
            GoogleDriveObjectResolutionStatus status,
            GoogleDriveRelativePath? path = null,
            GoogleDriveObjectKind? objectKind = null,
            GoogleDriveObjectMetadata? metadata = null,
            string? objectId = null,
            string? errorCode = null,
            string? message = null)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (objectKind is not null && !Enum.IsDefined(objectKind.Value))
                throw new ArgumentOutOfRangeException(nameof(objectKind));

            Status = status;
            Path = path;
            ObjectKind = objectKind;
            Metadata = metadata;
            string? normalizedObjectId = string.IsNullOrWhiteSpace(objectId)
                ? null
                : objectId;
            if (metadata is not null && normalizedObjectId is not null &&
                !string.Equals(metadata.Id, normalizedObjectId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The result object ID must match its metadata.",
                    nameof(objectId));
            }

            ObjectId = normalizedObjectId ?? metadata?.Id;
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
            Message = string.IsNullOrWhiteSpace(message) ? null : message;
        }

        public GoogleDriveObjectResolutionStatus Status { get; }

        public GoogleDriveRelativePath? Path { get; }

        public GoogleDriveObjectKind? ObjectKind { get; }

        public GoogleDriveObjectMetadata? Metadata { get; }

        public string? ObjectId { get; }

        public string? ErrorCode { get; }

        public string? Message { get; }

        /// <summary>
        /// Returns only fixed enum labels. Error codes and messages remain
        /// available to trusted callers but are not assumed safe to log.
        /// </summary>
        public string ToSafeDiagnosticString() =>
            $"Google Drive object resolution: status={Status}; " +
            $"kind={ObjectKind?.ToString() ?? "unknown"}";

        public override string ToString() => ToSafeDiagnosticString();
    }
}
