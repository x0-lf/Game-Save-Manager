using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Owns Google Drive query syntax and literal escaping. Completed queries
    /// can contain user-selected names and object IDs and must not be logged.
    /// </summary>
    internal sealed class GoogleDriveQueryBuilder
    {
        public string BuildExactNameChildQuery(string parentId, string name)
        {
            ValidateParentId(parentId);

            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "A Google Drive object name is required.",
                    nameof(name));
            }

            return $"'{EscapeLiteral(parentId)}' in parents and " +
                $"name = '{EscapeLiteral(name)}' and trashed = false";
        }

        public string BuildDirectChildrenQuery(
            string parentId,
            GoogleDriveObjectKind? expectedKind)
        {
            ValidateParentId(parentId);

            if (expectedKind is not null && !Enum.IsDefined(expectedKind.Value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedKind),
                    expectedKind,
                    "The expected Google Drive object kind is invalid.");
            }

            string query = $"'{EscapeLiteral(parentId)}' in parents and trashed = false";

            return expectedKind switch
            {
                GoogleDriveObjectKind.Folder =>
                    $"{query} and mimeType = '{GoogleDriveApplicationRoot.FolderMimeType}'",
                GoogleDriveObjectKind.File =>
                    $"{query} and mimeType != '{GoogleDriveApplicationRoot.FolderMimeType}'",
                null => query,
                _ => throw new ArgumentOutOfRangeException(nameof(expectedKind))
            };
        }

        private static void ValidateParentId(string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
            {
                throw new ArgumentException(
                    "A Google Drive parent object ID is required.",
                    nameof(parentId));
            }
        }

        internal static string EscapeLiteral(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
    }
}
