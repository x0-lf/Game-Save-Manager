using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Rewrites a manifest's backup-file paths to a new run root
    /// (used after ZIP import and sync download). Schema v2 manifests use deterministic
    /// relative payload paths; Schema v1 legacy manifests fall back to prefix matching and
    /// are automatically upgraded to Schema v2.
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

            // Deterministic path: Schema v2 or manifests with relative paths
            bool hasRelativePaths = manifest.Items.All(i => !string.IsNullOrWhiteSpace(i.RelativePath));
            if (hasRelativePaths || manifest.SchemaVersion >= TransferBackupManifest.CurrentSchemaVersion)
            {
                var newItems = new List<TransferOverwriteBackupItem>(manifest.Items.Count);
                bool allValid = true;

                foreach (TransferOverwriteBackupItem item in manifest.Items)
                {
                    string relative = item.GetRelativePayloadPath().Replace('/', Path.DirectorySeparatorChar);
                    string newBackupFile = Path.Combine(targetRoot, relative);

                    if (!File.Exists(newBackupFile))
                    {
                        allValid = false;
                        break;
                    }

                    newItems.Add(item with
                    {
                        BackupFile = newBackupFile,
                        RelativePath = item.GetRelativePayloadPath()
                    });
                }

                if (allValid)
                {
                    rewritten = manifest.ToSchemaV2() with { Items = newItems };
                    return true;
                }
            }

            // Backward compatibility fallback for Schema v1 legacy manifests:
            string firstPath = manifest.Items[0].BackupFile;
            string[] segments = firstPath.Split(Path.DirectorySeparatorChar);

            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (!segments[i].Equals("files", StringComparison.OrdinalIgnoreCase))
                    continue;

                int prefixLength = segments.Take(i)
                    .Sum(segment => segment.Length + 1);

                if (TryRewriteWithPrefix(manifest, prefixLength, targetRoot, out rewritten))
                {
                    rewritten = rewritten.ToSchemaV2();
                    return true;
                }
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

                newItems.Add(item with
                {
                    BackupFile = newBackupFile,
                    RelativePath = item.GetRelativePayloadPath()
                });
            }

            rewritten = manifest with { Items = newItems };
            return true;
        }
    }
}
