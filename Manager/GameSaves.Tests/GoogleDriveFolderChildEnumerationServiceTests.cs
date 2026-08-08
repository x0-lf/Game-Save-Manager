using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveFolderChildEnumerationServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("ea77d649-7e19-4bcb-aa86-9f704dad9d50");

    private const string ParentId = "authoritative-parent-id";

    public static TheoryData<string> InvalidChildNames => new()
    {
        string.Empty,
        ".",
        "..",
        "nested/name",
        "name/",
        "control\u0001name"
    };

    public static TheoryData<string[]> MalformedParentMetadata => new()
    {
        Array.Empty<string>(),
        new[] { ParentId, "private-unexpected-parent-id" },
        new[] { ParentId, ParentId },
        new[] { ParentId, string.Empty }
    };

    [Fact]
    public async Task FilesAndFolders_AreReturnedWithExactValidatedMetadata()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("folder-id", "Files", GoogleDriveApplicationRoot.FolderMimeType),
                Object("file-id", "Pokémon O'Brien\\save.dat", "application/octet-stream")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Collection(children,
            folder =>
            {
                Assert.Equal("folder-id", folder.ObjectId);
                Assert.Equal("Files", folder.ExactName);
                Assert.Equal(GoogleDriveRecursiveObjectKind.Folder, folder.Kind);
                Assert.Equal(GoogleDriveApplicationRoot.FolderMimeType, folder.MimeType);
            },
            file =>
            {
                Assert.Equal("file-id", file.ObjectId);
                Assert.Equal("Pokémon O'Brien\\save.dat", file.ExactName);
                Assert.Equal(GoogleDriveRecursiveObjectKind.BlobFile, file.Kind);
                Assert.Equal("application/octet-stream", file.MimeType);
            });
        Assert.All(children, child =>
        {
            Assert.Equal(new[] { ParentId }, child.ParentIds);
            Assert.False(child.Trashed);
            Assert.Null(child.DriveId);
        });
        Assert.Equal(ParentId, listing.ParentFolderId);
        Assert.Null(listing.ExpectedKind);
        Assert.Same(context.Credential, listing.Credential);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public async Task MixedDriveNativeChildren_AreClassifiedWithoutFollowingOrExporting()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("shortcut-id", "shortcut", "application/vnd.google-apps.shortcut"),
                Object("document-id", "document", "application/vnd.google-apps.document")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Equal(
            new[]
            {
                GoogleDriveRecursiveObjectKind.Shortcut,
                GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument
            },
            children.Select(child => child.Kind));
        Assert.Equal(1, listing.CallCount);
    }

    [Fact]
    public async Task EmptyFolder_ReturnsAnImmutableEmptyCollection()
    {
        var service = new GoogleDriveFolderChildEnumerationService(
            new RecordingListingApi());
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Empty(children);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<GoogleDriveFolderChildEntry>)children).Add(
                Entry("late-id", "late.dat", "application/octet-stream")));
    }

    [Theory]
    [MemberData(nameof(InvalidChildNames))]
    public async Task InvalidChildName_FailsWithoutReturningPartialChildren(
        string invalidName)
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("valid-id", "valid.dat", "application/octet-stream"),
                Object("invalid-id", invalidName, "application/octet-stream")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "valid-id",
            "valid.dat",
            "invalid-id",
            invalidName);
    }

    [Fact]
    public async Task MalformedUnicodeName_FailsWithoutReturningPartialChildren()
    {
        string invalidName = "malformed" + new string('\uD800', 1);
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("invalid-id", invalidName, "application/octet-stream")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "invalid-id");
    }

    [Fact]
    public async Task InvalidMimeType_FailsAsInvalidMetadata()
    {
        var listing = new RecordingListingApi
        {
            Result = new[] { Object("private-id", "save.dat", "not-a-mime") }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "private-id",
            "save.dat",
            "not-a-mime");
    }

    [Fact]
    public async Task WrongParent_FailsAsInvalidMetadata()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    new[] { "different-private-parent" })
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "private-id",
            "save.dat",
            "different-private-parent");
    }

    [Theory]
    [MemberData(nameof(MalformedParentMetadata))]
    public async Task MalformedParentMetadata_FailsAsInvalidMetadata(
        string[] parentIds)
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    parentIds)
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "private-id",
            "save.dat",
            "private-unexpected-parent-id");
    }

    [Fact]
    public async Task RecursiveKindMismatch_FailsAsTypeCollision()
    {
        const string mismatchedMimeType =
            "APPLICATION/VND.GOOGLE-APPS.FOLDER";
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("private-id", "folder", mismatchedMimeType)
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.TypeCollision,
            "private-id",
            "folder",
            mismatchedMimeType);
    }

    [Fact]
    public async Task TrashedChild_FailsClosed()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    trashed: true)
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.TrashedObject,
            "private-id",
            "save.dat");
    }

    [Fact]
    public async Task SharedDriveChild_FailsClosed()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    driveId: "private-drive-id")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
            "private-id",
            "save.dat",
            "private-drive-id");
    }

    [Theory]
    [InlineData(
        GoogleDriveApiFailure.AuthorizationRevoked,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired)]
    [InlineData(
        GoogleDriveApiFailure.InsufficientScope,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired)]
    [InlineData(
        GoogleDriveApiFailure.AccessDenied,
        GoogleDriveRecursiveFileListingStatus.AccessDenied)]
    [InlineData(
        GoogleDriveApiFailure.NotFound,
        GoogleDriveRecursiveFileListingStatus.FolderNotFound)]
    [InlineData(
        GoogleDriveApiFailure.RateLimited,
        GoogleDriveRecursiveFileListingStatus.RateLimited)]
    [InlineData(
        GoogleDriveApiFailure.QuotaExceeded,
        GoogleDriveRecursiveFileListingStatus.QuotaExceeded)]
    [InlineData(
        GoogleDriveApiFailure.Unavailable,
        GoogleDriveRecursiveFileListingStatus.Unavailable)]
    public async Task ApiFailures_MapToStableRecursiveListingFailures(
        object failureValue,
        object expectedStatusValue)
    {
        var failure = (GoogleDriveApiFailure)failureValue;
        var expectedStatus =
            (GoogleDriveRecursiveFileListingStatus)expectedStatusValue;
        var listing = new RecordingListingApi
        {
            Exception = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.ObjectChildList,
                failure,
                $"safe-{failure}")
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        Assert.Equal(expectedStatus, exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(expectedStatus),
            exception.Result.SafeErrorCode);
        Assert.Equal(
            failure is GoogleDriveApiFailure.RateLimited or
                GoogleDriveApiFailure.Unavailable,
            exception.Result.Retryable);
        AssertFailure(exception, expectedStatus, ParentId);
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDoesNotDisposeTheCallerOwnedContext()
    {
        using var cancellation = new CancellationTokenSource();
        var listing = new RecordingListingApi
        {
            Exception = new OperationCanceledException(cancellation.Token)
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnumerateAsync(context, ParentId, cancellation.Token));

        Assert.Equal(1, listing.CallCount);
        Assert.Equal(cancellation.Token, listing.CancellationToken);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public async Task ExistingObjectApi_PaginatesWithRequiredFieldsAndDisposesItsClient()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "private-page-token",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Object("private-file-id", "save.dat", "application/octet-stream") },
            null,
            IncompleteSearch: false));
        var api = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            new RecordingObjectClientFactory(client));
        var service = new GoogleDriveFolderChildEnumerationService(api);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Single(children);
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Equal(new[] { null, "private-page-token" },
            client.ListRequests.Select(request => request.PageToken));
        Assert.All(client.ListRequests, request =>
        {
            Assert.Equal(
                "'authoritative-parent-id' in parents and trashed = false",
                request.Query);
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
        });
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, client.GetCalls);
        Assert.Equal(0, client.CreateCalls);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public async Task Pagination_AccumulatesSeveralPagesIncludingAnEmptyIntermediatePage()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(Page(
            new[] { Object("first-id", "first.dat", "application/octet-stream") },
            "page-2"));
        client.Pages.Enqueue(Page(
            new[]
            {
                Object("second-id", "second.dat", "application/octet-stream"),
                Object("third-id", "third.dat", "application/octet-stream")
            },
            "page-3"));
        client.Pages.Enqueue(Page(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "page-4"));
        client.Pages.Enqueue(Page(
            new[] { Object("fourth-id", "fourth.dat", "application/octet-stream") },
            nextPageToken: null));
        var service = Service(client);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Equal(
            new[] { "first.dat", "second.dat", "third.dat", "fourth.dat" },
            children.Select(child => child.ExactName));
        Assert.Equal(
            new[] { null, "page-2", "page-3", "page-4" },
            client.ListRequests.Select(request => request.PageToken));
        Assert.All(client.ListRequests, AssertRequiredDirectChildRequest);
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, client.GetCalls);
        Assert.Equal(0, client.CreateCalls);
    }

    [Fact]
    public async Task Pagination_PreservesDuplicateObjectsSplitAcrossPages()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(Page(
            new[] { Object("same-id", "same.dat", "application/octet-stream") },
            "page-2"));
        client.Pages.Enqueue(Page(
            new[] { Object("same-id", "same.dat", "application/octet-stream") },
            nextPageToken: null));
        var service = Service(client);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Equal(2, children.Count);
        Assert.All(children, child =>
        {
            Assert.Equal("same-id", child.ObjectId);
            Assert.Equal("same.dat", child.ExactName);
        });
        Assert.Equal(2, client.ListRequests.Count);
        Assert.All(client.ListRequests, AssertRequiredDirectChildRequest);
    }

    [Fact]
    public async Task Pagination_ParentMismatchOnLaterPageFailsWithoutPartialResult()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(Page(
            new[] { Object("valid-id", "valid.dat", "application/octet-stream") },
            "page-2"));
        client.Pages.Enqueue(Page(
            new[]
            {
                Object(
                    "wrong-parent-id",
                    "wrong-parent.dat",
                    "application/octet-stream",
                    new[] { "different-parent-id" })
            },
            nextPageToken: null));
        var service = Service(client);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "valid-id",
            "valid.dat",
            "wrong-parent-id",
            "wrong-parent.dat",
            "different-parent-id");
        Assert.Equal(2, client.ListRequests.Count);
        Assert.All(client.ListRequests, AssertRequiredDirectChildRequest);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pagination_IncompleteSearchOnAnyPageFailsRetryably(
        bool failOnLaterPage)
    {
        var client = new RecordingObjectClient();
        if (failOnLaterPage)
        {
            client.Pages.Enqueue(Page(
                new[] { Object("valid-id", "valid.dat", "application/octet-stream") },
                "page-2"));
        }
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "private-ignored-token",
            IncompleteSearch: true));
        var service = Service(client);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.Unavailable,
            "valid-id",
            "valid.dat",
            "private-ignored-token");
        Assert.True(exception.Result.Retryable);
        Assert.Equal(failOnLaterPage ? 2 : 1, client.ListRequests.Count);
        Assert.All(client.ListRequests, AssertRequiredDirectChildRequest);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task Pagination_CancellationAfterProviderReturnsAPageRejectsThePage()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingObjectClient
        {
            BeforePageReturn = (_, _) => cancellation.Cancel()
        };
        client.Pages.Enqueue(Page(
            new[] { Object("late-id", "late.dat", "application/octet-stream") },
            "page-2"));
        var service = Service(client);
        using GoogleDriveRemoteOperationContext context = Context();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnumerateAsync(context, ParentId, cancellation.Token));

        Assert.Single(client.ListRequests);
        AssertRequiredDirectChildRequest(client.ListRequests[0]);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task Pagination_CancellationBetweenPagesStopsBeforeAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingObjectClient
        {
            RespectCancellation = false
        };
        var cancellingObjects = new CancelAfterEnumerationList<GoogleDriveObjectMetadata>(
            new[] { Object("first-id", "first.dat", "application/octet-stream") },
            cancellation);
        client.Pages.Enqueue(Page(cancellingObjects, "page-2"));
        client.Pages.Enqueue(Page(
            new[] { Object("late-id", "late.dat", "application/octet-stream") },
            nextPageToken: null));
        var service = Service(client);
        using GoogleDriveRemoteOperationContext context = Context();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnumerateAsync(context, ParentId, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(client.ListRequests);
        Assert.Null(client.ListRequests[0].PageToken);
        AssertRequiredDirectChildRequest(client.ListRequests[0]);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public void ChildEntry_IsImmutableAndSafeToFormat()
    {
        var parents = new List<string> { ParentId };
        GoogleDriveFolderChildEntry entry = new(
            "private-file-id",
            "Private Save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            parents,
            trashed: false,
            driveId: null);
        parents.Add("late-private-parent-id");

        Assert.Equal(new[] { ParentId }, entry.ParentIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)entry.ParentIds).Add("mutating-parent-id"));
        AssertSafe(
            entry,
            "private-file-id",
            "Private Save.dat",
            ParentId,
            "late-private-parent-id");
    }

    [Fact]
    public void ChildEntry_RejectsMissingAuthoritativeIdentity()
    {
        Assert.Throws<ArgumentException>(() => new GoogleDriveFolderChildEntry(
            string.Empty,
            "save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            new[] { ParentId },
            trashed: false,
            driveId: null));
        Assert.Throws<ArgumentException>(() => new GoogleDriveFolderChildEntry(
            "file-id",
            "save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            new[] { string.Empty },
            trashed: false,
            driveId: null));
        Assert.Throws<ArgumentException>(() => new GoogleDriveFolderChildEntry(
            "file-id",
            "save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            Array.Empty<string>(),
            trashed: false,
            driveId: null));
        Assert.Throws<ArgumentException>(() => new GoogleDriveFolderChildEntry(
            "file-id",
            "save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            new[] { ParentId, "different-parent-id" },
            trashed: false,
            driveId: null));
    }

    [Fact]
    public void MetadataContract_RejectsMissingIdentityNameOrMimeType()
    {
        Assert.Throws<ArgumentException>(() => new GoogleDriveObjectMetadata(
            string.Empty,
            "save.dat",
            "application/octet-stream",
            false,
            new[] { ParentId },
            null));
        Assert.Throws<ArgumentNullException>(() => new GoogleDriveObjectMetadata(
            "file-id",
            null!,
            "application/octet-stream",
            false,
            new[] { ParentId },
            null));
        Assert.Throws<ArgumentException>(() => new GoogleDriveObjectMetadata(
            "file-id",
            "save.dat",
            string.Empty,
            false,
            new[] { ParentId },
            null));
    }

    [Fact]
    public void DependencyInjection_RegistersEnumerationWithoutRemoteWork()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();
        IGoogleDriveFolderChildEnumerationService service =
            provider.GetRequiredService<IGoogleDriveFolderChildEnumerationService>();

        Assert.IsType<GoogleDriveFolderChildEnumerationService>(service);
    }

    private static GoogleDriveObjectMetadata Object(
        string id,
        string name,
        string mimeType,
        IReadOnlyList<string>? parentIds = null,
        bool trashed = false,
        string? driveId = null) =>
        new(
            id,
            name,
            mimeType,
            trashed,
            parentIds ?? new[] { ParentId },
            driveId);

    private static GoogleDriveFolderChildEntry Entry(
        string id,
        string name,
        string mimeType) =>
        new(
            id,
            name,
            mimeType,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType),
            new[] { ParentId },
            trashed: false,
            driveId: null);

    private static GoogleDriveObjectListPage Page(
        IReadOnlyList<GoogleDriveObjectMetadata> objects,
        string? nextPageToken) =>
        new(objects, nextPageToken, IncompleteSearch: false);

    private static GoogleDriveFolderChildEnumerationService Service(
        RecordingObjectClient client) =>
        new(new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            new RecordingObjectClientFactory(client)));

    private static void AssertRequiredDirectChildRequest(
        GoogleDriveObjectListRequest request)
    {
        Assert.Equal(
            "'authoritative-parent-id' in parents and trashed = false",
            request.Query);
        Assert.Equal(
            "nextPageToken,incompleteSearch," +
            "files(id,name,mimeType,trashed,parents,driveId)",
            request.Fields);
        Assert.Equal("drive", request.Spaces);
        Assert.Equal("user", request.Corpora);
        Assert.False(request.IncludeItemsFromAllDrives);
        Assert.False(request.SupportsAllDrives);
    }

    private static void AssertFailure(
        GoogleDriveRecursiveFileListingException exception,
        GoogleDriveRecursiveFileListingStatus status,
        params string[] privateValues)
    {
        Assert.Equal(status, exception.Result.Status);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
            exception.Result.SafeErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.Result.SafeUserMessage));
        AssertSafe(exception, privateValues);
        AssertSafe(exception.Result, privateValues);
    }

    private static void AssertSafe(object value, params string[] privateValues)
    {
        string text = value.ToString()!;
        foreach (string privateValue in privateValues.Where(value => value.Length > 0))
            Assert.DoesNotContain(privateValue, text, StringComparison.Ordinal);
    }

    private static GoogleDriveRemoteOperationContext Context() =>
        new(ProfileId, "authoritative-root-id", Credential(), new UnusedResolver());

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

    private sealed class RecordingListingApi : IGoogleDriveObjectListingApi
    {
        public IReadOnlyList<GoogleDriveObjectMetadata> Result { get; set; } =
            Array.Empty<GoogleDriveObjectMetadata>();

        public Exception? Exception { get; set; }

        public int CallCount { get; private set; }

        public GoogleAuthorizedCredential? Credential { get; private set; }

        public string? ParentFolderId { get; private set; }

        public GoogleDriveObjectKind? ExpectedKind { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            GoogleDriveObjectKind? expectedKind,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Credential = credential;
            ParentFolderId = parentFolderId;
            ExpectedKind = expectedKind;
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Exception is null
                ? Task.FromResult(Result)
                : Task.FromException<IReadOnlyList<GoogleDriveObjectMetadata>>(Exception);
        }
    }

    private sealed class RecordingObjectClientFactory
        : IGoogleDriveObjectClientFactory
    {
        private readonly RecordingObjectClient _client;

        public RecordingObjectClientFactory(RecordingObjectClient client) =>
            _client = client;

        public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential) =>
            _client;
    }

    private sealed class RecordingObjectClient : IGoogleDriveObjectClient
    {
        public Queue<GoogleDriveObjectListPage> Pages { get; } = new();

        public List<GoogleDriveObjectListRequest> ListRequests { get; } = new();

        public int GetCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool RespectCancellation { get; set; } = true;

        public Action<int, CancellationToken>? BeforePageReturn { get; set; }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            throw new InvalidOperationException("Enumeration must not get by ID.");
        }

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            if (RespectCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            ListRequests.Add(request);
            GoogleDriveObjectListPage page = Pages.Dequeue();
            BeforePageReturn?.Invoke(ListRequests.Count, cancellationToken);
            return Task.FromResult(page);
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("Enumeration must not create folders.");
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class CancelAfterEnumerationList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly CancellationTokenSource _cancellation;

        public CancelAfterEnumerationList(
            IReadOnlyList<T> items,
            CancellationTokenSource cancellation)
        {
            _items = items;
            _cancellation = cancellation;
        }

        public int Count => _items.Count;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            foreach (T item in _items)
                yield return item;

            _cancellation.Cancel();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class UnusedResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Enumeration must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Enumeration must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Enumeration must not create paths.");
    }
}
