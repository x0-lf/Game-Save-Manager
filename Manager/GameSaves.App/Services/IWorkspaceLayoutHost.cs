using System.Collections.Generic;
using System.Threading.Tasks;

namespace GameSaves.App.Services
{
    /// <summary>
    /// The interactive bridge between the Settings workspace-layout editor
    /// and the live main window. The view model owns the saved-layout list
    /// and its persistence; the host snapshots the currently detached
    /// windows, applies a saved detach set to the real window, and performs
    /// the user-driven file exchange for export and import. Applying a
    /// layout changes detach state and window bounds only — never the rail
    /// layout, theme, or any other setting.
    /// </summary>
    public interface IWorkspaceLayoutHost
    {
        /// <summary>The currently detached windows, in canonical tab order.</summary>
        IReadOnlyList<UiDetachedWindowSettings> CaptureDetachedTabs();

        /// <summary>
        /// Reattaches everything, then detaches exactly the given tabs at
        /// their saved bounds (clamped onto the current screens).
        /// </summary>
        void ApplyDetachedTabs(IReadOnlyList<UiDetachedWindowSettings> detached);

        /// <summary>Reattaches every detached window without applying a layout.</summary>
        void ReattachAllDetachedTabs();

        /// <summary>Asks the user where to save and writes the export payload.</summary>
        Task<WorkspaceFileOutcome> ExportAsync(string payload);

        /// <summary>Asks the user for a file and reads its text.</summary>
        Task<WorkspaceImportResult> ImportAsync();
    }

    public enum WorkspaceFileOutcome
    {
        Completed,
        Cancelled,
        Failed,
    }

    public sealed record WorkspaceImportResult(WorkspaceFileOutcome Outcome, string? Text);
}
