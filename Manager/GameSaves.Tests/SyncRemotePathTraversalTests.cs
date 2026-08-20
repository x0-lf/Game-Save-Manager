using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Tests;

/// <summary>
/// A remote listing is untrusted input. A hostile or compromised SFTP server
/// controls every directory entry name it returns, and those names became local
/// paths through Path.Combine, which silently discards its first argument when
/// the second is rooted. These tests pin that a remote name can never place a
/// file outside the run folder.
/// </summary>
public sealed class SyncRemotePathTraversalTests
{
    [Theory]
    // Windows separators survive the "." and ".." filters entirely.
    [InlineData(@"..\..\evil.txt")]
    [InlineData(@"sub\..\..\evil.txt")]
    // Path.Combine discards the local root when the remote name is rooted.
    [InlineData(@"C:\Windows\Temp\evil.txt")]
    [InlineData(@"\\attacker\share\evil.txt")]
    [InlineData("/etc/passwd")]
    // Forward-slash traversal, which the engine translates to separators.
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..")]
    [InlineData(".")]
    // Empty and whitespace segments.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a//b")]
    // Windows resolves these to a different final name.
    [InlineData("evil.")]
    [InlineData("evil ")]
    [InlineData("sub/evil.")]
    // A colon opens an alternate data stream.
    [InlineData("evil.txt:stream")]
    public void UnsafeRemoteNames_AreRejected(string remoteName)
    {
        Assert.False(TransferPathGuard.IsSafeRemoteRelativePath(remoteName));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("saves/profile.sav")]
    [InlineData("a/b/c/deep.bin")]
    [InlineData("name with spaces.txt")]
    [InlineData("unicode-Ünïcödé.dat")]
    [InlineData("dotted.name.with.parts.json")]
    public void OrdinaryRemoteNames_AreAccepted(string remoteName)
    {
        Assert.True(TransferPathGuard.IsSafeRemoteRelativePath(remoteName));
    }

    [Fact]
    public void RejectedNames_WouldOtherwiseHaveEscapedTheRunFolder()
    {
        // Non-vacuity. This is the exact composition the engine performs. If
        // the guard were removed, these names would resolve outside the run
        // folder, which is what makes rejecting them meaningful.
        string runFolder = Path.Combine(Path.GetTempPath(), "gamesaves-run");

        string[] escaping =
        [
            @"..\..\evil.txt",
            @"C:\Windows\Temp\evil.txt",
            "../evil.txt"
        ];

        foreach (string name in escaping)
        {
            string composed = Path.Combine(
                runFolder,
                name.Replace('/', Path.DirectorySeparatorChar));

            Assert.False(
                TransferPathGuard.IsStrictlyUnderRoot(composed, runFolder),
                $"Expected '{name}' to escape the run folder before the guard.");
            Assert.False(TransferPathGuard.IsSafeRemoteRelativePath(name));
        }
    }

    [Fact]
    public async Task DownloadingARunWithATraversingFileName_WritesNothingOutsideTheRunFolder()
    {
        using var local = new TempDir();
        using var outside = new TempDir();

        string escapeTarget = Path.Combine(outside.Path, "victim.txt");
        await File.WriteAllTextAsync(escapeTarget, "original user data");

        // The remote offers one run whose file name climbs out of the run
        // folder and lands on an existing local file.
        string traversing = Path.Combine("..", "..", Path.GetFileName(outside.Path))
            .Replace(Path.DirectorySeparatorChar, '/') + "/victim.txt";

        var remote = new TraversingRemoteFileSystem("run-2026-08-18", traversing);
        var engine = new SyncEngine(
            remote,
            "Hostile",
            "hostile://remote",
            new TraversalBackupHistoryService(local.Path),
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions
        {
            Upload = false,
            Download = true
        });

        SyncResult result = await engine.ExecuteAsync(plan, new SyncOptions
        {
            Upload = false,
            Download = true,
            DryRun = false,
            ConfirmExecution = true
        });

        // Non-vacuity: the plan really did offer the hostile run for download,
        // so the engine reached the code path under test.
        Assert.Contains(
            plan.Items,
            item => item.Action == SyncItemAction.DownloadToLocal);

        // The engine refused the traversing entry by name, before asking the
        // remote for a single byte. Without the guard the engine calls
        // DownloadFileAsync first and fails later for a different reason, so
        // this assertion discriminates the fix from its absence.
        Assert.False(remote.DownloadFileCalled);

        SyncItemResult failure = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Failed, failure.Status);
        Assert.Equal(
            "The remote run contains a file name that is not safe to write locally.",
            failure.Error);

        // The pre-existing file outside the backup base is untouched.
        Assert.Equal("original user data", await File.ReadAllTextAsync(escapeTarget));
    }

    [Fact]
    public async Task AnUnsafeNameLateInTheListing_StillWritesNothing()
    {
        using var local = new TempDir();

        // The safe entries come first, so a per-file check would already have
        // written them before reaching the traversing one and would leave a
        // folder carrying a manifest that passes for a complete run.
        var remote = new TraversingRemoteFileSystem(
            "run-2026-08-18",
            "manifest.json",
            "saves/profile.sav",
            @"..\..\evil.txt");

        var engine = new SyncEngine(
            remote,
            "Hostile",
            "hostile://remote",
            new TraversalBackupHistoryService(local.Path),
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(
            new SyncOptions { Upload = false, Download = true });

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                Upload = false,
                Download = true,
                DryRun = false,
                ConfirmExecution = true
            });

        Assert.False(remote.DownloadFileCalled);
        Assert.Equal(SyncItemStatus.Failed, Assert.Single(result.Items).Status);

        // No run folder was created, so nothing can pass for a complete run.
        Assert.Empty(Directory.GetDirectories(local.Path));
    }

    private sealed class TraversingRemoteFileSystem : IRemoteFileSystem
    {
        private readonly string _runName;
        private readonly IReadOnlyList<string> _relatives;

        public TraversingRemoteFileSystem(string runName, params string[] relatives)
        {
            _runName = runName;
            _relatives = relatives;
        }

        public bool DownloadFileCalled { get; private set; }

        public string DisplayRoot => "hostile://remote";

        public string GetDisplayPath(string relativePath) =>
            $"hostile://remote/{relativePath}";

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferPreviewWarning?>(null);

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([_runName]);

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            if (!relativePath.EndsWith("manifest.json", StringComparison.Ordinal))
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(
                """
                {"SchemaVersion":1,"Kind":"backup","Game":"Hostile","SteamAppId":"1",
                 "SourceAccountId":"s","TargetAccountId":"t",
                 "StartedUtc":"2026-08-18T12:00:00+00:00",
                 "CompletedUtc":"2026-08-18T12:00:01+00:00",
                 "FileCount":1,"TotalBytes":4,"Items":[]}
                """);
        }

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_relatives);

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            // If the engine ever calls this, the guard failed. Record it and
            // still refuse to write, so a failing test never destroys a file.
            DownloadFileCalled = true;
            return Task.FromResult(0L);
        }
    }

    private sealed class TraversalBackupHistoryService : IBackupHistoryService
    {
        private readonly string _basePath;

        public TraversalBackupHistoryService(string basePath) => _basePath = basePath;

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>([]);

        public string GetBackupBasePath() => _basePath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gamesaves-traversal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
