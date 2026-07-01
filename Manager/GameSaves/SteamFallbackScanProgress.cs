namespace GameSave
{
    public sealed record SteamFallbackScanProgress(
        string CurrentPath,
        int DirectoriesScanned,
        int LibrariesFound);
}