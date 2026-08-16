// TEMPORARY MILESTONE R TASK 20c LIVE ACCEPTANCE HARNESS.
//
// Delete this file after the live run. It must never be committed.
//
// It is inert unless GAMESAVES_R20_LIVE is set to 1, so the ordinary hermetic
// suite is unaffected. When enabled it talks to a real, explicitly authorized
// development Google account through the existing create-only upload path.
//
// Required local environment values, read from the process scope first and
// then the Windows user scope. They are private acceptance inputs: the harness
// never prints, serializes, or asserts on their values.
//
//   GAMESAVES_R20_LIVE          1 to enable
//   GAMESAVES_R20_RUN_FOLDER    controlled run-folder name below the app root
//   GAMESAVES_R20_PROFILE_ID    optional; only needed when more than one saved
//                               Google Drive profile exists
//
// Run with:
//   dotnet test Manager/GameSaves.Tests --filter FullyQualifiedName~R20Live ^
//     -p:UsedAvaloniaProducts= --logger "console;verbosity=detailed"

using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GameSaves.Tests;

public sealed class R20LiveAcceptanceHarness(ITestOutputHelper output)
{
    private const int LargeUploadBytes = (5 * 1024 * 1024) + 4096;

    [Fact]
    public async Task RunLiveUploadAcceptance()
    {
        if (Value("GAMESAVES_R20_LIVE") != "1")
        {
            output.WriteLine("R20_SKIPPED: live acceptance not enabled.");
            return;
        }

        string? runFolder = Value("GAMESAVES_R20_RUN_FOLDER");
        if (string.IsNullOrWhiteSpace(runFolder))
            throw new InvalidOperationException("R20_RUN_FOLDER_MISSING");

        var categories = new List<string>();
        using var temporary = new TemporaryUploadDirectory();
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();
        Guid profileId = ResolveProfileId(
            provider.GetRequiredService<ISyncRemoteProfileRepository>());
        IRemoteFileSystem remote = provider
            .GetRequiredService<IGoogleDriveRemoteFileSystemFactory>()
            .Create(profileId);

        // 1. Silent restore and root reachability.
        await Stage(categories, "R20_SILENT_RESTORE", async () =>
        {
            if (await remote.ValidateAsync() is not null)
                throw new InvalidOperationException("validation reported a warning");
            if (!await remote.RootExistsAsync())
                throw new InvalidOperationException("configured root is not reachable");
        });

        // 2. Zero, small, large, and deeply nested create-only uploads.
        await Stage(categories, "R20_UPLOAD_SIZES", async () =>
        {
            await AssertUpload(remote, temporary.Create("empty.bin", 0), $"{runFolder}/empty.bin", 0);
            await AssertUpload(remote, temporary.Create("small.bin", 2048), $"{runFolder}/small.bin", 2048);
            await AssertUpload(
                remote,
                temporary.Create("large.bin", LargeUploadBytes),
                $"{runFolder}/large.bin",
                LargeUploadBytes);
            await AssertUpload(
                remote,
                temporary.Create("nested.bin", 128),
                $"{runFolder}/saves/profile/deep/nested.bin",
                128);
        });

        // 3. Create-only guards: exact and case-only names are never overwritten.
        await Stage(categories, "R20_NO_OVERWRITE", async () =>
        {
            await AssertRefused(
                remote,
                temporary.Create("again.bin", 16),
                $"{runFolder}/small.bin",
                "GoogleDriveUploadTargetAlreadyExists");
            await AssertRefused(
                remote,
                temporary.Create("again-case.bin", 16),
                $"{runFolder}/SMALL.BIN",
                "GoogleDriveUploadTargetCaseCollision");
        });

        // 4. Cancellation during an active upload reports no success.
        await Stage(categories, "R20_CANCELLATION", async () =>
        {
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(250));
            try
            {
                await remote.UploadFileAsync(
                    temporary.Create("cancelled.bin", LargeUploadBytes),
                    $"{runFolder}/cancelled.bin",
                    cancellation.Token);
                throw new InvalidOperationException("cancellation produced a success");
            }
            catch (OperationCanceledException)
            {
            }
        });

        // 5. Manifest-last: the run is discoverable only once its manifest exists.
        await Stage(categories, "R20_MANIFEST_LAST", async () =>
        {
            IReadOnlyList<string> before = await remote.ListRunFolderNamesAsync();
            if (before.Contains(runFolder, StringComparer.Ordinal))
                throw new InvalidOperationException("run was discoverable before its manifest");

            await AssertUpload(
                remote,
                temporary.CreateText("manifest.json", "{\"schemaVersion\":1}"),
                $"{runFolder}/manifest.json",
                19);

            IReadOnlyList<string> after = await remote.ListRunFolderNamesAsync();
            if (!after.Contains(runFolder, StringComparer.Ordinal))
                throw new InvalidOperationException("run was not discoverable after its manifest");
        });

