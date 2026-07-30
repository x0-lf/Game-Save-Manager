using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Requests;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Tests;

public sealed class GoogleDriveRootValidationApiTests
{
    [Fact]
    public async Task GetById_UsesAuthoritativeSavedIdAndOnlyOneReadOperation()
    {
        const string rootId = "authoritative-root-id-marker";
        var client = new RecordingRootValidationClient
        {
            Result = Metadata()
        };
        var api = new GoogleDriveRootValidationApi(
            new RecordingRootValidationClientFactory(client));

        GoogleDriveRootValidationMetadata result = await api.GetByIdAsync(
            null!,
            rootId,
            CancellationToken.None);

        GoogleDriveRootValidationRequest request = Assert.Single(client.Requests);
        Assert.Equal(rootId, request.RootFolderId);
        Assert.True(result.IsFolder);
        Assert.Equal(1, client.DisposeCalls);
        Assert.DoesNotContain(rootId, request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Request_UsesExactCapabilityFieldsWithoutUnnecessaryMetadata()
    {
        var request = new GoogleDriveRootValidationRequest("root-id");

        Assert.Equal(
            "id,name,mimeType,trashed,parents,driveId," +
            "capabilities(canListChildren,canAddChildren)",
            request.Fields);
        Assert.True(request.SupportsAllDrives);
        Assert.DoesNotContain("owners", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permissions", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quota", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sharing", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumbnail", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revisions", request.Fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", request.Fields, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SdkRequest_IsFilesGetWithTheNarrowAuthoritativeIdContract()
    {
        const string rootId = "authoritative-root-id";
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var contract = new GoogleDriveRootValidationRequest(rootId);

        FilesResource.GetRequest request =
            GoogleDriveRootValidationClient.CreateGetRequest(drive, contract);

        Assert.Equal(rootId, request.FileId);
        Assert.Equal(contract.Fields, request.Fields);
        Assert.True(request.SupportsAllDrives);
    }

    [Fact]
    public void ValidationBoundary_ExposesNoListCreateUploadOrDownloadOperation()
    {
        Assert.Equal(
            new[] { "GetByIdAsync" },
            typeof(IGoogleDriveRootValidationApi)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray());
        Assert.Equal(
            new[] { "GetAsync" },
            typeof(IGoogleDriveRootValidationClient)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray());

        string[] productionMethods = typeof(GoogleDriveRootValidationClient)
            .GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("ListAsync", productionMethods);
        Assert.DoesNotContain("CreateAsync", productionMethods);
        Assert.DoesNotContain("CreateFolderAsync", productionMethods);
        Assert.DoesNotContain("UploadAsync", productionMethods);
        Assert.DoesNotContain("DownloadAsync", productionMethods);
    }

    [Fact]
    public void FolderMetadata_MapsTypeParentsAndCapabilitiesExactly()
    {
        var file = new DriveFile
        {
            Id = "private-root-id-marker",
            Name = "Backup root",
            MimeType = GoogleDriveApplicationRoot.FolderMimeType,
            Trashed = false,
            Parents = new[] { "private-parent-id-marker" },
            Capabilities = new DriveFile.CapabilitiesData
            {
                CanListChildren = true,
                CanAddChildren = true
            }
        };

        GoogleDriveRootValidationMetadata metadata =
            GoogleDriveRootValidationClient.Map(file);

        Assert.True(metadata.IsFolder);
        Assert.False(metadata.Trashed);
        Assert.Equal(new[] { "private-parent-id-marker" }, metadata.ParentIds);
        Assert.True(metadata.CanListChildren);
        Assert.True(metadata.CanAddChildren);
        Assert.False(metadata.IsInSharedDrive);
        Assert.DoesNotContain("private-root-id-marker", metadata.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-parent-id-marker", metadata.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrashedMetadata_IsPreserved()
    {
        GoogleDriveRootValidationMetadata metadata =
            GoogleDriveRootValidationClient.Map(new DriveFile
            {
                Name = "Backup root",
                MimeType = GoogleDriveApplicationRoot.FolderMimeType,
                Trashed = true
            });

        Assert.True(metadata.Trashed);
        Assert.Contains("trashed=True", metadata.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedDriveMetadata_IsPreservedForMyDriveRejection()
    {
        GoogleDriveRootValidationMetadata metadata =
            GoogleDriveRootValidationClient.Map(new DriveFile
            {
                Name = "Backup root",
                MimeType = GoogleDriveApplicationRoot.FolderMimeType,
                DriveId = "private-shared-drive-id-marker"
            });

        Assert.True(metadata.IsInSharedDrive);
        Assert.DoesNotContain("private-shared-drive-id-marker", metadata.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrFalseCapabilities_ArePreservedConservatively()
    {
        GoogleDriveRootValidationMetadata missing =
            GoogleDriveRootValidationClient.Map(new DriveFile
            {
                Name = "Backup root",
                MimeType = GoogleDriveApplicationRoot.FolderMimeType
            });
        GoogleDriveRootValidationMetadata explicitlyFalse =
            GoogleDriveRootValidationClient.Map(new DriveFile
            {
                Name = "Backup root",
                MimeType = GoogleDriveApplicationRoot.FolderMimeType,
                Capabilities = new DriveFile.CapabilitiesData
                {
                    CanListChildren = false,
                    CanAddChildren = false
                }
            });

        Assert.False(missing.CanListChildren);
        Assert.False(missing.CanAddChildren);
        Assert.False(explicitlyFalse.CanListChildren);
        Assert.False(explicitlyFalse.CanAddChildren);
    }

    [Fact]
    public void NotFound_MapsToSafeMissingValidationState()
    {
        GoogleDriveApiException exception = MapProviderFailure(
            HttpStatusCode.NotFound,
            reason: null);
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.NotFound, exception.Failure);
        Assert.Equal(GoogleDriveRemoteValidationStatus.RootMissing, result.Status);
        Assert.Equal(GoogleDriveRemoteValidationErrorCodes.RootMissing, result.ErrorCode);
    }

    [Fact]
    public void Unauthorized_MapsThroughAuthorizationRevocation()
    {
        GoogleDriveApiException exception = MapProviderFailure(
            HttpStatusCode.Unauthorized,
            "authError");
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.AuthorizationRevoked, exception.Failure);
        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            result.Status);
    }

    [Fact]
    public void GenericForbidden_DoesNotMapToRevocation()
    {
        GoogleDriveApiException exception = MapProviderFailure(
            HttpStatusCode.Forbidden,
            reason: null);
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.AccessDenied, exception.Failure);
        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootInaccessible,
            result.Status);
        Assert.NotEqual(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            result.Status);
    }

    [Theory]
    [InlineData("rateLimitExceeded")]
    [InlineData("userRateLimitExceeded")]
    public void RateLimitReasons_MapDistinctly(string reason)
    {
        GoogleDriveApiException exception = MapProviderFailure(
            HttpStatusCode.Forbidden,
            reason);
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.RateLimited, exception.Failure);
        Assert.Equal(GoogleDriveRemoteValidationStatus.RateLimited, result.Status);
        Assert.True(result.Retryable);
    }

    [Theory]
    [InlineData("storageQuotaExceeded")]
    [InlineData("quotaExceeded")]
    [InlineData("activeItemCreationLimitExceeded")]
    [InlineData("dailyLimitExceeded")]
    public void QuotaReasons_MapDistinctlyWithoutQuotaRequest(string reason)
    {
        GoogleDriveApiException exception = MapProviderFailure(
            HttpStatusCode.Forbidden,
            reason);
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.QuotaExceeded, exception.Failure);
        Assert.Equal(GoogleDriveRemoteValidationStatus.QuotaExceeded, result.Status);
        Assert.False(result.Retryable);
        Assert.DoesNotContain("quota", GoogleDriveRequestContract.RootValidationMetadataFields,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void ServerFailures_MapToRetryableUnavailable(HttpStatusCode status)
    {
        GoogleDriveApiException exception = MapProviderFailure(status, "backendError");
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.Unavailable, exception.Failure);
        Assert.Equal(GoogleDriveRemoteValidationStatus.Unavailable, result.Status);
        Assert.True(result.Retryable);
    }

    [Fact]
    public void NetworkFailure_MapsToRetryableUnavailable()
    {
        GoogleDriveApiException exception =
            GoogleDriveRootValidationApi.MapException(
                new HttpRequestException("private request URL object-id-marker"));
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Equal(GoogleDriveApiFailure.Unavailable, exception.Failure);
        Assert.Equal(GoogleDriveRemoteValidationStatus.Unavailable, result.Status);
        Assert.True(result.Retryable);
    }

    [Fact]
    public void RawProviderResponseAndIds_AreExcludedFromErrorsAndDiagnostics()
    {
        const string privateMarker =
            "access_token=secret object-id-marker user@example.invalid";
        var providerError = new GoogleApiException("Drive", privateMarker)
        {
            HttpStatusCode = HttpStatusCode.Forbidden,
            Error = new RequestError
            {
                Message = privateMarker,
                Errors = new List<SingleError>
                {
                    new() { Reason = "private-object-id-marker" }
                }
            }
        };

        GoogleDriveApiException exception =
            GoogleDriveRootValidationApi.MapException(providerError);
        GoogleDriveRemoteValidationResult result =
            GoogleDriveRemoteValidationMapper.FromApiFailure(exception.Details);

        Assert.Null(exception.Details.Reason);
        Assert.DoesNotContain("secret", exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object-id-marker", exception.Details.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", result.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DependencyInjection_RegistersValidationApiWithoutCallingDrive()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveRootValidationApi>(
            provider.GetRequiredService<IGoogleDriveRootValidationApi>());
        Assert.IsType<GoogleDriveRootValidationClientFactory>(
            provider.GetRequiredService<IGoogleDriveRootValidationClientFactory>());
    }

    private static GoogleDriveApiException MapProviderFailure(
        HttpStatusCode status,
        string? reason)
    {
        var providerError = new GoogleApiException(
            "Drive",
            "private response body object-id-marker")
        {
            HttpStatusCode = status,
            Error = new RequestError
            {
                Errors = reason is null
                    ? new List<SingleError>()
                    : new List<SingleError> { new() { Reason = reason } }
            }
        };

        return GoogleDriveRootValidationApi.MapException(providerError);
    }

    private static GoogleDriveRootValidationMetadata Metadata() =>
        new(
            "Backup root",
            GoogleDriveApplicationRoot.FolderMimeType,
            trashed: false,
            new[] { "parent-id" },
            driveId: null,
            canListChildren: true,
            canAddChildren: true);

    private sealed class RecordingRootValidationClientFactory
        : IGoogleDriveRootValidationClientFactory
    {
        private readonly RecordingRootValidationClient _client;

        public RecordingRootValidationClientFactory(
            RecordingRootValidationClient client) =>
            _client = client;

        public IGoogleDriveRootValidationClient Create(
            GoogleAuthorizedCredential credential) =>
            _client;
    }

    private sealed class RecordingRootValidationClient
        : IGoogleDriveRootValidationClient
    {
        public List<GoogleDriveRootValidationRequest> Requests { get; } = new();

        public GoogleDriveRootValidationMetadata Result { get; set; } = Metadata();

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveRootValidationMetadata> GetAsync(
            GoogleDriveRootValidationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Result);
        }

        public void Dispose() => DisposeCalls++;
    }
}
