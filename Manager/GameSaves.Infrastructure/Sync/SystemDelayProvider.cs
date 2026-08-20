using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// The production delay: a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>,
    /// which already returns promptly on cancellation and already rejects a
    /// duration below <c>-1</c> millisecond. Adding validation here would be a
    /// second opinion on rules the framework already enforces.
    /// </summary>
    public sealed class SystemDelayProvider : IDelayProvider
    {
        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            Task.Delay(duration, cancellationToken);
    }
}
