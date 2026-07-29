using System.Collections.Concurrent;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveObjectCacheInvalidationReason
    {
        AccountReconnect = 0,
        AccountDisconnect = 1,
        ApplicationRootReplacement = 2,
        ProfileDeletion = 3,
        AuthorizationRevocation = 4
    }

    internal readonly struct GoogleDriveObjectCacheScope : IEquatable<GoogleDriveObjectCacheScope>
    {
        public GoogleDriveObjectCacheScope(Guid remoteProfileId, string rootFolderId)
        {
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException("A remote profile ID is required.", nameof(remoteProfileId));
            if (string.IsNullOrWhiteSpace(rootFolderId))
                throw new ArgumentException("A Google Drive root ID is required.", nameof(rootFolderId));

            RemoteProfileId = remoteProfileId;
            RootFolderId = rootFolderId;
        }

        public Guid RemoteProfileId { get; }

        public string RootFolderId { get; }

        public bool Equals(GoogleDriveObjectCacheScope other) =>
            RemoteProfileId == other.RemoteProfileId &&
            string.Equals(RootFolderId, other.RootFolderId, StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is GoogleDriveObjectCacheScope other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                RemoteProfileId,
                StringComparer.Ordinal.GetHashCode(RootFolderId));

        public override string ToString() => "Google Drive object cache scope";
    }

    internal sealed class GoogleDriveObjectIdCacheEntry
    {
        public GoogleDriveObjectIdCacheEntry(GoogleDriveObjectMetadata metadata)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public GoogleDriveObjectMetadata Metadata { get; }

        public string ObjectId => Metadata.Id;

        public override string ToString() => "Google Drive object ID cache entry";
    }

    internal interface IGoogleDriveObjectIdCache
    {
        bool TryGet(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            out GoogleDriveObjectIdCacheEntry? entry);

        bool TryStoreUniqueValidated(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata);

        void Remove(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind);

        void ClearScope(GoogleDriveObjectCacheScope scope);

        void InvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason);
    }

    /// <summary>
    /// Holds only validated Drive identities in process memory. Callers must
    /// still validate a hit through the Drive API before using it across
    /// resolver calls.
    /// </summary>
    internal sealed class GoogleDriveObjectIdCache : IGoogleDriveObjectIdCache
    {
        private readonly ConcurrentDictionary<CacheKey, GoogleDriveObjectIdCacheEntry>
            _entries = new();

        public bool TryGet(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            out GoogleDriveObjectIdCacheEntry? entry)
        {
            ValidateKey(scope, parentId, exactName, expectedKind);
            return _entries.TryGetValue(
                new CacheKey(scope, parentId, exactName, expectedKind),
                out entry);
        }

        public bool TryStoreUniqueValidated(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata)
        {
            ValidateKey(scope, parentId, exactName, expectedKind);
            ArgumentNullException.ThrowIfNull(metadata);

            if (!IsValidEntry(parentId, exactName, expectedKind, metadata))
                return false;

            _entries[new CacheKey(scope, parentId, exactName, expectedKind)] =
                new GoogleDriveObjectIdCacheEntry(metadata);
            return true;
        }

        public void Remove(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind)
        {
            ValidateKey(scope, parentId, exactName, expectedKind);
            _entries.TryRemove(
                new CacheKey(scope, parentId, exactName, expectedKind),
                out _);
        }

        public void ClearScope(GoogleDriveObjectCacheScope scope)
        {
            foreach (CacheKey key in _entries.Keys)
            {
                if (key.Scope.Equals(scope))
                    _entries.TryRemove(key, out _);
            }
        }

        public void InvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason)
        {
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException("A remote profile ID is required.", nameof(remoteProfileId));
            if (!Enum.IsDefined(reason))
                throw new ArgumentOutOfRangeException(nameof(reason));

            foreach (CacheKey key in _entries.Keys)
            {
                if (key.Scope.RemoteProfileId == remoteProfileId)
                    _entries.TryRemove(key, out _);
            }
        }

        private static bool IsValidEntry(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata) =>
            !metadata.Trashed &&
            string.IsNullOrWhiteSpace(metadata.DriveId) &&
            string.Equals(metadata.Name, exactName, StringComparison.Ordinal) &&
            metadata.ParentIds.Contains(parentId, StringComparer.Ordinal) &&
            metadata.Kind == expectedKind;

        private static void ValidateKey(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind)
        {
            if (scope.RemoteProfileId == Guid.Empty ||
                string.IsNullOrWhiteSpace(scope.RootFolderId))
            {
                throw new ArgumentException("A valid Google Drive cache scope is required.", nameof(scope));
            }
            if (string.IsNullOrWhiteSpace(parentId))
                throw new ArgumentException("A Google Drive parent ID is required.", nameof(parentId));
            if (string.IsNullOrEmpty(exactName))
                throw new ArgumentException("An exact Google Drive object name is required.", nameof(exactName));
            if (!GoogleDriveRelativePath.TryParse(
                    exactName,
                    out GoogleDriveRelativePath? childPath) ||
                childPath is null ||
                childPath.IsRoot ||
                childPath.Segments.Count != 1)
            {
                throw new ArgumentException(
                    "A single Google Drive path segment is required.",
                    nameof(exactName));
            }
            if (!Enum.IsDefined(expectedKind))
                throw new ArgumentOutOfRangeException(nameof(expectedKind));
        }

        private readonly record struct CacheKey(
            GoogleDriveObjectCacheScope Scope,
            string ParentId,
            string ExactName,
            GoogleDriveObjectKind ExpectedKind);
    }
}
