using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Infrastructure-only object kinds used by recursive backup-file listing.
    /// Unlike <see cref="GoogleDriveObjectKind"/>, this policy distinguishes
    /// ordinary uploaded blobs from Drive-native objects that cannot be listed
    /// as restorable backup content.
    /// </summary>
    internal enum GoogleDriveRecursiveObjectKind
    {
        Folder = 0,
        BlobFile = 1,
        GoogleWorkspaceDocument = 2,
        Shortcut = 3,
        Unsupported = 4
    }

    /// <summary>
    /// Central MIME-type policy for objects encountered during recursive
    /// listing. Only ordinary uploaded blob files are valid backup-file
    /// entries; shortcuts and Drive-native Workspace objects are never
    /// followed or exported.
    /// </summary>
    internal static class GoogleDriveRecursiveObjectClassificationPolicy
    {
        private const string ShortcutMimeType =
            "application/vnd.google-apps.shortcut";
        private const string WorkspaceMimeTypePrefix =
            "application/vnd.google-apps.";

        public static GoogleDriveRecursiveObjectKind Classify(string? mimeType)
        {
            if (!IsWellFormedMimeType(mimeType))
                return GoogleDriveRecursiveObjectKind.Unsupported;

            if (string.Equals(
                    mimeType,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GoogleDriveRecursiveObjectKind.Folder;
            }

            if (string.Equals(
                    mimeType,
                    ShortcutMimeType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GoogleDriveRecursiveObjectKind.Shortcut;
            }

            if (mimeType!.StartsWith(
                    WorkspaceMimeTypePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument;
            }

            return GoogleDriveRecursiveObjectKind.BlobFile;
        }

        public static string ToSafeDiagnosticString(
            GoogleDriveRecursiveObjectKind kind)
        {
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind));

            return $"Google Drive recursive object classification: kind={kind}";
        }

        private static bool IsWellFormedMimeType(string? mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType) ||
                !string.Equals(mimeType, mimeType.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            int separatorIndex = mimeType.IndexOf('/');
            if (separatorIndex <= 0 ||
                separatorIndex != mimeType.LastIndexOf('/') ||
                separatorIndex == mimeType.Length - 1)
            {
                return false;
            }

            return IsMimeToken(mimeType.AsSpan(0, separatorIndex)) &&
                   IsMimeToken(mimeType.AsSpan(separatorIndex + 1));
        }

        private static bool IsMimeToken(ReadOnlySpan<char> value)
        {
            foreach (char character in value)
            {
                if (!IsMimeTokenCharacter(character))
                    return false;
            }

            return true;
        }

        private static bool IsMimeTokenCharacter(char value) =>
            value is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or
                '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
