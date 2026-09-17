using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
        private bool _isChildrenLoaded;
        private bool _isExpanded;
        private bool? _isVerified;

        public string Name { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; }
        public bool IsFile => !IsDirectory;
        public long SizeBytes { get; }
        public int FileCount { get; }
        public int Depth { get; }
        public bool HasChildren { get; }
        public string? Sha256 { get; }

        public string? Sha256Short => Sha256 is { Length: > 12 }
            ? Sha256[..12]
            : Sha256;

        public Thickness IndentMargin => new(Depth * 18, 0, 0, 0);

        public ObservableCollection<SaveFileTreeNodeViewModel> Children { get; } = new();

        public bool IsChildrenLoaded => _isChildrenLoaded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ExpanderGlyph));
                    if (value && !_isChildrenLoaded)
                    {
                        EnsureChildrenLoaded();
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
                    OnPropertyChanged(nameof(IsVerifiedSuccess));
                    OnPropertyChanged(nameof(IsVerifiedFailed));
                }
            }
        }

        public bool IsVerifiedSuccess => IsVerified == true;
        public bool IsVerifiedFailed => IsVerified == false;

        public string ExpanderGlyph => !IsDirectory || !HasChildren
            ? "  "
            : (IsExpanded ? "▼" : "▶");

        public string TypeGlyph => IsDirectory ? "📁" : "📄";

        public string SizeDisplay => FormatBytes(SizeBytes);

        public string FileCountDisplay => IsDirectory ? (FileCount == 1 ? "1 file" : $"{FileCount:N0} files") : string.Empty;

        public string StatusDisplay
        {
            get
            {
                if (IsDirectory)
                {
                    return FileCount == 1 ? "1 file" : $"{FileCount:N0} files";
                }

                return IsVerified switch
                {
                    true => "Verified",
                    false => "Mismatch",
                    null => "Recorded"
                };
            }
        }

        public string StatusGlyph
        {
            get
            {
                if (IsDirectory)
                {
                    return "📁";
                }

                return IsVerified switch
                {
                    true => "✓",
                    false => "✕",
                    null => "•"
                };
            }
        }

        public SaveFileTreeNodeViewModel(
            string name,
            string relativePath,
            string fullPath,
            bool isDirectory,
            long sizeBytes,
            int fileCount,
            int depth,
            Func<IEnumerable<SaveFileTreeNodeViewModel>>? childrenFactory,
            bool hasChildren = false,
            string? sha256 = null,
            bool? isVerified = null)
        {
            Name = name;
            RelativePath = relativePath;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            SizeBytes = sizeBytes;
            FileCount = fileCount;
            Depth = depth;
            _childrenFactory = childrenFactory;
            HasChildren = hasChildren;
            Sha256 = sha256;
            _isVerified = isVerified;

            if (IsDirectory && HasChildren && _childrenFactory is not null)
            {
                // Add a single lightweight placeholder child so standard TreeView controls
                // can show an expander chevron before children are instantiated.
                Children.Add(CreateDummy(Depth + 1));
            }
        }

        public void EnsureChildrenLoaded()
        {
            if (_isChildrenLoaded || !IsDirectory || _childrenFactory is null)
                return;

            _isChildrenLoaded = true;
            Children.Clear();

            foreach (SaveFileTreeNodeViewModel child in _childrenFactory())
            {
                Children.Add(child);
            }
        }

        public static SaveFileTreeNodeViewModel CreateDummy(int depth) =>
            new(
                name: "Loading...",
                relativePath: string.Empty,
                fullPath: string.Empty,
                isDirectory: false,
                sizeBytes: 0,
                fileCount: 0,
                depth: depth,
                childrenFactory: null,
                hasChildren: false);

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";

            double kb = bytes / 1024.0;
            if (kb < 1024)
                return $"{kb:0.##} KB";

            double mb = kb / 1024.0;
            if (mb < 1024)
                return $"{mb:0.##} MB";

            double gb = mb / 1024.0;
            return $"{gb:0.##} GB";
        }
    }
}
