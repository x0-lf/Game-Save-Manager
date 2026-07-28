using System.Collections.ObjectModel;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// An immutable, provider-relative Google Drive path. A slash is the only
    /// separator; all other valid characters retain their exact ordinal form.
    /// </summary>
    internal sealed class GoogleDriveRelativePath : IEquatable<GoogleDriveRelativePath>
    {
        private static readonly IReadOnlyList<string> EmptySegments =
            Array.AsReadOnly(Array.Empty<string>());

        private GoogleDriveRelativePath(string canonical, IReadOnlyList<string> segments)
        {
            Canonical = canonical;
            Segments = segments;
        }

        public static GoogleDriveRelativePath Root { get; } =
            new(string.Empty, EmptySegments);

        public string Canonical { get; }

        public IReadOnlyList<string> Segments { get; }

        public bool IsRoot => Segments.Count == 0;

        public static GoogleDriveRelativePath Parse(string value)
        {
            if (!TryParse(value, out GoogleDriveRelativePath? path))
            {
                throw new ArgumentException(
                    "The Google Drive relative path is invalid.",
                    nameof(value));
            }

            return path!;
        }

        public static bool TryParse(
            string? value,
            out GoogleDriveRelativePath? path)
        {
            path = null;

            if (value is null)
                return false;

            if (value.Length == 0)
            {
                path = Root;
                return true;
            }

            if (value[0] == '/' || value[^1] == '/')
                return false;

            string[] segments = value.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 ||
                    segment is "." or ".." ||
                    !IsSafeDriveName(segment))
                {
                    return false;
                }
            }

            var readOnlySegments = new ReadOnlyCollection<string>(segments);
            path = new GoogleDriveRelativePath(
                string.Join('/', readOnlySegments),
                readOnlySegments);
            return true;
        }

        private static bool IsSafeDriveName(string segment)
        {
            for (int index = 0; index < segment.Length; index++)
            {
                char character = segment[index];
                if (char.IsControl(character))
                    return false;

                if (!char.IsSurrogate(character))
                    continue;

                if (!char.IsHighSurrogate(character) ||
                    index + 1 >= segment.Length ||
                    !char.IsLowSurrogate(segment[index + 1]))
                {
                    return false;
                }

                index++;
            }

            return true;
        }

        public bool Equals(GoogleDriveRelativePath? other) =>
            other is not null &&
            string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is GoogleDriveRelativePath other && Equals(other);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Canonical);

        public override string ToString() =>
            IsRoot
                ? "Google Drive application root"
                : $"Google Drive relative path ({Segments.Count} segments)";
    }
}
