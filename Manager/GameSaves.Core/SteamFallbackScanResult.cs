using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
{
    public sealed class SteamFallbackScanResult
    {
        public List<string> LibraryPaths { get; } = new();

        public List<string> SkippedDirectories { get; } = new();

        public int DirectoriesScanned { get; set; }
    }
}
