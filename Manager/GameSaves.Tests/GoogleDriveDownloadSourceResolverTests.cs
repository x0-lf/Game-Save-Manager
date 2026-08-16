using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadSourceResolverTests
{
    private const string RootId = "root-id";

    private static readonly Guid ProfileId =
        Guid.Parse("5c1a7e3f-2b44-4d90-9c8a-1f0e6b7d2c31");

    [Fact]
    public async Task NestedSource_ResolvesToOneAuthoritativeBlobFile()
    {
        var enumeration = new StubChildEnumerationService
        {
            Children =
            {
                [RootId] = [Folder("run-id", "Run 42", RootId)],
                ["run-id"] = [Folder("saves-id", "saves", "run-id")],
                ["saves-id"] =
                [
                    Blob("other-id", "other.bin", "saves-id"),
                    Blob("file-id", "slot1.sav", "saves-id")
                ]
            }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveDownloadSource source = await resolver.ResolveAsync(
            context,
            GoogleDriveRelativePath.Parse("Run 42/saves/slot1.sav"));

        Assert.Equal("file-id", source.FileId);
        Assert.Equal("saves-id", source.ParentFolderId);
        Assert.Equal("slot1.sav", source.ExactName);
        Assert.Equal("application/octet-stream", source.MimeType);
        Assert.Equal([RootId, "run-id", "saves-id"], enumeration.ParentIds);
        Assert.Equal("Google Drive download source", source.ToString());
    }

    [Fact]
    public async Task RootLevelSource_ResolvesWithoutTraversingFolders()
    {
        var enumeration = new StubChildEnumerationService
        {
            Children = { [RootId] = [Blob("file-id", "save.bin", RootId)] }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveDownloadSource source = await resolver.ResolveAsync(
            context,
            GoogleDriveRelativePath.Parse("save.bin"));

        Assert.Equal("file-id", source.FileId);
        Assert.Equal(RootId, source.ParentFolderId);
        Assert.Equal([RootId], enumeration.ParentIds);
    }

    [Fact]
    public async Task MissingSource_FailsClosed()
    {
        var enumeration = new StubChildEnumerationService
        {
            Children = { [RootId] = [Blob("other-id", "other.bin", RootId)] }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("save.bin")));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.NotFound,
            exception.Result.ErrorCode);
    }

    [Fact]
    public async Task DuplicateExactNames_FailClosedAsAmbiguous()
    {
        var enumeration = new StubChildEnumerationService
        {
            Children =
            {
                [RootId] =
                [
                    Blob("first-id", "save.bin", RootId),
                    Blob("second-id", "save.bin", RootId)
                ]
            }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("save.bin")));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.Ambiguous,
            exception.Result.ErrorCode);
    }

    [Theory]
    [InlineData("SAVE.BIN")]
    [InlineData("Save.Bin")]
    public async Task CaseOnlySibling_FailsClosedEvenWithAnExactMatch(
        string sibling)
    {
        var enumeration = new StubChildEnumerationService
        {
            Children =
            {
                [RootId] =
                [
                    Blob("exact-id", "save.bin", RootId),
                    Blob("case-id", sibling, RootId)
                ]
            }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("save.bin")));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.CaseCollision,
            exception.Result.ErrorCode);
    }

    [Fact]
    public async Task FolderInsteadOfFile_FailsClosedAsTypeCollision()
    {
        var enumeration = new StubChildEnumerationService
        {
            Children = { [RootId] = [Folder("folder-id", "save.bin", RootId)] }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("save.bin")));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.TypeCollision,
            exception.Result.ErrorCode);
    }

    [Fact]
    public async Task FileInsteadOfFolderSegment_FailsClosedBeforeTheNextSegment()
    {
        var enumeration = new StubChildEnumerationService
        {
            Children = { [RootId] = [Blob("file-id", "Run 42", RootId)] }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("Run 42/save.bin")));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.TypeCollision,
            exception.Result.ErrorCode);
        Assert.Equal([RootId], enumeration.ParentIds);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.document")]
    [InlineData("application/vnd.google-apps.spreadsheet")]
    [InlineData("application/vnd.google-apps.shortcut")]
    public async Task UnsupportedObjects_FailClosed(string mimeType)
    {
        var enumeration = new StubChildEnumerationService
        {
            Children =
            {
                [RootId] =
                [
                    new GoogleDriveFolderChildEntry(
                        "unsupported-id",
                        "save.bin",
                        mimeType,
                        GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType),
                        [RootId],
                        trashed: false,
                        driveId: null)
                ]
            }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("save.bin")));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.UnsupportedObject,
            exception.Result.ErrorCode);
    }

    [Fact]
    public async Task EnumerationFailure_KeepsItsOwnCategory()
    {
        var enumeration = new StubChildEnumerationService
        {
            Failure = GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                GoogleDriveRecursiveFileListingStatus.TrashedObject)
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                resolver.ResolveAsync(
                    context,
                    GoogleDriveRelativePath.Parse("save.bin")));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.TrashedObject,
            exception.Result.Status);
    }

    [Fact]
    public async Task ResolutionForwardsTheCallerTokenAndStopsWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeration = new StubChildEnumerationService
        {
            Children = { [RootId] = [Blob("file-id", "save.bin", RootId)] }
        };
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        await resolver.ResolveAsync(
            context,
            GoogleDriveRelativePath.Parse("save.bin"),
            cancellation.Token);
        Assert.Equal(cancellation.Token, enumeration.CancellationTokens.Single());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(
                context,
                GoogleDriveRelativePath.Parse("save.bin"),
                cancellation.Token));
        Assert.Single(enumeration.CancellationTokens);
    }

    [Fact]
    public async Task InvalidInputs_AreRejected()
    {
        var enumeration = new StubChildEnumerationService();
        using GoogleDriveRemoteOperationContext context = Context();
        var resolver = new GoogleDriveDownloadSourceResolver(enumeration);

        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveDownloadSourceResolver(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            resolver.ResolveAsync(null!, GoogleDriveRelativePath.Parse("save.bin")));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            resolver.ResolveAsync(context, null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(context, GoogleDriveRelativePath.Root));
        Assert.Empty(enumeration.ParentIds);
    }

    [Fact]
    public void SourceConstruction_RequiresAValidatedBlobIdentity()
    {
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveDownloadSource(" ", "parent", "save.bin", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveDownloadSource("id", " ", "save.bin", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveDownloadSource("id", "parent", "", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveDownloadSource(
                "id",
                "parent",
                "save.bin",
                "application/vnd.google-apps.folder"));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveDownloadSource(
                "id",
                "parent",
                "save.bin",
                "application/vnd.google-apps.document"));
    }

    [Fact]
    public void FailureCodes_AreDistinctAndCarryNoPrivateValue()
    {
        string[] codes =
        [
            GoogleDriveDownloadSourceErrorCodes.NotFound,
            GoogleDriveDownloadSourceErrorCodes.Ambiguous,
            GoogleDriveDownloadSourceErrorCodes.CaseCollision,
            GoogleDriveDownloadSourceErrorCodes.TypeCollision,
            GoogleDriveDownloadSourceErrorCodes.UnsupportedObject
        ];

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code =>
            Assert.StartsWith("GoogleDriveDownloadSource", code, StringComparison.Ordinal));

        var source = new GoogleDriveDownloadSource(
            "private-id-marker",
            "private-parent-marker",
            "Personal Save.bin",
            "application/octet-stream");
        Assert.DoesNotContain("private-id-marker", source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-parent-marker", source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Save.bin", source.ToString(), StringComparison.Ordinal);
    }

    private static GoogleDriveFolderChildEntry Folder(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            GoogleDriveRecursiveObjectKind.Folder,
            [parentId],
            trashed: false,
            driveId: null);

    private static GoogleDriveFolderChildEntry Blob(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            [parentId],
            trashed: false,
            driveId: null);

    private static GoogleDriveRemoteOperationContext Context()
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
        var credential = new GoogleAuthorizedCredential(new UserCredential(
            flow,
            ProfileId.ToString("D"),
            new TokenResponse { AccessToken = "test-access-token" }));
        return new GoogleDriveRemoteOperationContext(
            ProfileId,
            RootId,
            credential,
            new UnusedResolver());
    }

    private sealed class StubChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        public Dictionary<string, GoogleDriveFolderChildEntry[]> Children { get; } =
            new(StringComparer.Ordinal);

        public Exception? Failure { get; set; }

        public List<string> ParentIds { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParentIds.Add(parentFolderId);
            CancellationTokens.Add(cancellationToken);

            if (Failure is not null)
            {
                return Task.FromException<IReadOnlyList<GoogleDriveFolderChildEntry>>(
                    Failure);
            }

            return Task.FromResult<IReadOnlyList<GoogleDriveFolderChildEntry>>(
                Children.TryGetValue(parentFolderId, out GoogleDriveFolderChildEntry[]? entries)
                    ? entries
                    : []);
        }
    }

    private sealed class UnusedResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
