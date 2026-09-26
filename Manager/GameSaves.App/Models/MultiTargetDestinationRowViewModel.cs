using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Common;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Linq;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One saved profile offered as a destination of a multi-profile upload:
    /// whether it is chosen, what its own preview found, and how its upload
    /// ended. Every state is spelled out in words; the glyph only repeats it.
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

        public void Clear()
        {
            PlanText = "";
            OutcomeText = "";
            OutcomeGlyph = "";
        }
    }
}
