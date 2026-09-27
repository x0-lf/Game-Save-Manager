namespace GameSaves.Core.Sync
{
    /// <summary>What one read-only health check found about a remote.</summary>
    public enum ProviderHealthState
    {
        /// <summary>The remote answered and accepts this app's requests.</summary>
        Healthy = 0,

        /// <summary>The provider is throttling requests; trying later should work.</summary>
        RateLimited = 1,

        /// <summary>The provider itself reported that no storage space is left.</summary>
        QuotaExhausted = 2,

        /// <summary>
        /// The check could not complete: not signed in, unreachable, refused,
        /// or misconfigured. The report's reason says which.
        /// </summary>
        Unavailable = 3
    }

    /// <summary>
    /// Storage space exactly as the provider reported it, in bytes. Free space
    /// is always present; total and used only when the provider stated them.
    /// Nothing here is estimated.
    /// </summary>
    public sealed record RemoteCapacity(
        long FreeBytes,
        long? TotalBytes = null,
        long? UsedBytes = null);

    /// <summary>
    /// The result of <see cref="ISyncProvider.CheckHealthAsync"/>. A null
    /// capacity means the provider did not report one on this check; whether
    /// it ever can is the catalog's
    /// <see cref="SyncProviderCapabilities.SupportsRemoteQuota"/>, so
    /// "unsupported" (never reported) and "unknown" (supported, not reported
    /// this time) stay distinct from a failed check, which is
    /// <see cref="ProviderHealthState.Unavailable"/> with its reason.
    /// </summary>
    public sealed record ProviderHealthReport(
        ProviderHealthState State,
        string Reason,
        RemoteCapacity? Capacity = null);
}
