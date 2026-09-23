using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Mega
{
    public interface IMegaApiClient
    {
        Task<MegaAuthenticationResult> LoginAsync(
            string email,
            string password,
            string? twoFactorCode = null,
            CancellationToken cancellationToken = default);

        Task<MegaQuotaInfo> GetQuotaAsync(
            MegaSessionToken session,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<MegaNode>> GetNodesAsync(
            MegaSessionToken session,
            CancellationToken cancellationToken = default);

        Task<MegaNode> CreateFolderAsync(
            MegaSessionToken session,
            string parentNodeId,
            string folderName,
            CancellationToken cancellationToken = default);

        Task<MegaNode> UploadFileChunkedAsync(
            MegaSessionToken session,
            string parentFolderNodeId,
            string fileName,
            Stream contentStream,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default);

        Task<Stream> DownloadFileAsync(
            MegaSessionToken session,
            string fileNodeId,
            CancellationToken cancellationToken = default);

        Task<string?> ReadTextFileAsync(
            MegaSessionToken session,
            string fileNodeId,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteNodeAsync(
            MegaSessionToken session,
            string nodeId,
            CancellationToken cancellationToken = default);
    }
}
