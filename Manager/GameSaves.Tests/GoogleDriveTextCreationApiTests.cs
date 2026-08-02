using System.Net;
using System.Text;
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

public sealed class GoogleDriveTextCreationApiTests
{
    private const string ParentId = "private-authoritative-parent-id";
    private const string FileId = "private-created-file-id";
    private const string FileName = "保存 O'Brien\\sync-log.json";

    public static IEnumerable<object[]> InvalidResponses()
    {
        yield return [0, GoogleDriveTextCreationErrorCodes.InvalidResponse];
        yield return [1, GoogleDriveTextCreationErrorCodes.InvalidResponse];
        yield return [2, GoogleDriveTextCreationErrorCodes.InvalidResponse];
        yield return [3, GoogleDriveTextCreationErrorCodes.NameMismatch];
        yield return [4, GoogleDriveTextCreationErrorCodes.InvalidResponse];
        yield return [5, GoogleDriveTextCreationErrorCodes.MimeTypeMismatch];
        yield return [6, GoogleDriveTextCreationErrorCodes.InvalidResponse];
        yield return [7, GoogleDriveTextCreationErrorCodes.Trashed];
        yield return [8, GoogleDriveTextCreationErrorCodes.InvalidResponse];
        yield return [9, GoogleDriveTextCreationErrorCodes.ParentMismatch];
        yield return [10, GoogleDriveTextCreationErrorCodes.ParentMismatch];
        yield return [11, GoogleDriveTextCreationErrorCodes.ParentMismatch];
        yield return [12, GoogleDriveTextCreationErrorCodes.UnsupportedLocation];
    }

