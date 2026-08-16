namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveBinaryDownloadStatus
    {
        Completed = 0,
        Failed = 1
    }

    internal static class GoogleDriveBinaryDownloadErrorCodes
    {
        public const string Failed = "GoogleDriveBinaryDownloadFailed";
        public const string InvalidSourcePath =
            "GoogleDriveDownloadInvalidSourcePath";
        public const string DestinationExists =
            "GoogleDriveDownloadDestinationExists";
    }

    /// <summary>
    /// Immutable, Infrastructure-only input for downloading one Google Drive
    /// file to one local destination. The destination is never overwritten,
    /// so the request carries no overwrite option.
    /// </summary>
    internal sealed class GoogleDriveBinaryDownloadRequest
    {
        public GoogleDriveBinaryDownloadRequest(
            Guid remoteProfileId,
            GoogleDriveRelativePath remotePath)
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
                    "A binary download source path is required.",
                    nameof(remotePath));
            }

            RemoteProfileId = remoteProfileId;
            RemotePath = remotePath;
        }

        public Guid RemoteProfileId { get; }

        public GoogleDriveRelativePath RemotePath { get; }

        public string CanonicalRemotePath => RemotePath.Canonical;

        public string ExactFileName => RemotePath.Segments[^1];

        public static GoogleDriveBinaryDownloadRequest Parse(
            Guid remoteProfileId,
            string relativeRemotePath) =>
            new(
                remoteProfileId,
                GoogleDriveRelativePath.Parse(relativeRemotePath));

        public string ToSafeDiagnosticString() =>
            "Google Drive binary download request " +
            $"(segments={RemotePath.Segments.Count})";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// Immutable, Infrastructure-only outcome for one binary download.
    /// Failure never reports completed bytes, and a completed download never
    /// carries an error code.
    /// </summary>
    internal sealed class GoogleDriveBinaryDownloadResult
    {
        public GoogleDriveBinaryDownloadResult(
            GoogleDriveBinaryDownloadStatus status,
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

            if (status == GoogleDriveBinaryDownloadStatus.Completed)
            {
                if (safeErrorCode is not null)
                {
                    throw new ArgumentException(
                        "A completed download cannot contain an error code.",
                        nameof(safeErrorCode));
                }
            }
            else
            {
                if (completedBytes != 0)
                {
                    throw new ArgumentException(
                        "A failed download cannot contain completed bytes.",
                        nameof(completedBytes));
                }
                if (string.IsNullOrWhiteSpace(safeErrorCode))
                {
                    throw new ArgumentException(
                        "A failed download requires a safe error code.",
                        nameof(safeErrorCode));
                }
            }

            Status = status;
            CompletedBytes = completedBytes;
            SafeErrorCode = safeErrorCode;
        }

        public GoogleDriveBinaryDownloadStatus Status { get; }

        public long CompletedBytes { get; }

        public string? SafeErrorCode { get; }

        public string ToSafeDiagnosticString() =>
            $"Google Drive binary download: status={Status}; " +
            $"completedBytes={CompletedBytes}";

        public override string ToString() => ToSafeDiagnosticString();
    }
}
