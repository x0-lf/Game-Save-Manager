namespace GameSaves.Core.Steam
{
    public sealed record SteamLibraryInfo(
        string LibraryPath,
        bool HasSteamApps,
        bool HasCommonFolder,
        int ManifestCount)
    {
        public bool IsValid => HasSteamApps;
    }
}