    [Fact]
    public void RequestContract_PreservesExactIdentityAndUsesMinimalSafeMetadata()
    {
        var request = new GoogleDriveTextCreateRequest(
            ParentId,
            FileName,
            contentLength: 17,
            GoogleDriveTextCreationMediaTypes.Json);
        string diagnostics = request.ToString();

        Assert.Equal(ParentId, request.ParentFolderId);
        Assert.Equal(new[] { ParentId }, request.ParentIds);
        Assert.Equal(FileName, request.ExactFileName);
        Assert.Equal(17, request.ContentLength);
        Assert.Equal("application/json", request.MediaType);
        Assert.Equal(
            "id,name,mimeType,trashed,parents,driveId",
            request.Fields);
        Assert.False(request.SupportsAllDrives);
        Assert.DoesNotContain(ParentId, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions", request.Fields,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owners", request.Fields,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("size", request.Fields,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTextFileAsync_PreservesUtf8ContentNameAndOneParent()
    {
        const string json = "{\"name\":\"保存\",\"enabled\":true}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var client = new RecordingTextCreationClient();
        var factory = new RecordingTextCreationClientFactory(client);
        var api = new GoogleDriveTextCreationApi(factory);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveTextCreationResult result = await api.CreateTextFileAsync(
            credential,
            ParentId,
            FileName,
            bytes,
            GoogleDriveTextCreationMediaTypes.Json,
            CancellationToken.None);

        GoogleDriveTextCreateRequest request = Assert.Single(client.Requests);
        Assert.Equal(ParentId, request.ParentFolderId);
        Assert.Equal(new[] { ParentId }, request.ParentIds);
        Assert.Equal(FileName, request.ExactFileName);
        Assert.Equal(bytes.Length, request.ContentLength);
        Assert.Equal(bytes, Assert.Single(client.Contents));
        Assert.Equal(FileId, result.FileId);
        Assert.DoesNotContain(FileId, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task EmptyContent_IsAValidBoundedUtf8Blob()
    {
        var client = new RecordingTextCreationClient();
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveTextCreationResult result = await api.CreateTextFileAsync(
            credential,
            ParentId,
            FileName,
            ReadOnlyMemory<byte>.Empty,
            GoogleDriveTextCreationMediaTypes.Json,
            CancellationToken.None);

        Assert.Equal(FileId, result.FileId);
        Assert.Empty(Assert.Single(client.Contents));
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task MaximumContentSize_IsAcceptedWithoutChangingBytes()
    {
        byte[] bytes = new byte[GoogleDriveTextCreationApi.MaxTextContentBytes];
        Array.Fill(bytes, (byte)'x');
        var client = new RecordingTextCreationClient();
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        await api.CreateTextFileAsync(
            credential,
            ParentId,
            FileName,
            bytes,
            GoogleDriveTextCreationMediaTypes.Json,
            CancellationToken.None);

        Assert.Equal(bytes, Assert.Single(client.Contents));
    }

    [Fact]
    public async Task OversizedContent_IsRejectedBeforeClientCreation()
    {
        byte[] bytes = new byte[GoogleDriveTextCreationApi.MaxTextContentBytes + 1];
        var factory = new RecordingTextCreationClientFactory(
            new RecordingTextCreationClient());
        var api = new GoogleDriveTextCreationApi(factory);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => api.CreateTextFileAsync(
                    credential,
                    ParentId,
                    FileName,
                    bytes,
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextCreationErrorCodes.ContentTooLarge,
            exception.Details.SafeErrorCode);
        Assert.False(exception.Details.Retryable);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task InvalidUtf8_IsRejectedBeforeClientCreation()
    {
        byte[] bytes = [0xC3, 0x28];
        var factory = new RecordingTextCreationClientFactory(
            new RecordingTextCreationClient());
        var api = new GoogleDriveTextCreationApi(factory);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => api.CreateTextFileAsync(
                    credential,
                    ParentId,
                    FileName,
                    bytes,
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveTextCreationErrorCodes.InvalidUtf8,
            exception.Details.SafeErrorCode);
        Assert.Equal(0, factory.CreateCalls);
        Assert.DoesNotContain(Convert.ToHexString(bytes), exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task InvalidCreateResponse_FailsClosedAndDisposesClient(
        int responseCase,
        string expectedErrorCode)
    {
        var client = new RecordingTextCreationClient
        {
            Response = InvalidResponse(responseCase)
        };
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => api.CreateTextFileAsync(
                    credential,
                    ParentId,
                    FileName,
                    Encoding.UTF8.GetBytes("{}"),
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.Details.SafeErrorCode);
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
        Assert.DoesNotContain(FileId, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ParentId, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeCreation_DoesNotCreateAClient()
    {
        var factory = new RecordingTextCreationClientFactory(
            new RecordingTextCreationClient());
        var api = new GoogleDriveTextCreationApi(factory);
        using GoogleAuthorizedCredential credential = Credential();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.CreateTextFileAsync(
                credential,
                ParentId,
                FileName,
                Encoding.UTF8.GetBytes("{}"),
                GoogleDriveTextCreationMediaTypes.Json,
                cancellation.Token));

        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task CancellationDuringCreation_IsForwardedAndDisposesResources()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingTextCreationClient
        {
            Handler = (_, _, cancellationToken) =>
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Response());
            }
        };
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.CreateTextFileAsync(
                credential,
                ParentId,
                FileName,
                Encoding.UTF8.GetBytes("{}"),
                GoogleDriveTextCreationMediaTypes.Json,
                cancellation.Token));

        Assert.Equal(cancellation.Token, Assert.Single(client.CancellationTokens));
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task QuotaFailure_UsesTheSharedClassifierWithoutPrivateDiagnostics()
    {
        const string privateMarker = "token-response-and-object-marker";
        var providerError = new GoogleApiException("Drive", privateMarker)
        {
            HttpStatusCode = HttpStatusCode.Forbidden,
            Error = new RequestError
            {
                Errors = new List<SingleError>
                {
                    new() { Reason = "storageQuotaExceeded" }
                }
            }
        };
        var client = new RecordingTextCreationClient
        {
            Failure = providerError
        };
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => api.CreateTextFileAsync(
                    credential,
                    ParentId,
                    FileName,
                    Encoding.UTF8.GetBytes("{}"),
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(GoogleDriveApiFailure.QuotaExceeded, exception.Failure);
        Assert.Equal("GoogleDriveTextCreationQuotaExceeded",
            exception.Details.SafeErrorCode);
        Assert.DoesNotContain(privateMarker, exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task MultipartProviderError_UsesBoundedReasonParsingAndSharedMapping()
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
            await GoogleDriveTextCreationClient.CreateProviderExceptionAsync(
                response,
                CancellationToken.None);
        GoogleDriveApiException mapped =
            GoogleDriveTextCreationApi.MapException(providerFailure);

        Assert.Equal(GoogleDriveApiFailure.QuotaExceeded, mapped.Failure);
        Assert.Equal("storageQuotaExceeded", mapped.Details.Reason);
        Assert.Equal("GoogleDriveTextCreationQuotaExceeded",
            mapped.Details.SafeErrorCode);
        Assert.DoesNotContain("private response body", mapped.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(rawBody, mapped.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialProviderFailure_IsNeverReturnedAsCreated()
    {
        const string privateMarker = "partial-upload-private-marker";
        var client = new RecordingTextCreationClient
        {
            Failure = new IOException(privateMarker)
        };
        var api = Api(client);
        using GoogleAuthorizedCredential credential = Credential();

        GoogleDriveApiException exception =
            await Assert.ThrowsAsync<GoogleDriveApiException>(
                () => api.CreateTextFileAsync(
                    credential,
                    ParentId,
                    FileName,
                    Encoding.UTF8.GetBytes("{}"),
                    GoogleDriveTextCreationMediaTypes.Json,
                    CancellationToken.None));

        Assert.Equal(GoogleDriveApiFailure.Failed, exception.Failure);
        Assert.Equal("GoogleDriveTextCreationFailed",
            exception.Details.SafeErrorCode);
        Assert.DoesNotContain(privateMarker, exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1, client.DisposeCalls);
        AssertCapturedStreamDisposed(client);
    }

    [Fact]
    public async Task SdkRequest_IsOneNonResumableMultipartCreateWithExactMetadata()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"version\":1}");
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var contract = new GoogleDriveTextCreateRequest(
            ParentId,
            FileName,
            bytes.Length,
            GoogleDriveTextCreationMediaTypes.Json);
        using var stream = new MemoryStream(bytes, writable: false);
        using HttpRequestMessage request =
            GoogleDriveTextCreationClient.CreateHttpRequest(
                drive,
                contract,
                stream);

        Assert.Equal(HttpMethod.Post, request.Method);
        string uri = request.RequestUri!.AbsoluteUri;
        Assert.Contains("uploadType=multipart", uri, StringComparison.Ordinal);
        Assert.Contains("supportsAllDrives=false", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("resumable", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ParentId, uri, StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, uri, StringComparison.Ordinal);
        Assert.Contains(
            Uri.EscapeDataString(contract.Fields),
            uri,
            StringComparison.Ordinal);

        var multipart = Assert.IsType<MultipartContent>(request.Content);
        HttpContent[] parts = multipart.ToArray();
        Assert.Equal(2, parts.Length);
        string metadata = await parts[0].ReadAsStringAsync();
        Google.Apis.Drive.v3.Data.File metadataFile =
            drive.Serializer.Deserialize<Google.Apis.Drive.v3.Data.File>(metadata);
        Assert.Equal(FileName, metadataFile.Name);
        Assert.Equal(new[] { ParentId }, metadataFile.Parents);
        Assert.Equal(GoogleDriveTextCreationMediaTypes.Json, metadataFile.MimeType);
        Assert.Equal(bytes, await parts[1].ReadAsByteArrayAsync());
        Assert.Equal(
            GoogleDriveTextCreationMediaTypes.Json,
            parts[1].Headers.ContentType!.MediaType);
    }

    [Fact]
    public void SdkResponseMapping_PreservesEveryRequiredValidationFieldSafely()
    {
        GoogleDriveTextCreationResponse response =
            GoogleDriveTextCreationClient.Map(new Google.Apis.Drive.v3.Data.File
            {
                Id = FileId,
                Name = FileName,
                MimeType = GoogleDriveTextCreationMediaTypes.Json,
                Trashed = false,
                Parents = [ParentId],
                DriveId = null
            });
        string diagnostics = response.ToString();

        Assert.Equal(FileId, response.Id);
        Assert.Equal(FileName, response.Name);
        Assert.Equal(GoogleDriveTextCreationMediaTypes.Json, response.MimeType);
        Assert.False(response.Trashed);
        Assert.Equal(new[] { ParentId }, response.ParentIds);
        Assert.Null(response.DriveId);
        Assert.DoesNotContain(FileId, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(ParentId, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void CreationBoundary_ExposesNoUpdateDeleteOrOtherMutationMethods()
    {
        string[] clientMethods = typeof(IGoogleDriveTextCreationClient)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();
        string[] apiMethods = typeof(IGoogleDriveTextCreationApi)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { "CreateAsync" }, clientMethods);
        Assert.Equal(new[] { "CreateTextFileAsync" }, apiMethods);
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Update", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Trash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clientMethods,
            name => name.Contains("Move", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(GoogleDriveRemoteFileSystem)
                .GetFields(System.Reflection.BindingFlags.Instance |
                           System.Reflection.BindingFlags.NonPublic)
                .Select(field => field.FieldType),
            type => type == typeof(IGoogleDriveTextCreationApi));
    }

    [Fact]
    public void DependencyInjection_ResolvesApiWithoutCreatingADriveClient()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<IGoogleDriveTextCreationApi>();
        var factory = provider.GetRequiredService<IGoogleDriveTextCreationClientFactory>();

        Assert.IsType<GoogleDriveTextCreationApi>(api);
        Assert.IsType<GoogleDriveTextCreationClientFactory>(factory);
    }

    private static GoogleDriveTextCreationApi Api(
        RecordingTextCreationClient client) =>
        new(new RecordingTextCreationClientFactory(client));

    private static GoogleDriveTextCreationResponse Response(
        string? id = FileId,
        string? name = FileName,
        string? mimeType = GoogleDriveTextCreationMediaTypes.Json,
        bool? trashed = false,
        IEnumerable<string>? parentIds = null,
        string? driveId = null) =>
        new(
            id,
            name,
            mimeType,
            trashed,
            parentIds ?? [ParentId],
            driveId);

    private static GoogleDriveTextCreationResponse InvalidResponse(int value) =>
        value switch
        {
            0 => null!,
            1 => Response(id: null),
            2 => Response(name: null),
            3 => Response(name: "other.json"),
            4 => Response(mimeType: null),
            5 => Response(mimeType: "text/plain"),
            6 => Response(trashed: null),
            7 => Response(trashed: true),
            8 => new GoogleDriveTextCreationResponse(
                FileId,
                FileName,
                GoogleDriveTextCreationMediaTypes.Json,
                trashed: false,
                parentIds: null,
                driveId: null),
            9 => Response(parentIds: []),
            10 => Response(parentIds: ["other-parent"]),
            11 => Response(parentIds: [ParentId, "other-parent"]),
            12 => Response(driveId: "private-shared-drive-id"),
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
            "text-creation-test-user",
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private static void AssertCapturedStreamDisposed(
        RecordingTextCreationClient client)
    {
        Assert.NotNull(client.ContentStream);
        Assert.Throws<ObjectDisposedException>(() => client.ContentStream.ReadByte());
    }

    private sealed class RecordingTextCreationClientFactory(
        RecordingTextCreationClient client)
        : IGoogleDriveTextCreationClientFactory
    {
        public int CreateCalls { get; private set; }

        public IGoogleDriveTextCreationClient Create(
            GoogleAuthorizedCredential credential)
        {
            Assert.NotNull(credential);
            CreateCalls++;
            return client;
        }
    }

    private sealed class RecordingTextCreationClient
        : IGoogleDriveTextCreationClient
    {
        public GoogleDriveTextCreationResponse Response { get; set; } =
            GoogleDriveTextCreationApiTests.Response();

        public Exception? Failure { get; set; }

        public Func<
            GoogleDriveTextCreateRequest,
            Stream,
            CancellationToken,
            Task<GoogleDriveTextCreationResponse>>? Handler { get; set; }

        public List<GoogleDriveTextCreateRequest> Requests { get; } = [];

        public List<byte[]> Contents { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Stream? ContentStream { get; private set; }

        public int DisposeCalls { get; private set; }

        public async Task<GoogleDriveTextCreationResponse> CreateAsync(
            GoogleDriveTextCreateRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CancellationTokens.Add(cancellationToken);
            ContentStream = content;
            cancellationToken.ThrowIfCancellationRequested();

            if (Handler is not null)
                return await Handler(request, content, cancellationToken);
            if (Failure is not null)
                throw Failure;

            using var captured = new MemoryStream();
            await content.CopyToAsync(captured, cancellationToken);
            Contents.Add(captured.ToArray());
            return Response;
        }

        public void Dispose() => DisposeCalls++;
    }
}
