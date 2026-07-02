using GameSave.External.Http;

namespace GameSave.External
{
    public sealed class PcgwHarvestOptions
    {
        public required string DatabasePath { get; init; }

        public required string OutputRoot { get; init; }

        public required string UserAgent { get; init; }

        public IReadOnlyList<string> SteamAppIds { get; init; } = Array.Empty<string>();

        public int RequestsPerMinute { get; init; } = 20;

        public int PauseEveryRequests { get; init; } = 100;

        public TimeSpan PauseEveryRequestsDuration { get; init; } = TimeSpan.FromMinutes(1);

        public int MaxTitlesToProcess { get; init; } = 0;

        public bool ImportExtractedMappingsDisabled { get; init; } = true;

        public RateLimitOptions ToRateLimitOptions()
        {
            return new RateLimitOptions
            {
                RequestsPerMinute = RequestsPerMinute,
                PauseEveryRequests = PauseEveryRequests,
                PauseEveryRequestsDuration = PauseEveryRequestsDuration
            };
        }
    }
}