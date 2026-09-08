using GameSaves.Core.Sync;

namespace GameSaves.App.Models
{
    /// <summary>
    /// Where a backup run actually is, as the sync plan sees it. Derived from
    /// the plan data the provider already returned; nothing here triggers a
    /// second remote enumeration.
    /// </summary>
    public enum SyncPresence
    {
        /// <summary>Only the local backup base holds this run.</summary>
        LocalOnly = 0,

        /// <summary>Only the remote holds this run.</summary>
        RemoteOnly = 1,

        /// <summary>Both sides hold it and the manifests agree.</summary>
        BothIdentical = 2,

        /// <summary>Both sides hold it and the manifests disagree.</summary>
        BothConflicting = 3,

        /// <summary>
        /// The plan carries no measurable content for the run (no files, no
        /// bytes), so neither side can be confirmed by content. An interrupted
        /// upload is the usual cause.
        /// </summary>
        Unverifiable = 4
    }

    /// <summary>
    /// The result of re-reading both sides after a transfer finished. Kept
    /// separate from <see cref="SyncItemStatus"/> on purpose: a run can be
    /// copied and unverified at the same time, and one enum that had to say
    /// both would have to lie about one of them.
    /// </summary>
    public enum SyncVerificationState
    {
        /// <summary>No revalidation has been attempted for this run yet.</summary>
        NotRequested = 0,

        /// <summary>Revalidation is running.</summary>
        Running = 1,

        /// <summary>Present on both sides with matching manifests.</summary>
        Verified = 2,

        /// <summary>Present on both sides, manifests differ.</summary>
        ContentMismatch = 3,

        /// <summary>The local side no longer reports the run.</summary>
        MissingLocally = 4,

        /// <summary>The remote no longer reports the run.</summary>
        MissingRemotely = 5,

        /// <summary>Neither side reports the run.</summary>
        MissingBothSides = 6,

        /// <summary>
        /// The endpoint could not be re-read at all (offline, unauthorized,
        /// rate limited). This says nothing about the content and never
        /// downgrades the transfer that already succeeded.
        /// </summary>
        EndpointUnavailable = 7,

        /// <summary>The user stopped the revalidation.</summary>
        Cancelled = 8
    }

    /// <summary>
    /// How strongly a row reads, independent of the accent. The accent owns
    /// selection and direction; success, warning, and failure keep their own
    /// meaning so a Rose accent never turns an ordinary upload into an alarm.
    /// </summary>
    public enum SyncStateSeverity
    {
        Neutral = 0,

        /// <summary>An ordinary pending direction; painted with the accent.</summary>
        Direction = 1,

        Success = 2,
        Warning = 3,
        Danger = 4
    }
}
