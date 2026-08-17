using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Shared offline fakes for the Milestone T Google Drive provider boundary.
/// They never authenticate, reach Drive, or touch the local filesystem.
/// </summary>
internal sealed class RecordingProviderRemoteFileSystem : IRemoteFileSystem
{
    public const string DefaultDisplayRoot = "GameSave Manager Backups";

    public List<string> Calls { get; } = new();

    public string? ProviderMetadata { get; set; }

    public string DisplayRoot { get; init; } = DefaultDisplayRoot;

    public string GetDisplayPath(string relativePath) =>
        $"{DisplayRoot}/{relativePath}";

    public Task<TransferPreviewWarning?> ValidateAsync(
        CancellationToken cancellationToken = default) =>
        Record<TransferPreviewWarning?>(null, cancellationToken);

    public Task<bool> RootExistsAsync(
        CancellationToken cancellationToken = default) =>
        Record(true, cancellationToken);

    public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
        CancellationToken cancellationToken = default) =>
        Record<IReadOnlyList<string>>([], cancellationToken);

    public Task<bool> FolderExistsAsync(
        string relativeFolder,
        CancellationToken cancellationToken = default) =>
        Record(false, cancellationToken);

    public Task<string?> ReadTextFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        Record<string?>(null, cancellationToken);

    public Task CreateTextFileIfMissingAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default) =>
        Record(true, cancellationToken);

    public Task<string?> ReadProviderMetadataAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        Record(ProviderMetadata, cancellationToken);

    public Task ReplaceProviderMetadataAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default) =>
        Record(true, cancellationToken);

    public Task<IReadOnlyList<string>> ListFilesAsync(
        string relativeFolder,
        CancellationToken cancellationToken = default) =>
        Record<IReadOnlyList<string>>([], cancellationToken);

    public Task<long> UploadFileAsync(
        string localFilePath,
        string relativeRemotePath,
        CancellationToken cancellationToken = default) =>
        Record(0L, cancellationToken);

    public Task<long> DownloadFileAsync(
        string relativeRemotePath,
        string localFilePath,
        CancellationToken cancellationToken = default) =>
        Record(0L, cancellationToken);

    private Task<T> Record<T>(
        T value,
        CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Calls.Add(caller);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value);
    }
}

internal sealed class RecordingRemoteFileSystemFactory
    : IGoogleDriveRemoteFileSystemFactory
{
    public RecordingProviderRemoteFileSystem FileSystem { get; } = new();

    public List<Guid> RequestedProfileIds { get; } = new();

    public IRemoteFileSystem Create(Guid remoteProfileId)
    {
        RequestedProfileIds.Add(remoteProfileId);
        return FileSystem;
    }
}

internal sealed class EmptyBackupHistoryService : IBackupHistoryService
{
    public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>([]);
    }

    public string GetBackupBasePath() =>
        Path.Combine(Path.GetTempPath(), "gamesaves-provider-tests");
}
