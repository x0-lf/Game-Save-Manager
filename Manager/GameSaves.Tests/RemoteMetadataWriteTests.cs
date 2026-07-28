using GameSaves.Infrastructure.Sync;
using Renci.SshNet.Common;
using System.Text;

namespace GameSaves.Tests;

public sealed class RemoteMetadataWriteTests
{
    [Fact]
    public void RemoteContract_UsesExplicitCreateAndMetadataOperations()
    {
        string[] methods = typeof(IRemoteFileSystem)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("CreateTextFileIfMissingAsync", methods);
        Assert.Contains("ReadProviderMetadataAsync", methods);
        Assert.Contains("ReplaceProviderMetadataAsync", methods);
        const string removedMethod = "WriteTextFile" + "Async";
        Assert.DoesNotContain(removedMethod, methods);

        string infrastructure = Path.Combine(
            FindManagerDirectory(),
            "GameSaves.Infrastructure",
            "Sync");
        string source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(infrastructure, "*.cs")
                .Select(File.ReadAllText));
        Assert.DoesNotContain(removedMethod, source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalCreateOnly_CreatesParentsAndRefusesOverwrite()
    {
        using var temp = new TemporaryDirectory();
        string remoteRoot = temp.GetPath("remote");
        var remote = new LocalFolderRemoteFileSystem(
            remoteRoot,
            temp.GetPath("local-backups"));
        const string relativePath = "run-one/nested/manifest.json";

        await remote.CreateTextFileIfMissingAsync(relativePath, "original");
        string path = Path.Combine(remoteRoot, "run-one", "nested", "manifest.json");
        byte[] originalBytes = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<IOException>(() =>
            remote.CreateTextFileIfMissingAsync(relativePath, "replacement"));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LocalMetadata_ReadsMissingAndReplacesThroughTemporarySibling()
    {
        using var temp = new TemporaryDirectory();
        string remoteRoot = temp.GetPath("remote");
        var remote = new LocalFolderRemoteFileSystem(
            remoteRoot,
            temp.GetPath("local-backups"));

        Assert.Null(await remote.ReadProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog));
        Assert.False(Directory.Exists(remoteRoot));

        await remote.ReplaceProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog,
            "first");
        Assert.Equal("first", await remote.ReadProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog));

