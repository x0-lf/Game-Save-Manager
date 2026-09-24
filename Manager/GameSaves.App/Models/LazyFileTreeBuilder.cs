using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Models
{
    /// <summary>
    /// Lightweight descriptor representing an individual file before hierarchy projection.
    /// </summary>
    public sealed record SaveFileItemDescriptor(
        string Path,
        long Bytes,
        string? Sha256 = null);

    /// <summary>
    /// High-performance builder that constructs hierarchical file trees with deferred / on-demand
    /// node expansion. Only root nodes are instantiated upfront as ViewModels, while descendant
    /// directories and files remain lightweight descriptors until a user expands them.
    /// </summary>
    public static class LazyFileTreeBuilder
    {
        private sealed class TrieNode(string name, string relativePath, string fullPath, bool isDirectory)
        {
            public string Name { get; } = name;
            public string RelativePath { get; } = relativePath;
            public string FullPath { get; } = fullPath;
            public bool IsDirectory { get; } = isDirectory;
            public long SizeBytes { get; set; }
            public int FileCount { get; set; }
            public string? Sha256 { get; init; }

            // Folders only; a file never has children.
            public Dictionary<string, TrieNode>? Children { get; } =
                isDirectory ? new(StringComparer.OrdinalIgnoreCase) : null;
        }

        /// <summary>
        /// Builds the tree of a backup run from each file's ORIGINAL location, so
        /// the tree reads like the save folder it came from and every file's
        /// <see cref="SaveFileTreeNodeViewModel.FullPath"/> is the key the
        /// payload verifier reports it under.
        /// </summary>
        public static List<SaveFileTreeNodeViewModel> BuildFromBackupItems(IEnumerable<TransferOverwriteBackupItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            return Build(items.Select(item => new SaveFileItemDescriptor(
                item.OriginalFile,
                item.Bytes,
                item.Sha256)));
        }

        public static List<SaveFileTreeNodeViewModel> Build(IEnumerable<SaveFileItemDescriptor> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            var rootTrie = new TrieNode(string.Empty, string.Empty, string.Empty, isDirectory: true);

            foreach (SaveFileItemDescriptor item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                    continue;

                string[] segments = item.Path.Split(
                    ['/', '\\'],
                    StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 0)
                    continue;

                TrieNode current = rootTrie;

                for (int i = 0; i < segments.Length - 1; i++)
                {
                    string dirName = segments[i];

                    if (!current.Children!.TryGetValue(dirName, out TrieNode? subDir))
                    {
                        subDir = new TrieNode(dirName, ChildPath(current, dirName), string.Empty, isDirectory: true);
                        current.Children[dirName] = subDir;
                    }

                    subDir.SizeBytes += item.Bytes;
                    subDir.FileCount += 1;
                    current = subDir;
                }

                string fileName = segments[^1];

                current.Children![fileName] = new TrieNode(fileName, ChildPath(current, fileName), item.Path, isDirectory: false)
                {
                    SizeBytes = item.Bytes,
                    FileCount = 1,
                    Sha256 = item.Sha256,
                };
            }

            return CreateChildren(rootTrie, depth: 0);
        }

        private static string ChildPath(TrieNode parent, string name) =>
            parent.RelativePath.Length == 0 ? name : $"{parent.RelativePath}/{name}";

        // Folders first, then by name; one ordering for every level.
        private static List<SaveFileTreeNodeViewModel> CreateChildren(TrieNode parent, int depth) =>
            parent.Children!.Values
                .OrderByDescending(child => child.IsDirectory)
                .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => CreateViewModel(child, depth))
                .ToList();

        private static SaveFileTreeNodeViewModel CreateViewModel(TrieNode node, int depth) =>
            new(
                name: node.Name,
                relativePath: node.RelativePath,
                fullPath: node.FullPath,
                isDirectory: node.IsDirectory,
                sizeBytes: node.SizeBytes,
                fileCount: node.FileCount,
                depth: depth,
                childrenFactory: node.IsDirectory ? () => CreateChildren(node, depth + 1) : null,
                sha256: node.Sha256);
    }
}