        // 6. Download and provider activation remain unavailable.
        await Stage(categories, "R20_STILL_INACTIVE", async () =>
        {
            try
            {
                await remote.DownloadFileAsync($"{runFolder}/small.bin", "unused.bin");
                throw new InvalidOperationException("download was available");
            }
            catch (NotSupportedException)
            {
            }

            if (new SyncProviderCatalog()
                .GetDescriptor(GameSaves.Core.Sync.SyncProviderKind.GoogleDrive)
                .IsImplemented)
            {
                throw new InvalidOperationException("Google Drive reported as implemented");
            }
        });

        output.WriteLine(categories.Count == 0
            ? "R20_RESULT: PASS; sanitized failure categories: none"
            : $"R20_RESULT: FAIL; sanitized failure categories: {string.Join(", ", categories)}");
        Assert.Empty(categories);
    }

    /// <summary>
    /// Uses the one saved Google Drive profile when there is exactly one, so
    /// the operator never has to look up or paste a profile GUID. The GUID is
    /// never printed. Set GAMESAVES_R20_PROFILE_ID only to disambiguate.
    /// </summary>
    private static Guid ResolveProfileId(ISyncRemoteProfileRepository repository)
    {
        if (Guid.TryParse(Value("GAMESAVES_R20_PROFILE_ID"), out Guid requested) &&
            requested != Guid.Empty)
        {
            return requested;
        }

        Guid[] driveProfiles = repository.GetAll()
            .Where(profile =>
                profile.ProviderKind == GameSaves.Core.Sync.SyncProviderKind.GoogleDrive)
            .Select(profile => profile.Id)
            .ToArray();

        return driveProfiles.Length switch
        {
            1 => driveProfiles[0],
            0 => throw new InvalidOperationException("R20_NO_SAVED_DRIVE_PROFILE"),
            _ => throw new InvalidOperationException(
                "R20_MULTIPLE_DRIVE_PROFILES: set GAMESAVES_R20_PROFILE_ID")
        };
    }

    private static async Task Stage(
        List<string> categories,
        string category,
        Func<Task> stage)
    {
        try
        {
            await stage();
        }
        catch (Exception exception)
        {
            categories.Add(SafeCategory(category, exception));
        }
    }

    private static string SafeCategory(string category, Exception exception) =>
        exception switch
        {
            GoogleDriveRemoteOperationException remote =>
                $"{category}:{remote.Result.ErrorCode}",
            GoogleDriveLocalUploadSourceException source =>
                $"{category}:{source.SafeErrorCode}",
            GoogleDriveUploadResponseException response =>
                $"{category}:{response.SafeErrorCode}",
            GoogleDriveRecursiveFileListingException listing =>
                $"{category}:{listing.Result.SafeErrorCode}",
            OperationCanceledException => $"{category}:CANCELLED",
            _ => $"{category}:{exception.GetType().Name}"
        };

    private static async Task AssertUpload(
        IRemoteFileSystem remote,
        string localPath,
        string remotePath,
        long expectedBytes)
    {
        long bytes = await remote.UploadFileAsync(localPath, remotePath);
        if (bytes != expectedBytes)
            throw new InvalidOperationException("completed byte count mismatch");
    }

    private static async Task AssertRefused(
        IRemoteFileSystem remote,
        string localPath,
        string remotePath,
        string expectedErrorCode)
    {
        try
        {
            await remote.UploadFileAsync(localPath, remotePath);
        }
        catch (GoogleDriveRemoteOperationException exception)
            when (string.Equals(
                exception.Result.ErrorCode,
                expectedErrorCode,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("the create-only guard did not refuse");
    }

    private static string? Value(string name) =>
        Environment.GetEnvironmentVariable(name) ??
        (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null);

    private sealed class TemporaryUploadDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"gamesaves-r20-{Guid.NewGuid():N}");

        public TemporaryUploadDirectory() => Directory.CreateDirectory(_root);

        public string Create(string name, int length)
        {
            string path = Path.Combine(_root, name);
            byte[] content = new byte[length];
            for (int index = 0; index < content.Length; index++)
                content[index] = (byte)(index % 251);
            File.WriteAllBytes(path, content);
            return path;
        }

        public string CreateText(string name, string content)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
