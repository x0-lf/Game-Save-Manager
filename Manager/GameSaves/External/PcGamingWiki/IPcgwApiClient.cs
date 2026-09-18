namespace GameSaves.External
{
    /// <summary>
    /// Contract for interacting with the PCGamingWiki MediaWiki and Cargo APIs.
    /// Enables deterministic offline testing with mocked responses.
    /// </summary>
    public interface IPcgwApiClient
    {
        /// <summary>
        /// Queries PCGamingWiki Cargo tables to resolve a Steam AppID to a wiki page title and metadata.
        /// </summary>
        Task<PcgwTitle?> ResolveSteamAppIdAsync(
            string steamAppId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches the raw wikitext for a given PCGamingWiki page ID via MediaWiki action=parse.
        /// </summary>
        Task<string> GetWikitextByPageIdAsync(
            int pageId,
            CancellationToken cancellationToken = default);
    }
}
