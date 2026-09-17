namespace GameSaves.Core.Sync;

/// <summary>
/// Event arguments delivered when a retry backoff delay begins or ends.
/// </summary>
public sealed record RetryBackoffEventArgs(
    int Attempt,
    int MaxAttempts,
    TimeSpan Delay,
    bool IsServerInstructed,
    Exception Exception,
    bool IsRateLimited);

/// <summary>
/// Observes and notifies retry backoff events, enabling active countdown
/// displays and throttling diagnostics across UI and sync layers.
/// </summary>
public interface IRetryBackoffNotifier
{
    void NotifyBackoffStarted(RetryBackoffEventArgs args);

    void NotifyBackoffEnded(RetryBackoffEventArgs args);

    event EventHandler<RetryBackoffEventArgs>? BackoffStarted;

    event EventHandler<RetryBackoffEventArgs>? BackoffEnded;
}

/// <summary>
/// Default in-memory implementation of <see cref="IRetryBackoffNotifier"/>.
/// </summary>
public sealed class RetryBackoffNotifier : IRetryBackoffNotifier
{
    public static readonly RetryBackoffNotifier Instance = new();

    public event EventHandler<RetryBackoffEventArgs>? BackoffStarted;

    public event EventHandler<RetryBackoffEventArgs>? BackoffEnded;

    public void NotifyBackoffStarted(RetryBackoffEventArgs args) =>
        BackoffStarted?.Invoke(this, args);

    public void NotifyBackoffEnded(RetryBackoffEventArgs args) =>
        BackoffEnded?.Invoke(this, args);
}
