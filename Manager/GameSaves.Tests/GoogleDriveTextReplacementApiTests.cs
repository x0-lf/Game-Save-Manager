using System.Net;
using System.Text;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Requests;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveTextReplacementApiTests
{
    private const string FileId = "private-authoritative-file-id";

    public static IEnumerable<object[]> InvalidMetadata()
    {
        yield return [0, GoogleDriveTextReplacementErrorCodes.InvalidMetadata];
        yield return [1, GoogleDriveTextReplacementErrorCodes.InvalidMetadata];
        yield return [2, GoogleDriveTextReplacementErrorCodes.InvalidMetadata];
        yield return [3, GoogleDriveTextReplacementErrorCodes.InvalidMetadata];
        yield return [4, GoogleDriveTextReplacementErrorCodes.InvalidMetadata];
        yield return [5, GoogleDriveTextReplacementErrorCodes.Trashed];
        yield return [6, GoogleDriveTextReplacementErrorCodes.Folder];
        yield return [7, GoogleDriveTextReplacementErrorCodes.WorkspaceDocument];
        yield return [8, GoogleDriveTextReplacementErrorCodes.UnsupportedLocation];
    }

    public static IEnumerable<object[]> InvalidResponses()
    {
        yield return [0, GoogleDriveTextReplacementErrorCodes.InvalidResponse];
        yield return [1, GoogleDriveTextReplacementErrorCodes.InvalidResponse];
        yield return [2, GoogleDriveTextReplacementErrorCodes.IdentityMismatch];
        yield return [3, GoogleDriveTextReplacementErrorCodes.UnsupportedLocation];
    }

    public static IEnumerable<object[]> ProviderFailures()
    {
        yield return [
            HttpStatusCode.Unauthorized,
            "authError",
            (int)GoogleDriveApiFailure.AuthorizationRevoked,
            false];
        yield return [
            HttpStatusCode.Forbidden,
            "storageQuotaExceeded",
            (int)GoogleDriveApiFailure.QuotaExceeded,
            false];
        yield return [
            HttpStatusCode.TooManyRequests,
            "rateLimitExceeded",
            (int)GoogleDriveApiFailure.RateLimited,
            true];
        yield return [
            HttpStatusCode.Forbidden,
            "insufficientFilePermissions",
            (int)GoogleDriveApiFailure.AccessDenied,
            false];
        yield return [
            HttpStatusCode.ServiceUnavailable,
            "backendError",
            (int)GoogleDriveApiFailure.Unavailable,
            true];
    }

    [Fact]
    public void RequestContracts_UseExactIdMinimalFieldsAndSafeDiagnostics()
    {
        var metadata = new GoogleDriveTextReplacementMetadataRequest(FileId);
        var update = new GoogleDriveTextReplacementRequest(
            FileId,
            contentLength: 17,
            GoogleDriveTextCreationMediaTypes.Json);

        Assert.Equal(FileId, metadata.FileId);
        Assert.Equal("id,mimeType,trashed,driveId", metadata.Fields);
        Assert.True(metadata.SupportsAllDrives);
        Assert.Equal(FileId, update.FileId);
        Assert.Equal(17, update.ContentLength);
        Assert.Equal("application/json", update.MediaType);
        Assert.Equal("id,driveId", update.Fields);
        Assert.False(update.SupportsAllDrives);
        Assert.DoesNotContain(FileId, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FileId, update.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("name", metadata.Fields,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parents", metadata.Fields,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permissions", update.Fields,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceTextContentAsync_UpdatesExactIdAndPreservesBytes()
    {
        const string json = "{\"name\":\"保存 O'Brien\\\\data\",\"value\":2}";
        byte[] replacement = Encoding.UTF8.GetBytes(json);
        var client = new RecordingTextReplacementClient();
        var factory = new RecordingTextReplacementClientFactory(client);
        var api = new GoogleDriveTextReplacementApi(factory);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveTextReplacementResult result =
            await api.ReplaceTextContentAsync(
                credential,
                FileId,
                replacement,
                GoogleDriveTextCreationMediaTypes.Json,
                CancellationToken.None);

        GoogleDriveTextReplacementMetadataRequest metadataRequest =
            Assert.Single(client.MetadataRequests);
        GoogleDriveTextReplacementRequest updateRequest =
            Assert.Single(client.UpdateRequests);
        Assert.Equal(FileId, metadataRequest.FileId);
        Assert.Equal(FileId, updateRequest.FileId);
        Assert.Equal(replacement.Length, updateRequest.ContentLength);
        Assert.Equal(replacement, Assert.Single(client.Contents));
        Assert.Equal(FileId, result.FileId);
        Assert.DoesNotContain(FileId, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task MaximumSize_IsAccepted_AndOversizeIsRejectedBeforeClientCreation()
    {
        byte[] maximum = new byte[GoogleDriveTextReplacementApi.MaxTextContentBytes];
        Array.Fill(maximum, (byte)'x');
        var acceptedClient = new RecordingTextReplacementClient();
        using GoogleAuthorizedCredential credential = Credential();

        await Api(acceptedClient).ReplaceTextContentAsync(
            credential,
            FileId,
            maximum,
            GoogleDriveTextCreationMediaTypes.Json,
            CancellationToken.None);

        Assert.Equal(maximum, Assert.Single(acceptedClient.Contents));

        byte[] oversized =
            new byte[GoogleDriveTextReplacementApi.MaxTextContentBytes + 1];
        var factory = new RecordingTextReplacementClientFactory(
            new RecordingTextReplacementClient());
        var rejectedApi = new GoogleDriveTextReplacementApi(factory);
        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => rejectedApi.ReplaceTextContentAsync(
                    credential,
                    FileId,
                    oversized,
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextReplacementErrorCodes.ContentTooLarge,
            exception.Details.SafeErrorCode);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task InvalidUtf8_IsRejectedBeforeClientCreation()
    {
        byte[] invalid = [0xC3, 0x28];
        var factory = new RecordingTextReplacementClientFactory(
            new RecordingTextReplacementClient());
        var api = new GoogleDriveTextReplacementApi(factory);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => api.ReplaceTextContentAsync(
                    credential,
                    FileId,
                    invalid,
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextReplacementErrorCodes.InvalidUtf8,
            exception.Details.SafeErrorCode);
        Assert.Equal(0, factory.CreateCalls);
        Assert.DoesNotContain(Convert.ToHexString(invalid), exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public async Task InvalidOrUnsupportedMetadata_FailsBeforeUpdate(
        int metadataCase,
        string expectedErrorCode)
    {
        var client = new RecordingTextReplacementClient
        {
            Metadata = MetadataCase(metadataCase)
        };
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => Api(client).ReplaceTextContentAsync(
                    credential,
                    FileId,
                    Encoding.UTF8.GetBytes("{}"),
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.Details.SafeErrorCode);
        Assert.Empty(client.UpdateRequests);
        Assert.Equal(1, client.DisposeCalls);
        Assert.DoesNotContain(FileId, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task InvalidUpdateResponse_FailsClosedWithoutChangingIdentity(
        int responseCase,
        string expectedErrorCode)
    {
        var client = new RecordingTextReplacementClient
        {
            Response = ResponseCase(responseCase)
        };
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => Api(client).ReplaceTextContentAsync(
                    credential,
                    FileId,
                    Encoding.UTF8.GetBytes("{}"),
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.Details.SafeErrorCode);
        Assert.Single(client.UpdateRequests);
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
        Assert.DoesNotContain(FileId, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeMetadata_DoesNotCreateClient()
    {
        var factory = new RecordingTextReplacementClientFactory(
            new RecordingTextReplacementClient());
        var api = new GoogleDriveTextReplacementApi(factory);
        using GoogleAuthorizedCredential credential = Credential();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.ReplaceTextContentAsync(
                credential,
                FileId,
                Encoding.UTF8.GetBytes("{}"),
                GoogleDriveTextCreationMediaTypes.Json,
                cancellation.Token));

        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task CancellationDuringUpdate_IsForwardedAndDisposesResources()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingTextReplacementClient
        {
            UpdateHandler = (_, _, cancellationToken) =>
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Response());
            }
        };
        using GoogleAuthorizedCredential credential = Credential();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Api(client).ReplaceTextContentAsync(
                credential,
                FileId,
                Encoding.UTF8.GetBytes("{}"),
                GoogleDriveTextCreationMediaTypes.Json,
                cancellation.Token));

        Assert.Equal(cancellation.Token, Assert.Single(client.UpdateTokens));
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task LateMetadataResultAfterCancellation_IsRejectedBeforeUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingTextReplacementClient
        {
            MetadataHandler = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(Metadata());
            }
        };
        using GoogleAuthorizedCredential credential = Credential();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Api(client).ReplaceTextContentAsync(
                credential,
                FileId,
                Encoding.UTF8.GetBytes("{}"),
                GoogleDriveTextCreationMediaTypes.Json,
                cancellation.Token));

        Assert.Empty(client.UpdateRequests);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task LateUpdateResultAfterCancellation_IsRejectedAndDisposesResources()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingTextReplacementClient
        {
            UpdateHandler = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(Response());
            }
        };
        using GoogleAuthorizedCredential credential = Credential();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Api(client).ReplaceTextContentAsync(
                credential,
                FileId,
                Encoding.UTF8.GetBytes("{}"),
                GoogleDriveTextCreationMediaTypes.Json,
                cancellation.Token));

        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task ProviderFailures_UseSharedSafeClassification(
        HttpStatusCode status,
        string reason,
        int expectedFailureValue,
        bool retryable)
    {
        const string privateMarker = "private-token-response-object-marker";
        var providerError = new GoogleApiException("Drive", privateMarker)
        {
            HttpStatusCode = status,
            Error = new RequestError
            {
                Errors = new List<SingleError>
                {
                    new() { Reason = reason }
                }
            }
        };
        var client = new RecordingTextReplacementClient
        {
            UpdateFailure = providerError
        };
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => Api(client).ReplaceTextContentAsync(
                    credential,
                    FileId,
                    Encoding.UTF8.GetBytes("{}"),
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        var expectedFailure = (GoogleDriveApiFailure)expectedFailureValue;
        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal("GoogleDriveTextReplacement" + expectedFailure,
            exception.Details.SafeErrorCode);
        Assert.Equal(retryable, exception.Details.Retryable);
        Assert.DoesNotContain(privateMarker, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaProviderError_UsesBoundedReasonParsingAndSharedMapping()
    {
        const string rawBody =
            "{\"error\":{\"message\":\"private response body\"," +
            "\"errors\":[{\"reason\":\"storageQuotaExceeded\"}]}}";
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                rawBody,
                Encoding.UTF8,
                "application/json")
        };

        GoogleApiException providerFailure =
            await GoogleDriveTextReplacementClient.CreateProviderExceptionAsync(
                response,
                CancellationToken.None);
        GoogleDriveApiException mapped =
            GoogleDriveTextReplacementApi.MapException(
                providerFailure,
                GoogleDriveApiOperation.TextContentReplace);

        Assert.Equal(GoogleDriveApiFailure.QuotaExceeded, mapped.Failure);
        Assert.Equal("storageQuotaExceeded", mapped.Details.Reason);
        Assert.Equal("GoogleDriveTextReplacementQuotaExceeded",
            mapped.Details.SafeErrorCode);
        Assert.DoesNotContain("private response body", mapped.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(rawBody, mapped.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaUpdateRequest_IsContentOnlyAndNonResumable()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"version\":2}");
        var contract = new GoogleDriveTextReplacementRequest(
            FileId,
            bytes.Length,
            GoogleDriveTextCreationMediaTypes.Json);
        using var stream = new MemoryStream(bytes, writable: false);
        using HttpRequestMessage request =
            GoogleDriveTextReplacementClient.CreateUpdateHttpRequest(
                contract,
                stream);

        Assert.Equal(HttpMethod.Patch, request.Method);
        string uri = request.RequestUri!.AbsoluteUri;
        Assert.Contains(Uri.EscapeDataString(FileId), uri, StringComparison.Ordinal);
        Assert.Contains("uploadType=media", uri, StringComparison.Ordinal);
        Assert.Contains("supportsAllDrives=false", uri, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("id,driveId"), uri,
            StringComparison.Ordinal);
        Assert.DoesNotContain("resumable", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parents", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permissions", uri, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(request.Content);
        Assert.IsNotType<MultipartContent>(request.Content);
        Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void MetadataRequestAndResponseMapping_KeepOnlyValidationIdentity()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var contract = new GoogleDriveTextReplacementMetadataRequest(FileId);
        FilesResource.GetRequest sdkRequest =
            GoogleDriveTextReplacementClient.CreateMetadataRequest(
                drive,
                contract);
        GoogleDriveTextReplacementMetadata metadata =
            GoogleDriveTextReplacementClient.MapMetadata(
                new Google.Apis.Drive.v3.Data.File
                {
                    Id = FileId,
                    MimeType = "application/json",
                    Trashed = false,
                    DriveId = null,
                    Name = "private-name-that-must-not-be-mapped",
                    Parents = ["private-parent-that-must-not-be-mapped"]
                });
        GoogleDriveTextReplacementResponse response =
            GoogleDriveTextReplacementClient.MapResponse(
                new Google.Apis.Drive.v3.Data.File
                {
                    Id = FileId,
                    DriveId = null
                });

        Assert.Equal(FileId, sdkRequest.FileId);
        Assert.Equal("id,mimeType,trashed,driveId", sdkRequest.Fields);
        Assert.True(sdkRequest.SupportsAllDrives);
        Assert.Equal(FileId, metadata.Id);
        Assert.Equal("application/json", metadata.MimeType);
        Assert.False(metadata.Trashed);
        Assert.Null(metadata.DriveId);
        Assert.Equal(FileId, response.Id);
        Assert.Null(response.DriveId);
        Assert.DoesNotContain(FileId, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FileId, response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementBoundary_HasNoNameLookupOrCreateDeleteFallback()
    {
        string[] clientMethods = typeof(IGoogleDriveTextReplacementClient)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();
        string[] apiMethods = typeof(IGoogleDriveTextReplacementApi)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(
            new[] { "GetMetadataAsync", "UpdateContentAsync" },
            clientMethods);
        Assert.Equal(new[] { "ReplaceTextContentAsync" }, apiMethods);
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Create", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Search", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Move", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(GoogleDriveRemoteFileSystem)
                .GetFields(System.Reflection.BindingFlags.Instance |
                           System.Reflection.BindingFlags.NonPublic)
                .Select(field => field.FieldType),
            type => type == typeof(IGoogleDriveTextReplacementApi));
    }

    [Fact]
    public void DependencyInjection_ResolvesApiWithoutCreatingDriveClient()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<IGoogleDriveTextReplacementApi>();
        var factory = provider.GetRequiredService<
            IGoogleDriveTextReplacementClientFactory>();

        Assert.IsType<GoogleDriveTextReplacementApi>(api);
        Assert.IsType<GoogleDriveTextReplacementClientFactory>(factory);
    }

    private static GoogleDriveTextReplacementApi Api(
        RecordingTextReplacementClient client) =>
        new(new RecordingTextReplacementClientFactory(client));

    private static GoogleDriveTextReplacementMetadata Metadata(
        string? id = FileId,
        string? mimeType = "application/json",
        bool? trashed = false,
        string? driveId = null) =>
        new(id, mimeType, trashed, driveId);

    private static GoogleDriveTextReplacementMetadata MetadataCase(int value) =>
        value switch
        {
            0 => null!,
            1 => Metadata(id: null),
            2 => Metadata(id: "different-private-file-id"),
            3 => Metadata(mimeType: null),
            4 => Metadata(trashed: null),
            5 => Metadata(trashed: true),
            6 => Metadata(mimeType: GoogleDriveApplicationRoot.FolderMimeType),
            7 => Metadata(mimeType: "application/vnd.google-apps.document"),
            8 => Metadata(driveId: "private-shared-drive-id"),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static GoogleDriveTextReplacementResponse Response(
        string? id = FileId,
        string? driveId = null) =>
        new(id, driveId);

    private static GoogleDriveTextReplacementResponse ResponseCase(int value) =>
        value switch
        {
            0 => null!,
            1 => Response(id: null),
            2 => Response(id: "different-private-file-id"),
            3 => Response(driveId: "private-shared-drive-id"),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

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
            "text-replacement-test-user",
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private static void AssertCapturedStreamDisposed(
        RecordingTextReplacementClient client)
    {
        Assert.NotNull(client.ContentStream);
        Assert.Throws<ObjectDisposedException>(() => client.ContentStream.ReadByte());
    }

    private sealed class RecordingTextReplacementClientFactory(
        RecordingTextReplacementClient client)
        : IGoogleDriveTextReplacementClientFactory
    {
        public int CreateCalls { get; private set; }

        public IGoogleDriveTextReplacementClient Create(
            GoogleAuthorizedCredential credential)
        {
            Assert.NotNull(credential);
            CreateCalls++;
            return client;
        }
    }

    private sealed class RecordingTextReplacementClient
        : IGoogleDriveTextReplacementClient
    {
        public GoogleDriveTextReplacementMetadata Metadata { get; set; } =
            GoogleDriveTextReplacementApiTests.Metadata();

        public GoogleDriveTextReplacementResponse Response { get; set; } =
            GoogleDriveTextReplacementApiTests.Response();

        public Exception? MetadataFailure { get; set; }

        public Exception? UpdateFailure { get; set; }

        public Func<CancellationToken, Task<GoogleDriveTextReplacementMetadata>>?
            MetadataHandler { get; set; }

        public Func<GoogleDriveTextReplacementRequest, Stream, CancellationToken,
            Task<GoogleDriveTextReplacementResponse>>? UpdateHandler { get; set; }

        public List<GoogleDriveTextReplacementMetadataRequest> MetadataRequests
            { get; } = [];

        public List<GoogleDriveTextReplacementRequest> UpdateRequests
            { get; } = [];

        public List<byte[]> Contents { get; } = [];

        public List<CancellationToken> UpdateTokens { get; } = [];

        public Stream? ContentStream { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveTextReplacementMetadata> GetMetadataAsync(
            GoogleDriveTextReplacementMetadataRequest request,
            CancellationToken cancellationToken)
        {
            MetadataRequests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (MetadataFailure is not null)
                throw MetadataFailure;
            if (MetadataHandler is not null)
                return MetadataHandler(cancellationToken);
            return Task.FromResult(Metadata);
        }

        public async Task<GoogleDriveTextReplacementResponse> UpdateContentAsync(
            GoogleDriveTextReplacementRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            UpdateRequests.Add(request);
            UpdateTokens.Add(cancellationToken);
            ContentStream = content;
            cancellationToken.ThrowIfCancellationRequested();

            if (UpdateHandler is not null)
                return await UpdateHandler(request, content, cancellationToken);
            if (UpdateFailure is not null)
                throw UpdateFailure;

            using var captured = new MemoryStream();
            await content.CopyToAsync(captured, cancellationToken);
            Contents.Add(captured.ToArray());
            return Response;
        }

        public void Dispose() => DisposeCalls++;
    }
}
