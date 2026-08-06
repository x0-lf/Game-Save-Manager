namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// An immutable canonical path relative to one requested backup-run
    /// folder. The run-folder path is deliberately excluded; each append adds
    /// exactly one validated Drive child name using '/' as the separator.
    /// </summary>
    internal sealed class GoogleDriveRecursiveRelativePath
        : IEquatable<GoogleDriveRecursiveRelativePath>
    {
        private readonly GoogleDriveRelativePath _relativePath;

        private GoogleDriveRecursiveRelativePath(
            GoogleDriveRelativePath relativePath) =>
            _relativePath = relativePath ??
                throw new ArgumentNullException(nameof(relativePath));

        public string Canonical => _relativePath.Canonical;

        public IReadOnlyList<string> Segments => _relativePath.Segments;

        public int Depth => _relativePath.Segments.Count;

        public int SegmentCount => _relativePath.Segments.Count;

        public bool IsRunFolderRoot => _relativePath.IsRoot;

        public static GoogleDriveRecursiveRelativePath Start(
            GoogleDriveRecursiveFileListingRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return new GoogleDriveRecursiveRelativePath(
                GoogleDriveRelativePath.Root);
        }

        public GoogleDriveRecursiveRelativePath AppendChild(string childName)
        {
            if (!GoogleDriveRelativePath.TryParse(
                    childName,
                    out GoogleDriveRelativePath? childPath) ||
                childPath is null ||
                childPath.IsRoot ||
                childPath.Segments.Count != 1 ||
                !string.Equals(
                    childPath.Canonical,
                    childName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A valid Google Drive child name is required.",
                    nameof(childName));
            }

            string canonical = _relativePath.IsRoot
                ? childName
                : string.Concat(_relativePath.Canonical, "/", childName);

            return new GoogleDriveRecursiveRelativePath(
                GoogleDriveRelativePath.Parse(canonical));
        }

        public bool Equals(GoogleDriveRecursiveRelativePath? other) =>
            other is not null && _relativePath.Equals(other._relativePath);

        public override bool Equals(object? obj) =>
            obj is GoogleDriveRecursiveRelativePath other && Equals(other);

        public override int GetHashCode() => _relativePath.GetHashCode();

        public string ToSafeDiagnosticString() =>
            "Google Drive recursive relative path " +
            $"(depth={Depth}; segments={SegmentCount})";

        public override string ToString() => ToSafeDiagnosticString();
    }
}
