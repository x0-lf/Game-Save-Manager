namespace GameSaves.Reviewer
{
    public sealed class MappingReviewItem
    {
        public long Id { get; init; }

        public string SteamAppId { get; init; } = string.Empty;

        public string GameName { get; init; } = string.Empty;

        public string Platform { get; init; } = string.Empty;

        public string PathTemplate { get; init; } = string.Empty;

        public string PathKind { get; init; } = string.Empty;

        public string SourceName { get; init; } = string.Empty;

        public string? SourceUrl { get; init; }

        public string? SourceLicense { get; init; }

        public string? Notes { get; init; }

        public int Priority { get; init; }

        public bool Enabled { get; init; }

        public string ReviewStatus { get; init; } = "Pending";

        public string? ReviewNotes { get; init; }

        public string DisplayTitle => string.IsNullOrWhiteSpace(GameName)
            ? $"Steam App {SteamAppId}"
            : GameName;
    }
}