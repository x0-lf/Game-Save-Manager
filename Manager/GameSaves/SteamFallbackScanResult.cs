using System.Collections.Generic;

namespace GameSave
{
    public sealed class SteamFallbackScanResult
    {
        public List<string> LibraryPaths { get; } = new();

        public List<string> SkippedDirectories { get; } = new();

        public int DirectoriesScanned { get; set; }
    }
}