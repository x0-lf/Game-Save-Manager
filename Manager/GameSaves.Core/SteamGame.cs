using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
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
