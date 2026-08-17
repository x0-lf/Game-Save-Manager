using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using System.Reflection;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class GoogleDriveSyncProviderTests
{
    private const string DisplayRoot =
        RecordingProviderRemoteFileSystem.DefaultDisplayRoot;

    [Fact]
    public void Provider_UsesTheFixedNameAndTheSanitizedDisplayRoot()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        using ISyncProvider provider = Provider(remote);

        Assert.Equal("Google Drive", provider.ProviderName);
        Assert.Equal(DisplayRoot, provider.RemoteRoot);
    }

    [Fact]
    public void Construction_IssuesNoRemoteCall()
    {
        var remote = new RecordingProviderRemoteFileSystem();

        using ISyncProvider provider = Provider(remote);

        Assert.Empty(remote.Calls);
    }

    [Fact]
    public async Task CreatePreviewAsync_RunsThroughTheSharedEngine()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        using ISyncProvider provider = Provider(remote);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());

        Assert.Equal("Google Drive", plan.ProviderName);
        Assert.Equal(DisplayRoot, plan.RemoteRoot);
        Assert.Contains(nameof(IRemoteFileSystem.ValidateAsync), remote.Calls);
        Assert.Contains(
            nameof(IRemoteFileSystem.ListRunFolderNamesAsync),
            remote.Calls);
        Assert.Contains(nameof(IRemoteFileSystem.RootExistsAsync), remote.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_RunsThroughTheSharedEngineAndItsHistory()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        var historyRepository = new RecordingHistoryRepository();
        using ISyncProvider provider = Provider(remote, historyRepository);
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        remote.Calls.Clear();

        SyncResult result = await provider.ExecuteAsync(plan, new SyncOptions());

        Assert.Contains(nameof(IRemoteFileSystem.ValidateAsync), remote.Calls);
        TransferRunRecord record = Assert.Single(historyRepository.Records);
        Assert.Equal(TransferRunKind.Sync, record.Kind);
        Assert.Equal(DisplayRoot, record.TargetAccountId);
        Assert.Equal(0, result.Uploaded + result.Downloaded);
    }

    [Fact]
    public async Task GetSyncLogAsync_ReturnsWhatTheSharedEngineParses()
    {
        var entry = new SyncLogEntry(
            DeviceName: "device",
            TimestampUtc: DateTimeOffset.Parse("2026-08-17T09:00:00Z"),
            Uploaded: 2,
            Downloaded: 1,
            Conflicts: 0,
            BytesCopied: 4096,
            UploadedRuns: ["run-a", "run-b"],
            DownloadedRuns: ["run-c"]);
        var remote = new RecordingProviderRemoteFileSystem
        {
            ProviderMetadata = JsonSerializer.Serialize(new[] { entry })
        };
        using ISyncProvider provider = Provider(remote);

        IReadOnlyList<SyncLogEntry> log = await provider.GetSyncLogAsync();

        SyncLogEntry parsed = Assert.Single(log);
        Assert.Equal(entry.DeviceName, parsed.DeviceName);
        Assert.Equal(entry.TimestampUtc, parsed.TimestampUtc);
        Assert.Equal(entry.Uploaded, parsed.Uploaded);
        Assert.Equal(entry.Downloaded, parsed.Downloaded);
        Assert.Equal(entry.BytesCopied, parsed.BytesCopied);
        Assert.Equal(entry.UploadedRuns, parsed.UploadedRuns);
        Assert.Equal(entry.DownloadedRuns, parsed.DownloadedRuns);
        Assert.Contains(
            nameof(IRemoteFileSystem.ReadProviderMetadataAsync),
            remote.Calls);
    }

    [Fact]
    public async Task EveryOperation_ForwardsTheCallerToken()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        using ISyncProvider provider = Provider(remote);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CreatePreviewAsync(new SyncOptions(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetSyncLogAsync(cancellation.Token));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        ISyncProvider provider = Provider(remote);

        provider.Dispose();
        provider.Dispose();
        provider.Dispose();

        Assert.Empty(remote.Calls);
    }

    [Fact]
    public async Task DisposedProvider_RefusesEveryOperationWithoutRemoteWork()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        ISyncProvider provider = Provider(remote);
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        provider.Dispose();
        remote.Calls.Clear();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.CreatePreviewAsync(new SyncOptions()));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.ExecuteAsync(plan, new SyncOptions()));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.GetSyncLogAsync());

        Assert.Empty(remote.Calls);
    }

    [Fact]
    public async Task DisposedProvider_RefusesBeforeCheckingCancellation()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        ISyncProvider provider = Provider(remote);
        provider.Dispose();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.GetSyncLogAsync(cancellation.Token));

        Assert.Empty(remote.Calls);
    }

    [Fact]
    public void DriveFileSystem_StillHoldsNothingThatNeedsReleasing()
    {
        // Disposal is a no-op only because the Drive boundary owns no
        // connection. If that ever changes, this fails and the provider must
        // release it exactly once.
        Assert.False(
            typeof(GoogleDriveRemoteFileSystem).IsAssignableTo(typeof(IDisposable)));
        Assert.False(
            typeof(GoogleDriveRemoteFileSystem).IsAssignableTo(
                typeof(IAsyncDisposable)));
    }

    [Fact]
    public void Wrapper_HoldsNothingButTheSharedEngine()
    {
        FieldInfo[] fields = typeof(GoogleDriveSyncProvider).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.Equal(
            new[] { typeof(bool), typeof(string), typeof(SyncEngine) },
            fields.Select(field => field.FieldType)
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Wrapper_DeclaresNoOperationBeyondTheProviderContract()
    {
        string[] declared = typeof(GoogleDriveSyncProvider)
            .GetMethods(BindingFlags.Instance | BindingFlags.DeclaredOnly |
                        BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ISyncProvider.CreatePreviewAsync),
                nameof(IDisposable.Dispose),
                nameof(ISyncProvider.ExecuteAsync),
                nameof(ISyncProvider.GetSyncLogAsync)
            },
            declared);
    }

    private static ISyncProvider Provider(
        IRemoteFileSystem remote,
        ITransferHistoryRepository? historyRepository = null) =>
        new GoogleDriveSyncProvider(
            remote,
            new EmptyBackupHistoryService(),
            historyRepository ?? new RecordingHistoryRepository());
}
