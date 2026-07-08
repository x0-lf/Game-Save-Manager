namespace GameSaves.Core.Steam
{
    public interface ISteamAppManifestReader
    {
        IEnumerable<SteamGame> ReadInstalledGames(string libraryPath, SteamDiscoveryConfidence confidenceWhenFolderExists = SteamDiscoveryConfidence.High);
    }
}