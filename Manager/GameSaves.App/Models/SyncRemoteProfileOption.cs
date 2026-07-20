using GameSaves.Core.Sync;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One entry in the saved-profile selector. A null Profile is the explicit
    /// unsaved mode and keeps the current form settings authoritative.
    /// </summary>
    public sealed record SyncRemoteProfileOption(
        SyncRemoteProfile? Profile,
        string DisplayName);
}
