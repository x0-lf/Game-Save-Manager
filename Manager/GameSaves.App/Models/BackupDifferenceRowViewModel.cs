using GameSaves.App.Common;
using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One file of a run comparison, in words first: the class is text and an
    /// accessible name, and the glyph only repeats it. "Baseline" is the run
    /// picked to compare with; "selected" is the run selected on the page.
    /// </summary>
    public sealed class BackupDifferenceRowViewModel
    {
        public BackupDifferenceRowViewModel(BackupManifestFileDifference difference)
        {
            Difference = difference;
        }

        public BackupManifestFileDifference Difference { get; }

        public BackupManifestDifferenceKind Kind => Difference.Kind;

        public string RelativePath => Difference.RelativePath;

        public string KindText => Kind switch
        {
            BackupManifestDifferenceKind.Added => "Added",
            BackupManifestDifferenceKind.Removed => "Removed",
            BackupManifestDifferenceKind.Changed => "Changed",
            _ => "Identical"
        };

        public string KindGlyph => Kind switch
        {
            BackupManifestDifferenceKind.Added => "+",
            BackupManifestDifferenceKind.Removed => "−",
            BackupManifestDifferenceKind.Changed => "≠",
            _ => "="
        };

        public string KindDetail => Kind switch
        {
            BackupManifestDifferenceKind.Added => "In the selected run but not in the baseline.",
            BackupManifestDifferenceKind.Removed => "In the baseline but not in the selected run.",
            BackupManifestDifferenceKind.Changed => "In both runs with different content (SHA-256).",
            _ => "In both runs with the same content."
        };

        public string BaselineSizeDisplay => Size(Difference.BaselineBytes);

        public string SelectedSizeDisplay => Size(Difference.TargetBytes);

        public string BaselineSha256 => Difference.BaselineSha256 ?? "";

        public string SelectedSha256 => Difference.TargetSha256 ?? "";

        public string BaselineSha256Short => Short(Difference.BaselineSha256);

        public string SelectedSha256Short => Short(Difference.TargetSha256);

        public string AccessibleName =>
            $"{KindText}: {RelativePath}. Baseline {BaselineSizeDisplay}, selected run {SelectedSizeDisplay}.";

        private static string Size(long? bytes) => bytes is { } value ? ByteSize.Format(value) : "—";

        private static string Short(string? sha256) => sha256 switch
        {
            null => "—",
            { Length: > 12 } => sha256[..12],
            _ => sha256
        };
    }
}
