using GameSaves.App.Common;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;

namespace GameSaves.App.Models
{
    /// <summary>
    /// Coordinates hierarchical save file trees, deferred node expansion, and flattened
    /// visible rows for virtualized DataGrid / ListBox rendering at 60 FPS.
    /// </summary>
    public sealed class SaveFileHierarchyController
    {
        // The last verification results for this tree, applied again to every
        // file that is created later by expanding its folder.
        private IReadOnlyDictionary<string, bool>? _fileResults;

        public List<SaveFileTreeNodeViewModel> RootNodes { get; private set; } = [];

        public BulkObservableCollection<SaveFileTreeNodeViewModel> VisibleRows { get; } = [];

        public void LoadItems(IEnumerable<TransferOverwriteBackupItem> items) =>
            ApplyRoots(LazyFileTreeBuilder.BuildFromBackupItems(items));

        public void LoadFromDescriptors(IEnumerable<SaveFileItemDescriptor> items) =>
            ApplyRoots(LazyFileTreeBuilder.Build(items));

        public void Clear() => ApplyRoots([]);

        public void ToggleExpand(SaveFileTreeNodeViewModel node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (!node.IsDirectory)
                return;

            int index = VisibleRows.IndexOf(node);
            if (index < 0)
                return;

            if (node.IsExpanded)
            {
                // Collapse: remove all currently visible descendants, from the
                // back so no removal shifts the rows still to be removed.
                node.IsExpanded = false;

                int end = index + 1;
                while (end < VisibleRows.Count && VisibleRows[end].Depth > node.Depth)
                    end++;

                for (int i = end - 1; i > index; i--)
                    VisibleRows.RemoveAt(i);
            }
            else
            {
                Expand(node);

                var toInsert = new List<SaveFileTreeNodeViewModel>();
                CollectVisibleDescendants(node, toInsert);

                for (int i = 0; i < toInsert.Count; i++)
                {
                    VisibleRows.Insert(index + 1 + i, toInsert[i]);
                }
            }
        }

        public void ExpandAll()
        {
            var rows = new List<SaveFileTreeNodeViewModel>();
            foreach (SaveFileTreeNodeViewModel root in RootNodes)
            {
                ExpandRecursively(root, rows);
            }

            VisibleRows.ReplaceAll(rows);
        }

        public void CollapseAll()
        {
            foreach (SaveFileTreeNodeViewModel root in RootNodes)
            {
                CollapseRecursively(root);
            }

            VisibleRows.ReplaceAll(RootNodes);
        }

        public void UpdateVerification(IReadOnlyDictionary<string, bool>? fileResults)
        {
            if (fileResults is null || fileResults.Count == 0)
                return;

            _fileResults = fileResults;
            ApplyVerification(RootNodes);
        }

        private void ApplyRoots(List<SaveFileTreeNodeViewModel> roots)
        {
            _fileResults = null;
            RootNodes = roots;
            VisibleRows.ReplaceAll(roots);
        }

        // Expanding loads the folder's children the first time; those new
        // nodes pick up any verification that already ran.
        private void Expand(SaveFileTreeNodeViewModel node)
        {
            bool wasLoaded = node.IsChildrenLoaded;
            node.IsExpanded = true;

            if (!wasLoaded)
                ApplyVerification(node.Children);
        }

        private void ApplyVerification(IEnumerable<SaveFileTreeNodeViewModel> nodes)
        {
            if (_fileResults is null)
                return;

            foreach (SaveFileTreeNodeViewModel node in nodes)
            {
                if (node.IsFile)
                {
                    if (_fileResults.TryGetValue(node.FullPath, out bool matched))
                        node.IsVerified = matched;
                }
                else if (node.IsChildrenLoaded)
                {
                    ApplyVerification(node.Children);
                }
            }
        }

        private static void CollectVisibleDescendants(
            SaveFileTreeNodeViewModel parent,
            List<SaveFileTreeNodeViewModel> destination)
        {
            foreach (SaveFileTreeNodeViewModel child in parent.Children)
            {
                destination.Add(child);

                if (child.IsDirectory && child.IsExpanded)
                {
                    CollectVisibleDescendants(child, destination);
                }
            }
        }

        private void ExpandRecursively(
            SaveFileTreeNodeViewModel node,
            List<SaveFileTreeNodeViewModel> destination)
        {
            destination.Add(node);

            if (node.IsDirectory)
            {
                Expand(node);

                foreach (SaveFileTreeNodeViewModel child in node.Children)
                {
                    ExpandRecursively(child, destination);
                }
            }
        }

        private static void CollapseRecursively(SaveFileTreeNodeViewModel node)
        {
            if (node.IsDirectory)
            {
                node.IsExpanded = false;

                foreach (SaveFileTreeNodeViewModel child in node.Children)
                {
                    CollapseRecursively(child);
                }
            }
        }
    }
}
