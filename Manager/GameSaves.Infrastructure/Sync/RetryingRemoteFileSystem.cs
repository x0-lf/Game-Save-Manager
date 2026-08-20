using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Retries a remote operation whose failure the backend has already
    /// classified as retryable, waiting a bounded exponential backoff between
    /// attempts.
    /// </summary>
    /// <remarks>
    /// It sits at <see cref="IRemoteFileSystem"/> rather than inside each
    /// backend service for three reasons. It is provider-neutral, so no Google
    /// type reaches this file and any future backend gets the same behaviour by
    /// supplying its own predicate. It wraps the eleven operations the engine
    /// actually calls, instead of the dozens of client calls beneath them. And
    /// it changes no existing service, so the call-count assertions throughout
    /// the suite keep measuring what they were written to measure.
    ///
    /// Retrying a write is safe here precisely because every write is
    /// create-only or an allowlisted metadata replacement. A retry after a
    /// create that succeeded server-side but failed to report back will find
    /// the object present and refuse it, which surfaces as a failure rather
    /// than as an overwrite. Failing that way is the correct outcome: the rule
    /// that existing content is never replaced outranks completing the run.
    /// </remarks>
    internal sealed class RetryingRemoteFileSystem : IRemoteFileSystem
    {
        internal const int DefaultMaxAttempts = 4;

        internal static readonly TimeSpan DefaultBaseDelay =
            TimeSpan.FromSeconds(1);

        /// <summary>
        /// The ceiling on everything one operation may spend waiting. With the
        /// defaults the computed backoff totals seven seconds, so this is
        /// headroom rather than a constraint; it exists so a future change to
        /// the base delay or the attempt count cannot quietly turn a failing
        /// sync into a hang.
        /// </summary>
        internal static readonly TimeSpan MaximumTotalDelay =
            TimeSpan.FromSeconds(30);

        private readonly IRemoteFileSystem _inner;
        private readonly IDelayProvider _delay;
        private readonly Func<Exception, bool> _isRetryable;
        private readonly int _maxAttempts;
        private readonly TimeSpan _baseDelay;

        public RetryingRemoteFileSystem(
            IRemoteFileSystem inner,
            IDelayProvider delay,
            Func<Exception, bool> isRetryable,
            int maxAttempts = DefaultMaxAttempts,
            TimeSpan? baseDelay = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            _isRetryable = isRetryable ??
                throw new ArgumentNullException(nameof(isRetryable));

            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAttempts),
                    "At least one attempt is required.");
            }

            _maxAttempts = maxAttempts;
            _baseDelay = baseDelay ?? DefaultBaseDelay;

            if (_baseDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseDelay),
                    "A backoff delay cannot be negative.");
            }
        }

        public string DisplayRoot => _inner.DisplayRoot;

        public string GetDisplayPath(string relativePath) =>
            _inner.GetDisplayPath(relativePath);

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            RunAsync(token => _inner.ValidateAsync(token), cancellationToken);

        public Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default) =>
            RunAsync(token => _inner.RootExistsAsync(token), cancellationToken);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default) =>
            RunAsync(token => _inner.ListRunFolderNamesAsync(token), cancellationToken);

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.FolderExistsAsync(relativeFolder, token),
                cancellationToken);

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.ReadTextFileAsync(relativePath, token),
                cancellationToken);

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.CreateTextFileIfMissingAsync(relativePath, content, token),
                cancellationToken);

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.ReadProviderMetadataAsync(relativePath, token),
                cancellationToken);

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.ReplaceProviderMetadataAsync(relativePath, content, token),
                cancellationToken);

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.ListFilesAsync(relativeFolder, token),
                cancellationToken);

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.UploadFileAsync(localFilePath, relativeRemotePath, token),
                cancellationToken);

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                token => _inner.DownloadFileAsync(relativeRemotePath, localFilePath, token),
                cancellationToken);

        private async Task RunAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            await RunAsync<object?>(
                async token =>
                {
                    await operation(token);
                    return null;
                },
                cancellationToken);
        }

        private async Task<T> RunAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            TimeSpan spent = TimeSpan.Zero;

            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await operation(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // A cancelled operation is not a failure to retry.
                    throw;
                }
                catch (Exception exception) when (
                    attempt < _maxAttempts && _isRetryable(exception))
                {
                    TimeSpan wait = NextDelay(attempt, spent);

                    if (wait <= TimeSpan.Zero)
                        throw;

                    spent += wait;

                    // The token is passed so a user cancelling during a backoff
                    // is not made to wait it out.
                    await _delay.DelayAsync(wait, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Exponential backoff from the base delay, clamped so the total spent
        /// waiting never exceeds <see cref="MaximumTotalDelay"/>. Returns zero
        /// when the budget is exhausted, which the caller treats as "stop
        /// retrying" rather than as "wait for no time".
        /// </summary>
        private TimeSpan NextDelay(int attempt, TimeSpan alreadySpent)
        {
            TimeSpan remaining = MaximumTotalDelay - alreadySpent;

            if (remaining <= TimeSpan.Zero)
                return TimeSpan.Zero;

            double seconds = _baseDelay.TotalSeconds * Math.Pow(2, attempt - 1);
            TimeSpan wait = TimeSpan.FromSeconds(seconds);

            return wait > remaining ? remaining : wait;
        }
    }
}
