using GameSaves.Core.Sync;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One provider shown in the Sync selector. Milestone B exposes only
    /// implemented providers; future enum values remain unavailable.
    /// </summary>
    public sealed record SyncProviderOption(
        SyncProviderKind Kind,
        string DisplayName);
}
