namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveLocalDownloadDestinationFailure
    {
        InvalidPath = 0,
        AlreadyExists = 1,
        DirectoryUnavailable = 2,
        Unwritable = 3,
        Failed = 4
    }

    internal static class GoogleDriveLocalDownloadDestinationErrorCodes
    {
        public const string InvalidPath =
            "GoogleDriveDownloadInvalidDestinationPath";
        public const string AlreadyExists =
            GoogleDriveBinaryDownloadErrorCodes.DestinationExists;
        public const string DirectoryUnavailable =
            "GoogleDriveDownloadDestinationDirectoryUnavailable";
        public const string Unwritable =
            "GoogleDriveDownloadDestinationUnwritable";
        public const string Failed = "GoogleDriveDownloadDestinationFailed";

        public static string ForFailure(
            GoogleDriveLocalDownloadDestinationFailure failure) =>
            failure switch
            {
                GoogleDriveLocalDownloadDestinationFailure.InvalidPath =>
                    InvalidPath,
                GoogleDriveLocalDownloadDestinationFailure.AlreadyExists =>
                    AlreadyExists,
                GoogleDriveLocalDownloadDestinationFailure
                    .DirectoryUnavailable => DirectoryUnavailable,
                GoogleDriveLocalDownloadDestinationFailure.Unwritable =>
                    Unwritable,
                GoogleDriveLocalDownloadDestinationFailure.Failed => Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    internal sealed class GoogleDriveLocalDownloadDestinationException
        : Exception
    {
        public GoogleDriveLocalDownloadDestinationException(
            GoogleDriveLocalDownloadDestinationFailure failure)
            : base(SafeMessage(failure))
        {
            if (!Enum.IsDefined(failure))
                throw new ArgumentOutOfRangeException(nameof(failure));

            Failure = failure;
            SafeErrorCode =
                GoogleDriveLocalDownloadDestinationErrorCodes.ForFailure(failure);
        }

        public GoogleDriveLocalDownloadDestinationFailure Failure { get; }

        public string SafeErrorCode { get; }

        public override string ToString() =>
            $"{GetType().FullName}: {Message} ({SafeErrorCode})";

        private static string SafeMessage(
            GoogleDriveLocalDownloadDestinationFailure failure) =>
            failure switch
            {
                GoogleDriveLocalDownloadDestinationFailure.InvalidPath =>
                    "The local download destination path is invalid.",
                GoogleDriveLocalDownloadDestinationFailure.AlreadyExists =>
                    "The local download destination already exists and is never replaced.",
                GoogleDriveLocalDownloadDestinationFailure
                    .DirectoryUnavailable =>
                    "The local download destination folder is unavailable.",
                GoogleDriveLocalDownloadDestinationFailure.Unwritable =>
                    "The local download destination could not be opened for writing.",
                GoogleDriveLocalDownloadDestinationFailure.Failed =>
                    "The local download destination could not be prepared.",
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    /// <summary>
    /// One prepared download destination: an unused final path and one open
    /// temporary sibling that receives the streamed content. Nothing existing
    /// is opened, truncated, moved, or deleted here.
    /// </summary>
    internal sealed class GoogleDriveLocalDownloadDestination : IDisposable
    {
        internal const string TemporarySuffix = ".gsdownload";

        public GoogleDriveLocalDownloadDestination(
            string finalPath,
            string temporaryPath,
            FileStream stream)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                throw new ArgumentException(
                    "A final destination path is required.",
                    nameof(finalPath));
            }
            if (string.IsNullOrWhiteSpace(temporaryPath))
            {
                throw new ArgumentException(
                    "A temporary destination path is required.",
                    nameof(temporaryPath));
            }
            if (string.Equals(finalPath, temporaryPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The temporary path cannot be the final path.",
                    nameof(temporaryPath));
            }

            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanWrite || stream.CanRead)
            {
                throw new ArgumentException(
                    "A write-only stream is required.",
                    nameof(stream));
            }

            FinalPath = finalPath;
            TemporaryPath = temporaryPath;
            Stream = stream;
        }

        public string FinalPath { get; }

        public string TemporaryPath { get; }

        public FileStream Stream { get; }

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>
    /// Refuses an occupied destination before any Drive work and opens one
    /// unique temporary sibling beside it. Temporary-file removal belongs to
    /// the later cleanup slice; this type never deletes anything.
    /// </summary>
    internal sealed class GoogleDriveLocalDownloadDestinationOpener
    {
        public Task<GoogleDriveLocalDownloadDestination> OpenAsync(
            string localFilePath,
            CancellationToken cancellationToken = default) =>
            Task.Run(() => Open(localFilePath, cancellationToken), cancellationToken);

        internal static void ValidateAvailable(string localFilePath)
        {
            if (string.IsNullOrWhiteSpace(localFilePath))
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);

            try
            {
                if (File.Exists(localFilePath) || Directory.Exists(localFilePath))
                {
                    throw Failure(
                        GoogleDriveLocalDownloadDestinationFailure.AlreadyExists);
                }
            }
            catch (GoogleDriveLocalDownloadDestinationException)
            {
                throw;
            }
            catch (ArgumentException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);
            }
            catch (NotSupportedException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);
            }
            catch (PathTooLongException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);
            }
            catch (UnauthorizedAccessException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.Unwritable);
            }
            catch (IOException)
            {
                throw Failure(
                    GoogleDriveLocalDownloadDestinationFailure.DirectoryUnavailable);
            }
        }

        private static GoogleDriveLocalDownloadDestination Open(
            string localFilePath,
            CancellationToken cancellationToken)
        {
            FileStream? stream = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateAvailable(localFilePath);
                cancellationToken.ThrowIfCancellationRequested();

                string directory = DestinationDirectory(localFilePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                cancellationToken.ThrowIfCancellationRequested();
                string temporaryPath = TemporaryPathFor(localFilePath);
                stream = new FileStream(
                    temporaryPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous |
                            FileOptions.SequentialScan
                    });

                cancellationToken.ThrowIfCancellationRequested();
                ValidateAvailable(localFilePath);

                var destination = new GoogleDriveLocalDownloadDestination(
                    localFilePath,
                    temporaryPath,
                    stream);
                cancellationToken.ThrowIfCancellationRequested();
                stream = null;
                return destination;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveLocalDownloadDestinationException)
            {
                throw;
            }
            catch (ArgumentException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);
            }
            catch (NotSupportedException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);
            }
            catch (PathTooLongException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);
            }
            catch (UnauthorizedAccessException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.Unwritable);
            }
            catch (DirectoryNotFoundException)
            {
                throw Failure(
                    GoogleDriveLocalDownloadDestinationFailure.DirectoryUnavailable);
            }
            catch (IOException)
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.Unwritable);
            }
            catch
            {
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.Failed);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        internal static string TemporaryPathFor(string localFilePath)
        {
            string directory = DestinationDirectory(localFilePath);
            string name = Path.GetFileName(localFilePath);
            if (string.IsNullOrEmpty(name))
                throw Failure(GoogleDriveLocalDownloadDestinationFailure.InvalidPath);

            return Path.Combine(
                directory,
                $"{name}.{Guid.NewGuid():N}{GoogleDriveLocalDownloadDestination.TemporarySuffix}");
        }

        private static string DestinationDirectory(string localFilePath)
        {
            string? directory = Path.GetDirectoryName(localFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw Failure(
                    GoogleDriveLocalDownloadDestinationFailure.DirectoryUnavailable);
            }

            return directory;
        }

        private static GoogleDriveLocalDownloadDestinationException Failure(
            GoogleDriveLocalDownloadDestinationFailure failure) => new(failure);
    }
}
