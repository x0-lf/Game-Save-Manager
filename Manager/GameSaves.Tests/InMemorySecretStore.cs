using GameSaves.Core.Secrets;

namespace GameSaves.Tests;

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<SecretKey, byte[]> _values = new();
    private readonly HashSet<SecretKey> _corrupted = new();

    public bool SimulateUnavailable { get; set; }

    public Guid? LastDeleteAllOwnerId { get; private set; }

    public void MarkCorrupted(SecretKey key) => _corrupted.Add(key);

    public Task<SecretOperationResult> StoreAsync(
        SecretKey key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SimulateUnavailable)
        {
            return Task.FromResult(
                SecretOperationResult.Unavailable("SecretStoreUnavailable"));
        }

        _values[key] = value.ToArray();
        _corrupted.Remove(key);
        return Task.FromResult(SecretOperationResult.Success(1));
    }

    public Task<SecretReadResult> ReadAsync(
        SecretKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SimulateUnavailable)
        {
            return Task.FromResult(
                SecretReadResult.Unavailable("SecretStoreUnavailable"));
        }

        if (_corrupted.Contains(key))
        {
            return Task.FromResult(
                SecretReadResult.Corrupted("SecretDataCorrupted"));
        }

        return Task.FromResult(
            _values.TryGetValue(key, out byte[]? value)
                ? SecretReadResult.Found(value)
                : SecretReadResult.NotFound());
    }

    public Task<SecretOperationResult> DeleteAsync(
        SecretKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SimulateUnavailable)
        {
            return Task.FromResult(
                SecretOperationResult.Unavailable("SecretStoreUnavailable"));
        }

        _corrupted.Remove(key);
        return Task.FromResult(
            SecretOperationResult.Success(_values.Remove(key) ? 1 : 0));
    }

    public Task<bool> ExistsAsync(
        SecretKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!SimulateUnavailable && _values.ContainsKey(key));
    }

    public Task<SecretOperationResult> DeleteAllForOwnerAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastDeleteAllOwnerId = ownerId;

        if (SimulateUnavailable)
        {
            return Task.FromResult(
                SecretOperationResult.Unavailable("SecretStoreUnavailable"));
        }

        SecretKey[] keys = _values.Keys
            .Where(key => key.OwnerId == ownerId)
            .ToArray();

        foreach (SecretKey key in keys)
        {
            _values.Remove(key);
            _corrupted.Remove(key);
        }

        return Task.FromResult(SecretOperationResult.Success(keys.Length));
    }
}
