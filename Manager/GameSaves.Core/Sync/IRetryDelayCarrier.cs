namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Implemented by exceptions or error classifications that carry an instructed
    /// server delay from an HTTP Retry-After response header or provider backoff advice.
    /// </summary>
    public interface IRetryDelayCarrier
    {
        /// <summary>
        /// The earliest delay instructed by the server before retrying, or null
        /// if no explicit retry timing was supplied by the remote endpoint.
        /// </summary>
        TimeSpan? RetryAfterDelay { get; }
    }
}
