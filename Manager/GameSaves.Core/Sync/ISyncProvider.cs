namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Syncs backup runs between the local backup base and a remote location.
    /// Copy-only in both directions: a run missing on one side is copied there,
    /// never deleted from the other; conflicts are reported, never resolved
    /// automatically; nothing existing is ever overwritten. Providers may hold
    /// live connections; dispose them when a new provider replaces them.
    /// </summary>
    public interface ISyncProvider : IDisposable
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
    /// Creates providers for configured remotes. Local folder and SFTP exist
    /// today; WebDAV and cloud providers come later.
    /// </summary>
    public interface ISyncProviderFactory
    {
        ISyncProvider CreateLocalFolderProvider(string remoteRoot);

        ISyncProvider CreateSftpProvider(SftpConnectionSettings settings);

        /// <summary>
        /// Removes the stored host-key fingerprint for a server, so the next
        /// connection is treated as a first connect again.
        /// </summary>
        void ForgetSftpHostKey(string host, int port);
    }
}
