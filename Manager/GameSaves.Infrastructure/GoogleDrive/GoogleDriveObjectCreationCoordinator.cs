namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Coordinates same-parent, same-name creation attempts within this
    /// process. Entries live only while a holder or waiter exists; this is not
    /// an object-ID cache and cannot guarantee cross-process uniqueness.
    /// </summary>
    internal sealed class GoogleDriveObjectCreationCoordinator
    {
        private readonly object _gate = new();
        private readonly Dictionary<CreationKey, Entry> _entries = new();

        public async ValueTask<IDisposable> AcquireAsync(
            string parentId,
            string exactName,
            CancellationToken cancellationToken)
        {
            var key = new CreationKey(parentId, exactName);
            Entry entry;

            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry();
                    _entries.Add(key, entry);
                }

                entry.References++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
                return new Lease(this, key, entry);
            }
            catch
            {
                RemoveReference(key, entry);
                throw;
            }
        }

        private void Release(CreationKey key, Entry entry)
        {
            entry.Semaphore.Release();
            RemoveReference(key, entry);
        }

        private void RemoveReference(CreationKey key, Entry entry)
        {
            lock (_gate)
            {
                entry.References--;
                if (entry.References != 0)
                    return;

                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }

        private readonly record struct CreationKey(
            string ParentId,
            string ExactName);

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int References { get; set; }
        }

        private sealed class Lease : IDisposable
        {
            private GoogleDriveObjectCreationCoordinator? _owner;
            private readonly CreationKey _key;
            private readonly Entry _entry;

            public Lease(
                GoogleDriveObjectCreationCoordinator owner,
                CreationKey key,
                Entry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                GoogleDriveObjectCreationCoordinator? owner =
                    Interlocked.Exchange(ref _owner, null);
                owner?.Release(_key, _entry);
            }
        }
    }
}
