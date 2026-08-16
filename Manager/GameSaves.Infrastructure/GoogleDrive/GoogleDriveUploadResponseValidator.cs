namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveUploadResponseFailure
    {
        InvalidResponse = 0,
        NameMismatch = 1,
        MimeTypeMismatch = 2,
        ParentMismatch = 3,
        Trashed = 4,
        UnsupportedLocation = 5,
        SizeMismatch = 6
    }

    internal static class GoogleDriveUploadResponseErrorCodes
    {
        public const string InvalidResponse =
            "GoogleDriveUploadInvalidResponse";
        public const string NameMismatch =
            "GoogleDriveUploadNameMismatch";
        public const string MimeTypeMismatch =
            "GoogleDriveUploadMimeTypeMismatch";
        public const string ParentMismatch =
            "GoogleDriveUploadParentMismatch";
        public const string Trashed = "GoogleDriveUploadTrashed";
        public const string UnsupportedLocation =
            "GoogleDriveUploadUnsupportedLocation";
        public const string SizeMismatch =
            "GoogleDriveUploadSizeMismatch";

        public static string ForFailure(
            GoogleDriveUploadResponseFailure failure) =>
            failure switch
            {
                GoogleDriveUploadResponseFailure.InvalidResponse =>
                    InvalidResponse,
                GoogleDriveUploadResponseFailure.NameMismatch =>
                    NameMismatch,
                GoogleDriveUploadResponseFailure.MimeTypeMismatch =>
                    MimeTypeMismatch,
                GoogleDriveUploadResponseFailure.ParentMismatch =>
                    ParentMismatch,
                GoogleDriveUploadResponseFailure.Trashed => Trashed,
                GoogleDriveUploadResponseFailure.UnsupportedLocation =>
                    UnsupportedLocation,
                GoogleDriveUploadResponseFailure.SizeMismatch => SizeMismatch,
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    internal sealed class GoogleDriveUploadResponseException : Exception
    {
        public GoogleDriveUploadResponseException(
            GoogleDriveUploadResponseFailure failure)
            : base(SafeMessage(failure))
        {
            if (!Enum.IsDefined(failure))
                throw new ArgumentOutOfRangeException(nameof(failure));

            Failure = failure;
            SafeErrorCode = GoogleDriveUploadResponseErrorCodes.ForFailure(
                failure);
        }

        public GoogleDriveUploadResponseFailure Failure { get; }

        public string SafeErrorCode { get; }

        public override string ToString() =>
            $"{GetType().FullName}: {Message} ({SafeErrorCode})";

        private static string SafeMessage(
            GoogleDriveUploadResponseFailure failure) =>
            failure switch
            {
                GoogleDriveUploadResponseFailure.InvalidResponse =>
                    "The Google Drive upload response is incomplete.",
                GoogleDriveUploadResponseFailure.NameMismatch =>
                    "The Google Drive upload response name is invalid.",
                GoogleDriveUploadResponseFailure.MimeTypeMismatch =>
                    "The Google Drive upload response media type is invalid.",
                GoogleDriveUploadResponseFailure.ParentMismatch =>
                    "The Google Drive upload response parent is invalid.",
                GoogleDriveUploadResponseFailure.Trashed =>
                    "The Google Drive upload response is trashed.",
                GoogleDriveUploadResponseFailure.UnsupportedLocation =>
                    "The Google Drive upload response location is unsupported.",
                GoogleDriveUploadResponseFailure.SizeMismatch =>
                    "The Google Drive upload response size is invalid.",
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    internal static class GoogleDriveUploadResponseValidator
    {
        public static void Validate(
            GoogleDriveMediaUploadMetadata? response,
            string expectedParentId,
            string expectedFileName,
            long expectedLength)
        {
            if (string.IsNullOrWhiteSpace(expectedParentId))
            {
                throw new ArgumentException(
                    "An authoritative parent ID is required.",
                    nameof(expectedParentId));
            }
            if (string.IsNullOrEmpty(expectedFileName))
            {
                throw new ArgumentException(
                    "An exact target name is required.",
                    nameof(expectedFileName));
            }
            if (expectedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedLength));

            if (response is null ||
                string.IsNullOrWhiteSpace(response.Id) ||
                response.Name is null ||
                response.MimeType is null ||
                response.Trashed is null ||
                response.ParentIds is null ||
                response.Size is null)
            {
                throw Failure(
                    GoogleDriveUploadResponseFailure.InvalidResponse);
            }

            if (!string.Equals(
                    response.Name,
                    expectedFileName,
                    StringComparison.Ordinal))
            {
                throw Failure(GoogleDriveUploadResponseFailure.NameMismatch);
            }

            // The request always asks for the opaque media type, but Google
            // Drive may store its own type for a known extension. Requiring an
            // exact echo rejected real manifest uploads, so the response only
            // has to remain an ordinary uploaded blob: never a folder, a
            // Workspace document, a shortcut, or a malformed type.
            if (GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                    response.MimeType) !=
                GoogleDriveRecursiveObjectKind.BlobFile)
            {
                throw Failure(
                    GoogleDriveUploadResponseFailure.MimeTypeMismatch);
            }

            if (response.ParentIds.Count != 1 ||
                !string.Equals(
                    response.ParentIds[0],
                    expectedParentId,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveUploadResponseFailure.ParentMismatch);
            }

            if (response.Trashed.Value)
                throw Failure(GoogleDriveUploadResponseFailure.Trashed);

            if (response.DriveId is not null)
            {
                throw Failure(
                    GoogleDriveUploadResponseFailure.UnsupportedLocation);
            }

            if (response.Size.Value != expectedLength)
                throw Failure(GoogleDriveUploadResponseFailure.SizeMismatch);
        }

        private static GoogleDriveUploadResponseException Failure(
            GoogleDriveUploadResponseFailure failure) => new(failure);
    }
}
