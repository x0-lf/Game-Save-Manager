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
            if (string.IsNullOrWhiteSpace(parentId))
            {
                throw new ArgumentException(
                    "A Google Drive parent object ID is required.",
                    nameof(parentId));
            }

            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "A Google Drive object name is required.",
                    nameof(name));
            }

            return $"'{EscapeLiteral(parentId)}' in parents and " +
                $"name = '{EscapeLiteral(name)}' and trashed = false";
        }

        internal static string EscapeLiteral(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
    }
}
