using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Rewrites a manifest's absolute backup-file paths to a new run root
    /// (used after ZIP import and sync download). The original run root is not
    /// stored in the manifest, but every backup-file path is
    /// &lt;oldRoot&gt;\files\&lt;mirror&gt;; the correct prefix is the one that makes
    /// every item's relative path point at an existing file under the new root.
    /// </summary>
    internal static class BackupManifestPathRewriter
    {
        public static bool TryRewrite(
            TransferBackupManifest manifest,
            string targetRoot,
            out TransferBackupManifest rewritten)
        {
            rewritten = manifest;

            if (manifest.Items.Count == 0)
                return true;

            string firstPath = manifest.Items[0].BackupFile;
            string[] segments = firstPath.Split(Path.DirectorySeparatorChar);

            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (!segments[i].Equals("files", StringComparison.OrdinalIgnoreCase))
                    continue;

                int prefixLength = segments.Take(i)
                    .Sum(segment => segment.Length + 1);

                if (TryRewriteWithPrefix(manifest, prefixLength, targetRoot, out rewritten))
                    return true;
            }

            return false;
        }

        private static bool TryRewriteWithPrefix(
            TransferBackupManifest manifest,
            int prefixLength,
            string targetRoot,
            out TransferBackupManifest rewritten)
        {
            rewritten = manifest;

            var newItems = new List<TransferOverwriteBackupItem>(manifest.Items.Count);

            foreach (TransferOverwriteBackupItem item in manifest.Items)
            {
                if (item.BackupFile.Length <= prefixLength)
                    return false;

                string relative = item.BackupFile[prefixLength..];

                if (!relative.StartsWith("files" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string newBackupFile = Path.Combine(targetRoot, relative);

                if (!File.Exists(newBackupFile))
                    return false;

                newItems.Add(item with { BackupFile = newBackupFile });
            }

            rewritten = manifest with { Items = newItems };
            return true;
        }
    }
}
