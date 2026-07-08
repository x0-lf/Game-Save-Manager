namespace GameSaves.Core.Steam
{
    public sealed record SteamFallbackScanProgress(
        string CurrentPath,
        int DirectoriesScanned,
        int LibrariesFound);
}