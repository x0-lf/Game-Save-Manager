using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    internal sealed record OneDriveTokenResponse(
        string AccessToken,
        string? RefreshToken,
        int ExpiresInSeconds,
        string TokenType,
        string? Scope)
    {
        // The generated record ToString would print both tokens.
        public override string ToString() => nameof(OneDriveTokenResponse);
    }

    internal sealed record OneDriveItemInfo(
        string Name,
        bool IsFolder,
        bool IsFile);

    internal sealed record OneDriveAccountInfo(
        string DisplayName,
        string EmailOrUpn);

    /// <summary>
    /// A failed Microsoft Graph or token-endpoint request reduced to its status
    /// category. It never carries a response body, remote path, or token, so its
    /// message is safe for status text and sync history.
    /// </summary>
    internal sealed class OneDriveApiException : Exception, IRetryDelayCarrier
    {
        public OneDriveApiException(
            int? statusCode,
            TimeSpan? retryAfterDelay = null,
            string? oauthError = null,
            Exception? innerException = null)
            : base(
                statusCode is { } code
                    ? $"Microsoft OneDrive request failed (HTTP {code})."
                    : "Microsoft OneDrive could not be reached (network error or timeout).",
                innerException)
        {
            StatusCode = statusCode;
            RetryAfterDelay = retryAfterDelay;
            OAuthError = oauthError;
        }

        /// <summary>The HTTP status, or null when no response arrived.</summary>
        public int? StatusCode { get; }

        public TimeSpan? RetryAfterDelay { get; }

        /// <summary>The token endpoint's OAuth error code, such as <c>invalid_grant</c>.</summary>
        public string? OAuthError { get; }
    }

    /// <summary>
    /// Contract for Microsoft Graph and identity endpoint communication.
    /// Enables 100% offline, deterministic testing using mock or in-memory handlers.
    /// </summary>
    internal interface IOneDriveApiClient
    {
        Task<OneDriveTokenResponse> ExchangeCodeForTokenAsync(
            string clientId,
            string code,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken = default);

        Task<OneDriveTokenResponse> RefreshTokenAsync(
            string clientId,
            string refreshToken,
            CancellationToken cancellationToken = default);

        Task<OneDriveAccountInfo> GetAccountInfoAsync(
            string accessToken,
            CancellationToken cancellationToken = default);

        Task<OneDriveQuotaInfo> GetQuotaAsync(
            string accessToken,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<OneDriveItemInfo>> ListChildrenAsync(
            string accessToken,
            string pathUnderAppRoot = "",
            CancellationToken cancellationToken = default);

        /// <summary>The item at a path under the app folder (the app folder itself for ""); null when absent.</summary>
        Task<OneDriveItemInfo?> GetItemAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default);

        Task<string?> ReadTextAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads a seekable stream. With <paramref name="createOnly"/> an existing
        /// file is never replaced: the upload is refused with an InvalidOperationException.
        /// </summary>
        Task UploadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream contentStream,
            bool createOnly,
            CancellationToken cancellationToken = default);

        Task DownloadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream destinationStream,
            CancellationToken cancellationToken = default);
    }
}
