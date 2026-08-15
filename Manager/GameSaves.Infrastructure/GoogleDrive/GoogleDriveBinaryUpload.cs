namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveBinaryUploadStatus
    {
        Completed = 0,
        Failed = 1,
        Indeterminate = 2
    }

    internal static class GoogleDriveBinaryUploadErrorCodes
    {
        public const string Failed = "GoogleDriveBinaryUploadFailed";
        public const string CompletionIndeterminate =
            "GoogleDriveUploadCompletionIndeterminate";
        public const string CacheRejected =
            "GoogleDriveUploadCacheRejected";
    }

    internal sealed class GoogleDriveUploadCompletionIndeterminateException
        : Exception
    {
        public GoogleDriveUploadCompletionIndeterminateException()
            : base("The Google Drive upload completion is uncertain.")
        {
        }

        public string SafeErrorCode =>
            GoogleDriveBinaryUploadErrorCodes.CompletionIndeterminate;

        public override string ToString() =>
            $"{GetType().FullName}: {Message} ({SafeErrorCode})";
    }

    /// <summary>
    /// Immutable, Infrastructure-only input for uploading one binary file to
    /// one canonical Google Drive path.
    /// </summary>
    internal sealed class GoogleDriveBinaryUploadRequest
    {
        public GoogleDriveBinaryUploadRequest(
            Guid remoteProfileId,
            GoogleDriveRelativePath remotePath,
            long expectedLength)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            ArgumentNullException.ThrowIfNull(remotePath);
            if (remotePath.IsRoot)
            {
                throw new ArgumentException(
                    "A binary upload target path is required.",
                    nameof(remotePath));
            }

            if (expectedLength < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedLength),
                    "The expected upload length cannot be negative.");
            }

            RemoteProfileId = remoteProfileId;
            RemotePath = remotePath;
            ExpectedLength = expectedLength;
        }

        public Guid RemoteProfileId { get; }

        public GoogleDriveRelativePath RemotePath { get; }

        public string CanonicalRemotePath => RemotePath.Canonical;

        public long ExpectedLength { get; }

        public static GoogleDriveBinaryUploadRequest Parse(
            Guid remoteProfileId,
            string relativeRemotePath,
            long expectedLength) =>
            new(
                remoteProfileId,
                GoogleDriveRelativePath.Parse(relativeRemotePath),
                expectedLength);

        public string ToSafeDiagnosticString() =>
            "Google Drive binary upload request " +
            $"(segments={RemotePath.Segments.Count}; " +
            $"expectedBytes={ExpectedLength})";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// Immutable, Infrastructure-only outcome for one binary upload.
    /// Failure never reports completed bytes.
    /// </summary>
    internal sealed class GoogleDriveBinaryUploadResult
    {
        public GoogleDriveBinaryUploadResult(
            GoogleDriveBinaryUploadStatus status,
            long completedBytes,
            string? safeErrorCode = null)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (completedBytes < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedBytes),
                    "Completed bytes cannot be negative.");
            }

            if (status == GoogleDriveBinaryUploadStatus.Completed)
            {
                if (safeErrorCode is not null)
                {
                    throw new ArgumentException(
                        "A completed upload cannot contain an error code.",
                        nameof(safeErrorCode));
                }
            }
            else
            {
                if (completedBytes != 0)
                {
                    throw new ArgumentException(
                        "A failed upload cannot contain completed bytes.",
                        nameof(completedBytes));
                }

                string expectedErrorCode = status switch
                {
                    GoogleDriveBinaryUploadStatus.Failed =>
                        GoogleDriveBinaryUploadErrorCodes.Failed,
                    GoogleDriveBinaryUploadStatus.Indeterminate =>
                        GoogleDriveBinaryUploadErrorCodes
                            .CompletionIndeterminate,
                    _ => throw new ArgumentOutOfRangeException(nameof(status))
                };
                if (!string.Equals(
                        safeErrorCode,
                        expectedErrorCode,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The safe error code does not match the upload status.",
                        nameof(safeErrorCode));
                }
            }

            Status = status;
            CompletedBytes = completedBytes;
            SafeErrorCode = safeErrorCode;
        }

        public GoogleDriveBinaryUploadStatus Status { get; }

        public long CompletedBytes { get; }

        public string? SafeErrorCode { get; }

        public string ToSafeDiagnosticString() =>
            $"Google Drive binary upload: status={Status}; " +
            $"completedBytes={CompletedBytes}";

        public override string ToString() => ToSafeDiagnosticString();
    }
}
