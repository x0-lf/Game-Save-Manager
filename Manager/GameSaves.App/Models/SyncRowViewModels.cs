using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Common;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Linq;

namespace GameSaves.App.Models
{
    public sealed partial class SyncItemRowViewModel : ObservableObject
    {
        private readonly Action? _selectionChanged;

        // Actionable runs are included by default; unticking excludes just
        // that run from execution without rebuilding the preview.
        [ObservableProperty]
        private bool includeInSync = true;

        public SyncItemRowViewModel(
            SyncItem item,
            string remoteLabel,
            DateTimeOffset checkedAtUtc,
            Action? selectionChanged = null)
        {
            Item = item;
            RemoteLabel = string.IsNullOrWhiteSpace(remoteLabel)
                ? "Remote"
                : remoteLabel;
            CheckedAtUtc = checkedAtUtc;
            _selectionChanged = selectionChanged;
        }

        partial void OnIncludeInSyncChanged(bool value)
        {
            OnPropertyChanged(nameof(SelectionStateText));
            OnPropertyChanged(nameof(ActionAccessibleName));
            _selectionChanged?.Invoke();
        }

        public SyncItem Item { get; }

        /// <summary>
        /// What the remote side is called in this plan, so a row can say
        /// "Google Drive only" rather than a generic "remote only".
        /// </summary>
        public string RemoteLabel { get; }

        /// <summary>When the preview that produced this row read both sides.</summary>
        public DateTimeOffset CheckedAtUtc { get; }

        public bool IsSelectable =>
            Item.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal;

        public string RunName => Item.RunName;

        public string GameName => Item.GameName;

        public string ActionText => Item.Action switch
        {
            SyncItemAction.UploadToRemote => "Upload",
            SyncItemAction.DownloadToLocal => "Download",
            SyncItemAction.Conflict => "Conflict",
            _ => "In sync"
        };

        /// <summary>
        /// A manifest that reports no files and no bytes cannot be compared by
        /// content, which is what an interrupted upload leaves behind. Saying
        /// "0 file(s)" there would present a guess as a measurement.
        /// </summary>
        public bool HasMeasuredContent => Item.FileCount > 0 && Item.TotalBytes > 0;

        public SyncPresence Presence => !HasMeasuredContent
            ? SyncPresence.Unverifiable
            : Item.Action switch
            {
                SyncItemAction.UploadToRemote => SyncPresence.LocalOnly,
                SyncItemAction.DownloadToLocal => SyncPresence.RemoteOnly,
                SyncItemAction.InSync => SyncPresence.BothIdentical,
                SyncItemAction.Conflict => SyncPresence.BothConflicting,
                _ => SyncPresence.Unverifiable
            };

        public string PresenceText => Presence switch
        {
            SyncPresence.LocalOnly => "Local only",
            SyncPresence.RemoteOnly => $"{RemoteLabel} only",
            SyncPresence.BothIdentical => "On both, identical",
            SyncPresence.BothConflicting => "On both, conflicting",
            _ => "Incomplete or unverifiable"
        };

        public SyncStateSeverity Severity => Presence switch
        {
            SyncPresence.LocalOnly or SyncPresence.RemoteOnly =>
                SyncStateSeverity.Direction,
            SyncPresence.BothIdentical => SyncStateSeverity.Success,
            _ => SyncStateSeverity.Warning
        };

        public bool IsDirectionState => Severity == SyncStateSeverity.Direction;

        public bool IsSuccessState => Severity == SyncStateSeverity.Success;

        public bool IsWarningState => Severity == SyncStateSeverity.Warning;

        public bool IsDangerState => Severity == SyncStateSeverity.Danger;

        public string LocalLocationDisplay => DescribeLocation(
            Item.LocalPath,
            Item.ExistsLocally,
            "the local backup base");

        public string RemoteLocationDisplay => DescribeLocation(
            Item.RemotePath,
            Item.ExistsRemotely,
            RemoteLabel);

        public string FilesDisplay =>
            HasMeasuredContent ? $"{Item.FileCount} file(s)" : "Not available";

        public string SizeDisplay =>
            HasMeasuredContent ? FormatBytes(Item.TotalBytes) : "Not available";

        public string LastVerifiedDisplay =>
            $"Checked {CheckedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        public string SelectionStateText => !IsSelectable
            ? "Not selectable"
            : IncludeInSync ? "Selected" : "Not selected";

