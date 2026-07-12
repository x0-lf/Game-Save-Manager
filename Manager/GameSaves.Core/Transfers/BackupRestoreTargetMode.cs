namespace GameSaves.Core.Transfers
{
    /// <summary>Where the files of a backup run are restored/copied to.</summary>
    public enum BackupRestoreTargetMode
    {
        /// <summary>The original paths recorded in the backup manifest.</summary>
        OriginalPath = 0,

        /// <summary>
        /// The Steam userdata game folder of a user-selected profile:
        /// files under userdata\&lt;any account&gt;\&lt;AppId&gt; are redirected to
        /// targetProfile.UserDataPath\&lt;AppId&gt;. Files outside a userdata game
        /// folder cannot be redirected and are skipped.
        /// </summary>
        SelectedSteamProfileUserData = 1,

        /// <summary>Reserved for a future user-supplied custom path.</summary>
        CustomPathLater = 2,

        /// <summary>
        /// A location resolved from an approved save-path mapping in the
        /// database, chosen by TargetMappingId and re-resolved at execution
        /// time. Only approved/enabled mappings that resolve to exactly one
        /// path can be used.
        /// </summary>
        ApprovedMappingLocation = 3
    }
}
