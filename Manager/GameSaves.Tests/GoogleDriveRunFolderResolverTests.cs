using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRunFolderResolverTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("6f41906c-20f2-4970-8cca-29b2e4cba8c7");
    private const string RootFolderId = "authoritative-root-id";
    private const string RunFolderId = "authoritative-run-folder-id";

    [Fact]
    public async Task UniqueFolder_TransfersAuthoritativeIdentityAndContextOwnership()
    {
        GoogleDriveRecursiveFileListingRequest request = Request("archive/run-001");
        var resolver = new RecordingResolver
        {
            Result = Found(request.FolderPath)
        };
        GoogleDriveRemoteOperationContext context = Context(resolver);
        var factory = new RecordingContextFactory { Context = context };
        var service = new GoogleDriveRunFolderResolver(factory);

        GoogleDriveResolvedRunFolder resolved =
            await service.ResolveAsync(request);

        Assert.Equal(RunFolderId, resolved.FolderId);
        Assert.Same(context, resolved.OperationContext);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(ProfileId, factory.ProfileIds.Single());
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(RootFolderId, resolver.RootFolderIds.Single());
        Assert.Same(request.FolderPath, resolver.Paths.Single());
        Assert.Equal(
            GoogleDriveObjectKind.Folder,
            resolver.ExpectedFinalKinds.Single());
        Assert.Equal(0, resolver.FindChildCalls);
        Assert.Equal(0, resolver.EnsureFolderPathCalls);
        Assert.False(context.IsDisposed);

        resolved.Dispose();
        resolved.Dispose();

        Assert.True(resolved.IsDisposed);
        Assert.True(context.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => resolved.OperationContext);
    }

    [Theory]
    [InlineData(
        GoogleDriveObjectResolutionStatus.NotFound,
        GoogleDriveRecursiveFileListingStatus.FolderNotFound,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.InvalidPath,
        GoogleDriveRecursiveFileListingStatus.InvalidPath,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.Ambiguous,
        GoogleDriveRecursiveFileListingStatus.Ambiguous,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.TypeMismatch,
        GoogleDriveRecursiveFileListingStatus.TypeCollision,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.Trashed,
        GoogleDriveRecursiveFileListingStatus.TrashedObject,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.UnsupportedLocation,
        GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.ReauthenticationRequired,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.AccessDenied,
        GoogleDriveRecursiveFileListingStatus.AccessDenied,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.RateLimited,
        GoogleDriveRecursiveFileListingStatus.RateLimited,
        true)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.QuotaExceeded,
        GoogleDriveRecursiveFileListingStatus.QuotaExceeded,
        false)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.Unavailable,
        GoogleDriveRecursiveFileListingStatus.Unavailable,
        true)]
    [InlineData(
        GoogleDriveObjectResolutionStatus.Failed,
        GoogleDriveRecursiveFileListingStatus.Failed,
        false)]
    public async Task ResolutionFailures_MapSafelyAndDisposeCredential(
        object resolutionStatusValue,
        object expectedStatusValue,
        bool retryable)
    {
        GoogleDriveObjectResolutionStatus resolutionStatus =
            (GoogleDriveObjectResolutionStatus)resolutionStatusValue;
        GoogleDriveRecursiveFileListingStatus expectedStatus =
            (GoogleDriveRecursiveFileListingStatus)expectedStatusValue;
        GoogleDriveRecursiveFileListingRequest request = Request("run-001");
        var resolver = new RecordingResolver
        {
            Result = new GoogleDriveObjectResolutionResult(
                resolutionStatus,
                request.FolderPath,
                GoogleDriveObjectKind.Folder)
        };
        GoogleDriveRemoteOperationContext context = Context(resolver);
        GoogleAuthorizedCredential credential = context.Credential;
        var service = new GoogleDriveRunFolderResolver(
            new RecordingContextFactory { Context = context });

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ResolveAsync(request));

        AssertFailure(exception, expectedStatus, retryable);
        Assert.True(context.IsDisposed);
        Assert.True(credential.IsDisposed);
    }

    [Theory]
    [InlineData(
        GoogleDriveApiFailure.AuthorizationRevoked,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
        false)]
    [InlineData(
        GoogleDriveApiFailure.InsufficientScope,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
        false)]
    [InlineData(
        GoogleDriveApiFailure.AccessDenied,
        GoogleDriveRecursiveFileListingStatus.AccessDenied,
        false)]
    [InlineData(
        GoogleDriveApiFailure.NotFound,
        GoogleDriveRecursiveFileListingStatus.FolderNotFound,
        false)]
    [InlineData(
        GoogleDriveApiFailure.RateLimited,
        GoogleDriveRecursiveFileListingStatus.RateLimited,
        true)]
    [InlineData(
        GoogleDriveApiFailure.QuotaExceeded,
        GoogleDriveRecursiveFileListingStatus.QuotaExceeded,
        false)]
    [InlineData(
        GoogleDriveApiFailure.Unavailable,
        GoogleDriveRecursiveFileListingStatus.Unavailable,
        true)]
    public async Task ExistingApiFailures_UseSanitizedListingTaxonomy(
        object apiFailureValue,
        object expectedStatusValue,
        bool retryable)
    {
        GoogleDriveApiFailure apiFailure =
            (GoogleDriveApiFailure)apiFailureValue;
        GoogleDriveRecursiveFileListingStatus expectedStatus =
            (GoogleDriveRecursiveFileListingStatus)expectedStatusValue;
        var resolver = new RecordingResolver
        {
            Exception = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.ObjectChildList,
                apiFailure,
                "SafeFixedCode")
        };
        GoogleDriveRemoteOperationContext context = Context(resolver);
        GoogleAuthorizedCredential credential = context.Credential;
        var service = new GoogleDriveRunFolderResolver(
            new RecordingContextFactory { Context = context });

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ResolveAsync(Request("run-001")));

        AssertFailure(exception, expectedStatus, retryable);
        Assert.True(context.IsDisposed);
        Assert.True(credential.IsDisposed);
        Assert.DoesNotContain(RootFolderId, exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(RunFolderId, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
        false)]
    [InlineData(
        GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
        false)]
    [InlineData(
        GoogleDriveRemoteValidationStatus.AuthenticationUnavailable,
        GoogleDriveRecursiveFileListingStatus.Unavailable,
        true)]
    [InlineData(
        GoogleDriveRemoteValidationStatus.Unavailable,
        GoogleDriveRecursiveFileListingStatus.Unavailable,
        true)]
    public async Task ContextFailures_MapWithoutAttemptingResolution(
        object validationStatusValue,
        object expectedStatusValue,
        bool retryable)
    {
        GoogleDriveRemoteValidationStatus validationStatus =
            (GoogleDriveRemoteValidationStatus)validationStatusValue;
        GoogleDriveRecursiveFileListingStatus expectedStatus =
            (GoogleDriveRecursiveFileListingStatus)expectedStatusValue;
        var factory = new RecordingContextFactory
        {
            Exception = new GoogleDriveRemoteOperationContextException(
                GoogleDriveRemoteValidationMapper.FromStatus(validationStatus))
        };
        var service = new GoogleDriveRunFolderResolver(factory);

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ResolveAsync(Request("run-001")));

        AssertFailure(exception, expectedStatus, retryable);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task NullRequest_FailsBeforeContextCreation()
    {
        var factory = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderResolver(factory);

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ResolveAsync(null!));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidPath,
            retryable: false);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task MismatchedProfileContext_FailsClosedAndDisposesCredential()
    {
        var resolver = new RecordingResolver();
        GoogleDriveRemoteOperationContext context = Context(
            resolver,
            Guid.Parse("fda4dfa5-4554-44a7-a464-251b4d88e8a6"));
        GoogleAuthorizedCredential credential = context.Credential;
        var service = new GoogleDriveRunFolderResolver(
            new RecordingContextFactory { Context = context });

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ResolveAsync(Request("run-001")));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.Failed,
            retryable: false);
        Assert.True(context.IsDisposed);
        Assert.True(credential.IsDisposed);
        Assert.Equal(0, resolver.ResolveCalls);
    }

    [Fact]
    public async Task LateCancellation_RejectsResolutionAndDisposesCredential()
    {
        using var cancellation = new CancellationTokenSource();
        GoogleDriveRecursiveFileListingRequest request = Request("run-001");
        var resolver = new RecordingResolver
        {
            Result = Found(request.FolderPath),
            BeforeReturn = cancellation.Cancel
        };
        GoogleDriveRemoteOperationContext context = Context(resolver);
        GoogleAuthorizedCredential credential = context.Credential;
        var service = new GoogleDriveRunFolderResolver(
            new RecordingContextFactory { Context = context });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveAsync(request, cancellation.Token));

        Assert.Equal(1, resolver.ResolveCalls);
        Assert.True(context.IsDisposed);
        Assert.True(credential.IsDisposed);
    }

    [Fact]
    public async Task LateContextCreation_IsRejectedAndDisposesCredential()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new RecordingResolver();
        GoogleDriveRemoteOperationContext context = Context(resolver);
        GoogleAuthorizedCredential credential = context.Credential;
        var factory = new RecordingContextFactory
        {
            Context = context,
            BeforeReturn = cancellation.Cancel
        };
        var service = new GoogleDriveRunFolderResolver(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveAsync(Request("run-001"), cancellation.Token));

        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.True(context.IsDisposed);
        Assert.True(credential.IsDisposed);
    }

    [Fact]
    public async Task CancellationBeforeContextCreation_PerformsNoRemoteWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderResolver(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveAsync(Request("run-001"), cancellation.Token));

        Assert.Equal(0, factory.CreateCalls);
    }

    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("wrong-name")]
    [InlineData("wrong-type")]
    [InlineData("trashed")]
    [InlineData("shared-drive")]
    public async Task InvalidFoundMetadata_FailsClosedAndDisposesCredential(
        string metadataCase)
    {
        GoogleDriveRecursiveFileListingRequest request = Request("run-001");
        GoogleDriveObjectMetadata? metadata = metadataCase switch
        {
            "missing-metadata" => null,
            "wrong-name" => Metadata(name: "another-run"),
            "wrong-type" => Metadata(mimeType: "application/octet-stream"),
            "trashed" => Metadata(trashed: true),
            "shared-drive" => Metadata(driveId: "private-shared-drive-id"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadataCase))
        };
        var resolver = new RecordingResolver
        {
            Result = new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Found,
                request.FolderPath,
                GoogleDriveObjectKind.Folder,
                metadata,
                RunFolderId)
        };
        GoogleDriveRemoteOperationContext context = Context(resolver);
        GoogleAuthorizedCredential credential = context.Credential;
        var service = new GoogleDriveRunFolderResolver(
            new RecordingContextFactory { Context = context });

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ResolveAsync(request));

        GoogleDriveRecursiveFileListingStatus expectedStatus = metadataCase switch
        {
            "wrong-type" => GoogleDriveRecursiveFileListingStatus.TypeCollision,
            "trashed" => GoogleDriveRecursiveFileListingStatus.TrashedObject,
            "shared-drive" =>
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
            _ => GoogleDriveRecursiveFileListingStatus.InvalidMetadata
        };
        AssertFailure(exception, expectedStatus, retryable: false);
        Assert.True(context.IsDisposed);
        Assert.True(credential.IsDisposed);
        Assert.DoesNotContain("private-shared-drive-id", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessFormatting_RevealsNoProfilePathOrDriveIdentity()
    {
        GoogleDriveRecursiveFileListingRequest request =
            Request("Player's saves/run-測試");
        var resolver = new RecordingResolver
        {
            Result = Found(request.FolderPath, name: "run-測試")
        };
        var service = new GoogleDriveRunFolderResolver(
            new RecordingContextFactory { Context = Context(resolver) });

        using GoogleDriveResolvedRunFolder resolved =
            await service.ResolveAsync(request);
        string diagnostic = resolved.ToString();

        Assert.DoesNotContain(ProfileId.ToString(), diagnostic,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RootFolderId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(RunFolderId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(request.CanonicalFolderPath, diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Player", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("測試", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyRegistration_AddsResolverWithoutStartingRemoteWork()
    {
        var services = new ServiceCollection();

        services.AddGameSavesInfrastructure();

        ServiceDescriptor descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(IGoogleDriveRunFolderResolver));
        Assert.Equal(typeof(GoogleDriveRunFolderResolver),
            descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static GoogleDriveRecursiveFileListingRequest Request(string path) =>
        GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, path);

    private static GoogleDriveObjectResolutionResult Found(
        GoogleDriveRelativePath path,
        string? name = null) =>
        new(
            GoogleDriveObjectResolutionStatus.Found,
            path,
            GoogleDriveObjectKind.Folder,
            Metadata(name ?? path.Segments[^1]),
            RunFolderId);

    private static GoogleDriveObjectMetadata Metadata(
        string name = "run-001",
        string mimeType = GoogleDriveApplicationRoot.FolderMimeType,
        bool trashed = false,
        string? driveId = null) =>
        new(
            RunFolderId,
            name,
            mimeType,
            trashed,
            new[] { "authoritative-parent-id" },
            driveId);

    private static GoogleDriveRemoteOperationContext Context(
        IGoogleDriveObjectPathResolver resolver,
        Guid? profileId = null) =>
        new(
            profileId ?? ProfileId,
            RootFolderId,
            Credential(),
            resolver);

    private static GoogleAuthorizedCredential Credential()
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
            IssuedUtc = new DateTime(2026, 8, 6, 9, 0, 0, DateTimeKind.Utc)
        };
        return new GoogleAuthorizedCredential(
            new UserCredential(flow, ProfileId.ToString("D"), token));
    }

    private static void AssertFailure(
        GoogleDriveRecursiveFileListingException exception,
        GoogleDriveRecursiveFileListingStatus status,
        bool retryable)
    {
        Assert.Equal(status, exception.Result.Status);
        Assert.Equal(retryable, exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
            exception.Result.SafeErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.Result.SafeUserMessage));
        Assert.DoesNotContain(RootFolderId, exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(RunFolderId, exception.ToString(),
            StringComparison.Ordinal);
    }

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        public GoogleDriveRemoteOperationContext? Context { get; set; }
        public Exception? Exception { get; set; }
        public Action? BeforeReturn { get; set; }
        public int CreateCalls { get; private set; }
        public List<Guid> ProfileIds { get; } = new();

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            ProfileIds.Add(remoteProfileId);
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
                throw Exception;
            GoogleDriveRemoteOperationContext context = Context ??
                throw new InvalidOperationException("No context was configured.");
            BeforeReturn?.Invoke();
            return Task.FromResult(context);
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public GoogleDriveObjectResolutionResult? Result { get; set; }
        public Exception? Exception { get; set; }
        public Action? BeforeReturn { get; set; }
        public int FindChildCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public int EnsureFolderPathCalls { get; private set; }
        public List<string> RootFolderIds { get; } = new();
        public List<GoogleDriveRelativePath> Paths { get; } = new();
        public List<GoogleDriveObjectKind?> ExpectedFinalKinds { get; } = new();

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default)
        {
            FindChildCalls++;
            throw new InvalidOperationException("FindChildAsync was not expected.");
        }

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            RootFolderIds.Add(rootFolderId);
            Paths.Add(relativePath);
            ExpectedFinalKinds.Add(expectedFinalKind);
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
                throw Exception;
            GoogleDriveObjectResolutionResult result = Result ??
                throw new InvalidOperationException("No result was configured.");
            BeforeReturn?.Invoke();
            return Task.FromResult(result);
        }

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            EnsureFolderPathCalls++;
            throw new InvalidOperationException(
                "EnsureFolderPathAsync was not expected.");
        }
    }
}
