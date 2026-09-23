using System;

namespace GameSaves.Core.Sync
{
    public enum MegaAuthenticationStatus
    {
        Connected = 0,
        NoStoredAuthentication = 1,
        TwoFactorRequired = 2,
        InvalidCredentials = 3,
        InvalidTwoFactorCode = 4,
        SessionExpired = 5,
        AccountBlocked = 6,
        SecretStoreUnavailable = 7,
        StorageFailure = 8,
        NetworkFailed = 9,
        Failed = 10
    }

    public enum MegaDisconnectionStatus
    {
        Disconnected = 0,
        AlreadyDisconnected = 1,
        SecretStoreUnavailable = 2,
        Failed = 3
    }

    public enum MegaNodeType
    {
        File = 0,
        Folder = 1,
        Root = 2,
        Inbox = 3,
        Trash = 4
    }

    public static class MegaErrorCodes
    {
        public const string Success = "MegaSuccess";
        public const string InvalidArguments = "MegaInvalidArguments";
        public const string InvalidCredentials = "MegaInvalidCredentials";
        public const string TwoFactorRequired = "MegaTwoFactorRequired";
        public const string InvalidTwoFactorCode = "MegaInvalidTwoFactorCode";
        public const string SessionExpired = "MegaSessionExpired";
        public const string OverQuota = "MegaOverQuota";
        public const string NodeNotFound = "MegaNodeNotFound";
        public const string AlreadyExists = "MegaNodeAlreadyExists";
        public const string SecretStoreUnavailable = "MegaSecretStoreUnavailable";
        public const string NetworkFailed = "MegaNetworkFailed";
        public const string Failed = "MegaFailed";
    }

    public sealed record MegaTwoFactorChallenge(bool IsRequired, string? ChallengeToken = null);

    /// <summary>
    /// Represents an active authenticated MEGA session and its decrypted master key.
    /// Crucially, MasterKey and the full SessionId are never revealed in ToString() or logging.
    /// </summary>
    public sealed record MegaSessionToken
    {
        public MegaSessionToken(string sessionId, byte[] masterKey, string userEmail, DateTimeOffset createdUtc)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
            if (masterKey is null || masterKey.Length == 0)
                throw new ArgumentException("Master key cannot be empty.", nameof(masterKey));

            SessionId = sessionId;
            MasterKey = (byte[])masterKey.Clone();
            UserEmail = userEmail?.Trim() ?? string.Empty;
            CreatedUtc = createdUtc;
        }

        public string SessionId { get; }

        public byte[] MasterKey { get; }

        public string UserEmail { get; }

        public DateTimeOffset CreatedUtc { get; }

        public override string ToString() =>
            $"MegaSessionToken {{ UserEmail = '{UserEmail}', SessionId = '***', CreatedUtc = {CreatedUtc:O} }}";
    }

    public sealed record MegaAuthenticationResult(
        MegaAuthenticationStatus Status,
        string? UserEmail,
        string? SessionId,
        MegaTwoFactorChallenge? TwoFactorChallenge = null,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool Succeeded => Status == MegaAuthenticationStatus.Connected;

        public bool RequiresTwoFactor => Status == MegaAuthenticationStatus.TwoFactorRequired;

        public static MegaAuthenticationResult Success(string email, string sessionId) =>
            new(MegaAuthenticationStatus.Connected, email, sessionId, null, MegaErrorCodes.Success, "Authentication successful.");

        public static MegaAuthenticationResult TwoFactorNeeded(string email, string? challengeToken = null) =>
            new(MegaAuthenticationStatus.TwoFactorRequired, email, null, new MegaTwoFactorChallenge(true, challengeToken), MegaErrorCodes.TwoFactorRequired, "Two-factor authentication code required.");

        public static MegaAuthenticationResult Failure(MegaAuthenticationStatus status, string errorCode, string message, string? email = null) =>
            new(status, email, null, null, errorCode, message);

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode}): {Message}";
    }

    public sealed record MegaQuotaInfo(long TotalBytes, long UsedBytes, long RemainingBytes)
    {
        public double UsagePercentage =>
            TotalBytes > 0 ? Math.Min(100.0, Math.Round((double)UsedBytes / TotalBytes * 100.0, 1)) : 0.0;

        public bool IsLowStorage =>
            TotalBytes > 0 && RemainingBytes < TotalBytes * 0.10; // Less than 10% remaining

        public string FormattedTotal => FormatBytes(TotalBytes);

        public string FormattedUsed => FormatBytes(UsedBytes);

        public string FormattedRemaining => FormatBytes(RemainingBytes);

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dblBytes = bytes;
            while (dblBytes >= 1024.0 && i < suffixes.Length - 1)
            {
                dblBytes /= 1024.0;
                i++;
            }
            return $"{dblBytes:0.##} {suffixes[i]}";
        }

        public override string ToString() =>
            $"{FormattedUsed} used of {FormattedTotal} ({FormattedRemaining} remaining, {UsagePercentage}%)";
    }

    public sealed record MegaDisconnectionResult(
        MegaDisconnectionStatus Status,
        bool LocalSessionRemoved,
        string? Message = null)
    {
        public bool Succeeded =>
            Status is MegaDisconnectionStatus.Disconnected or MegaDisconnectionStatus.AlreadyDisconnected;
    }

    public sealed record MegaNode(
        string Id,
        string? ParentId,
        MegaNodeType Type,
        string Name,
        long Size,
        DateTimeOffset ModificationTimeUtc);
}
