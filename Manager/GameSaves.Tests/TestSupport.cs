using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "GameSaves.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(params string[] segments)
    {
        return segments.Aggregate(Path, System.IO.Path.Combine);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // A failed test cleanup must not hide the assertion that failed.
        }
    }
}

internal sealed class TestDatabasePathProvider : IAppDatabasePathProvider
{
    private readonly string _databasePath;

    public TestDatabasePathProvider(string databasePath)
    {
        _databasePath = databasePath;
    }

    public string GetDatabasePath() => _databasePath;
}

internal sealed class RecordingHistoryRepository : ITransferHistoryRepository
{
    public List<TransferRunRecord> Records { get; } = new();

    public long RecordRun(TransferRunRecord record)
    {
        Records.Add(record);
        return Records.Count;
    }

    public IReadOnlyList<TransferRunInfo> GetRecentRuns(int limit) => Array.Empty<TransferRunInfo>();

    public IReadOnlyList<TransferRunItemRecord> GetRunItems(long runId) =>
        Records.ElementAtOrDefault((int)runId - 1)?.Items ?? Array.Empty<TransferRunItemRecord>();

    public int CountRuns() => Records.Count;
}

internal sealed class ThrowingHistoryRepository : ITransferHistoryRepository
{
    public long RecordRun(TransferRunRecord record) => throw new IOException("History unavailable.");
    public IReadOnlyList<TransferRunInfo> GetRecentRuns(int limit) => throw new IOException();
    public IReadOnlyList<TransferRunItemRecord> GetRunItems(long runId) => throw new IOException();
    public int CountRuns() => throw new IOException();
}

internal sealed class EmptyMappingRepository : ISavePathMappingRepository
{
    public IReadOnlyList<SavePathMapping> GetApprovedMappingsForApp(string steamAppId, string platform) =>
        Array.Empty<SavePathMapping>();

    public IReadOnlyList<SavePathMapping> GetMappingsForApp(
        string steamAppId,
        string platform,
        bool includeDisabled) => Array.Empty<SavePathMapping>();

    public IReadOnlyDictionary<string, SavePathMappingStatus> GetMappingStatusesForApps(
        IEnumerable<string> steamAppIds,
        string platform) => new Dictionary<string, SavePathMappingStatus>();

    public int CountApprovedMappings(string platform) => 0;
    public int CountNeedsFixMappings(string platform) => 0;
    public int CountPendingMappings(string platform) => 0;
}

internal sealed class EmptySteamDiscoveryService : ISteamDiscoveryService
{
    public SteamDiscoveryResult Discover(
        SteamDiscoveryOptions? options = null,
        IProgress<SteamFallbackScanProgress>? fallbackProgress = null,
        CancellationToken cancellationToken = default) => new();
}

internal sealed class WindowsPlatformProvider : ICurrentPlatformProvider
{
    public string GetCurrentPlatformKey() => "windows";
}

internal static class TestData
{
    public static TransferBackupRunInfo CreateBackupRun(
        string runRoot,
        string originalFile,
        string content,
        DateTimeOffset? startedUtc = null)
    {
        string backupFile = System.IO.Path.Combine(runRoot, "files", "payload.sav");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, content);

        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupFile)));
        DateTimeOffset started = startedUtc ?? DateTimeOffset.UtcNow;

        var item = new TransferOverwriteBackupItem(
            originalFile,
            backupFile,
            new FileInfo(backupFile).Length,
            hash,
            started);

        var manifest = new TransferBackupManifest(
            SchemaVersion: 1,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Test Game",
            SteamAppId: "1234",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: started,
            CompletedUtc: started.AddSeconds(1),
            FileCount: 1,
            TotalBytes: item.Bytes,
            Items: new[] { item });

        string manifestPath = System.IO.Path.Combine(runRoot, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return new TransferBackupRunInfo(runRoot, manifestPath, manifest);
    }

    public static TransferPreviewPlan CreateTransferPlan(string sourceFile, string targetFile)
    {
        var game = new SteamGame(
            "1234", "Test Game", "TestGame", "", "", "", true,
            SteamDiscoveryConfidence.High);

        var sourceProfile = new SteamProfile("source", null, "Source", "source-root", 1, false);
        var targetProfile = new SteamProfile("target", null, "Target", "target-root", 1, false);

        var item = new TransferPreviewItem(
            SourceType: TransferSourceType.ApprovedMapping,
            MappingId: 1,
            MappingTemplate: sourceFile,
            SteamAppId: game.AppId,
            GameName: game.Name,
            SourceRoot: sourceFile,
            TargetRoot: targetFile,
            SourcePath: sourceFile,
            TargetPath: targetFile,
            CopyScope: TransferCopyScope.SingleFile,
            SourceExists: true,
            TargetExists: File.Exists(targetFile),
            FileCount: 1,
            TotalBytes: new FileInfo(sourceFile).Length,
            ConflictStatus: File.Exists(targetFile)
                ? TransferConflictStatus.TargetExists
                : TransferConflictStatus.None,
            StatusText: "Ready",
            ActionText: "Copy");

        return new TransferPreviewPlan(
            game,
            sourceProfile,
            targetProfile,
            new[] { item },
            Array.Empty<TransferPreviewWarning>(),
            CanExecute: true,
            TotalFiles: 1,
            TotalBytes: item.TotalBytes);
    }
}
