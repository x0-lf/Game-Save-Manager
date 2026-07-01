namespace GameSave.SavePaths
{
    public sealed record SavePathImportItem(
        string SteamAppId,
        string? GameName,
        string Platform,
        string PathTemplate,
        string PathKind,
        string SourceName,
        string? SourceUrl,
        string? SourceLicense,
        string? Notes,
        int Priority = 100);
}