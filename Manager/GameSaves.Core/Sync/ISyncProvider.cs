namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Syncs backup runs between the local backup base and a remote location.
    /// Copy-only in both directions: a run missing on one side is copied there,
    /// never deleted from the other; conflicts are reported, never resolved
    /// automatically; nothing existing is ever overwritten.
    /// </summary>
    public interface ISyncProvider
    {
        string ProviderName { get; }

        string RemoteRoot { get; }

        Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default);

        Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>The remote's sync log (version history), newest first.</summary>
        Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Creates providers for configured remotes. Only the local-folder
    /// provider exists today; WebDAV/SFTP/cloud providers come later.
    /// </summary>
    public interface ISyncProviderFactory
    {
        ISyncProvider CreateLocalFolderProvider(string remoteRoot);
    }
}
