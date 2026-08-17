using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using System.Reflection;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// Milestone T Task 5. Everything that crosses the provider-neutral
/// ISyncProvider boundary must stay sanitized, and Core and App must keep
/// seeing no Google type through it.
/// </summary>
public sealed class GoogleDriveSyncProviderBoundaryTests
{
    private const string AccountEmail = "user@example.invalid";
    private const string ObjectId = "private-object-id-marker";
    private const string RemotePathMarker = "Private Run Folder/save.dat";
    private const string QueryMarker = "'private-parent' in parents";
    private const string UrlMarker =
        "https://www.googleapis.com/drive/v3/files/private-object-id-marker";
    private const string TokenMarker = "ya29.private-token-marker";

    private static readonly string[] PrivateMarkers =
    [
        AccountEmail,
        ObjectId,
        RemotePathMarker,
        QueryMarker,
        UrlMarker,
        TokenMarker
    ];

    public static TheoryData<string> FailingOperations() =>
        new(
            nameof(IRemoteFileSystem.ValidateAsync),
            nameof(IRemoteFileSystem.RootExistsAsync),
            nameof(IRemoteFileSystem.ListRunFolderNamesAsync),
            nameof(IRemoteFileSystem.ReadProviderMetadataAsync));

    [Theory]
    [MemberData(nameof(FailingOperations))]
    public async Task DriveFailures_CrossTheBoundaryUnchangedAndSanitized(
        string failingMember)
    {
        GoogleDriveRemoteOperationException drive = RemoteFailure();
        var remote = new RecordingProviderRemoteFileSystem();
        remote.Failures[failingMember] = drive;
        using ISyncProvider provider = Provider(remote);

        Exception escaped = await Escaping(provider, failingMember);

        // The wrapper must not re-wrap, re-message, or re-type the failure.
        Assert.Same(drive, escaped);
        AssertSanitized(escaped);
    }

    [Fact]
    public async Task ListingFailures_AlsoCrossTheBoundarySanitized()
    {
        GoogleDriveRecursiveFileListingException listing =
            GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                GoogleDriveRecursiveFileListingStatus.Ambiguous);
        var remote = new RecordingProviderRemoteFileSystem();
        remote.Failures[nameof(IRemoteFileSystem.ListRunFolderNamesAsync)] = listing;
        using ISyncProvider provider = Provider(remote);

        Exception escaped = await Assert.ThrowsAsync<
            GoogleDriveRecursiveFileListingException>(
            () => provider.CreatePreviewAsync(new SyncOptions()));

