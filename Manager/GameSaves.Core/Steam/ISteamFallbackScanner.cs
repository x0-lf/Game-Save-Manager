namespace GameSaves.Core.Steam
{
    public interface ISteamFallbackScanner
    {
        SteamFallbackScanResult Scan(SteamDiscoveryOptions options, IProgress<SteamFallbackScanProgress>? progress = null, CancellationToken cancellationToken = default);
    }
}