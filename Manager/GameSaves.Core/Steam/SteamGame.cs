namespace GameSaves.Core.Steam
{
    public sealed record SteamGame(
        string AppId,
        string Name,
        string InstallDirectory,
        string LibraryPath,
        string ManifestPath,
        string GamePath,
        bool FolderExists,
        SteamDiscoveryConfidence Confidence);
}