        /// <summary>
        /// The whole sentence a screen reader reads for the direction control:
        /// which run, which direction, whether it is selected now, and what
        /// activating it will do.
        /// </summary>
        public string ActionAccessibleName => IsSelectable
            ? $"{RunName}. {ActionText}. {SelectionStateText}. " +
              (IncludeInSync
                  ? "Activate to exclude this run from the next sync."
                  : "Activate to include this run in the next sync.")
            : $"{RunName}. {ActionText}. Not selectable. {StatusText}";

        public string ActionToolTip => IsSelectable
            ? "Click, or press Space, to include or exclude this run. Nothing is copied until you confirm and press Sync Now."
            : StatusText;

        public string StatusText => Item.StatusText;

        private static string DescribeLocation(
            string? path,
            bool exists,
            string sideName)
        {
            if (string.IsNullOrWhiteSpace(path))
                return $"Not available for {sideName}";

            return exists ? path! : $"{path} (not created yet)";
        }

        internal static string FormatBytes(long bytes) => ByteSize.Format(bytes);
    }

    /// <summary>
    /// One executed run. The transfer status the provider returned is never
    /// rewritten; revalidation is layered on top as its own state so a copied
    /// run is not described as verified, and a verification failure does not
    /// erase the transfer that succeeded.
    /// </summary>
    public sealed partial class SyncItemResultRowViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyPropertyChangedFor(nameof(StateGlyph))]
        [NotifyPropertyChangedFor(nameof(StateDetail))]
        [NotifyPropertyChangedFor(nameof(AffectedSideText))]
        [NotifyPropertyChangedFor(nameof(Severity))]
        [NotifyPropertyChangedFor(nameof(IsDirectionState))]
        [NotifyPropertyChangedFor(nameof(IsSuccessState))]
        [NotifyPropertyChangedFor(nameof(IsWarningState))]
        [NotifyPropertyChangedFor(nameof(IsDangerState))]
        [NotifyPropertyChangedFor(nameof(IsVerified))]
        private SyncVerificationState verification = SyncVerificationState.NotRequested;

        public SyncItemResultRowViewModel(
            SyncItemResult result,
            string remoteLabel = "the remote")
        {
            Result = result;
            RemoteLabel = string.IsNullOrWhiteSpace(remoteLabel)
                ? "the remote"
                : remoteLabel;

            // A finished copy is only a copy. Its verification state comes
            // from the post-sync re-read, never from the transfer itself.
        }

        public SyncItemResult Result { get; }

        public string RemoteLabel { get; }

        public string RunName => Result.Item.RunName;

        /// <summary>The raw provider status, unchanged by revalidation.</summary>
        public string Status => Result.Status.ToString();

        public string SizeDisplay => FormatBytes(Result.Bytes);

        public string? Error => Result.Error;

        public bool WasCopied =>
            Result.Status is SyncItemStatus.Uploaded or SyncItemStatus.Downloaded;

        public bool IsVerified => Verification is SyncVerificationState.ManifestMatch or
                                                 SyncVerificationState.SidecarManifestMatch or
                                                 SyncVerificationState.PayloadVerified;

        /// <summary>
        /// Accessible non-color glyph for WCAG 2.x AA compliance.
        /// </summary>
        public string StateGlyph => Result.Status switch
        {
            SyncItemStatus.Failed => "✕",
            SyncItemStatus.Incomplete => "⚠",
            SyncItemStatus.SkippedConflict => "⚠",
            SyncItemStatus.SkippedDeselected or SyncItemStatus.SkippedAlreadyExists or
                SyncItemStatus.DryRun => "ℹ",
            SyncItemStatus.Uploaded or SyncItemStatus.Downloaded => Verification switch
            {
                SyncVerificationState.PayloadVerified => "✓✓",
                SyncVerificationState.ManifestMatch => "✓",
                SyncVerificationState.SidecarManifestMatch => "⚠",
                SyncVerificationState.ContentMismatch or
                    SyncVerificationState.PayloadMismatch or
                    SyncVerificationState.MissingLocally or
                    SyncVerificationState.MissingRemotely or
                    SyncVerificationState.MissingBothSides => "✕",
                SyncVerificationState.EndpointUnavailable => "⚠",
                SyncVerificationState.Cancelled => "⊘",
                SyncVerificationState.Running => "⟳",
                _ => "◷"
            },
            _ => "ℹ"
        };

        /// <summary>
        /// Short user-facing label. Every distinct outcome gets its own words:
        /// a copied-but-unchecked run must never read like a verified one.
        /// </summary>
        public string StateText => Result.Status switch
        {
            SyncItemStatus.Failed when Result.Bytes > 0 =>
                "Failed after copying part of the run",
            SyncItemStatus.Failed => "Failed before copying",
            SyncItemStatus.Incomplete => "Incomplete: partly copied",
            SyncItemStatus.SkippedConflict => "Conflict skipped",
            SyncItemStatus.SkippedDeselected => "Deselected, not copied",
            SyncItemStatus.SkippedAlreadyExists => "Already present, nothing copied",
            SyncItemStatus.DryRun => "Preview only, nothing copied",
            SyncItemStatus.Uploaded or SyncItemStatus.Downloaded => Verification switch
            {
                SyncVerificationState.NotRequested => "Copied (unverified)",
                SyncVerificationState.Running => "Copied, verifying...",
                SyncVerificationState.SidecarManifestMatch => "Sidecar manifest match",
                SyncVerificationState.ManifestMatch => "Manifest match",
                SyncVerificationState.PayloadVerified => "Payload verified",
                SyncVerificationState.ContentMismatch =>
                    "Copied, manifest mismatch",
                SyncVerificationState.PayloadMismatch =>
                    "Copied, payload hash mismatch",
                SyncVerificationState.MissingLocally =>
                    "Copied, verification found it missing locally",
                SyncVerificationState.MissingRemotely =>
                    $"Copied, verification found it missing on {RemoteLabel}",
                SyncVerificationState.MissingBothSides =>
                    "Copied, verification found it on neither side",
                SyncVerificationState.EndpointUnavailable =>
                    "Copied, verification unavailable",
                SyncVerificationState.Cancelled => "Copied, verification cancelled",
                _ => "Copied"
            },
            _ => "Unknown"
        };

        /// <summary>
        /// The longer text: what happened, what it did not do, and whether
        /// retrying is safe. Also the accessible help text for the row.
        /// </summary>
        public string StateDetail => Result.Status switch
        {
            SyncItemStatus.Failed when Result.Bytes > 0 =>
                "Some files were copied before the run stopped. Nothing was deleted or " +
                "overwritten, so running the sync again is safe: uploads only create files " +
                "and downloads never replace an existing one.",
            SyncItemStatus.Failed =>
                "No file was copied for this run. Nothing was deleted or overwritten. " +
                "Fix the reported cause, then preview and sync again.",
            SyncItemStatus.Incomplete =>
                "Part of the run reached the far side before it stopped. Nothing was " +
                "deleted or overwritten, so selecting this run again copies only what " +
                "is still missing.",
            SyncItemStatus.SkippedConflict =>
                "Both sides hold this run with different content. It is never copied " +
                "automatically. Resolve it manually, for example by exporting one side, " +
                "before syncing it.",
            SyncItemStatus.SkippedDeselected =>
                "You excluded this run from the plan, so nothing was copied for it. " +
                "Select it and sync again to copy it.",
            SyncItemStatus.SkippedAlreadyExists =>
                "The target appeared between the preview and the transfer, so nothing " +
                "was copied and nothing was overwritten.",
            SyncItemStatus.DryRun =>
                "This was a preview. Nothing was copied.",
            SyncItemStatus.Uploaded or SyncItemStatus.Downloaded => Verification switch
            {
                SyncVerificationState.NotRequested =>
                    "The transfer finished. Neither side has been re-read yet, so this " +
                    "run is not confirmed to be in sync. Use Verify to check manifests.",
                SyncVerificationState.Running =>
                    "Re-reading both sides. Nothing is copied, moved, or deleted while " +
                    "verification runs.",
                SyncVerificationState.SidecarManifestMatch =>
                    "Matched remote sidecar descriptor (.manifest.json). The destination manifest " +
                    "agrees with the local manifest, but container payload bytes and embedded manifest have not been verified.",
                SyncVerificationState.ManifestMatch =>
                    "The run exists on both sides and their manifests match. File counts, " +
                    "sizes, timestamps, and recorded hashes match; payload bytes were not re-read.",
                SyncVerificationState.PayloadVerified =>
                    "Every payload file has been read and verified byte-for-byte against " +
                    "its recorded SHA-256 cryptographic hash.",
                SyncVerificationState.ContentMismatch =>
                    "Both sides hold this run but their manifests differ. Nothing was " +
                    "changed. Resolve the difference manually before syncing it again.",
                SyncVerificationState.PayloadMismatch =>
                    "Payload hash check failed: one or more files differ from their recorded SHA-256 hashes.",
                SyncVerificationState.MissingLocally =>
                    "The local backup base no longer reports this run. Nothing was deleted " +
                    "by this app. Download it again; downloads never overwrite.",
                SyncVerificationState.MissingRemotely =>
                    $"{RemoteLabel} no longer reports this run. Nothing was deleted by this " +
                    "app. Upload it again; uploads only create files.",
                SyncVerificationState.MissingBothSides =>
                    "Neither side reports this run any more. Nothing was deleted by this " +
                    "app. Check the backup base and the remote before retrying.",
                SyncVerificationState.EndpointUnavailable =>
                    "The endpoint could not be re-read, so the copy could not be confirmed. " +
                    "The transfer itself is unchanged. Retry verification when the endpoint " +
                    "is reachable again; that does not repeat the transfer.",
                SyncVerificationState.Cancelled =>
                    "You stopped the check. The transfer is unchanged and still recorded. " +
                    "Retry verification whenever you want; it copies nothing.",
                _ => "The transfer finished."
            },
            _ => "The provider reported no recognised status for this run."
        };

        /// <summary>Which side the state is about, named rather than implied.</summary>
        public string AffectedSideText => Result.Status switch
        {
            SyncItemStatus.Uploaded => RemoteLabel,
            SyncItemStatus.Downloaded => "Local backup base",
            SyncItemStatus.SkippedConflict => "Both sides",
            _ => Verification switch
            {
                SyncVerificationState.MissingLocally => "Local backup base",
                SyncVerificationState.MissingRemotely => RemoteLabel,
                SyncVerificationState.MissingBothSides => "Both sides",
                _ => "This run"
            }
        };

        public SyncStateSeverity Severity => Result.Status switch
        {
            SyncItemStatus.Failed => SyncStateSeverity.Danger,
            SyncItemStatus.Incomplete => SyncStateSeverity.Warning,
            SyncItemStatus.SkippedConflict => SyncStateSeverity.Warning,
            SyncItemStatus.SkippedDeselected or SyncItemStatus.SkippedAlreadyExists or
                SyncItemStatus.DryRun => SyncStateSeverity.Neutral,
            SyncItemStatus.Uploaded or SyncItemStatus.Downloaded => Verification switch
            {
                SyncVerificationState.PayloadVerified => SyncStateSeverity.Success,
                SyncVerificationState.ManifestMatch => SyncStateSeverity.Success,
                SyncVerificationState.SidecarManifestMatch => SyncStateSeverity.Warning,
                SyncVerificationState.ContentMismatch or
                    SyncVerificationState.PayloadMismatch or
                    SyncVerificationState.MissingLocally or
                    SyncVerificationState.MissingRemotely or
                    SyncVerificationState.MissingBothSides => SyncStateSeverity.Danger,
                SyncVerificationState.EndpointUnavailable or
                    SyncVerificationState.Cancelled => SyncStateSeverity.Warning,
                _ => SyncStateSeverity.Direction
            },
            _ => SyncStateSeverity.Neutral
        };

        public bool IsDirectionState => Severity == SyncStateSeverity.Direction;

        public bool IsSuccessState => Severity == SyncStateSeverity.Success;

        public bool IsWarningState => Severity == SyncStateSeverity.Warning;

        public bool IsDangerState => Severity == SyncStateSeverity.Danger;

        private static string FormatBytes(long bytes) =>
            SyncItemRowViewModel.FormatBytes(bytes);
    }

    public sealed class SyncLogEntryRowViewModel
    {
        public SyncLogEntryRowViewModel(SyncLogEntry entry)
        {
            Entry = entry;
        }

        public SyncLogEntry Entry { get; }

        public string TimestampDisplay =>
            Entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public string DeviceName => Entry.DeviceName;

        public string SummaryDisplay =>
            $"{Entry.Uploaded} uploaded, {Entry.Downloaded} downloaded" +
            (Entry.Conflicts > 0 ? $", {Entry.Conflicts} conflict(s)" : "");

        public string RunsDisplay
        {
            get
            {
                var parts = Entry.UploadedRuns
                    .Select(name => $"↑ {name}")
                    .Concat(Entry.DownloadedRuns.Select(name => $"↓ {name}"));

                return string.Join("   ", parts);
            }
        }
    }
}
