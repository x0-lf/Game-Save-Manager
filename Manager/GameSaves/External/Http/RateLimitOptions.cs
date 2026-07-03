namespace GameSave.External.Http
{
    public sealed class RateLimitOptions
    {
        public int RequestsPerMinute { get; init; } = 20;

        public int PauseEveryRequests { get; init; } = 20;

        public TimeSpan PauseEveryRequestsDuration { get; init; } = TimeSpan.FromMinutes(1);

        public int MaxRetries { get; init; } = 2;

        public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    }
}