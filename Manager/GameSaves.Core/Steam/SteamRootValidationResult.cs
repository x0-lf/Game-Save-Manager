namespace GameSaves.Core.Steam
{
    public sealed record SteamRootValidationResult(
        string SteamRoot,
        bool HasSteamExe,
        bool HasSteamDll,
        bool HasSteamAppsDirectory,
        bool HasConfigDirectory)
    {
        public bool IsLikelySteamRoot =>
            HasSteamExe &&
            HasSteamDll &&
            HasSteamAppsDirectory;
    }
}