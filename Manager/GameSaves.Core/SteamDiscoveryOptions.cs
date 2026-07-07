using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
{
    public sealed class SteamDiscoveryOptions
    {
        public SteamFallbackScanMode FallbackScanMode { get; init; }
            = SteamFallbackScanMode.WhenNormalDiscoveryFails;

        public bool EnableDeepFallbackScan { get; init; } = true;

        public int FallbackMaxDepth { get; init; } = 5;

        public TimeSpan? FallbackTimeout { get; init; } = TimeSpan.FromSeconds(30);

        public int MaxSkippedDirectoryLogEntries { get; init; } = 100;
    }
}
