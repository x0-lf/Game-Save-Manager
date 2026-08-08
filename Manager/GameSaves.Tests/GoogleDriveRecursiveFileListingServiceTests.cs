using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveRecursiveFileListingServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("e3177a38-0c06-4783-96c3-06e13f388407");

    [Fact]
    public async Task InvalidPath_FailsBeforeResolution()
    {
        var resolver = new RecordingRunFolderResolver();
        var traversal = new RecordingTraversalService();
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(
                () => service.ListAsync(ProfileId, "../private-run"));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.InvalidPath,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.InvalidPath,
            exception.Result.SafeErrorCode);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, traversal.Calls);
    }

    [Fact]
    public async Task MissingRequestedFolder_ReturnsEmpty()
    {
        var resolver = new RecordingRunFolderResolver
        {
            Handler = (_, _) => Task.FromException<GoogleDriveResolvedRunFolder>(
                Failure(GoogleDriveRecursiveFileListingStatus.FolderNotFound))
        };
        var traversal = new RecordingTraversalService();
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);

        IReadOnlyList<string> paths =
            await service.ListAsync(ProfileId, "Run 42");

        Assert.Empty(paths);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, traversal.Calls);
    }

    [Fact]
    public async Task CancellationBeforeResolution_Propagates()
    {
        var resolver = new RecordingRunFolderResolver();
        var traversal = new RecordingTraversalService();
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAsync(
                ProfileId,
                "Run 42",
                cancellation.Token));

        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, traversal.Calls);
    }

    [Fact]
    public async Task CompletedListing_ReturnsOrderedCanonicalPaths()
    {
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();
        var resolver = new RecordingRunFolderResolver
        {
            Handler = (_, _) => Task.FromResult(resolved)
        };
        var traversal = new RecordingTraversalService
        {
            Result = Completed(
                Entry("file-z-id", "z.dat", "z.dat"),
                Entry("file-a-id", "a.dat", "nested/a.dat"))
        };
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);

        IReadOnlyList<string> paths =
            await service.ListAsync(ProfileId, "Run 42");

        Assert.Equal(new[] { "nested/a.dat", "z.dat" }, paths);
        Assert.Equal("Run 42", resolver.Requests.Single().CanonicalFolderPath);
        Assert.Equal(1, traversal.Calls);
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task EmptyFolder_ReturnsEmpty()
    {
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();
        var resolver = new RecordingRunFolderResolver
        {
            Handler = (_, _) => Task.FromResult(resolved)
        };
        var traversal = new RecordingTraversalService
        {
            Result = Completed()
        };
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);

        IReadOnlyList<string> paths =
            await service.ListAsync(ProfileId, "Run 42");

        Assert.Empty(paths);
        Assert.True(resolved.IsDisposed);
    }

    [Theory]
    [InlineData((int)GoogleDriveRecursiveFileListingStatus.FolderNotFound)]
    [InlineData((int)GoogleDriveRecursiveFileListingStatus.Ambiguous)]
    [InlineData((int)GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired)]
    [InlineData((int)GoogleDriveRecursiveFileListingStatus.AccessDenied)]
    [InlineData((int)GoogleDriveRecursiveFileListingStatus.Unavailable)]
    public async Task TraversalFailure_FailsClosed(int statusValue)
    {
        var status = (GoogleDriveRecursiveFileListingStatus)statusValue;
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();
        var resolver = new RecordingRunFolderResolver
        {
            Handler = (_, _) => Task.FromResult(resolved)
        };
        var traversal = new RecordingTraversalService
        {
            Handler = (_, _) =>
                Task.FromException<GoogleDriveRecursiveFileListingResult>(
                    Failure(status))
        };
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(
                () => service.ListAsync(ProfileId, "Run 42"));

        Assert.Equal(status, exception.Result.Status);
        Assert.Empty(exception.Result.Entries);
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task UnexpectedProviderFailure_IsSanitized()
    {
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();
        var resolver = new RecordingRunFolderResolver
        {
            Handler = (_, _) => Task.FromResult(resolved)
        };
        var traversal = new RecordingTraversalService
        {
            Handler = (_, _) => throw new InvalidOperationException(
                "private-provider-response")
        };
        var service = new GoogleDriveRecursiveFileListingService(
            resolver,
            traversal);

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(
                () => service.ListAsync(ProfileId, "Run 42"));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.Failed,
            exception.Result.Status);
        Assert.DoesNotContain(
            "private-provider-response",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.True(resolved.IsDisposed);
    }

    private static GoogleDriveRecursiveFileEntry Entry(
        string fileId,
        string exactName,
        string path) =>
        new(
            fileId,
            "authoritative-parent-id",
            exactName,
            path,
            "application/octet-stream");

    private static GoogleDriveRecursiveFileListingResult Completed(
        params GoogleDriveRecursiveFileEntry[] entries) =>
        new(
            GoogleDriveRecursiveFileListingStatus.Completed,
            entries,
            retryable: false);

    private static GoogleDriveRecursiveFileListingException Failure(
        GoogleDriveRecursiveFileListingStatus status) =>
        GoogleDriveRecursiveFileListingFailureMapper.FromStatus(status);

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
            "authoritative-root-id",
            credential,
            new NeverCalledResolver());
        return new GoogleDriveResolvedRunFolder(
            "authoritative-run-folder-id",
            context);
    }

    private sealed class RecordingRunFolderResolver
        : IGoogleDriveRunFolderResolver
    {
        public Func<
            GoogleDriveRecursiveFileListingRequest,
            CancellationToken,
            Task<GoogleDriveResolvedRunFolder>>? Handler { get; set; }

        public int Calls { get; private set; }

        public List<GoogleDriveRecursiveFileListingRequest> Requests { get; } =
            new();

        public Task<GoogleDriveResolvedRunFolder> ResolveAsync(
            GoogleDriveRecursiveFileListingRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Handler?.Invoke(request, cancellationToken) ??
                throw new InvalidOperationException("Resolution was not expected.");
        }
    }

    private sealed class RecordingTraversalService
        : IGoogleDriveOneLevelFileListingService
    {
        public GoogleDriveRecursiveFileListingResult Result { get; set; } =
            Completed();

        public Func<
            GoogleDriveResolvedRunFolder,
            CancellationToken,
            Task<GoogleDriveRecursiveFileListingResult>>? Handler { get; set; }

        public int Calls { get; private set; }

        public Task<GoogleDriveRecursiveFileListingResult> ListAsync(
            GoogleDriveResolvedRunFolder resolvedRunFolder,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Handler is null
                ? Task.FromResult(Result)
                : Handler(resolvedRunFolder, cancellationToken);
        }
    }

    private sealed class NeverCalledResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Path resolution was not expected.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Path resolution was not expected.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Path creation was not expected.");
    }
}
