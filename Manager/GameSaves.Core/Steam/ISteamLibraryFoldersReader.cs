
namespace GameSaves.Core.Steam
{
    public interface ISteamLibraryFoldersReader
    {
        IEnumerable<string> ReadLibraryPaths(string steamRoot);
    }
}