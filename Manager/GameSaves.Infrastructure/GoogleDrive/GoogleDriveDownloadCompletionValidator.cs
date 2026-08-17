namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveDownloadCompletionFailure
    {
        InvalidMetadata = 0,
        IdentityMismatch = 1,
        NameMismatch = 2,
        UnsupportedType = 3,
        Trashed = 4,
        UnsupportedLocation = 5,
        SizeMismatch = 6
    }

    internal static class GoogleDriveDownloadCompletionErrorCodes
    {
        public const string InvalidMetadata =
            "GoogleDriveDownloadInvalidSourceMetadata";
        public const string IdentityMismatch =
            "GoogleDriveDownloadIdentityMismatch";
        public const string NameMismatch = "GoogleDriveDownloadNameMismatch";
        public const string UnsupportedType =
            "GoogleDriveDownloadUnsupportedSourceType";
        public const string Trashed = "GoogleDriveDownloadSourceTrashed";
        public const string UnsupportedLocation =
            "GoogleDriveDownloadUnsupportedSourceLocation";
        public const string SizeMismatch = "GoogleDriveDownloadSizeMismatch";

        public static string ForFailure(
            GoogleDriveDownloadCompletionFailure failure) =>
            failure switch
            {
                GoogleDriveDownloadCompletionFailure.InvalidMetadata =>
                    InvalidMetadata,
                GoogleDriveDownloadCompletionFailure.IdentityMismatch =>
                    IdentityMismatch,
                GoogleDriveDownloadCompletionFailure.NameMismatch => NameMismatch,
                GoogleDriveDownloadCompletionFailure.UnsupportedType =>
                    UnsupportedType,
                GoogleDriveDownloadCompletionFailure.Trashed => Trashed,
                GoogleDriveDownloadCompletionFailure.UnsupportedLocation =>
                    UnsupportedLocation,
                GoogleDriveDownloadCompletionFailure.SizeMismatch => SizeMismatch,
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    internal sealed class GoogleDriveDownloadCompletionException : Exception
    {
        public GoogleDriveDownloadCompletionException(
            GoogleDriveDownloadCompletionFailure failure)
            : base(SafeMessage(failure))
        {
            if (!Enum.IsDefined(failure))
                throw new ArgumentOutOfRangeException(nameof(failure));

            Failure = failure;
            SafeErrorCode =
                GoogleDriveDownloadCompletionErrorCodes.ForFailure(failure);
        }

        public GoogleDriveDownloadCompletionFailure Failure { get; }

        public string SafeErrorCode { get; }

        public override string ToString() =>
            $"{GetType().FullName}: {Message} ({SafeErrorCode})";

        private static string SafeMessage(
            GoogleDriveDownloadCompletionFailure failure) =>
            failure switch
            {
                GoogleDriveDownloadCompletionFailure.InvalidMetadata =>
                    "The Google Drive download source metadata is incomplete.",
                GoogleDriveDownloadCompletionFailure.IdentityMismatch =>
                    "The Google Drive download source identity changed.",
                GoogleDriveDownloadCompletionFailure.NameMismatch =>
                    "The Google Drive download source name changed.",
                GoogleDriveDownloadCompletionFailure.UnsupportedType =>
                    "The Google Drive download source is not an ordinary file.",
                GoogleDriveDownloadCompletionFailure.Trashed =>
                    "The Google Drive download source is trashed.",
                GoogleDriveDownloadCompletionFailure.UnsupportedLocation =>
                    "The Google Drive download source location is unsupported.",
                GoogleDriveDownloadCompletionFailure.SizeMismatch =>
                    "The downloaded byte count does not match the source size.",
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    /// <summary>
    /// Decides whether a finished transfer may be placed. It compares what the
    /// local file actually holds with the authoritative source size, and
    /// re-checks the source identity, exact name, ordinary-blob type, trash
    /// state, and My Drive location. It issues no further provider request.
    /// </summary>
    internal static class GoogleDriveDownloadCompletionValidator
    {
        public static void Validate(
            GoogleDriveMediaDownloadMetadata? metadata,
            GoogleDriveDownloadSource source,
            long writtenBytes)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (writtenBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(writtenBytes));

            if (metadata is null ||
                string.IsNullOrWhiteSpace(metadata.Id) ||
                metadata.Name is null ||
                metadata.MimeType is null ||
                metadata.Trashed is null ||
                metadata.ParentIds is null ||
                metadata.Size is null)
            {
                throw Failure(
                    GoogleDriveDownloadCompletionFailure.InvalidMetadata);
            }

            if (!string.Equals(metadata.Id, source.FileId, StringComparison.Ordinal))
                throw Failure(GoogleDriveDownloadCompletionFailure.IdentityMismatch);

            if (!string.Equals(
                    metadata.Name,
                    source.ExactName,
                    StringComparison.Ordinal))
            {
                throw Failure(GoogleDriveDownloadCompletionFailure.NameMismatch);
            }

            if (GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                    metadata.MimeType) !=
                GoogleDriveRecursiveObjectKind.BlobFile)
            {
                throw Failure(GoogleDriveDownloadCompletionFailure.UnsupportedType);
            }

            if (metadata.Trashed.Value)
                throw Failure(GoogleDriveDownloadCompletionFailure.Trashed);

            if (metadata.DriveId is not null)
            {
                throw Failure(
                    GoogleDriveDownloadCompletionFailure.UnsupportedLocation);
            }

            if (metadata.ParentIds.Count != 1 ||
                !string.Equals(
                    metadata.ParentIds[0],
                    source.ParentFolderId,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveDownloadCompletionFailure.UnsupportedLocation);
            }

            if (metadata.Size.Value < 0 || metadata.Size.Value != writtenBytes)
                throw Failure(GoogleDriveDownloadCompletionFailure.SizeMismatch);
        }

        private static GoogleDriveDownloadCompletionException Failure(
            GoogleDriveDownloadCompletionFailure failure) => new(failure);
    }
}
