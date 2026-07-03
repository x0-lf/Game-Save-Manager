namespace GameSave.External.Steam
{
    public enum SteamCatalogAppKind
    {
        Game,
        Dlc
    }

    public sealed record SteamCatalogApp(
        string SteamAppId,
        string Name,
        SteamCatalogAppKind Kind,
        long? LastModifiedUnix,
        string? PriceChangeNumber);

    public sealed class SteamCatalogFetchOptions
    {
        public required string DatabasePath { get; init; }

        public required string OutputRoot { get; init; }

        public required string SteamWebApiKey { get; init; }

        public SteamCatalogAppKind Kind { get; init; } = SteamCatalogAppKind.Game;

        public int MaxResultsPerPage { get; init; } = 50_000;

        public int MaxAppsToFetch { get; init; } = 0;

        public long? IfModifiedSinceUnix { get; init; }

        public string UserAgent { get; init; } = "SaveGameManager/0.1 github.com/x0-lf andwyrdan@protonmail.com .NET/8.0";
    }

    public sealed record SteamCatalogFetchResult(
        SteamCatalogAppKind Kind,
        int AppsFetched,
        string JsonOutputPath,
        string AppIdsOutputPath);

    public sealed record SteamCatalogMissingExportResult(
        int MissingCount,
        string OutputPath);
}