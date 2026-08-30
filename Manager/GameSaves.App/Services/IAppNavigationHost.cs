using GameSaves.Core.Sync;

namespace GameSaves.App.Services
{
    /// <summary>
    /// The one place a view model may ask to be taken somewhere else in the
    /// shell. Navigation is a window concern — which tab is attached, which has
    /// been floated into its own window — so the main window implements this
    /// and hands it to the view models that need it, exactly as it already does
    /// with <see cref="IWorkspaceLayoutHost"/>.
    /// </summary>
    public interface IAppNavigationHost
    {
        /// <summary>
        /// Opens the Sync section and selects <paramref name="kind"/> so its
        /// existing configuration panel is showing. This is navigation only: it
        /// never enables a provider, changes a connection, or touches a
        /// credential — it puts the user in front of the setup experience that
        /// already exists.
        /// </summary>
        void ShowSyncProviderConfiguration(SyncProviderKind kind);
    }
}
