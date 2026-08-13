namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveLocalUploadSourceFailure
    {
        InvalidPath = 0,
        NotFound = 1,
        NotRegularFile = 2,
        ReparsePoint = 3,
        Unreadable = 4,
        InvalidLength = 5,
        Failed = 6
    }

    internal static class GoogleDriveLocalUploadSourceErrorCodes
    {
        public const string InvalidPath = "GoogleDriveUploadSourceInvalidPath";
        public const string NotFound = "GoogleDriveUploadSourceNotFound";
        public const string NotRegularFile =
            "GoogleDriveUploadSourceNotRegularFile";
        public const string ReparsePoint =
            "GoogleDriveUploadSourceReparsePoint";
        public const string Unreadable =
            "GoogleDriveUploadSourceUnreadable";
        public const string InvalidLength =
            "GoogleDriveUploadSourceInvalidLength";
        public const string Failed = "GoogleDriveUploadSourceFailed";

        public static string ForFailure(
            GoogleDriveLocalUploadSourceFailure failure) =>
            failure switch
            {
                GoogleDriveLocalUploadSourceFailure.InvalidPath => InvalidPath,
                GoogleDriveLocalUploadSourceFailure.NotFound => NotFound,
                GoogleDriveLocalUploadSourceFailure.NotRegularFile =>
                    NotRegularFile,
                GoogleDriveLocalUploadSourceFailure.ReparsePoint => ReparsePoint,
                GoogleDriveLocalUploadSourceFailure.Unreadable => Unreadable,
                GoogleDriveLocalUploadSourceFailure.InvalidLength =>
                    InvalidLength,
                GoogleDriveLocalUploadSourceFailure.Failed => Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    internal sealed class GoogleDriveLocalUploadSourceException : Exception
    {
        public GoogleDriveLocalUploadSourceException(
            GoogleDriveLocalUploadSourceFailure failure)
            : base(SafeMessage(failure))
        {
            if (!Enum.IsDefined(failure))
                throw new ArgumentOutOfRangeException(nameof(failure));

            Failure = failure;
            SafeErrorCode = GoogleDriveLocalUploadSourceErrorCodes.ForFailure(
                failure);
        }

        public GoogleDriveLocalUploadSourceFailure Failure { get; }

        public string SafeErrorCode { get; }

        private static string SafeMessage(
            GoogleDriveLocalUploadSourceFailure failure) =>
            failure switch
            {
                GoogleDriveLocalUploadSourceFailure.InvalidPath =>
                    "The local upload source path is invalid.",
                GoogleDriveLocalUploadSourceFailure.NotFound =>
                    "The local upload source file was not found.",
                GoogleDriveLocalUploadSourceFailure.NotRegularFile =>
                    "The local upload source is not a regular file.",
                GoogleDriveLocalUploadSourceFailure.ReparsePoint =>
                    "The local upload source cannot be a reparse point.",
                GoogleDriveLocalUploadSourceFailure.Unreadable =>
                    "The local upload source file could not be read.",
                GoogleDriveLocalUploadSourceFailure.InvalidLength =>
                    "The local upload source length is invalid.",
                GoogleDriveLocalUploadSourceFailure.Failed =>
                    "The local upload source could not be validated.",
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    internal static class GoogleDriveLocalUploadSourceValidator
    {
        public static void Validate(string localFilePath)
        {
            if (string.IsNullOrWhiteSpace(localFilePath))
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);

            try
            {
                ValidateAttributes(File.GetAttributes(localFilePath));
            }
            catch (GoogleDriveLocalUploadSourceException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.NotFound);
            }
            catch (DirectoryNotFoundException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.NotFound);
            }
            catch (ArgumentException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);
            }
            catch (NotSupportedException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);
            }
            catch (PathTooLongException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);
            }
            catch (UnauthorizedAccessException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.Unreadable);
            }
            catch (IOException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.Unreadable);
            }
            catch
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.Failed);
            }
        }

        internal static void ValidateAttributes(FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Failure(GoogleDriveLocalUploadSourceFailure.ReparsePoint);
            if ((attributes &
                    (FileAttributes.Directory | FileAttributes.Device)) != 0)
                throw Failure(GoogleDriveLocalUploadSourceFailure.NotRegularFile);
        }

        private static GoogleDriveLocalUploadSourceException Failure(
            GoogleDriveLocalUploadSourceFailure failure) => new(failure);
    }

    internal sealed class GoogleDriveLocalUploadSource : IDisposable
    {
        public GoogleDriveLocalUploadSource(FileStream stream, long length)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead || stream.CanWrite)
            {
                throw new ArgumentException(
                    "A read-only stream is required.",
                    nameof(stream));
            }
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            Stream = stream;
            Length = length;
        }

        public FileStream Stream { get; }

        public long Length { get; }

        public void Dispose() => Stream.Dispose();
    }

    internal sealed class GoogleDriveLocalUploadSourceOpener
    {
        public Task<GoogleDriveLocalUploadSource> OpenAsync(
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Open(localFilePath, cancellationToken),
                cancellationToken);
        }

        private static GoogleDriveLocalUploadSource Open(
            string localFilePath,
            CancellationToken cancellationToken)
        {
            FileStream? stream = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveLocalUploadSourceValidator.Validate(localFilePath);
                cancellationToken.ThrowIfCancellationRequested();

                stream = new FileStream(
                    localFilePath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        Options = FileOptions.Asynchronous |
                            FileOptions.SequentialScan
                    });

                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveLocalUploadSourceValidator.Validate(localFilePath);

                long length = stream.Length;
                if (length < 0)
                {
                    throw new GoogleDriveLocalUploadSourceException(
                        GoogleDriveLocalUploadSourceFailure.InvalidLength);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var source = new GoogleDriveLocalUploadSource(stream, length);
                cancellationToken.ThrowIfCancellationRequested();
                stream = null;
                return source;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveLocalUploadSourceException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.NotFound);
            }
            catch (DirectoryNotFoundException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.NotFound);
            }
            catch (ArgumentException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);
            }
            catch (NotSupportedException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);
            }
            catch (PathTooLongException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.InvalidPath);
            }
            catch (UnauthorizedAccessException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.Unreadable);
            }
            catch (IOException)
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.Unreadable);
            }
            catch
            {
                throw Failure(GoogleDriveLocalUploadSourceFailure.Failed);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private static GoogleDriveLocalUploadSourceException Failure(
            GoogleDriveLocalUploadSourceFailure failure) => new(failure);
    }
}
