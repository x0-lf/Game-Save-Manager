namespace GameSaves.External
{
    public sealed record PcgwTitle(
        int PageId,
        string PageName,
        string? DisplayTitle,
        List<string> SteamAppIds,
        string SourceUrl);
}