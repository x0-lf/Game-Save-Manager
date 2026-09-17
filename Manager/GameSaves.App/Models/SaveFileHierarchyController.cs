using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GameSaves.App.Models
{
    /// <summary>
    /// Coordinates hierarchical save file trees, deferred node expansion, and flattened
    /// visible rows for virtualized DataGrid / ListBox rendering at 60 FPS.
    /// </summary>
    public sealed partial class SaveFileHierarchyController : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalSizeDisplay))]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        private int totalFileCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalSizeDisplay))]
        private long totalSizeBytes;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        private int rootCount;

        public ObservableCollection<SaveFileTreeNodeViewModel> RootNodes { get; } = new();

        public ObservableCollection<SaveFileTreeNodeViewModel> VisibleRows { get; } = new();

        public bool IsEmpty => RootNodes.Count == 0;

        public string TotalSizeDisplay => SaveFileTreeNodeViewModel.FormatBytes(TotalSizeBytes);

        public void LoadItems(IEnumerable<TransferOverwriteBackupItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            var roots = LazyFileTreeBuilder.BuildFromBackupItems(items);
            ApplyRoots(roots);
        }

        public void LoadFromDescriptors(IEnumerable<SaveFileItemDescriptor> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            var roots = LazyFileTreeBuilder.Build(items);
            ApplyRoots(roots);
        }

        public void Clear()
        {
            RootNodes.Clear();
            VisibleRows.Clear();
            TotalFileCount = 0;
            TotalSizeBytes = 0;
            RootCount = 0;
        }

        public void ToggleExpand(SaveFileTreeNodeViewModel node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (!node.IsDirectory || !node.HasChildren)
                return;

            int index = VisibleRows.IndexOf(node);
            if (index < 0)
                return;

            if (node.IsExpanded)
            {
                // Collapse: remove all currently visible descendants
                node.IsExpanded = false;

                int removeCount = 0;
                for (int i = index + 1; i < VisibleRows.Count; i++)
                {
                    if (VisibleRows[i].Depth > node.Depth)
                        removeCount++;
                    else
                        break;
                }

                for (int i = 0; i < removeCount; i++)
                {
                    VisibleRows.RemoveAt(index + 1);
                }
            }
            else
            {
                // Expand: ensure immediate children are instantiated and insert them
                node.IsExpanded = true;
                node.EnsureChildrenLoaded();

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
            VisibleRows.Clear();
            foreach (SaveFileTreeNodeViewModel root in RootNodes)
            {
                ExpandRecursively(root, VisibleRows);
            }
        }

        public void CollapseAll()
        {
            VisibleRows.Clear();
            foreach (SaveFileTreeNodeViewModel root in RootNodes)
            {
                CollapseRecursively(root);
                VisibleRows.Add(root);
            }
        }

        public void UpdateVerification(IReadOnlyDictionary<string, bool>? fileResults)
        {
            if (fileResults is null || fileResults.Count == 0)
                return;

            UpdateVerificationRecursive(RootNodes, fileResults);
        }

        private void ApplyRoots(List<SaveFileTreeNodeViewModel> roots)
        {
            RootNodes.Clear();
            VisibleRows.Clear();

            long bytesAcc = 0;
            int filesAcc = 0;

            foreach (SaveFileTreeNodeViewModel root in roots)
            {
                RootNodes.Add(root);
                VisibleRows.Add(root);

                bytesAcc += root.SizeBytes;
                filesAcc += root.FileCount;
            }

            TotalSizeBytes = bytesAcc;
            TotalFileCount = filesAcc;
            RootCount = RootNodes.Count;
        }

        private static void CollectVisibleDescendants(
            SaveFileTreeNodeViewModel parent,
            List<SaveFileTreeNodeViewModel> destination)
        {
            foreach (SaveFileTreeNodeViewModel child in parent.Children)
            {
                destination.Add(child);

                if (child.IsDirectory && child.IsExpanded && child.IsChildrenLoaded)
                {
                    CollectVisibleDescendants(child, destination);
                }
            }
        }

        private void ExpandRecursively(
            SaveFileTreeNodeViewModel node,
            ICollection<SaveFileTreeNodeViewModel> destination)
        {
            destination.Add(node);

            if (node.IsDirectory && node.HasChildren)
            {
                node.IsExpanded = true;
                node.EnsureChildrenLoaded();

                foreach (SaveFileTreeNodeViewModel child in node.Children)
                {
                    ExpandRecursively(child, destination);
                }
            }
        }

        private void CollapseRecursively(SaveFileTreeNodeViewModel node)
        {
            if (node.IsDirectory)
            {
                node.IsExpanded = false;

                if (node.IsChildrenLoaded)
                {
                    foreach (SaveFileTreeNodeViewModel child in node.Children)
                    {
                        CollapseRecursively(child);
                    }
                }
            }
        }

        private void UpdateVerificationRecursive(
            IEnumerable<SaveFileTreeNodeViewModel> nodes,
            IReadOnlyDictionary<string, bool> fileResults)
        {
            foreach (SaveFileTreeNodeViewModel node in nodes)
            {
                if (node.IsFile)
                {
                    if (fileResults.TryGetValue(node.RelativePath, out bool matched) ||
                        fileResults.TryGetValue(node.FullPath, out matched) ||
                        fileResults.TryGetValue(node.Name, out matched))
                    {
                        node.IsVerified = matched;
                    }
                }
                else if (node.IsChildrenLoaded)
                {
                    UpdateVerificationRecursive(node.Children, fileResults);
                }
            }
        }
    }
}
