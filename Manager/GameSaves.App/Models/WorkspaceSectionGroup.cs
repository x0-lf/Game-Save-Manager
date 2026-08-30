using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Services;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One page's rows in the Settings section-visibility list. Grouping by
    /// page is what keeps Settings from becoming one flat list of forty
    /// checkboxes with no way to tell which page each belongs to.
    /// </summary>
    public sealed class WorkspaceSectionGroup
    {
        public WorkspaceSectionGroup(
            string pageKey,
            string header,
            IWorkspaceLayoutPage layout)
        {
            PageKey = pageKey;
            Header = header;
            Layout = layout;

            Sections = new ObservableCollection<WorkspaceSectionOption>(
                WorkspaceLayoutCatalog.PanelsFor(pageKey)
                    .Where(definition => definition.CanHide)
                    .Select(definition => new WorkspaceSectionOption(definition, layout)));

            // A page whose sections are all pinned would render an empty group.
            HasSections = Sections.Count > 0;
        }

        public string PageKey { get; }

        /// <summary>The page's name, as the navigation rail shows it.</summary>
        public string Header { get; }

        public IWorkspaceLayoutPage Layout { get; }

        public ObservableCollection<WorkspaceSectionOption> Sections { get; }

        public bool HasSections { get; }
    }

    /// <summary>
    /// One section's visibility row. Writes straight through to the page's
    /// live layout, so a section hidden here disappears from the page
    /// immediately and a section hidden from the page's own menu ticks off
    /// here — one piece of state, two surfaces.
    /// </summary>
    public sealed partial class WorkspaceSectionOption : ObservableObject
    {
        private readonly IWorkspaceLayoutPage _layout;
        private bool _suppress;

        public WorkspaceSectionOption(
            WorkspacePanelDefinition definition,
            IWorkspaceLayoutPage layout)
        {
            Key = definition.Key;
            Header = definition.Title;
            _layout = layout;

            isVisible = !IsHiddenInLayout();
            layout.PlacementsChanged += OnPlacementsChanged;
        }

        public string Key { get; }

        /// <summary>The section's own heading, as the page shows it.</summary>
        public string Header { get; }

        [ObservableProperty]
        private bool isVisible;

        partial void OnIsVisibleChanged(bool value)
        {
            if (_suppress)
                return;

            _layout.SetHidden(Key, !value);
        }

        // The layout is the single source of truth; when it changes for any
        // other reason — the panel menu, applying a saved layout, a reset —
        // this row follows without writing back and looping.
        private void OnPlacementsChanged(object? sender, EventArgs e)
        {
            bool visible = !IsHiddenInLayout();

            if (visible == IsVisible)
                return;

            _suppress = true;

            try
            {
                IsVisible = visible;
            }
            finally
            {
                _suppress = false;
            }
        }

        private bool IsHiddenInLayout()
        {
            foreach (UiPanelPlacement placement in _layout.Placements)
            {
                if (string.Equals(placement.Key, Key, StringComparison.Ordinal))
                    return placement.Hidden;
            }

            return false;
        }
    }

    /// <summary>
    /// One page's row in the "where the scan action appears" list.
    /// </summary>
    public sealed partial class ScanPageOption : ObservableObject
    {
        public ScanPageOption(string key, string header, bool isVisible)
        {
            Key = key;
            Header = header;
            this.isVisible = isVisible;
        }

        public string Key { get; }

        public string Header { get; }

        [ObservableProperty]
        private bool isVisible;
    }
}