        await remote.ReplaceProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog,
            "second");
        Assert.Equal("second", await remote.ReadProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog));

        string metadataDirectory = Path.Combine(remoteRoot, ".gamesave-sync");
        Assert.Empty(Directory.EnumerateFiles(
            metadataDirectory,
            "*.tmp-*",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../.gamesave-sync/sync-log.json")]
    [InlineData(".gamesave-sync/../sync-log.json")]
    [InlineData(".gamesave-sync//sync-log.json")]
    [InlineData("run-one/manifest.json")]
    [InlineData(".gamesave-sync/manifest.json")]
    [InlineData(".gamesave-sync\\..\\run-one\\manifest.json")]
    [InlineData("C:/temp/.gamesave-sync/sync-log.json")]
    [InlineData("/.gamesave-sync/sync-log.json")]
    public async Task MetadataOperations_RejectPathsOutsideExactAllowlist(string path)
    {
        using var temp = new TemporaryDirectory();
        var remote = new LocalFolderRemoteFileSystem(
            temp.GetPath("remote"),
            temp.GetPath("local-backups"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.ReadProviderMetadataAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.ReplaceProviderMetadataAsync(path, "content"));
    }

    [Fact]
    public async Task LocalCancelledMetadataReplacement_LeavesNoTemporaryFile()
    {
        using var temp = new TemporaryDirectory();
        string remoteRoot = temp.GetPath("remote");
        var remote = new LocalFolderRemoteFileSystem(
            remoteRoot,
            temp.GetPath("local-backups"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            remote.ReplaceProviderMetadataAsync(
                RemoteProviderMetadataPath.SyncLog,
                "content",
                cancellation.Token));

        Assert.False(Directory.Exists(remoteRoot));
    }

    [Fact]
    public void SftpCreateOnly_UsesExclusiveCreateAndPreservesExistingContent()
    {
        var client = new FakeSftpTextFileClient();
        const string path = "/remote/run-one/manifest.json";
        client.Files[path] = "original";
        var operations = new SftpTextFileOperations(client);

        Assert.Throws<IOException>(() =>
            operations.CreateTextFileIfMissing(path, "replacement", CancellationToken.None));

        Assert.Equal("original", client.Files[path]);
        Assert.Empty(client.DirectWrites);
        Assert.Contains((path, FileMode.CreateNew), client.OpenCalls);
    }

    [Fact]
    public void SftpCreateOnly_CreatesMissingTextWithoutOverwriteApi()
    {
        var client = new FakeSftpTextFileClient();
        const string path = "/remote/run-one/manifest.json";
        var operations = new SftpTextFileOperations(client);

        operations.CreateTextFileIfMissing(path, "manifest", CancellationToken.None);

        Assert.Equal("manifest", client.Files[path]);
        Assert.Empty(client.DirectWrites);
        Assert.Contains((path, FileMode.CreateNew), client.OpenCalls);
    }

    [Fact]
    public void SftpMetadata_ReadsMissingAndUsesPosixReplacement()
    {
        var client = new FakeSftpTextFileClient();
        const string path = "/remote/.gamesave-sync/sync-log.json";
        var operations = new SftpTextFileOperations(client);

        Assert.Null(operations.ReadProviderMetadata(path, CancellationToken.None));
        operations.ReplaceProviderMetadata(path, "first", CancellationToken.None);
        operations.ReplaceProviderMetadata(path, "second", CancellationToken.None);

        Assert.Equal("second", operations.ReadProviderMetadata(path, CancellationToken.None));
        Assert.Equal(2, client.PosixRenameCalls);
        Assert.Empty(client.DirectWrites);
        Assert.DoesNotContain(client.Files.Keys,
            key => key.Contains(".tmp-", StringComparison.Ordinal));
        Assert.All(client.DeletedPaths,
            deleted => Assert.Contains("/.gamesave-sync/", deleted,
                StringComparison.Ordinal));
    }

    [Fact]
    public void SftpMetadata_FallsBackOnlyToDirectMetadataReplacement()
    {
        var client = new FakeSftpTextFileClient { FailPosixRename = true };
        const string path = "/remote/.gamesave-sync/sync-log.json";
        client.Files[path] = "old";
        var operations = new SftpTextFileOperations(client);

        operations.ReplaceProviderMetadata(path, "new", CancellationToken.None);

        Assert.Equal("new", client.Files[path]);
        Assert.Equal(new[] { path }, client.DirectWrites);
        Assert.DoesNotContain(client.Files.Keys,
            key => key.Contains(".tmp-", StringComparison.Ordinal));
        Assert.All(client.DeletedPaths,
            deleted => Assert.Contains("/.gamesave-sync/", deleted,
                StringComparison.Ordinal));
    }

    [Fact]
    public void SftpMetadata_RenameAndFallbackFailureCleansOnlyTemporaryMetadata()
    {
        var client = new FakeSftpTextFileClient
        {
            FailPosixRename = true,
            FailDirectWrite = true
        };
        const string path = "/remote/.gamesave-sync/sync-log.json";
        client.Files[path] = "old";
        var operations = new SftpTextFileOperations(client);

        Assert.Throws<IOException>(() =>
            operations.ReplaceProviderMetadata(path, "new", CancellationToken.None));

        Assert.Equal("old", client.Files[path]);
        Assert.DoesNotContain(client.DeletedPaths, deleted => deleted == path);
        Assert.Single(client.DeletedPaths);
        Assert.Contains("/.gamesave-sync/", client.DeletedPaths[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public void SftpMetadata_RespectsCancellationBeforeRemoteMutation()
    {
        var client = new FakeSftpTextFileClient();
        var operations = new SftpTextFileOperations(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            operations.ReplaceProviderMetadata(
                "/remote/.gamesave-sync/sync-log.json",
                "new",
                cancellation.Token));

        Assert.Empty(client.Files);
        Assert.Empty(client.DeletedPaths);
    }

    private sealed class FakeSftpTextFileClient : ISftpTextFileClient
    {
        public Dictionary<string, string> Files { get; } =
            new(StringComparer.Ordinal);
        public List<(string Path, FileMode Mode)> OpenCalls { get; } = new();
        public List<string> DirectWrites { get; } = new();
        public List<string> DeletedPaths { get; } = new();
        public bool FailPosixRename { get; set; }
        public bool FailDirectWrite { get; set; }
        public int PosixRenameCalls { get; private set; }

        public bool Exists(string path) => Files.ContainsKey(path);

        public Stream Open(string path, FileMode mode, FileAccess access)
        {
            OpenCalls.Add((path, mode));

            if (mode == FileMode.CreateNew && Files.ContainsKey(path))
                throw new IOException("The remote file already exists.");

            return new CommitMemoryStream(content => Files[path] = content);
        }

        public string ReadAllText(string path) => Files[path];

        public void WriteAllText(string path, string content)
        {
            DirectWrites.Add(path);

            if (FailDirectWrite)
                throw new IOException("Deterministic direct-write failure.");

            Files[path] = content;
        }

        public void RenameFile(string oldPath, string newPath, bool isPosix)
        {
            Assert.True(isPosix);
            PosixRenameCalls++;

            if (FailPosixRename)
                throw new SshException("POSIX rename is unavailable in this test.");

            Files[newPath] = Files[oldPath];
            Files.Remove(oldPath);
        }

        public void DeleteFile(string path)
        {
            DeletedPaths.Add(path);
            Files.Remove(path);
        }
    }

    private sealed class CommitMemoryStream : MemoryStream
    {
        private readonly Action<string> _commit;
        private bool _committed;

        public CommitMemoryStream(Action<string> commit) => _commit = commit;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                _commit(Encoding.UTF8.GetString(ToArray()));
            }

            base.Dispose(disposing);
        }
    }

    private static string FindManagerDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}
