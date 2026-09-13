using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace GameSaves.App.Views
{
    // Abstraction over the floating window so the coordinator's bookkeeping
    // (index recording, detach/reattach state machine) is testable headless.
    internal interface IDetachedTabWindow
    {
        string? Title { get; set; }

        object? Content { get; set; }

        object? DataContext { get; set; }

        // The window's placement in DIPs relative to the virtual desktop.
        // Reading captures a live window for workspace snapshots; writing
        // places a freshly detached window at saved workspace bounds.
        Rect Bounds { get; set; }

        event EventHandler? CloseRequested;

        void Show(Window? owner);

        void Activate();

        void Close();
    }

    // Owns detach/reattach for the main navigation TabControl. The detached
    // view control itself is moved between the TabItem and the floating
    // window, never recreated, so shared view-model state is preserved.
    // Reattachment restores the TabItem at its original index, clamped to
    // the current item count in case other tabs were detached in between —
    // unless a persisted tab layout has been applied, in which case the
    // reattached tab returns to its position in that layout.
    internal sealed class TabDetachCoordinator
    {
        private readonly Func<IDetachedTabWindow> _windowFactory;
        private readonly Dictionary<TabItem, DetachedTab> _detached = new();
        private readonly HashSet<TabItem> _hidden = new();
        private IReadOnlyList<TabItem>? _tabOrder;
        private bool _ownerClosing;

        public TabDetachCoordinator(Func<IDetachedTabWindow> windowFactory)
        {
            _windowFactory = windowFactory;
        }

        public bool IsDetached(TabItem tab) => _detached.ContainsKey(tab);

        internal IReadOnlyList<IDetachedTabWindow> DetachedWindowsForTest() =>
            _detached.Values.Select(detached => detached.Window).ToArray();

        // Called from the main window's closing: owned windows close with
        // their owner, and that must not trigger reattachment churn during
        // shutdown.
        public void NotifyOwnerClosing() => _ownerClosing = true;

        // The header settings button contract: select the tab if it is
        // attached, otherwise surface the already-floating window.
        public void SelectOrActivate(TabControl navigation, TabItem tab)
        {
            if (_detached.TryGetValue(tab, out DetachedTab? detached))
            {
                detached.Window.Activate();
                return;
            }

            if (navigation.SelectedItem != tab)
                navigation.SelectedItem = tab;
        }

        // Ctrl+1..Ctrl+9 selection. Slot numbers are the rail's original
        // creation order, so a shortcut keeps meaning the same section
        // whether that tab is docked or floating: a detached target
        // surfaces its window, an attached target is selected, and an
        // out-of-range slot does nothing. A hidden tab's slot is also a
        // no-op — slots stay canonical over all nine tabs and never shift
        // meaning when tabs are hidden or reordered.
        public void SelectOrActivateSlot(
            TabControl navigation,
            IReadOnlyList<TabItem> navigationTabs,
            int slotNumber)
        {
            if (slotNumber < 1 || slotNumber > navigationTabs.Count)
                return;

            TabItem tab = navigationTabs[slotNumber - 1];

            if (_hidden.Contains(tab))
                return;

            SelectOrActivate(navigation, tab);
        }

        // Applies the persisted rail layout. Attached tabs are reordered in
        // place to the given order; detached tabs stay floating and are
        // simply skipped, because reattachment computes its position from
        // the same order. Hidden tabs keep their TabItem in the collection
        // with IsVisible=false, so their state survives and they remain
        // Ctrl+slot-addressable (as no-ops). Selection is preserved when it
        // is still attached and visible, and otherwise moves to the first
        // visible attached tab, so the content area never sits behind an
        // invisible rail entry.
        public void ApplyTabLayout(
            TabControl navigation,
            IReadOnlyList<TabItem> orderedTabs,
            IReadOnlyCollection<TabItem> hiddenTabs)
        {
            _tabOrder = orderedTabs.ToArray();
            _hidden.Clear();

            foreach (TabItem tab in hiddenTabs)
                _hidden.Add(tab);

            ItemCollection items = navigation.Items;
            TabItem? previousSelection = navigation.SelectedItem as TabItem;

            int targetIndex = 0;
            foreach (TabItem tab in _tabOrder)
            {
                int currentIndex = items.IndexOf(tab);

                if (currentIndex < 0)
                    continue;

                if (currentIndex != targetIndex)
                {
                    items.RemoveAt(currentIndex);
                    items.Insert(targetIndex, tab);
                }

                targetIndex++;
            }

            foreach (TabItem tab in _tabOrder)
                tab.IsVisible = !_hidden.Contains(tab);

            TabItem? selected =
                previousSelection is not null &&
                items.IndexOf(previousSelection) >= 0 &&
                !_hidden.Contains(previousSelection)
                    ? previousSelection
                    : FirstVisibleAttached(navigation);

            if (selected is not null && !ReferenceEquals(navigation.SelectedItem, selected))
                navigation.SelectedItem = selected;
        }

        private TabItem? FirstVisibleAttached(TabControl navigation)
        {
            if (_tabOrder is null)
                return null;

            foreach (TabItem tab in _tabOrder)
            {
                if (!_hidden.Contains(tab) && navigation.Items.IndexOf(tab) >= 0)
                    return tab;
            }

            return null;
        }

        public void Detach(TabControl navigation, TabItem tab, Window owner)
        {
            Detach(navigation, tab, owner, owner.DataContext, owner);
        }

        // Detaches and places the floating window at explicit workspace
        // bounds instead of the owner-centered default.
        public void Detach(TabControl navigation, TabItem tab, Window owner, Rect bounds)
        {
            Detach(navigation, tab, owner, owner.DataContext, owner, bounds);
        }

        // Core detach, split so the bookkeeping is testable headless (Window
        // cannot be constructed without a windowing platform). owner may be
        // null only in tests, where the window host is a fake.
        internal void Detach(
            TabControl navigation,
            TabItem tab,
            Window? owner,
            object? ownerDataContext,
            Window? showOwner,
            Rect? bounds = null)
        {
            if (_detached.ContainsKey(tab))
                return;

            ItemCollection items = navigation.Items;

            int index = items.IndexOf(tab);
            if (index < 0)
                return;

            if (tab.Content is not Control content)
                return;

            bool wasSelected = navigation.SelectedItem == tab;

            string title = GetHeaderText(tab);
            tab.Content = null;
            items.Remove(tab);
            DisassociateParent(content);

            if (wasSelected && items.Count > 0)
            {
                // Prefer the first visible attached tab in the applied
                // layout; without a layout (no ApplyTabLayout yet) the
                // original index-based fallback keeps its behavior.
                TabItem? fallback = FirstVisibleAttached(navigation);

                if (fallback is not null)
                    navigation.SelectedItem = fallback;
                else
                    navigation.SelectedIndex = Math.Min(index, items.Count - 1);
            }

            IDetachedTabWindow window = _windowFactory();
            window.Title = title;
            window.Content = content;
            // The Dashboard tab relies on inherited DataContext (its
            // bindings target the main window view model); every other
            // view carries its own and keeps it. Flow the owner's context
            // down so inherited bindings survive the reparent.
            if (content.DataContext is null)
                window.DataContext = ownerDataContext;

            // Saved workspace bounds replace the CenterOwner startup
            // placement before the window is shown.
            if (bounds is { } placement)
                window.Bounds = placement;

            EventHandler closeHandler = (_, _) =>
            {
                if (_ownerClosing)
                    return;

                Reattach(navigation, tab, closeWindow: false, selectTab: true);
            };

            window.CloseRequested += closeHandler;
            _detached[tab] = new DetachedTab(index, content, window, closeHandler);

            window.Show(showOwner!);
        }

        public void Reattach(TabControl navigation, TabItem tab) =>
            Reattach(navigation, tab, closeWindow: true, selectTab: true);

        internal void Reattach(
            TabControl navigation,
            TabItem tab,
            bool closeWindow,
            bool selectTab = true)
        {
            if (!_detached.Remove(tab, out DetachedTab? detached))
                return;

            detached.Window.CloseRequested -= detached.CloseHandler;

            // Release content from the floating window and ensure all visual
            // and logical parent links are completely severed before reattaching.
            detached.Window.Content = null;
            DisassociateParent(detached.Content);

            // Programmatic reattach closes the floating window frame BEFORE
            // attaching content to the main navigation tree so the floating
            // window's layout queues and window teardown cannot arrange or
            // invalidate the reattached control under a new layout root.
            if (closeWindow)
            {
                try
                {
                    detached.Window.Close();
                }
                catch
                {
                    // Ignore if already closed or unattached
                }
            }

            // Restore content to the TabItem and re-insert into navigation
            tab.Content = detached.Content;

            ItemCollection items = navigation.Items;
            int index = ComputeReattachIndex(navigation, tab, detached.OriginalIndex);
            items.Insert(index, tab);

            // A tab that was hidden while detached reattaches invisibly
            // (IsVisible stays false from the layout application); only a
            // visible tab takes selection on return when selection was requested.
            if (selectTab && tab.IsVisible)
                navigation.SelectedItem = tab;
        }

        // Reattaches every detached tab in a deterministic order: the applied
        // rail layout order when there is one, otherwise the recorded
        // original index. Workspace apply and reset always start from this
        // fully attached state, so applying a layout is idempotent whatever
        // the previous detach state was.
        public void ReattachAll(TabControl navigation)
        {
            if (_detached.Count == 0)
                return;

            TabItem[] detached = _detached.Keys.ToArray();

            if (_tabOrder is not null)
            {
                var order = new Dictionary<TabItem, int>();

                for (int index = 0; index < _tabOrder.Count; index++)
                    order[_tabOrder[index]] = index;

                Array.Sort(detached, (left, right) =>
                    order[left].CompareTo(order[right]));
            }
            else
            {
                Array.Sort(detached, (left, right) =>
                    _detached[left].OriginalIndex.CompareTo(
                        _detached[right].OriginalIndex));
            }

            foreach (TabItem tab in detached)
                Reattach(navigation, tab, closeWindow: true, selectTab: false);

            if (navigation.SelectedItem is not TabItem current ||
                !current.IsVisible ||
                !navigation.Items.Contains(current))
            {
                TabItem? target = FirstVisibleAttached(navigation);
                if (target is not null)
                    navigation.SelectedItem = target;
            }
        }

        internal static void DisassociateParent(Control? control)
        {
            if (control is null)
                return;

            switch (control.Parent)
            {
                case ContentControl contentControl when ReferenceEquals(contentControl.Content, control):
                    contentControl.Content = null;
                    break;
                case ContentPresenter contentPresenter when ReferenceEquals(contentPresenter.Content, control):
                    contentPresenter.Content = null;
                    break;
                case Panel panel:
                    panel.Children.Remove(control);
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, control):
                    decorator.Child = null;
                    break;
            }

            Visual? visualParent = control.GetVisualParent();
            switch (visualParent)
            {
                case Panel visualPanel:
                    visualPanel.Children.Remove(control);
                    break;
                case Decorator visualDecorator when ReferenceEquals(visualDecorator.Child, control):
                    visualDecorator.Child = null;
                    break;
                case ContentPresenter visualPresenter when ReferenceEquals(visualPresenter.Content, control):
                    visualPresenter.Content = null;
                    break;
                case ContentControl visualContentControl when ReferenceEquals(visualContentControl.Content, control):
                    visualContentControl.Content = null;
                    break;
            }
        }

        // The floating window's current placement for workspace snapshots.
        // Only meaningful for a tab IsDetached reports as detached.
        public Rect GetDetachedBounds(TabItem tab) =>
            _detached.TryGetValue(tab, out DetachedTab? detached)
                ? detached.Window.Bounds
                : default;

        // Reattachment position follows the last applied persisted tab order
        // when there is one: the tab goes after every attached tab that
        // precedes it in that order. Without an applied order, the recorded
        // original index — clamped — keeps the pre-layout behavior.
        private int ComputeReattachIndex(
            TabControl navigation,
            TabItem tab,
            int originalIndex)
        {
            if (_tabOrder is null)
                return Math.Min(originalIndex, navigation.Items.Count);

            int index = 0;

            foreach (TabItem ordered in _tabOrder)
            {
                if (ReferenceEquals(ordered, tab))
                    return index;

                if (navigation.Items.Contains(ordered))
                    index++;
            }

            return index;
        }

        private static string GetHeaderText(TabItem tab)
        {
            if (tab.Header is Panel panel)
            {
                foreach (Control child in panel.Children)
                {
                    if (child is TextBlock text && text.Classes.Contains("navLabel"))
                        return text.Text ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private sealed record DetachedTab(
            int OriginalIndex,
            Control Content,
            IDetachedTabWindow Window,
            EventHandler CloseHandler);
    }
}
