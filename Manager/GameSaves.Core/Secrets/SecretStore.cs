namespace GameSaves.Core.Secrets
{
    /// <summary>
    /// Stable, non-secret identity for one protected value. OwnerId is normally
    /// a saved sync profile GUID; mutable profile names are never identities.
    /// </summary>
    public sealed record SecretKey
    {
        public const int MaximumNameLength = 64;

        public SecretKey(Guid ownerId, string name)
        {
            if (ownerId == Guid.Empty)
                throw new ArgumentException("A non-empty secret owner ID is required.", nameof(ownerId));

            OwnerId = ownerId;
            Name = NormalizeName(name);
        }

        public Guid OwnerId { get; }

        public string Name { get; }

        public static string NormalizeName(string? name)
        {
            string normalized = name?.Trim() ?? string.Empty;

            if (normalized.Length == 0)
                throw new ArgumentException("A secret name is required.", nameof(name));

            if (normalized.Length > MaximumNameLength)
            {
                throw new ArgumentException(
                    $"A secret name cannot exceed {MaximumNameLength} characters.",
                    nameof(name));
            }

            if (!normalized.All(character =>
                    character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw new ArgumentException(
                    "A secret name may contain only lowercase letters, digits, and hyphens.",
                    nameof(name));
            }

            return normalized;
        }
    }

    public static class SecretNames
    {
        public const string OAuthTokenData = "oauth-token-data";
        public const string OneDriveTokenData = "onedrive-token-data";
        public const string WebDavPassword = "webdav-password";
        public const string SftpPassword = "sftp-password";
        public const string SftpPrivateKeyPassphrase = "sftp-private-key-passphrase";

        public static IReadOnlyList<string> StoredAuthenticationNames { get; } =
            new[]
            {
                OAuthTokenData,
                OneDriveTokenData,
                WebDavPassword,
                SftpPassword,
                SftpPrivateKeyPassphrase
            };
    }

    public enum SecretReadStatus
    {
        Found = 0,
        NotFound = 1,
        Unavailable = 2,
        Corrupted = 3,
        Failed = 4
    }

    /// <summary>
    /// A secret read whose formatting deliberately excludes its payload.
    /// Value returns a fresh copy so callers cannot mutate internal storage.
    /// </summary>
    public sealed class SecretReadResult
    {
        private readonly byte[]? _value;

        private SecretReadResult(
            SecretReadStatus status,
            byte[]? value,
            string? errorCode)
        {
            Status = status;
            _value = value?.ToArray();
            ErrorCode = errorCode;
        }

        public SecretReadStatus Status { get; }

        public byte[]? Value => _value?.ToArray();

        public string? ErrorCode { get; }

        public static SecretReadResult Found(ReadOnlySpan<byte> value) =>
            new(SecretReadStatus.Found, value.ToArray(), null);

        public static SecretReadResult NotFound() =>
            new(SecretReadStatus.NotFound, null, null);

        public static SecretReadResult Unavailable(string errorCode) =>
            new(SecretReadStatus.Unavailable, null, errorCode);

        public static SecretReadResult Corrupted(string errorCode) =>
            new(SecretReadStatus.Corrupted, null, errorCode);

        public static SecretReadResult Failed(string errorCode) =>
            new(SecretReadStatus.Failed, null, errorCode);

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    public enum SecretOperationStatus
    {
        Succeeded = 0,
        Unavailable = 1,
        Failed = 2
    }

    public sealed record SecretOperationResult(
        SecretOperationStatus Status,
        int AffectedCount = 0,
        string? ErrorCode = null)
    {
        public bool Succeeded => Status == SecretOperationStatus.Succeeded;

        public static SecretOperationResult Success(int affectedCount = 0) =>
            new(SecretOperationStatus.Succeeded, affectedCount);

        public static SecretOperationResult Unavailable(string errorCode) =>
            new(SecretOperationStatus.Unavailable, ErrorCode: errorCode);

        public static SecretOperationResult Failed(string errorCode) =>
            new(SecretOperationStatus.Failed, ErrorCode: errorCode);
    }

    public interface ISecretStore
    {
        Task<SecretOperationResult> StoreAsync(
            SecretKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default);

        Task<SecretReadResult> ReadAsync(
            SecretKey key,
            CancellationToken cancellationToken = default);

        Task<SecretOperationResult> DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default);

        Task<bool> ExistsAsync(
            SecretKey key,
            CancellationToken cancellationToken = default);

        Task<SecretOperationResult> DeleteAllForOwnerAsync(
            Guid ownerId,
            CancellationToken cancellationToken = default);
    }
}
