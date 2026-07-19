using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.Core.Sync;
using System;
using System.Linq;

namespace GameSaves.App.Models
{
    public sealed partial class SyncItemRowViewModel : ObservableObject
    {
        private readonly Action? _selectionChanged;

        // Actionable runs are included by default; unticking excludes just
        // that run from execution without rebuilding the preview.
        [ObservableProperty]
        private bool includeInSync = true;

        public SyncItemRowViewModel(SyncItem item, Action? selectionChanged = null)
        {
            Item = item;
            _selectionChanged = selectionChanged;
        }

        partial void OnIncludeInSyncChanged(bool value)
        {
            _selectionChanged?.Invoke();
        }

        public SyncItem Item { get; }

        public bool IsSelectable =>
            Item.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal;

        public string RunName => Item.RunName;

        public string GameName => Item.GameName;

        public string ActionText => Item.Action switch
        {
            SyncItemAction.UploadToRemote => "Upload",
            SyncItemAction.DownloadToLocal => "Download",
            SyncItemAction.Conflict => "Conflict",
            _ => "In sync"
        };

        public string FilesDisplay => $"{Item.FileCount} file(s)";

        public string SizeDisplay => FormatBytes(Item.TotalBytes);

        public string StatusText => Item.StatusText;

        private static string FormatBytes(long bytes)
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

    public sealed class SyncItemResultRowViewModel
    {
        public SyncItemResultRowViewModel(SyncItemResult result)
        {
            Result = result;
        }

        public SyncItemResult Result { get; }

        public string RunName => Result.Item.RunName;

        public string Status => Result.Status.ToString();

        public string SizeDisplay => FormatBytes(Result.Bytes);

        public string? Error => Result.Error;

        private static string FormatBytes(long bytes)
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

    public sealed class SyncLogEntryRowViewModel
    {
        public SyncLogEntryRowViewModel(SyncLogEntry entry)
        {
            Entry = entry;
        }

        public SyncLogEntry Entry { get; }

        public string TimestampDisplay =>
            Entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public string DeviceName => Entry.DeviceName;

        public string SummaryDisplay =>
            $"{Entry.Uploaded} uploaded, {Entry.Downloaded} downloaded" +
            (Entry.Conflicts > 0 ? $", {Entry.Conflicts} conflict(s)" : "");

        public string RunsDisplay
        {
            get
            {
                var parts = Entry.UploadedRuns
                    .Select(name => $"↑ {name}")
                    .Concat(Entry.DownloadedRuns.Select(name => $"↓ {name}"));

                return string.Join("   ", parts);
            }
        }
    }
}
