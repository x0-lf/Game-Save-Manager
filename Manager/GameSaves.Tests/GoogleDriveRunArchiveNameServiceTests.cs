using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

/// <summary>
/// The archive counterpart of the run-folder listing: one child listing of
/// the application root, files only, and only the names that are containers.
/// </summary>
public sealed class GoogleDriveRunArchiveNameServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("a41be4bb-1d6c-48f2-b41a-16e774705a22");

    [Fact]
    public async Task ListsOnlyTheContainersBeneathTheRoot_AndReadsNothingElse()
    {
        var listing = new RecordingListingApi
        {
            Result =
            [
                File("zip-id", "Run B.zip"),
                File("sidecar-id", "Run A.7z.manifest.json"),
                File("sevenzip-id", "Run A.7z"),
                File("note-id", "notes.txt", "text/plain")
            ]
        };
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunArchiveNameService(contexts, listing);

        IReadOnlyList<string> names = await service.ListAsync(ProfileId);

        Assert.Equal(new[] { "Run A.7z", "Run B.zip" }, names);
        Assert.Equal(RecordingContextFactory.RootId, listing.ParentFolderId);
        Assert.Equal(GoogleDriveObjectKind.File, listing.ExpectedKind);
        Assert.Equal(0, contexts.Resolver.OperationCalls);
        Assert.True(contexts.LastCredential!.IsDisposed);
    }

    // Two containers whose names differ only by case would be one run on a
    // case-insensitive local disk; the folder listing refuses the same thing.
    [Fact]
    public async Task ContainerNamesDifferingOnlyByCase_FailClosed()
    {
        var listing = new RecordingListingApi
        {
            Result = [File("one-id", "Run.7z"), File("two-id", "run.7z")]
        };
        var service = new GoogleDriveRunArchiveNameService(
            new RecordingContextFactory(),
            listing);

        var failure = await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
            () => service.ListAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRunArchiveListingErrorCodes.AmbiguousRunName,
            failure.Result.ErrorCode);
    }

    // A Docs file named like an archive has no bytes to download, so it is
    // refused rather than offered as a run that could never be restored.
    [Fact]
    public async Task ADriveNativeObjectNamedLikeAContainer_FailsClosed()
    {
        var listing = new RecordingListingApi
        {
            Result = [File("doc-id", "Run.7z", "application/vnd.google-apps.document")]
        };
        var service = new GoogleDriveRunArchiveNameService(
            new RecordingContextFactory(),
            listing);

        var failure = await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
            () => service.ListAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRunArchiveListingErrorCodes.InvalidMetadata,
            failure.Result.ErrorCode);
    }

    [Fact]
    public void DependencyInjection_RegistersTheServiceWithoutRemoteWork()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveRunArchiveNameService>(
            provider.GetRequiredService<IGoogleDriveRunArchiveNameService>());
    }

    private static GoogleDriveObjectMetadata File(
        string id,
        string name,
        string mimeType = "application/octet-stream") =>
        new(
            id,
            name,
            mimeType,
            trashed: false,
            parentIds: new[] { RecordingContextFactory.RootId },
            driveId: null);

    private static GoogleAuthorizedCredential Credential()
    {
        var flow = new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = "test-client-id",
                    ClientSecret = "test-client-secret"
                }
            });
        var user = new UserCredential(
            flow,
            ProfileId.ToString("D"),
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        public const string RootId = "authoritative-root-id";

        public RecordingResolver Resolver { get; } = new();

        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCredential = Credential();
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                LastCredential,
                Resolver));
        }
    }

    private sealed class RecordingListingApi : IGoogleDriveObjectListingApi
    {
        public IReadOnlyList<GoogleDriveObjectMetadata> Result { get; set; } =
            Array.Empty<GoogleDriveObjectMetadata>();

        public string? ParentFolderId { get; private set; }

        public GoogleDriveObjectKind? ExpectedKind { get; private set; }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            GoogleDriveObjectKind? expectedKind,
            CancellationToken cancellationToken)
        {
            ParentFolderId = parentFolderId;
            ExpectedKind = expectedKind;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public int OperationCalls { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) => Unexpected();

        private Task<GoogleDriveObjectResolutionResult> Unexpected()
        {
            OperationCalls++;
            throw new InvalidOperationException(
                "Listing archive containers must not resolve any path.");
        }
    }
}
