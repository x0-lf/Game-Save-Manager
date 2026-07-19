namespace GameSaves.Core.Sync
{
    /// <summary>What a sync would do with one backup run.</summary>
    public enum SyncItemAction
    {
        /// <summary>Present and identical on both sides; nothing to do.</summary>
        InSync = 0,

        /// <summary>Exists locally only; copy it to the remote.</summary>
        UploadToRemote = 1,

        /// <summary>Exists remotely only; copy it to the local backup base.</summary>
        DownloadToLocal = 2,

        /// <summary>
        /// Exists on both sides with the same name but different content.
        /// Conflicts are reported and never copied automatically.
        /// </summary>
        Conflict = 3
    }

    public enum SyncItemStatus
    {
        Unknown = 0,
        DryRun = 1,
        Uploaded = 2,
        Downloaded = 3,

        // Conflicts are never copied; resolve manually (e.g. export one side)
        SkippedConflict = 4,

        // The target appeared since the preview; nothing is ever overwritten
        SkippedAlreadyExists = 5,

        Failed = 6,

        // The user deselected this run in the plan; it was not copied
        SkippedDeselected = 7
    }
}
