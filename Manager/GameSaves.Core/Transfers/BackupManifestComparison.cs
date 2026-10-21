namespace GameSaves.Core.Transfers
{
    /// <summary>How one payload file differs between a baseline run and a target run.</summary>
    public enum BackupManifestDifferenceKind
    {
        /// <summary>Present in both with the same SHA-256 and size.</summary>
        Identical = 0,

        /// <summary>Present in the target only.</summary>
        Added = 1,

        /// <summary>Present in the baseline only.</summary>
        Removed = 2,

        /// <summary>Present in both with a different SHA-256.</summary>
        Changed = 3
    }

    /// <summary>
    /// One payload file as both manifests record it. The sides that lack the
    /// file carry null size and hash. <see cref="RelativePath"/> is the
    /// baseline's spelling when the baseline has the file, otherwise the target's.
    /// </summary>
    public sealed record BackupManifestFileDifference(
        string RelativePath,
        BackupManifestDifferenceKind Kind,
        long? BaselineBytes,
        string? BaselineSha256,
        long? TargetBytes,
        string? TargetSha256);

    /// <summary>
    /// What comparing two manifests found: every file in path order, or a
    /// refusal naming the manifest that could not be compared. Bytes per class
    /// come from the side that has the file; changed files count their target
    /// size.
    /// </summary>
    public sealed record BackupManifestComparisonResult(
        IReadOnlyList<BackupManifestFileDifference> Files,
        string? Refusal = null)
    {
        public bool IsRefused => Refusal is not null;

        public int AddedCount => Count(BackupManifestDifferenceKind.Added);

        public int RemovedCount => Count(BackupManifestDifferenceKind.Removed);

        public int ChangedCount => Count(BackupManifestDifferenceKind.Changed);

        public int IdenticalCount => Count(BackupManifestDifferenceKind.Identical);

        public long AddedBytes => Bytes(BackupManifestDifferenceKind.Added, target: true);

        public long RemovedBytes => Bytes(BackupManifestDifferenceKind.Removed, target: false);

        public long ChangedBytes => Bytes(BackupManifestDifferenceKind.Changed, target: true);

        public long IdenticalBytes => Bytes(BackupManifestDifferenceKind.Identical, target: true);

        public static BackupManifestComparisonResult Refused(string reason) => new([], reason);

        private int Count(BackupManifestDifferenceKind kind) => Files.Count(file => file.Kind == kind);

        private long Bytes(BackupManifestDifferenceKind kind, bool target) => Files
            .Where(file => file.Kind == kind)
            .Sum(file => (target ? file.TargetBytes : file.BaselineBytes) ?? 0);
    }

    /// <summary>
    /// Compares two backup runs by their manifests alone: no payload is read,
    /// extracted, or downloaded, so folder, ZIP, 7z, local and remote runs all
    /// compare the same way. A file is identified by its normalized relative
    /// payload path, compared without regard to case as Windows paths are;
    /// its content by the recorded SHA-256. A manifest that cannot be trusted
    /// to describe its run is refused with a reason instead of compared.
    /// </summary>
    public static class BackupManifestComparer
    {
        /// <summary>
        /// Compares <paramref name="baseline"/> with <paramref name="target"/>:
        /// a file only in the target is added, only in the baseline removed.
        /// <paramref name="progress"/> receives the number of files compared so
        /// far; cancellation surfaces as <see cref="OperationCanceledException"/>
        /// and never as a partial result.
        /// </summary>
        public static BackupManifestComparisonResult Compare(
            TransferBackupManifest? baseline,
            TransferBackupManifest? target,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (TryIndex(baseline, "baseline", cancellationToken, out var baselineFiles, out string? refusal) is false ||
                TryIndex(target, "target", cancellationToken, out var targetFiles, out refusal) is false)
                return BackupManifestComparisonResult.Refused(refusal!);

            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(baselineFiles.Keys);
            paths.UnionWith(targetFiles.Keys);

            var files = new List<BackupManifestFileDifference>(paths.Count);

            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                baselineFiles.TryGetValue(path, out TransferOverwriteBackupItem? before);
                targetFiles.TryGetValue(path, out TransferOverwriteBackupItem? after);

                BackupManifestDifferenceKind kind;

                if (before is null)
                    kind = BackupManifestDifferenceKind.Added;
                else if (after is null)
                    kind = BackupManifestDifferenceKind.Removed;
                else if (!string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase))
                    kind = BackupManifestDifferenceKind.Changed;
                else if (before.Bytes != after.Bytes)
                {
                    // One SHA-256 cannot describe two sizes: one record is wrong,
                    // and a comparison built on it would be wrong too.
                    return BackupManifestComparisonResult.Refused(
                        $"{path} has the same SHA-256 but a different size in the two manifests; one of them is corrupt.");
                }
                else
                    kind = BackupManifestDifferenceKind.Identical;

                files.Add(new BackupManifestFileDifference(
                    path,
                    kind,
                    before?.Bytes,
                    before?.Sha256,
                    after?.Bytes,
                    after?.Sha256));

                progress?.Report(files.Count);
            }

            return new BackupManifestComparisonResult(files);
        }

        /// <summary>
        /// True when a deserialized manifest can be read without dereferencing
        /// a null. <c>System.Text.Json</c> does not enforce a record's
        /// non-nullable members, so a manifest from a remote, an archive, or an
        /// interrupted local write can arrive with null members. The sync
        /// engine applies the same rule before it compares run identities.
        /// </summary>
        public static bool IsUsable(TransferBackupManifest? manifest)
        {
            if (manifest is null ||
                manifest.Kind is null ||
                manifest.Game is null ||
                manifest.SteamAppId is null ||
                manifest.Items is null)
            {
                return false;
            }

            foreach (TransferOverwriteBackupItem item in manifest.Items)
            {
                if (item is null || item.OriginalFile is null || item.Sha256 is null)
                    return false;
            }

            return true;
        }

        // Indexes a manifest by payload path, refusing anything a viewer must
        // not present as fact: an unusable or unsupported manifest, an unsafe
        // path, or one path listed twice.
        private static bool TryIndex(
            TransferBackupManifest? manifest,
            string side,
            CancellationToken cancellationToken,
            out Dictionary<string, TransferOverwriteBackupItem> files,
            out string? refusal)
        {
            files = new Dictionary<string, TransferOverwriteBackupItem>(StringComparer.OrdinalIgnoreCase);

            if (manifest is null)
            {
                refusal = $"The {side} manifest is missing.";
                return false;
            }

            if (!IsUsable(manifest))
            {
                refusal = $"The {side} manifest is incomplete: its kind, game, app id, items, or an item's path or hash is missing.";
                return false;
            }

            if (!manifest.TryValidate(out string? error))
            {
                refusal = $"The {side} manifest cannot be used: {error}";
                return false;
            }

            foreach (TransferOverwriteBackupItem item in manifest.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = item.GetRelativePayloadPath();

                if (!TransferOverwriteBackupItem.IsSafeRelativePayloadPath(path))
                {
                    refusal = $"The {side} manifest lists an unsafe payload path: {path}";
                    return false;
                }

                if (!files.TryAdd(path, item))
                {
                    refusal = $"The {side} manifest lists {path} twice.";
                    return false;
                }
            }

            refusal = null;
            return true;
        }
    }
}
