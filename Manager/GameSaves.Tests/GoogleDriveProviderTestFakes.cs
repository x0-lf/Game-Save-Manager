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

    /// <summary>Failures to throw, keyed by the member that should fail.</summary>
    public Dictionary<string, Exception> Failures { get; } =
        new(StringComparer.Ordinal);

    /// <summary>The token each recorded call received, in call order.</summary>
    public List<CancellationToken> Tokens { get; } = new();

    /// <summary>Runs before a member records, so a test can cancel mid-call.</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>Content returned by every immutable text read.</summary>
    public string? TextFileContent { get; init; }

    public string DisplayRoot { get; init; } = DefaultDisplayRoot;

    public string GetDisplayPath(string relativePath) =>
        $"{DisplayRoot}/{relativePath}";

    public Task<TransferPreviewWarning?> ValidateAsync(
        CancellationToken cancellationToken = default) =>
        Record<TransferPreviewWarning?>(null, cancellationToken);

    public Task<bool> RootExistsAsync(
        CancellationToken cancellationToken = default) =>
        Record(true, cancellationToken);

    public IReadOnlyList<string> RunFolderNames { get; init; } = [];

    public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
        CancellationToken cancellationToken = default) =>
        Record(RunFolderNames, cancellationToken);

    public Task<bool> FolderExistsAsync(
        string relativeFolder,
        CancellationToken cancellationToken = default) =>
        Record(false, cancellationToken);

    public Task<string?> ReadTextFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        Record(TextFileContent, cancellationToken);

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
        OnCall?.Invoke(caller);
        Calls.Add(caller);
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failures.TryGetValue(caller, out Exception? failure))
            throw failure;

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
