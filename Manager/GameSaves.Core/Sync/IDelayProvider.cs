namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Waits for a requested duration. The seam exists so bounded retry backoff
    /// can be tested without spending the delay: nothing in Core or
    /// Infrastructure calls <c>Task.Delay</c> directly, so a test can substitute
    /// an implementation that records what was requested and returns at once.
    /// </summary>
    /// <remarks>
    /// Deliberately as thin as <see cref="IUtcClock"/>. It carries no retry
    /// policy, no attempt counting, and no backoff arithmetic; those belong to
    /// the caller that decides to wait.
    /// </remarks>
    public interface IDelayProvider
    {
        /// <summary>
        /// Completes after <paramref name="duration"/> has elapsed, or sooner
        /// with an <see cref="OperationCanceledException"/> if the token is
        /// cancelled. A cancelled wait must return promptly rather than
        /// sleeping out the remainder, because a user cancelling a sync during
        /// a backoff must not wait for it.
        /// </summary>
        Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default);
    }
}
