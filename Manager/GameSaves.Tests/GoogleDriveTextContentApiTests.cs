using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Tests;

public sealed class GoogleDriveTextContentApiTests
{
    private const string FileId = "private-authoritative-file-id";

    [Fact]
    public async Task EmptyBlob_ReturnsEmptyRawContentAndDisposesResources()
    {
        var client = Client(Metadata(declaredSize: 0), Array.Empty<byte>());
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveTextContentResult result =
            await api.DownloadTextContentAsync(
                credential,
                FileId,
                CancellationToken.None);

        Assert.Equal(0, result.Length);
        Assert.Empty(result.ToArray());
        Assert.Equal(1, client.DisposeCalls);
        Assert.Single(client.MetadataRequests);
        Assert.Single(client.MediaRequests);
        AssertDestinationDisposed(client);
    }

    [Fact]
    public async Task BoundedBlob_ReturnsDefensiveRawBytesWithoutDecoding()
    {
        byte[] content = [0xff, 0xfe, 0x00, 0x7f];
        var client = Client(Metadata(declaredSize: content.Length), content);
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveTextContentResult result = await api.DownloadTextContentAsync(
            credential,
            FileId,
            CancellationToken.None);
        byte[] firstCopy = result.ToArray();
        firstCopy[0] = 0;

        Assert.Equal(content, result.ToArray());
        Assert.Equal(content.Length, result.Length);
        Assert.DoesNotContain(Convert.ToHexString(content), result.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeclaredOversize_FailsBeforeAnyMediaRequest()
    {
        var client = Client(Metadata(
            declaredSize: GoogleDriveTextContentApi.MaxTextContentBytes + 1));
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(
            () => api.DownloadTextContentAsync(
                credential,
                FileId,
                CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextContentErrorCodes.DeclaredSizeTooLarge,
            exception.Details.SafeErrorCode);
        Assert.False(exception.Details.Retryable);
        Assert.Empty(client.MediaRequests);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task StreamedOversize_IsStoppedByTheBoundedDestination()
    {
        var client = Client(Metadata(
            declaredSize: GoogleDriveTextContentApi.MaxTextContentBytes));
        client.DownloadAction = async (destination, cancellationToken) =>
        {
            byte[] maximum = new byte[GoogleDriveTextContentApi.MaxTextContentBytes];
            await destination.WriteAsync(maximum, cancellationToken);
            destination.WriteByte(1);
        };
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(
            () => api.DownloadTextContentAsync(
                credential,
                FileId,
                CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextContentErrorCodes.StreamedSizeTooLarge,
            exception.Details.SafeErrorCode);
        Assert.False(exception.Details.Retryable);
        Assert.Equal(1, client.DisposeCalls);
        AssertDestinationDisposed(client);
    }

    [Fact]
    public async Task FolderInput_FailsBeforeDownload()
    {
        await AssertMetadataRejectedAsync(
            Metadata(
                mimeType: GoogleDriveApplicationRoot.FolderMimeType,
                declaredSize: null),
            GoogleDriveTextContentErrorCodes.Folder);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.document")]
    [InlineData("application/vnd.google-apps.spreadsheet")]
    [InlineData("application/vnd.google-apps.presentation")]
    public async Task WorkspaceDocument_IsNeverExported(string mimeType)
    {
        await AssertMetadataRejectedAsync(
            Metadata(mimeType: mimeType, declaredSize: null),
            GoogleDriveTextContentErrorCodes.WorkspaceDocument);
    }

    [Fact]
    public async Task DownloadCapabilityFalse_FailsBeforeDownload()
    {
        await AssertMetadataRejectedAsync(
            Metadata(declaredSize: 2, canDownload: false),
            GoogleDriveTextContentErrorCodes.DownloadNotAllowed);
    }

    [Fact]
    public async Task TrashedBlob_FailsBeforeDownload()
    {
        await AssertMetadataRejectedAsync(
            Metadata(declaredSize: 2, trashed: true),
            GoogleDriveTextContentErrorCodes.Trashed);
    }

    [Fact]
    public async Task SharedDriveBlob_FailsBeforeDownload()
    {
        await AssertMetadataRejectedAsync(
            Metadata(declaredSize: 2, driveId: "private-shared-drive-id"),
            GoogleDriveTextContentErrorCodes.UnsupportedLocation);
    }

    [Fact]
    public async Task MissingDeclaredSize_FailsClosedBeforeDownload()
    {
        await AssertMetadataRejectedAsync(
            Metadata(declaredSize: null),
            GoogleDriveTextContentErrorCodes.DeclaredSizeMissing);
    }

    [Fact]
    public async Task TruncatedDownload_IsRetryableAndNeverReturned()
    {
        var client = Client(Metadata(declaredSize: 5), [1, 2, 3]);
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(
            () => api.DownloadTextContentAsync(
                credential,
                FileId,
                CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextContentErrorCodes.Truncated,
            exception.Details.SafeErrorCode);
        Assert.Equal(GoogleDriveApiFailure.Unavailable, exception.Failure);
        Assert.True(exception.Details.Retryable);
        Assert.Equal(1, client.DisposeCalls);
        AssertDestinationDisposed(client);
    }

    [Fact]
    public async Task CancellationDuringDownload_PropagatesAndDisposesResources()
    {
        using var cancellation = new CancellationTokenSource();
        var client = Client(Metadata(declaredSize: 1));
        client.DownloadAction = (_, cancellationToken) =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.DownloadTextContentAsync(
                credential,
                FileId,
                cancellation.Token));

        Assert.Equal(1, client.DisposeCalls);
        AssertDestinationDisposed(client);
    }

    [Fact]
    public async Task ProviderFailure_IsMappedWithoutPrivateDiagnostics()
    {
        const string rawSecret = "token-and-response-body-marker";
        var client = Client(Metadata(declaredSize: 1));
        client.DownloadException = new IOException(rawSecret + FileId);
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(
            () => api.DownloadTextContentAsync(
                credential,
                FileId,
                CancellationToken.None));
        string diagnostics = exception + " " + exception.Details;

        Assert.DoesNotContain(rawSecret, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(FileId, diagnostics, StringComparison.Ordinal);
        Assert.Equal(
            GoogleDriveApiOperation.TextContentDownload,
            exception.Details.Operation);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public void RequestContracts_UseAuthoritativeIdMinimalFieldsAndSafeFormatting()
    {
        var metadata = new GoogleDriveTextContentMetadataRequest(FileId);
        var media = new GoogleDriveTextContentMediaRequest(FileId);

        Assert.Equal(FileId, metadata.FileId);
        Assert.Equal(
            "id,mimeType,trashed,driveId,size,capabilities(canDownload)",
            metadata.Fields);
        Assert.True(metadata.SupportsAllDrives);
        Assert.Equal(FileId, media.FileId);
        Assert.False(media.SupportsAllDrives);
        Assert.False(media.AcknowledgeAbuse);
        Assert.DoesNotContain(FileId, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FileId, media.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("name", metadata.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parents", metadata.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permissions", metadata.Fields,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SdkRequests_UseFilesGetWithoutExportOrAbuseAcknowledgement()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var metadataContract = new GoogleDriveTextContentMetadataRequest(FileId);
        var mediaContract = new GoogleDriveTextContentMediaRequest(FileId);

        FilesResource.GetRequest metadata =
            GoogleDriveTextContentClient.CreateMetadataRequest(
                drive,
                metadataContract);
        FilesResource.GetRequest media =
            GoogleDriveTextContentClient.CreateMediaRequest(drive, mediaContract);

        Assert.Equal(FileId, metadata.FileId);
        Assert.Equal(metadataContract.Fields, metadata.Fields);
        Assert.True(metadata.SupportsAllDrives);
        Assert.Equal(FileId, media.FileId);
        Assert.False(media.SupportsAllDrives);
        Assert.False(media.AcknowledgeAbuse);
        Assert.DoesNotContain(
            "Export",
            typeof(GoogleDriveTextContentClient).GetMethods()
                .Select(method => method.Name));
    }

    [Fact]
    public void SdkMetadataMapping_PreservesOnlyRequiredDownloadState()
    {
        GoogleDriveTextContentMetadata metadata =
            GoogleDriveTextContentClient.Map(new DriveFile
            {
                Id = FileId,
                MimeType = "application/octet-stream",
                Trashed = false,
                DriveId = null,
                Size = 12,
                Capabilities = new DriveFile.CapabilitiesData
                {
                    CanDownload = true
                }
            });

        Assert.Equal(FileId, metadata.Id);
        Assert.Equal(12, metadata.DeclaredSize);
        Assert.True(metadata.CanDownload);
        Assert.False(metadata.IsFolder);
        Assert.False(metadata.IsWorkspaceObject);
        Assert.DoesNotContain(FileId, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("application/octet-stream", metadata.ToString(),
            StringComparison.Ordinal);
    }

    private static async Task AssertMetadataRejectedAsync(
        GoogleDriveTextContentMetadata metadata,
        string expectedErrorCode)
    {
        var client = Client(metadata);
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(
            () => api.DownloadTextContentAsync(
                credential,
                FileId,
                CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.Details.SafeErrorCode);
        Assert.Empty(client.MediaRequests);
        Assert.Equal(1, client.DisposeCalls);
    }

    private static GoogleDriveTextContentApi Api(RecordingTextContentClient client) =>
        new(new RecordingTextContentClientFactory(client));

    private static RecordingTextContentClient Client(
        GoogleDriveTextContentMetadata metadata,
        byte[]? content = null) =>
        new()
        {
            Metadata = metadata,
            Content = content ?? Array.Empty<byte>()
        };

    private static GoogleDriveTextContentMetadata Metadata(
        long? declaredSize,
        string mimeType = "application/octet-stream",
        bool trashed = false,
        string? driveId = null,
        bool canDownload = true) =>
        new(
            FileId,
            mimeType,
            trashed,
            driveId,
            declaredSize,
            canDownload);

    private static void AssertDestinationDisposed(RecordingTextContentClient client)
    {
        Assert.NotNull(client.Destination);
        Assert.Throws<ObjectDisposedException>(() => client.Destination.WriteByte(0));
    }

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
            "text-content-test-user",
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private sealed class RecordingTextContentClientFactory(
        RecordingTextContentClient client)
        : IGoogleDriveTextContentClientFactory
    {
        public IGoogleDriveTextContentClient Create(
            GoogleAuthorizedCredential credential)
        {
            Assert.NotNull(credential);
            return client;
        }
    }

    private sealed class RecordingTextContentClient : IGoogleDriveTextContentClient
    {
        public GoogleDriveTextContentMetadata Metadata { get; set; } =
            GoogleDriveTextContentApiTests.Metadata(declaredSize: 0);

        public byte[] Content { get; set; } = Array.Empty<byte>();

        public Exception? MetadataException { get; set; }

        public Exception? DownloadException { get; set; }

        public Func<Stream, CancellationToken, Task>? DownloadAction { get; set; }

        public List<GoogleDriveTextContentMetadataRequest> MetadataRequests { get; } = [];

        public List<GoogleDriveTextContentMediaRequest> MediaRequests { get; } = [];

        public Stream? Destination { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveTextContentMetadata> GetMetadataAsync(
            GoogleDriveTextContentMetadataRequest request,
            CancellationToken cancellationToken)
        {
            MetadataRequests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (MetadataException is not null)
                throw MetadataException;

            return Task.FromResult(Metadata);
        }

        public async Task DownloadAsync(
            GoogleDriveTextContentMediaRequest request,
            Stream destination,
            CancellationToken cancellationToken)
        {
            MediaRequests.Add(request);
            Destination = destination;
            cancellationToken.ThrowIfCancellationRequested();
            if (DownloadException is not null)
                throw DownloadException;

            if (DownloadAction is not null)
            {
                await DownloadAction(destination, cancellationToken);
                return;
            }

            await destination.WriteAsync(Content, cancellationToken);
        }

        public void Dispose() => DisposeCalls++;
    }
}