        Assert.Same(listing, escaped);
        AssertSanitized(escaped);
    }

    [Fact]
    public async Task AFailedRunReportsOnlyTheSanitizedDriveMessage()
    {
        using var local = new TemporaryDirectory();
        const string runName = "2026-08-17_10-00-00_manual";
        string runRoot = Path.Combine(local.Path, runName);
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(
            Path.Combine(runRoot, "manifest.json"),
            JsonSerializer.Serialize(Manifest()));

        GoogleDriveRemoteOperationException drive = RemoteFailure();
        var remote = new RecordingProviderRemoteFileSystem();
        remote.Failures[nameof(IRemoteFileSystem.FolderExistsAsync)] = drive;
        using ISyncProvider provider = Provider(
            remote,
            history: new StaticLocalRunHistoryService(local.Path, runName));

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        SyncResult result = await provider.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true });

        SyncItemResult failed = Assert.Single(
            result.Items,
            item => item.Status == SyncItemStatus.Failed);
        Assert.Equal(drive.Message, failed.Error);
        AssertSanitized(failed.Error!);
        foreach (TransferPreviewWarning warning in result.Warnings)
            AssertSanitized(warning.Message);
    }

    [Fact]
    public async Task AnUnreadableRemoteRunReportsAFixedWarningOnly()
    {
        var remote = new RecordingProviderRemoteFileSystem
        {
            RunFolderNames = ["2026-08-17_10-00-00_manual"]
        };
        remote.Failures[nameof(IRemoteFileSystem.ReadTextFileAsync)] =
            RemoteFailure();
        using ISyncProvider provider = Provider(remote);

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());

        Assert.Contains(plan.Warnings, warning => warning.Code == "RemoteRunUnreadable");
        foreach (TransferPreviewWarning warning in plan.Warnings)
            AssertSanitized(warning.Message);
    }

    [Fact]
    public async Task TheBoundaryDoesNotScrub_SoDriveFailuresMustArriveSanitized()
    {
        // SyncEngine copies a failed run's exception message verbatim into the
        // result the App shows. Nothing downstream sanitizes it. This proves the
        // marker sweep above has teeth, and pins why every Drive failure must
        // already be safe when it is thrown.
        using var local = new TemporaryDirectory();
        const string runName = "2026-08-17_10-00-00_manual";
        string runRoot = Path.Combine(local.Path, runName);
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(
            Path.Combine(runRoot, "manifest.json"),
            JsonSerializer.Serialize(Manifest()));

        var remote = new RecordingProviderRemoteFileSystem();
        remote.Failures[nameof(IRemoteFileSystem.FolderExistsAsync)] =
            new InvalidOperationException(ObjectId);
        using ISyncProvider provider = Provider(
            remote,
            history: new StaticLocalRunHistoryService(local.Path, runName));

        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        SyncResult result = await provider.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true });

        SyncItemResult failed = Assert.Single(
            result.Items,
            item => item.Status == SyncItemStatus.Failed);
        Assert.Equal(ObjectId, failed.Error);
    }

    [Fact]
    public void TheProviderSurfaceExposesOnlyCoreTypes()
    {
        Assembly core = typeof(ISyncProvider).Assembly;
        IEnumerable<Type> surface = typeof(GoogleDriveSyncProvider)
            .GetMethods(BindingFlags.Instance | BindingFlags.DeclaredOnly |
                        BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(method =>
                method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType))
            .SelectMany(Unwrap);

        foreach (Type type in surface)
        {
            Assert.True(
                type.Assembly == core ||
                type.Assembly == typeof(object).Assembly ||
                type == typeof(void),
                $"{type.FullName} is not a Core or framework type.");
        }
    }

    [Fact]
    public void CoreAndAppStillReferenceNoGoogleAssembly()
    {
        foreach (Assembly assembly in new[]
                 {
                     typeof(ISyncProvider).Assembly,
                     typeof(GameSaves.App.Services.ISyncSettingsStore).Assembly
                 })
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name is not null &&
                             reference.Name.StartsWith(
                                 "Google.", StringComparison.Ordinal));
        }
    }

    private static async Task<Exception> Escaping(
        ISyncProvider provider,
        string failingMember) =>
        failingMember == nameof(IRemoteFileSystem.ReadProviderMetadataAsync)
            ? await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => provider.GetSyncLogAsync())
            : await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => provider.CreatePreviewAsync(new SyncOptions()));

    private static GoogleDriveRemoteOperationException RemoteFailure() =>
        new(GoogleDriveRemoteValidationMapper.FromStatus(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked));

    private static void AssertSanitized(Exception exception)
    {
        AssertSanitized(exception.Message);
        AssertSanitized(exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "Google.Apis",
            exception.GetType().Assembly.FullName ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static void AssertSanitized(string surface)
    {
        foreach (string marker in PrivateMarkers)
        {
            Assert.DoesNotContain(
                marker, surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type nested in Unwrap(argument))
                yield return nested;
        }
    }

    private static ISyncProvider Provider(
        IRemoteFileSystem remote,
        IBackupHistoryService? history = null) =>
        new GoogleDriveSyncProvider(
            remote,
            history ?? new EmptyBackupHistoryService(),
            new RecordingHistoryRepository());

    private static TransferBackupManifest Manifest() =>
        new(
            SchemaVersion: 1,
            Kind: "manual",
            Game: "Example Game",
            SteamAppId: "424242",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            CompletedUtc: DateTimeOffset.Parse("2026-08-17T10:01:00Z"),
            FileCount: 0,
            TotalBytes: 0,
            Items: []);

    private sealed class StaticLocalRunHistoryService : IBackupHistoryService
    {
        private readonly string _basePath;
        private readonly string _runName;

        public StaticLocalRunHistoryService(string basePath, string runName)
        {
            _basePath = basePath;
            _runName = runName;
        }

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string runRoot = Path.Combine(_basePath, _runName);
            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(
            [
                new TransferBackupRunInfo(
                    runRoot,
                    Path.Combine(runRoot, "manifest.json"),
                    Manifest())
            ]);
        }

        public string GetBackupBasePath() => _basePath;
    }
}
