using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Models
{
    /// <summary>
    /// Represents an individual directory or file node in a save file hierarchy.
    /// Supports deferred / on-demand loading of child nodes upon expansion to eliminate
    /// UI memory spikes and layout freezes on ultra-large save sets (10,000+ files).
    /// </summary>
    public sealed partial class SaveFileTreeNodeViewModel : ObservableObject
    {
        private readonly Func<IEnumerable<SaveFileTreeNodeViewModel>>? _childrenFactory;
        private bool _isExpanded;
        private bool? _isVerified;

        public string Name { get; }
        public string RelativePath { get; }

        /// <summary>The file's original path, exactly as the verifier keys its results.</summary>
        public string FullPath { get; }
        public bool IsDirectory { get; }
        public bool IsFile => !IsDirectory;
        public long SizeBytes { get; }
        public int FileCount { get; }
        public int Depth { get; }
        public string? Sha256 { get; }

        public string? Sha256Short => Sha256 is { Length: > 12 }
            ? Sha256[..12]
            : Sha256;

        public Thickness IndentMargin => new(Depth * 18, 0, 0, 0);

        /// <summary>Empty until the node is first expanded, then filled once.</summary>
        public IReadOnlyList<SaveFileTreeNodeViewModel> Children { get; private set; } = [];

        public bool IsChildrenLoaded { get; private set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ExpanderGlyph));
                    if (value && !IsChildrenLoaded && _childrenFactory is not null)
                    {
                        IsChildrenLoaded = true;
                        Children = _childrenFactory().ToList();
                    }
                }
            }
        }

        public bool? IsVerified
        {
            get => _isVerified;
            set
            {
                if (SetProperty(ref _isVerified, value))
                {
                    OnPropertyChanged(nameof(StatusDisplay));
                    OnPropertyChanged(nameof(StatusGlyph));
                }
            }
        }

        public string ExpanderGlyph => !IsDirectory
            ? "  "
            : (IsExpanded ? "▼" : "▶");

        public string TypeGlyph => IsDirectory ? "📁" : "📄";

        public string SizeDisplay => ByteSize.Format(SizeBytes);

        public string FileCountDisplay => IsDirectory ? (FileCount == 1 ? "1 file" : $"{FileCount:N0} files") : string.Empty;

        // A folder's file count and glyph already show in their own columns.
        public string StatusDisplay => IsDirectory
            ? string.Empty
            : IsVerified switch
            {
                true => "Verified",
                false => "Mismatch",
                null => "Recorded"
            };

        public string StatusGlyph => IsDirectory
            ? string.Empty
            : IsVerified switch
            {
                true => "✓",
                false => "✕",
                null => "•"
            };

        public SaveFileTreeNodeViewModel(
            string name,
            string relativePath,
            string fullPath,
            bool isDirectory,
            long sizeBytes,
            int fileCount,
            int depth,
            Func<IEnumerable<SaveFileTreeNodeViewModel>>? childrenFactory,
            string? sha256 = null)
        {
            Name = name;
            RelativePath = relativePath;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            SizeBytes = sizeBytes;
            FileCount = fileCount;
            Depth = depth;
            _childrenFactory = childrenFactory;
            Sha256 = sha256;
        }
    }
}
