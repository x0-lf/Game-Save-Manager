using System.Collections.Generic;

namespace GameSaves.Core.Steam
{
    public sealed class SteamFallbackScanResult
    {
        public List<string> LibraryPaths { get; } = new();

        public List<string> SkippedDirectories { get; } = new();

        public int DirectoriesScanned { get; set; }
    }
}