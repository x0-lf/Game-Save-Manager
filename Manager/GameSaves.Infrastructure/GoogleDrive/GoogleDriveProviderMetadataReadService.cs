using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveProviderMetadataReadService
    {
        Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Enforces the mutable-provider-metadata path allowlist before opening an
    /// authenticated Google Drive operation. The underlying text reader keeps
    /// resolution and download read-only and bounded.
    /// </summary>
    internal sealed class GoogleDriveProviderMetadataReadService
        : IGoogleDriveProviderMetadataReadService
    {
        private readonly IGoogleDriveTextFileReadService _textFileReadService;

        public GoogleDriveProviderMetadataReadService(
            IGoogleDriveTextFileReadService textFileReadService) =>
            _textFileReadService = textFileReadService ??
                throw new ArgumentNullException(nameof(textFileReadService));

        public Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            string validatedPath = RemoteProviderMetadataPath.Validate(relativePath);

            return _textFileReadService.ReadAsync(
                remoteProfileId,
                validatedPath,
                cancellationToken);
        }
    }
}
