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
        string? Sha256 = null,
        string? FullPath = null,
        bool? IsVerified = null);

    /// <summary>
    /// High-performance builder that constructs hierarchical file trees with deferred / on-demand
    /// node expansion. Only root nodes are instantiated upfront as ViewModels, while descendant
    /// directories and files remain lightweight descriptors until a user expands them.
    /// </summary>
    public static class LazyFileTreeBuilder
    {
        private sealed class TrieNode
        {
            public string Name { get; }
            public string RelativePath { get; }
            public string FullPath { get; set; } = string.Empty;
            public bool IsDirectory { get; set; }
            public long SizeBytes { get; set; }
            public int FileCount { get; set; }
            public string? Sha256 { get; set; }
            public bool? IsVerified { get; set; }
            public Dictionary<string, TrieNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

            public TrieNode(string name, string relativePath, bool isDirectory)
            {
                Name = name;
                RelativePath = relativePath;
                IsDirectory = isDirectory;
            }
        }

        public static List<SaveFileTreeNodeViewModel> BuildFromBackupItems(IEnumerable<TransferOverwriteBackupItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            var descriptors = items.Select(item => new SaveFileItemDescriptor(
                Path: string.IsNullOrWhiteSpace(item.BackupFile) ? item.OriginalFile : item.BackupFile,
                Bytes: item.Bytes,
                Sha256: item.Sha256,
                FullPath: item.OriginalFile));

            return Build(descriptors);
        }

        public static List<SaveFileTreeNodeViewModel> Build(IEnumerable<SaveFileItemDescriptor> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            var rootTrie = new TrieNode(string.Empty, string.Empty, isDirectory: true);

            foreach (SaveFileItemDescriptor item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                    continue;

                string normalized = item.Path.Replace('\\', '/').Trim('/');
                string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 0)
                    continue;

                TrieNode current = rootTrie;
                current.SizeBytes += item.Bytes;
                current.FileCount += 1;

                string pathAcc = string.Empty;

                for (int i = 0; i < segments.Length - 1; i++)
                {
                    string dirName = segments[i];
                    pathAcc = string.IsNullOrEmpty(pathAcc) ? dirName : $"{pathAcc}/{dirName}";

                    if (!current.Children.TryGetValue(dirName, out TrieNode? subDir))
                    {
                        subDir = new TrieNode(dirName, pathAcc, isDirectory: true);
                        current.Children[dirName] = subDir;
                    }

                    subDir.SizeBytes += item.Bytes;
                    subDir.FileCount += 1;
                    current = subDir;
                }

                string fileName = segments[^1];
                string filePath = string.IsNullOrEmpty(pathAcc) ? fileName : $"{pathAcc}/{fileName}";

                var fileNode = new TrieNode(fileName, filePath, isDirectory: false)
                {
                    SizeBytes = item.Bytes,
                    FileCount = 1,
                    Sha256 = item.Sha256,
                    FullPath = item.FullPath ?? item.Path,
                    IsVerified = item.IsVerified,
                };

                current.Children[fileName] = fileNode;
            }

            return rootTrie.Children.Values
                .OrderByDescending(c => c.IsDirectory)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => CreateViewModel(child, depth: 0))
                .ToList();
        }

        private static SaveFileTreeNodeViewModel CreateViewModel(TrieNode node, int depth)
        {
            if (!node.IsDirectory)
            {
                return new SaveFileTreeNodeViewModel(
                    name: node.Name,
                    relativePath: node.RelativePath,
                    fullPath: node.FullPath,
                    isDirectory: false,
                    sizeBytes: node.SizeBytes,
                    fileCount: 1,
                    depth: depth,
                    childrenFactory: null,
                    hasChildren: false,
                    sha256: node.Sha256,
                    isVerified: node.IsVerified);
            }

            bool hasChildren = node.Children.Count > 0;

            Func<IEnumerable<SaveFileTreeNodeViewModel>> childrenFactory = () =>
            {
                return node.Children.Values
                    .OrderByDescending(c => c.IsDirectory)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => CreateViewModel(child, depth + 1))
                    .ToList();
            };

            return new SaveFileTreeNodeViewModel(
                name: node.Name,
                relativePath: node.RelativePath,
                fullPath: node.FullPath,
                isDirectory: true,
                sizeBytes: node.SizeBytes,
                fileCount: node.FileCount,
                depth: depth,
                childrenFactory: childrenFactory,
                hasChildren: hasChildren);
        }
    }
}
