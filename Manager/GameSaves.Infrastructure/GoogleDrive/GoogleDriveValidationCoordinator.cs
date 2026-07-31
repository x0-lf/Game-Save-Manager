namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveValidationCoordinator
    {
        GoogleDriveValidationOperation Begin(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        void Cancel(Guid remoteProfileId);

        bool IsActive(Guid remoteProfileId);
    }

    /// <summary>
    /// Coordinates validation generations per saved profile. Starting a newer
    /// validation actively cancels the previous generation for that profile,
    /// while unrelated profiles remain independent.
    /// </summary>
    internal sealed class GoogleDriveValidationCoordinator
        : IGoogleDriveValidationCoordinator
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, OperationState> _operations = new();
        private long _nextGeneration;

        public GoogleDriveValidationOperation Begin(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var state = new OperationState(
                Interlocked.Increment(ref _nextGeneration),
                cancellation);
            OperationState? previous;

            lock (_gate)
            {
                _operations.TryGetValue(remoteProfileId, out previous);
                _operations[remoteProfileId] = state;
            }

            CancelSafely(previous);
            return new GoogleDriveValidationOperation(
                this,
                remoteProfileId,
                state);
        }

        public void Cancel(Guid remoteProfileId)
        {
            if (remoteProfileId == Guid.Empty)
                return;

            OperationState? current;
            lock (_gate)
            {
                if (!_operations.Remove(remoteProfileId, out current))
                    return;
            }

            CancelSafely(current);
        }

        public bool IsActive(Guid remoteProfileId)
        {
            if (remoteProfileId == Guid.Empty)
                return false;

            lock (_gate)
                return _operations.ContainsKey(remoteProfileId);
        }

        internal bool IsCurrent(Guid remoteProfileId, OperationState state)
        {
            lock (_gate)
            {
                return _operations.TryGetValue(remoteProfileId, out OperationState? current) &&
                    ReferenceEquals(current, state);
            }
        }

        internal void Complete(Guid remoteProfileId, OperationState state)
        {
            lock (_gate)
            {
                if (_operations.TryGetValue(remoteProfileId, out OperationState? current) &&
                    ReferenceEquals(current, state))
                {
                    _operations.Remove(remoteProfileId);
                }
            }

            state.Cancellation.Dispose();
        }

        private static void CancelSafely(OperationState? state)
        {
            try
            {
                state?.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race. The generation is already inactive.
            }
        }

        internal sealed record OperationState(
            long Generation,
            CancellationTokenSource Cancellation);
    }

    internal sealed class GoogleDriveValidationOperation : IDisposable
    {
        private readonly GoogleDriveValidationCoordinator _coordinator;
        private readonly Guid _remoteProfileId;
        private readonly GoogleDriveValidationCoordinator.OperationState _state;
        private int _disposed;

        internal GoogleDriveValidationOperation(
            GoogleDriveValidationCoordinator coordinator,
            Guid remoteProfileId,
            GoogleDriveValidationCoordinator.OperationState state)
        {
            _coordinator = coordinator;
            _remoteProfileId = remoteProfileId;
            _state = state;
        }

        public long Generation => _state.Generation;

        public CancellationToken CancellationToken => _state.Cancellation.Token;

        public bool IsCurrent =>
            _coordinator.IsCurrent(_remoteProfileId, _state);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _coordinator.Complete(_remoteProfileId, _state);
        }

        public override string ToString() =>
            $"GoogleDriveValidationOperation {{ Generation = {Generation}, IsCurrent = {IsCurrent} }}";
    }
}
