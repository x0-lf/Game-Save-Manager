using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameSaves.Tests;

public sealed class GoogleDriveRecursivePaginationIntegrationTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("7f77c5ac-1aa7-40f8-acd7-3a402173cd30");

    private const string RunFolderId = "authoritative-run-folder-id";

    [Fact]
    public async Task RecursivePagination_ConsumesEveryPageAtEveryDepth()
    {
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Blob("root-b-id", "root-b.dat", RunFolderId),
                    Folder("folder-a-id", "a", RunFolderId)
                },
                "private-root-page-2"),
            Page(
                Array.Empty<GoogleDriveObjectMetadata>(),
                "private-root-page-3"),
            Page(
                new[]
                {
                    Folder("folder-b-id", "b", RunFolderId),
                    Blob("root-a-id", "root-a.dat", RunFolderId)
                },
                nextPageToken: null));
        factory.AddPages(
            "folder-a-id",
            Page(
                new[]
                {
                    Blob("a-second-id", "a-second.dat", "folder-a-id"),
                    Folder("nested-folder-id", "nested", "folder-a-id")
                },
                "private-a-page-2"),
            Page(
                new[] { Blob("a-first-id", "a-first.dat", "folder-a-id") },
                nextPageToken: null));
        factory.AddPages(
            "folder-b-id",
            Page(
                Array.Empty<GoogleDriveObjectMetadata>(),
                "private-b-page-2"),
            Page(
                new[] { Blob("b-file-id", "b.dat", "folder-b-id") },
                nextPageToken: null));
        factory.AddPages(
            "nested-folder-id",
            Page(
                new[]
                {
                    Blob("nested-z-id", "nested-z.dat", "nested-folder-id")
                },
                "private-nested-page-2"),
            Page(
                Array.Empty<GoogleDriveObjectMetadata>(),
                "private-nested-page-3"),
            Page(
                new[]
                {
                    Blob("nested-a-id", "nested-a.dat", "nested-folder-id")
                },
                nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(GoogleDriveRecursiveFileListingStatus.Completed, result.Status);
        Assert.Equal(
            new[]
            {
                "a/a-first.dat",
                "a/a-second.dat",
                "a/nested/nested-a.dat",
                "a/nested/nested-z.dat",
                "b/b.dat",
                "root-a.dat",
                "root-b.dat"
            },
            result.Entries.Select(entry => entry.CanonicalRelativePath));
        Assert.Equal(
            result.Entries.Count,
            result.Entries.Select(entry => entry.FileId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(4, factory.Clients.Count);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            null,
            "private-root-page-2",
            "private-root-page-3");
        AssertClient(
            factory.ClientFor("folder-a-id"),
            "folder-a-id",
            null,
            "private-a-page-2");
        AssertClient(
            factory.ClientFor("folder-b-id"),
            "folder-b-id",
            null,
            "private-b-page-2");
        AssertClient(
            factory.ClientFor("nested-folder-id"),
            "nested-folder-id",
            null,
            "private-nested-page-2",
            "private-nested-page-3");
        Assert.All(
            factory.Clients,
            client => Assert.Equal(0, client.RemainingPageCount));
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            result,
            "private-root-page-2",
            "private-root-page-3",
            "private-a-page-2",
            "private-b-page-2",
            "private-nested-page-2",
            "private-nested-page-3");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task RemoteFileSystem_ListsCanonicalPathsThroughFakeApi()
    {
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Blob("root-z-id", "z.dat", RunFolderId),
                    Folder("nested-folder-id", "nested", RunFolderId)
                },
                "private-run-page-2"),
            Page(
                new[] { Blob("root-a-id", "a.dat", RunFolderId) },
                nextPageToken: null));
        factory.AddPages(
            "nested-folder-id",
            Page(
                new[]
                {
                    Blob(
                        "nested-file-id",
                        "save.dat",
                        "nested-folder-id")
                },
                nextPageToken: null));
        var resolver = new FixedRunFolderResolver();
        var recursiveListing = new GoogleDriveRecursiveFileListingService(
            resolver,
            Service(factory));
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<ISyncRemoteProfileRepository>();
        services.RemoveAll<IGoogleDriveRecursiveFileListingService>();
        services.AddSingleton<ISyncRemoteProfileRepository>(
            new InMemorySyncRemoteProfileRepository());
        services.AddSingleton<IGoogleDriveRecursiveFileListingService>(
            recursiveListing);

        using ServiceProvider provider = services.BuildServiceProvider();
        IRemoteFileSystem remote = provider
            .GetRequiredService<IGoogleDriveRemoteFileSystemFactory>()
            .Create(ProfileId);
        IReadOnlyList<string> paths =
            await remote.ListFilesAsync("Run 42");

        Assert.Equal(
            new[] { "a.dat", "nested/save.dat", "z.dat" },
            paths);
        Assert.Equal(ProfileId, resolver.Requests.Single().RemoteProfileId);
        Assert.Equal(
            "Run 42",
            resolver.Requests.Single().CanonicalFolderPath);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            null,
            "private-run-page-2");
        AssertClient(
            factory.ClientFor("nested-folder-id"),
            "nested-folder-id",
            new string?[] { null });
        AssertNoMutation(factory);
    }

    [Fact]
    public async Task ExactDuplicateNamesSplitAcrossPages_FailAfterAllSiblingPages()
    {
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Blob(
                        "private-first-duplicate-id",
                        "private-duplicate.dat",
                        RunFolderId),
                    Folder(
                        "private-untraversed-folder-id",
                        "private-untraversed",
                        RunFolderId)
                },
                "private-duplicate-page-2"),
            Page(
                new[]
                {
                    Blob(
                        "private-second-duplicate-id",
                        "private-duplicate.dat",
                        RunFolderId)
                },
                nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.Ambiguous,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.Ambiguous,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Single(factory.Clients);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            null,
            "private-duplicate-page-2");
        Assert.Equal(0, factory.ClientFor(RunFolderId).RemainingPageCount);
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            "private-first-duplicate-id",
            "private-second-duplicate-id",
            "private-untraversed-folder-id",
            "private-duplicate.dat",
            "private-duplicate-page-2");
        AssertPrivateValuesAbsent(
            exception.Result,
            "private-first-duplicate-id",
            "private-second-duplicate-id",
            "private-untraversed-folder-id",
            "private-duplicate.dat",
            "private-duplicate-page-2");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task RepeatedProviderMetadataAcrossPages_FailsWithoutDeduplication()
    {
        var factory = new PagedTreeClientFactory();
        GoogleDriveObjectMetadata repeated = Blob(
            "private-repeated-id",
            "private-repeated.dat",
            RunFolderId);
        factory.AddPages(
            RunFolderId,
            Page(new[] { repeated }, "private-repeated-page-2"),
            Page(new[] { repeated }, nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.Ambiguous,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.Ambiguous,
            exception.Result.SafeErrorCode);
        Assert.Empty(exception.Result.Entries);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            null,
            "private-repeated-page-2");
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            "private-repeated-id",
            "private-repeated.dat",
            "private-repeated-page-2");
        AssertPrivateValuesAbsent(
            exception.Result,
            "private-repeated-id",
            "private-repeated.dat",
            "private-repeated-page-2");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task RepeatedIdentityWithDifferentNamesAcrossPages_FailsClosed()
    {
        const string repeatedId = "private-repeated-id";
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Blob(repeatedId, "private-first.dat", RunFolderId),
                    Folder(
                        "private-untraversed-folder-id",
                        "private-untraversed",
                        RunFolderId)
                },
                "private-repeated-page-2"),
            Page(
                new[]
                {
                    Blob(repeatedId, "private-second.dat", RunFolderId)
                },
                nextPageToken: null));
        factory.AddPages(
            "private-untraversed-folder-id",
            Page(
                new[]
                {
                    Blob(
                        "private-untraversed-file-id",
                        "private-untraversed.dat",
                        "private-untraversed-folder-id")
                },
                nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.InvalidMetadata,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Single(factory.Clients);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            null,
            "private-repeated-page-2");
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            repeatedId,
            "private-first.dat",
            "private-second.dat",
            "private-untraversed-folder-id",
            "private-repeated-page-2");
        AssertPrivateValuesAbsent(
            exception.Result,
            repeatedId,
            "private-first.dat",
            "private-second.dat",
            "private-untraversed-folder-id",
            "private-repeated-page-2");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task CaseInsensitiveNamesSplitAcrossPages_FailAfterAllSiblingPages()
    {
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Blob(
                        "private-first-collision-id",
                        "private-save.dat",
                        RunFolderId),
                    Folder(
                        "private-untraversed-folder-id",
                        "private-untraversed",
                        RunFolderId)
                },
                "private-collision-page-2"),
            Page(
                new[]
                {
                    Blob(
                        "private-second-collision-id",
                        "PRIVATE-SAVE.DAT",
                        RunFolderId)
                },
                nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.CaseCollision,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.CaseCollision,
            exception.Result.SafeErrorCode);
        Assert.Equal(
            "The Google Drive backup folder contains names that differ only by case.",
            exception.Result.SafeUserMessage);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Single(factory.Clients);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            null,
            "private-collision-page-2");
        Assert.Equal(0, factory.ClientFor(RunFolderId).RemainingPageCount);
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            "private-first-collision-id",
            "private-second-collision-id",
            "private-untraversed-folder-id",
            "private-save.dat",
            "PRIVATE-SAVE.DAT",
            "private-collision-page-2");
        AssertPrivateValuesAbsent(
            exception.Result,
            "private-first-collision-id",
            "private-second-collision-id",
            "private-untraversed-folder-id",
            "private-save.dat",
            "PRIVATE-SAVE.DAT",
            "private-collision-page-2");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task NestedIncompleteSearch_FailsWithoutPartialResultOrPrivateTokens()
    {
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Folder("private-nested-folder-id", "nested", RunFolderId)
                },
                nextPageToken: null));
        factory.AddPages(
            "private-nested-folder-id",
            Page(
                new[]
                {
                    Blob(
                        "private-partial-file-id",
                        "private-partial.dat",
                        "private-nested-folder-id")
                },
                "private-incomplete-page-token"),
            new GoogleDriveObjectListPage(
                Array.Empty<GoogleDriveObjectMetadata>(),
                "private-ignored-page-token",
                IncompleteSearch: true));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.Unavailable,
            exception.Result.Status);
        Assert.True(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(2, factory.Clients.Count);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            new string?[] { null });
        AssertClient(
            factory.ClientFor("private-nested-folder-id"),
            "private-nested-folder-id",
            null,
            "private-incomplete-page-token");
        Assert.All(
            factory.Clients,
            client => Assert.Equal(0, client.RemainingPageCount));
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            "private-nested-folder-id",
            "private-partial-file-id",
            "private-partial.dat",
            "private-incomplete-page-token",
            "private-ignored-page-token");
        AssertPrivateValuesAbsent(
            exception.Result,
            "private-nested-folder-id",
            "private-partial-file-id",
            "private-partial.dat",
            "private-incomplete-page-token",
            "private-ignored-page-token");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task MalformedParentMetadataOnNestedLaterPage_FailsWithoutPartialResult()
    {
        const string nestedFolderId = "private-nested-folder-id";
        const string unexpectedParentId = "private-unexpected-parent-id";
        var factory = new PagedTreeClientFactory();
        factory.AddPages(
            RunFolderId,
            Page(
                new[]
                {
                    Blob("private-root-file-id", "root.dat", RunFolderId),
                    Folder(nestedFolderId, "nested", RunFolderId),
                    Folder(
                        "private-untraversed-folder-id",
                        "untraversed",
                        RunFolderId)
                },
                nextPageToken: null));
        factory.AddPages(
            nestedFolderId,
            Page(
                new[]
                {
                    Blob("private-nested-file-id", "nested.dat", nestedFolderId)
                },
                "private-nested-page-2"),
            Page(
                new[]
                {
                    Object(
                        "private-invalid-id",
                        "invalid.dat",
                        "application/octet-stream",
                        nestedFolderId,
                        new[] { nestedFolderId, unexpectedParentId })
                },
                nextPageToken: null));
        factory.AddPages(
            "private-untraversed-folder-id",
            Page(
                new[]
                {
                    Blob(
                        "private-untraversed-file-id",
                        "untraversed.dat",
                        "private-untraversed-folder-id")
                },
                nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.InvalidMetadata,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(2, factory.Clients.Count);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            new string?[] { null });
        AssertClient(
            factory.ClientFor(nestedFolderId),
            nestedFolderId,
            null,
            "private-nested-page-2");
        Assert.All(
            factory.Clients,
            client => Assert.Equal(0, client.RemainingPageCount));
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            nestedFolderId,
            unexpectedParentId,
            "private-root-file-id",
            "private-nested-file-id",
            "private-invalid-id",
            "private-untraversed-folder-id",
            "private-nested-page-2");
        AssertPrivateValuesAbsent(
            exception.Result,
            nestedFolderId,
            unexpectedParentId,
            "private-root-file-id",
            "private-nested-file-id",
            "private-invalid-id",
            "private-untraversed-folder-id",
            "private-nested-page-2");
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task CancellationWhileReadingNestedPage_StopsBeforeContinuation()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new PagedTreeClientFactory
        {
            BeforePageReturn = (parentFolderId, requestNumber, _) =>
            {
                if (string.Equals(
                        parentFolderId,
                        "nested-folder-id",
                        StringComparison.Ordinal) &&
                    requestNumber == 2)
                {
                    cancellation.Cancel();
                }
            }
        };
        factory.AddPages(
            RunFolderId,
            Page(
                new[] { Folder("nested-folder-id", "nested", RunFolderId) },
                nextPageToken: null));
        factory.AddPages(
            "nested-folder-id",
            Page(
                new[]
                {
                    Blob("first-file-id", "first.dat", "nested-folder-id")
                },
                "private-nested-page-2"),
            Page(
                new[]
                {
                    Blob("cancelled-file-id", "cancelled.dat", "nested-folder-id")
                },
                "private-never-requested-page-3"),
            Page(
                new[]
                {
                    Blob("late-file-id", "late.dat", "nested-folder-id")
                },
                nextPageToken: null));
        GoogleDriveOneLevelFileListingService service = Service(factory);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ListAsync(resolved, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, factory.Clients.Count);
        AssertClient(
            factory.ClientFor(RunFolderId),
            RunFolderId,
            new string?[] { null });
        AssertClient(
            factory.ClientFor("nested-folder-id"),
            "nested-folder-id",
            null,
            "private-nested-page-2");
        Assert.Equal(0, factory.ClientFor(RunFolderId).RemainingPageCount);
        Assert.Equal(
            1,
            factory.ClientFor("nested-folder-id").RemainingPageCount);
        AssertNoMutation(factory);
        AssertPrivateValuesAbsent(
            exception,
            "private-nested-page-2",
            "private-never-requested-page-3");
        Assert.True(resolved.IsDisposed);
    }

    private static GoogleDriveOneLevelFileListingService Service(
        PagedTreeClientFactory factory)
    {
        var objectApi = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            factory);
        var enumeration =
            new GoogleDriveFolderChildEnumerationService(objectApi);
        return new GoogleDriveOneLevelFileListingService(enumeration);
    }

    private static GoogleDriveObjectListPage Page(
        IReadOnlyList<GoogleDriveObjectMetadata> objects,
        string? nextPageToken) =>
        new(objects, nextPageToken, IncompleteSearch: false);

    private static GoogleDriveObjectMetadata Blob(
        string objectId,
        string exactName,
        string parentFolderId) =>
        Object(
            objectId,
            exactName,
            "application/octet-stream",
            parentFolderId);

    private static GoogleDriveObjectMetadata Folder(
        string objectId,
        string exactName,
        string parentFolderId) =>
        Object(
            objectId,
            exactName,
            GoogleDriveApplicationRoot.FolderMimeType,
            parentFolderId);

    private static GoogleDriveObjectMetadata Object(
        string objectId,
        string exactName,
        string mimeType,
        string parentFolderId,
        IReadOnlyList<string>? parentIds = null) =>
        new(
            objectId,
            exactName,
            mimeType,
            trashed: false,
            parentIds ?? new[] { parentFolderId },
            driveId: null);

    private static GoogleDriveResolvedRunFolder ResolvedRunFolder()
    {
        var flow = new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = "fake-client-id",
                    ClientSecret = "fake-client-secret"
                }
            });
        var token = new TokenResponse
        {
            AccessToken = "fake-access-token",
            RefreshToken = "fake-refresh-token",
            ExpiresInSeconds = 3600,
            IssuedUtc = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc)
        };
        var credential = new GoogleAuthorizedCredential(
            new UserCredential(flow, ProfileId.ToString("D"), token),
            wasAuthenticationRefreshed: false);
        var context = new GoogleDriveRemoteOperationContext(
            ProfileId,
            "authoritative-application-root-id",
            credential,
            new NeverCalledResolver());
        return new GoogleDriveResolvedRunFolder(RunFolderId, context);
    }

    private static void AssertClient(
        RecordingPagedObjectClient client,
        string parentFolderId,
        params string?[] expectedPageTokens)
    {
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(expectedPageTokens, client.ListRequests
            .Select(request => request.PageToken));
        Assert.All(client.ListRequests, request =>
        {
            Assert.Equal(
                $"'{parentFolderId}' in parents and trashed = false",
                request.Query);
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
            foreach (string token in expectedPageTokens
                         .Where(token => token is not null)
                         .Cast<string>())
            {
                Assert.DoesNotContain(
                    token,
                    request.ToString(),
                    StringComparison.Ordinal);
            }
        });
    }

    private static void AssertNoMutation(PagedTreeClientFactory factory)
    {
        Assert.All(factory.Clients, client =>
        {
            Assert.Equal(0, client.GetCalls);
            Assert.Equal(0, client.CreateCalls);
            Assert.Equal(1, client.DisposeCalls);
        });
    }

    private static void AssertPrivateValuesAbsent(
        object value,
        params string[] privateValues)
    {
        string text = value.ToString()!;
        foreach (string privateValue in privateValues)
            Assert.DoesNotContain(privateValue, text, StringComparison.Ordinal);
    }

    private sealed class PagedTreeClientFactory
        : IGoogleDriveObjectClientFactory
    {
        private readonly Dictionary<string, PageScript> _scriptsByQuery =
            new(StringComparer.Ordinal);

        public List<RecordingPagedObjectClient> Clients { get; } = new();

        public Action<string, int, CancellationToken>? BeforePageReturn { get; set; }

        public void AddPages(
            string parentFolderId,
            params GoogleDriveObjectListPage[] pages)
        {
            string query =
                $"'{parentFolderId}' in parents and trashed = false";
            _scriptsByQuery.Add(
                query,
                new PageScript(parentFolderId, query, pages));
        }

        public RecordingPagedObjectClient ClientFor(string parentFolderId) =>
            Assert.Single(
                Clients,
                client => string.Equals(
                    client.ParentFolderId,
                    parentFolderId,
                    StringComparison.Ordinal));

        public IGoogleDriveObjectClient Create(
            GoogleAuthorizedCredential credential)
        {
            var client = new RecordingPagedObjectClient(this);
            Clients.Add(client);
            return client;
        }

        public PageScript Claim(string query)
        {
            if (!_scriptsByQuery.TryGetValue(query, out PageScript? script) ||
                script.Claimed)
            {
                throw new InvalidOperationException(
                    "No independent pagination script exists for this folder.");
            }

            script.Claimed = true;
            return script;
        }
    }

    private sealed class PageScript
    {
        public PageScript(
            string parentFolderId,
            string query,
            IEnumerable<GoogleDriveObjectListPage> pages)
        {
            ParentFolderId = parentFolderId;
            Query = query;
            Pages = new Queue<GoogleDriveObjectListPage>(pages);
        }

        public string ParentFolderId { get; }

        public string Query { get; }

        public Queue<GoogleDriveObjectListPage> Pages { get; }

        public bool Claimed { get; set; }
    }

    private sealed class RecordingPagedObjectClient
        : IGoogleDriveObjectClient
    {
        private readonly PagedTreeClientFactory _factory;
        private PageScript? _script;

        public RecordingPagedObjectClient(PagedTreeClientFactory factory) =>
            _factory = factory;

        public string? ParentFolderId => _script?.ParentFolderId;

        public List<GoogleDriveObjectListRequest> ListRequests { get; } = new();

        public int GetCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public int RemainingPageCount => _script?.Pages.Count ?? 0;

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            throw new InvalidOperationException(
                "Recursive pagination must not inspect objects by ID.");
        }

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _script ??= _factory.Claim(request.Query);
            if (!string.Equals(
                    request.Query,
                    _script.Query,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Pagination state leaked between parent folders.");
            }
            if (_script.Pages.Count == 0)
            {
                throw new InvalidOperationException(
                    "The traversal requested an unexpected page.");
            }

            ListRequests.Add(request);
            GoogleDriveObjectListPage page = _script.Pages.Dequeue();
            _factory.BeforePageReturn?.Invoke(
                _script.ParentFolderId,
                ListRequests.Count,
                cancellationToken);
            return Task.FromResult(page);
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException(
                "Recursive pagination must not create folders.");
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class NeverCalledResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Recursive pagination must not resolve display paths.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Recursive pagination must not resolve display paths.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Recursive pagination must not create paths.");
    }

    private sealed class FixedRunFolderResolver
        : IGoogleDriveRunFolderResolver
    {
        public List<GoogleDriveRecursiveFileListingRequest> Requests { get; } =
            new();

        public Task<GoogleDriveResolvedRunFolder> ResolveAsync(
            GoogleDriveRecursiveFileListingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(ResolvedRunFolder());
        }
    }
}
