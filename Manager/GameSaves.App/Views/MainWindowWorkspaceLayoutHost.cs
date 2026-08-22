using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GameSaves.App.Services;

namespace GameSaves.App.Views
{
    // Bridges the Settings workspace-layout editor to the live main window.
    // Snapshots carry the coordinator's detached windows keyed by their
    // stable rail keys; applying reattaches everything first, then detaches
    // the layout's tabs at bounds clamped onto the current screens so a
    // stale position can never strand a window off-screen. The file exchange
    // runs through the window's storage provider and touches only the file
    // the user picked.
    internal sealed class MainWindowWorkspaceLayoutHost : IWorkspaceLayoutHost
    {
        private const double CascadeOffset = 24;

        private static readonly FilePickerFileType LayoutFileType =
            new("JSON layout file")
            {
                Patterns = new[] { "*.json" },
            };

        private readonly MainWindow _window;
        private readonly TabDetachCoordinator _coordinator;
        private readonly IReadOnlyDictionary<string, TabItem> _tabsByKey;
        private readonly Dictionary<TabItem, string> _keysByTab;

        public MainWindowWorkspaceLayoutHost(
            MainWindow window,
            TabDetachCoordinator coordinator,
            IReadOnlyDictionary<string, TabItem> tabsByKey)
        {
            _window = window;
            _coordinator = coordinator;
            _tabsByKey = tabsByKey;
            _keysByTab = tabsByKey.ToDictionary(
                pair => pair.Value,
                pair => pair.Key);
        }

        public IReadOnlyList<UiDetachedWindowSettings> CaptureDetachedTabs()
        {
            var detached = new List<UiDetachedWindowSettings>();

            // Canonical creation order keeps the snapshot deterministic
            // regardless of the order tabs were detached in.
            foreach (TabItem tab in _window.NavigationTabs)
            {
                if (!_coordinator.IsDetached(tab) || !_keysByTab.TryGetValue(tab, out string? key))
                    continue;

                Rect bounds = _coordinator.GetDetachedBounds(tab);

                if (UiDetachedWindowSettings.TryCreate(
                        key,
                        bounds.X,
                        bounds.Y,
                        bounds.Width,
                        bounds.Height) is { } entry)
                {
                    detached.Add(entry);
                }
            }

            return detached;
        }

        public void ApplyDetachedTabs(IReadOnlyList<UiDetachedWindowSettings> detached)
        {
            // Deterministic start: everything attached, then detach exactly
            // the layout's tabs, whatever the previous detach state was.
            _coordinator.ReattachAll(_window.MainNavigation);

            double scale = _window.RenderScaling;
            var workingAreas = new Rect[_window.Screens.All.Count];

            for (int index = 0; index < workingAreas.Length; index++)
            {
                workingAreas[index] =
                    _window.Screens.All[index].WorkingArea.ToRectWithDpi(scale);
            }
            Rect ownerBounds = new Rect(
                _window.Position.X / scale,
                _window.Position.Y / scale,
                _window.Width,
                _window.Height);
            int cascadeIndex = 0;

            foreach (UiDetachedWindowSettings entry in detached)
            {
                if (!_tabsByKey.TryGetValue(entry.TabKey, out TabItem? tab))
                    continue;

                if (_coordinator.IsDetached(tab))
                    continue;

                Rect bounds = MainWindow.ClampToScreens(
                    new Rect(entry.Left, entry.Top, entry.Width, entry.Height),
                    workingAreas,
                    ownerBounds,
                    cascadeIndex,
                    out bool cascaded);

                if (cascaded)
                    cascadeIndex++;

                _coordinator.Detach(_window.MainNavigation, tab, _window, bounds);
            }
        }

        public void ReattachAllDetachedTabs() =>
            _coordinator.ReattachAll(_window.MainNavigation);

        public async Task<WorkspaceFileOutcome> ExportAsync(string payload)
        {
            IStorageFile? file = await _window.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Export workspace layouts",
                    SuggestedFileName = "game-save-workspace-layouts",
                    FileTypeChoices = new[] { LayoutFileType },
                    ShowOverwritePrompt = true,
                });

            string? path = file?.TryGetLocalPath();

            if (path is null)
                return WorkspaceFileOutcome.Cancelled;

            try
            {
                File.WriteAllText(path, payload);
                return WorkspaceFileOutcome.Completed;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return WorkspaceFileOutcome.Failed;
            }
        }

        public async Task<WorkspaceImportResult> ImportAsync()
        {
            IReadOnlyList<IStorageFile> files =
                await _window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Import workspace layouts",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { LayoutFileType },
                    });

            string? path = files.FirstOrDefault()?.TryGetLocalPath();

            if (path is null)
                return new WorkspaceImportResult(WorkspaceFileOutcome.Cancelled, null);

            try
            {
                return new WorkspaceImportResult(
                    WorkspaceFileOutcome.Completed,
                    File.ReadAllText(path));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return new WorkspaceImportResult(WorkspaceFileOutcome.Failed, null);
            }
        }
    }
}
