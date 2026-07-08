using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core.Steam
{
    public interface ISteamDiscoveryService
    {
        SteamDiscoveryResult Discover(
            SteamDiscoveryOptions? options = null,
            IProgress<SteamFallbackScanProgress>? fallbackProgress = null,
            CancellationToken cancellationToken = default
            );
    }
}
