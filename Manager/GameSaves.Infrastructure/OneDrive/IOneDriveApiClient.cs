using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    public sealed record OneDriveTokenResponse(
        string AccessToken,
        string? RefreshToken,
        int ExpiresInSeconds,
        string TokenType,
        string? Scope,
        DateTimeOffset ExpiresAtUtc);

    public sealed record OneDriveItemInfo(
        string Id,
        string Name,
        long Size,
        bool IsFolder,
        bool IsFile,
        string RelativePath,
        DateTimeOffset? LastModifiedUtc = null,
        string? ETag = null,
        string? QuickXorHash = null,
        string? Sha1Hash = null);

    public sealed record OneDriveAccountInfo(
        string DisplayName,
        string EmailOrUpn,
        string Id);

    /// <summary>
    /// Contract for Microsoft Graph and identity endpoint communication.
    /// Enables 100% offline, deterministic testing using mock or in-memory handlers.
    /// </summary>
    public interface IOneDriveApiClient
    {
        Task<OneDriveTokenResponse> ExchangeCodeForTokenAsync(
            string code,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken = default);

        Task<OneDriveTokenResponse> RefreshTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken = default);

        Task<OneDriveAccountInfo> GetAccountInfoAsync(
            string accessToken,
            CancellationToken cancellationToken = default);

        Task<OneDriveQuotaInfo> GetQuotaAsync(
            string accessToken,
            CancellationToken cancellationToken = default);

        Task<OneDriveItemInfo?> GetAppRootAsync(
            string accessToken,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<OneDriveItemInfo>> ListChildrenAsync(
            string accessToken,
            string pathUnderAppRoot = "",
            CancellationToken cancellationToken = default);

        Task<OneDriveItemInfo?> GetItemAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default);

        Task<string?> ReadTextAsync(
            string accessToken,
            string pathUnderAppRoot,
            CancellationToken cancellationToken = default);

        Task<OneDriveItemInfo> UploadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream contentStream,
            CancellationToken cancellationToken = default);

        Task DownloadContentAsync(
            string accessToken,
            string pathUnderAppRoot,
            Stream destinationStream,
            CancellationToken cancellationToken = default);
    }
}
