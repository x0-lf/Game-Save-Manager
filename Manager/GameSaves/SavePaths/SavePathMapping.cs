namespace GameSave.SavePaths
{
    public enum SavePathKind
    {
        Directory,
        File,
        Glob
    }

    public sealed record SavePathMapping(
        long Id,
        string SteamAppId,
        string? GameName,
        string Platform,
        string PathTemplate,
        SavePathKind PathKind,
        string SourceName,
        string? SourceUrl,
        string? SourceLicense,
        string? Notes,
        int Priority,
        bool Enabled);
}