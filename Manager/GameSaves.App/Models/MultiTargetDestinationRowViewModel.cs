using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Common;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Linq;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One saved profile that can connect on its own. As a destination of a
    /// multi-profile upload: whether it is chosen, what its own preview found,
    /// and how its upload ended. As a row of the health panel (SYNC-004): what
    /// its last check found and the storage space it reported. Every state is
    /// spelled out in words; a glyph only repeats it.
    /// </summary>
    public sealed partial class MultiTargetDestinationRowViewModel : ObservableObject
    {
        private readonly Action _selectionChanged;

        [ObservableProperty]
        private bool isSelected;

        [ObservableProperty]
        private string planText = "";

        [ObservableProperty]
        private string outcomeText = "";

        [ObservableProperty]
        private string outcomeGlyph = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HealthAccessibleName))]
        private string healthText = "Not checked yet.";

        [ObservableProperty]
        private string healthGlyph = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HealthAccessibleName))]
        private string capacityText = "";

        public MultiTargetDestinationRowViewModel(
            SyncRemoteProfile profile,
            string providerName,
            Action selectionChanged)
        {
            Profile = profile;
            ProviderName = providerName;
            _selectionChanged = selectionChanged;
        }

        public SyncRemoteProfile Profile { get; }

        public string DisplayName => Profile.DisplayName;

        public string ProviderName { get; }

        public string EndpointText =>
            string.IsNullOrWhiteSpace(Profile.RemoteRootDisplayName)
                ? ProviderName
                : $"{ProviderName} — {Profile.RemoteRootDisplayName}";

        public string AccessibleName => $"Upload to {DisplayName}, {EndpointText}";

        public string HealthAccessibleName =>
            string.Join(" ", new[] { $"{DisplayName}, {EndpointText}:", HealthText, CapacityText }
                .Where(part => part.Length > 0));

        partial void OnIsSelectedChanged(bool value) => _selectionChanged();

        public void ShowPreview(SyncDestinationPreview preview)
        {
            OutcomeText = "";
            OutcomeGlyph = "";

            if (preview.Plan is null)
            {
                PlanText = $"Preview failed: {preview.Error}";
                return;
            }

            SyncPlan plan = preview.Plan;
            string? blocker = plan.Warnings
                .FirstOrDefault(warning => warning.Severity == TransferWarningSeverity.Error)?
                .Message;

            PlanText = blocker is not null
                ? $"Cannot upload: {blocker}"
                : $"Upload: {plan.UploadCount} run(s) ({ByteSize.Format(plan.BytesToUpload)})   " +
                  $"Already there: {plan.InSyncCount}   Conflicts: {plan.ConflictCount}";
        }

        public void ShowOutcome(SyncDestinationResult result)
        {
            (OutcomeGlyph, string label) = result.Outcome switch
            {
                SyncDestinationOutcome.Completed => ("✓", "Uploaded"),
                SyncDestinationOutcome.CompletedWithErrors => ("⚠", "Uploaded with errors"),
                SyncDestinationOutcome.Failed => ("✕", "Failed"),
                SyncDestinationOutcome.Skipped => ("ℹ", "Skipped"),
                SyncDestinationOutcome.Cancelled => ("⊘", "Cancelled"),
                _ => ("⊘", "Not started")
            };

            string detail = result.Result is { } copied
                ? $"{copied.Uploaded} run(s), {ByteSize.Format(copied.BytesCopied)}"
                : "";

            OutcomeText = string.Join(
                ". ",
                new[] { label, detail, result.Message }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        /// <summary>
        /// A capacity is shown only when the provider reported one. Without
        /// one, the text says whether this provider never reports space or
        /// did not report it this time; a failed or throttled check read no
        /// space at all, so it shows none.
        /// </summary>
        public void ShowHealth(ProviderHealthReport report, bool providerReportsStorage)
        {
            (HealthGlyph, string label) = report.State switch
            {
                ProviderHealthState.Healthy => ("✓", "Healthy"),
                ProviderHealthState.RateLimited => ("⚠", "Rate limited"),
                ProviderHealthState.QuotaExhausted => ("⚠", "Storage full"),
                _ => ("✕", "Unavailable")
            };

            HealthText = $"{label}. {report.Reason}";
            CapacityText = report switch
            {
                { Capacity: { TotalBytes: { } total } capacity } =>
                    $"Storage: {ByteSize.Format(capacity.FreeBytes)} free of {ByteSize.Format(total)}.",
                { Capacity: { } capacity } =>
                    $"Storage: {ByteSize.Format(capacity.FreeBytes)} free.",
                { State: ProviderHealthState.Unavailable or ProviderHealthState.RateLimited } => "",
                _ when providerReportsStorage => "Storage: not reported by the server on this check.",
                _ => "Storage: this provider does not report free space."
            };
        }

        public void ShowHealthNote(string note)
        {
            HealthGlyph = "";
            HealthText = note;
            CapacityText = "";
        }

        public void Clear()
        {
            PlanText = "";
            OutcomeText = "";
            OutcomeGlyph = "";
        }
    }
}
