using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRemoteValidationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingOrEmptyProfile_ReturnsProfileNotFoundWithoutRemoteWork()
    {
        ValidationContext context = CreateContext(addProfile: false);

        GoogleDriveRemoteValidationResult empty =
            await context.Service.ValidateAsync(Guid.Empty);
        GoogleDriveRemoteValidationResult missing =
            await context.Service.ValidateAsync(Guid.NewGuid());

        Assert.Equal(GoogleDriveRemoteValidationStatus.ProfileNotFound, empty.Status);
        Assert.Equal(GoogleDriveRemoteValidationStatus.ProfileNotFound, missing.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
        Assert.Equal(0, context.Api.GetCalls);
    }

    [Fact]
    public async Task WrongProviderKind_IsRejectedBeforeAuthentication()
    {
        SyncRemoteProfile profile = Profile() with
        {
            ProviderKind = SyncProviderKind.LocalFolder,
            ProviderSettings = new LocalFolderSyncRemoteSettings("C:\\Backups")
        };
        ValidationContext context = CreateContext(profile);

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.WrongProviderKind, result.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    [Fact]
    public async Task MissingProviderSettings_IsRejectedBeforeAuthentication()
    {
        SyncRemoteProfile profile = Profile() with { ProviderSettings = null };
        ValidationContext context = CreateContext(profile);

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.UnsupportedScope, result.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task UnsupportedOrUnreadableGoogleSettings_AreRejected(
        int schemaVersion,
        bool hasSettingsError)
    {
        SyncRemoteProfile profile = Profile() with
        {
            ProviderSettings = new UnsupportedGoogleDriveSettings(schemaVersion),
            SettingsError = hasSettingsError
                ? "The saved Google Drive settings are unsupported."
                : null
        };
        ValidationContext context = CreateContext(profile);

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.UnsupportedScope, result.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    [Fact]
    public async Task MissingRootId_ReturnsRootNotConfigured()
    {
        SyncRemoteProfile profile = Profile() with { RemoteFolderId = null };
        ValidationContext context = CreateContext(profile);

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.RootNotConfigured, result.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    public static TheoryData<int, int> SessionFailures => new()
    {
        { (int)GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication,
            (int)GoogleDriveRemoteValidationStatus.NotConnected },
        { (int)GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable,
            (int)GoogleDriveRemoteValidationStatus.AuthenticationUnavailable },
        { (int)GoogleDriveAuthorizedSessionFailure.TokenCorrupted,
            (int)GoogleDriveRemoteValidationStatus.AuthenticationCorrupted },
        { (int)GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked,
            (int)GoogleDriveRemoteValidationStatus.AuthorizationRevoked },
        { (int)GoogleDriveAuthorizedSessionFailure.ReauthenticationRequired,
            (int)GoogleDriveRemoteValidationStatus.ReauthenticationRequired }
    };

    [Theory]
    [MemberData(nameof(SessionFailures))]
    public async Task SessionFailures_MapWithoutOpeningBrowser(
        int failureValue,
        int expectedValue)
    {
        ValidationContext context = CreateContext();
        context.SessionFactory.Failure =
            (GoogleDriveAuthorizedSessionFailure)failureValue;

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal((GoogleDriveRemoteValidationStatus)expectedValue, result.Status);
        Assert.Equal(1, context.SessionFactory.RestoreCalls);
        Assert.Equal(0, context.Api.GetCalls);
        Assert.Equal(
            new[] { "RestoreAsync" },
            typeof(IGoogleDriveAuthorizedSessionFactory)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray());
    }

    [Fact]
    public async Task SilentRefreshSuccess_IsReportedAndValidatesTheRoot()
    {
        ValidationContext context = CreateContext();
        context.SessionFactory.WasAuthenticationRefreshed = true;

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.Valid, result.Status);
        Assert.True(result.WasAuthenticationRefreshed);
        Assert.Equal(1, context.Api.GetCalls);
    }

    public static TheoryData<int, int> ApiFailures => new()
    {
        { (int)GoogleDriveApiFailure.AuthorizationRevoked,
            (int)GoogleDriveRemoteValidationStatus.AuthorizationRevoked },
        { (int)GoogleDriveApiFailure.NotFound,
            (int)GoogleDriveRemoteValidationStatus.RootMissing },
        { (int)GoogleDriveApiFailure.AccessDenied,
            (int)GoogleDriveRemoteValidationStatus.RootInaccessible },
        { (int)GoogleDriveApiFailure.RateLimited,
            (int)GoogleDriveRemoteValidationStatus.RateLimited },
        { (int)GoogleDriveApiFailure.QuotaExceeded,
            (int)GoogleDriveRemoteValidationStatus.QuotaExceeded },
        { (int)GoogleDriveApiFailure.Unavailable,
            (int)GoogleDriveRemoteValidationStatus.Unavailable }
    };

    [Theory]
    [MemberData(nameof(ApiFailures))]
    public async Task DriveFailures_MapSafely(
        int failureValue,
        int expectedValue)
    {
        ValidationContext context = CreateContext();
        context.Api.Failure = (GoogleDriveApiFailure)failureValue;

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal((GoogleDriveRemoteValidationStatus)expectedValue, result.Status);
        Assert.DoesNotContain(context.Profile.RemoteFolderId!, result.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", result.UserMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<int, int> InvalidMetadata => new()
    {
        { 0, (int)GoogleDriveRemoteValidationStatus.RootTrashed },
        { 1, (int)GoogleDriveRemoteValidationStatus.RootWrongType },
        { 2, (int)GoogleDriveRemoteValidationStatus.RootUnsupportedLocation },
        { 3, (int)GoogleDriveRemoteValidationStatus.RootCannotListChildren },
        { 4, (int)GoogleDriveRemoteValidationStatus.RootCannotAddChildren }
    };

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public async Task InvalidRootMetadata_IsRejectedAndClearsRootCache(
        int metadataCase,
        int expectedValue)
    {
        ValidationContext context = CreateContext();
        context.Api.Result = metadataCase switch
        {
            0 => Metadata(trashed: true),
            1 => Metadata(mimeType: "application/octet-stream"),
            2 => Metadata(driveId: "shared-drive-id"),
            3 => Metadata(canListChildren: false),
            4 => Metadata(canAddChildren: false),
            _ => throw new ArgumentOutOfRangeException(nameof(metadataCase))
        };

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal((GoogleDriveRemoteValidationStatus)expectedValue, result.Status);
        Assert.True(result.CacheInvalidated);
        Assert.Single(context.Cache.ClearedScopes);
        Assert.Equal(context.Profile.Id, context.Cache.ClearedScopes[0].RemoteProfileId);
    }

    [Fact]
    public async Task ValidRoot_UsesAuthoritativeIdAndUpdatesTimestamps()
    {
        ValidationContext context = CreateContext();

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.Valid, result.Status);
        Assert.Equal("Backup root", result.RootDisplayName);
        Assert.Equal(new[] { context.Profile.RemoteFolderId! }, context.Api.RootIds);
        Assert.Equal(Now, context.Repository.Profile!.LastUsedUtc);
        Assert.Equal(Now, context.Repository.Profile.LastSuccessfulConnectionUtc);
        Assert.False(result.CacheInvalidated);
    }

    [Fact]
    public async Task MovedMyDriveRoot_RemainsValidByItsAuthoritativeId()
    {
        ValidationContext context = CreateContext();
        context.Api.Result = Metadata(parentIds: new[] { "moved-parent-id" });

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.Valid, result.Status);
        Assert.Equal(new[] { context.Profile.RemoteFolderId! }, context.Api.RootIds);
        Assert.False(result.CacheInvalidated);
    }

    [Fact]
    public async Task AuthorizationRevocation_InvalidatesTheProfileCache()
    {
        ValidationContext context = CreateContext();
        context.SessionFactory.Failure =
            GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked;

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.AuthorizationRevoked, result.Status);
        Assert.True(result.CacheInvalidated);
        Assert.Equal(
            (context.Profile.Id,
                GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation),
            Assert.Single(context.Cache.ProfileInvalidations));
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelledAndDisposesCredential()
    {
        ValidationContext context = CreateContext();
        context.Api.Cancel = true;

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.Cancelled, result.Status);
        Assert.True(context.SessionFactory.LastCredential!.IsDisposed);
        Assert.Equal(0, context.Repository.TimestampUpdates);
    }

    [Fact]
    public async Task ApiFailure_DisposesCredential()
    {
        ValidationContext context = CreateContext();
        context.Api.Failure = GoogleDriveApiFailure.Unavailable;

        await context.Service.ValidateAsync(context.Profile.Id);

        Assert.True(context.SessionFactory.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task TimestampFailure_DoesNotInvalidateSuccessfulValidation()
    {
        ValidationContext context = CreateContext();
        context.Repository.ThrowOnTimestampUpdate = true;

        GoogleDriveRemoteValidationResult result =
            await context.Service.ValidateAsync(context.Profile.Id);

        Assert.Equal(GoogleDriveRemoteValidationStatus.Valid, result.Status);
        Assert.True(context.SessionFactory.LastCredential!.IsDisposed);
    }

    [Fact]
    public void ValidationBoundary_HasNoMutationOrGoogleSdkSurface()
    {
        Assert.Equal(
            new[] { "ValidateAsync" },
            typeof(IGoogleDriveRemoteValidationService)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray());

        Type[] fieldTypes = typeof(GoogleDriveRemoteValidationService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        Assert.DoesNotContain(typeof(IGoogleDriveObjectPathResolver), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveRootFolderApi), fieldTypes);
        Assert.DoesNotContain(typeof(GoogleDriveObjectCreationCoordinator), fieldTypes);

        string[] coreReferences = typeof(SyncProviderKind).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        string[] appReferences = typeof(SyncViewModel).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(coreReferences,
            name => name.StartsWith("Google.", StringComparison.Ordinal));
        Assert.DoesNotContain(appReferences,
            name => name.StartsWith("Google.", StringComparison.Ordinal));
    }

    [Fact]
    public void DependencyInjection_RegistersValidationServiceWithoutRunningIt()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveRemoteValidationService>(
            provider.GetRequiredService<IGoogleDriveRemoteValidationService>());
    }

    private static ValidationContext CreateContext(
        SyncRemoteProfile? profile = null,
        bool addProfile = true)
    {
        profile ??= Profile();
        var repository = new RecordingProfileRepository(
            addProfile ? profile : null);
        var sessionFactory = new RecordingSessionFactory();
        var api = new RecordingRootValidationApi();
        var cache = new RecordingObjectIdCache();
        var service = new GoogleDriveRemoteValidationService(
            repository,
            sessionFactory,
            api,
            cache,
            new FixedUtcClock(Now));
        return new ValidationContext(
            profile,
            repository,
            sessionFactory,
            api,
            cache,
            service);
    }

    private static SyncRemoteProfile Profile() =>
        new(
            Guid.NewGuid(),
            "Google profile",
            SyncProviderKind.GoogleDrive,
            "Example User",
            "Backup root",
            new GoogleDriveSyncRemoteSettings(
                "user@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile),
            Now.AddDays(-2),
            Now.AddDays(-1),
            null,
            null,
            "authoritative-root-id");

    private static GoogleDriveRootValidationMetadata Metadata(
        string? name = "Backup root",
        string? mimeType = GoogleDriveApplicationRoot.FolderMimeType,
        bool trashed = false,
        IReadOnlyList<string>? parentIds = null,
        string? driveId = null,
        bool canListChildren = true,
        bool canAddChildren = true) =>
        new(
            name,
            mimeType,
            trashed,
            parentIds ?? new[] { "my-drive-parent-id" },
            driveId,
            canListChildren,
            canAddChildren);

    private sealed record UnsupportedGoogleDriveSettings(int Version)
        : SyncRemoteProfileSettings(Version);

    private sealed record ValidationContext(
        SyncRemoteProfile Profile,
        RecordingProfileRepository Repository,
        RecordingSessionFactory SessionFactory,
        RecordingRootValidationApi Api,
        RecordingObjectIdCache Cache,
        GoogleDriveRemoteValidationService Service);

    private sealed class RecordingSessionFactory
        : IGoogleDriveAuthorizedSessionFactory
    {
        public GoogleDriveAuthorizedSessionFailure? Failure { get; set; }

        public bool WasAuthenticationRefreshed { get; set; }

        public int RestoreCalls { get; private set; }

        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public Task<GoogleDriveAuthorizedSession> RestoreAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is { } failure)
                throw new GoogleDriveAuthorizedSessionException(failure);

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
                IssuedUtc = Now.UtcDateTime
            };
            LastCredential = new GoogleAuthorizedCredential(
                new UserCredential(flow, profile.Id.ToString("D"), token),
                wasAuthenticationRefreshed: WasAuthenticationRefreshed);
            return Task.FromResult(new GoogleDriveAuthorizedSession(
                LastCredential,
                new GoogleDriveAccountInfo("Example User", "user@example.invalid")));
        }
    }

    private sealed class RecordingRootValidationApi
        : IGoogleDriveRootValidationApi
    {
        public GoogleDriveRootValidationMetadata Result { get; set; } = Metadata();

        public GoogleDriveApiFailure? Failure { get; set; }

        public bool Cancel { get; set; }

        public int GetCalls { get; private set; }

        public List<string> RootIds { get; } = new();

        public Task<GoogleDriveRootValidationMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string rootFolderId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            RootIds.Add(rootFolderId);

            if (Cancel)
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure)
            {
                throw GoogleDriveApiFailureMapper.Create(
                    GoogleDriveApiOperation.RootValidationMetadataGet,
                    failure,
                    "GoogleDriveTestValidationFailure");
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingObjectIdCache : IGoogleDriveObjectIdCache
    {
        public List<GoogleDriveObjectCacheScope> ClearedScopes { get; } = new();

        public List<(Guid ProfileId, GoogleDriveObjectCacheInvalidationReason Reason)>
            ProfileInvalidations { get; } = new();

        public bool TryGet(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            out GoogleDriveObjectIdCacheEntry? entry)
        {
            entry = null;
            return false;
        }

        public bool TryStoreUniqueValidated(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata) => false;

        public void Remove(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind)
        {
        }

        public void ClearScope(GoogleDriveObjectCacheScope scope) =>
            ClearedScopes.Add(scope);

        public void InvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            ProfileInvalidations.Add((remoteProfileId, reason));
    }

    private sealed class RecordingProfileRepository : ISyncRemoteProfileRepository
    {
        public RecordingProfileRepository(SyncRemoteProfile? profile) =>
            Profile = profile;

        public SyncRemoteProfile? Profile { get; private set; }

        public bool ThrowOnTimestampUpdate { get; set; }

        public int TimestampUpdates { get; private set; }

        public IReadOnlyList<SyncRemoteProfile> GetAll() =>
            Profile is null ? Array.Empty<SyncRemoteProfile>() : new[] { Profile };

        public SyncRemoteProfile? GetById(Guid id) =>
            Profile?.Id == id ? Profile : null;

        public SyncRemoteProfile Create(SyncRemoteProfile profile) =>
            Profile = profile;

        public SyncRemoteProfile Update(SyncRemoteProfile profile) =>
            Profile = profile;

        public SyncRemoteProfile Rename(
            Guid id,
            string displayName,
            DateTimeOffset updatedUtc) =>
            Profile = Require(id) with
            {
                DisplayName = displayName,
                UpdatedUtc = updatedUtc
            };

        public void Delete(Guid id) => Profile = null;

        public SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset lastUsedUtc)
        {
            TimestampUpdates++;
            if (ThrowOnTimestampUpdate)
                throw new InvalidOperationException("Timestamp storage unavailable.");
            return Profile = Require(id) with { LastUsedUtc = lastUsedUtc };
        }

        public SyncRemoteProfile UpdateLastSuccessfulConnection(
            Guid id,
            DateTimeOffset lastSuccessfulConnectionUtc)
        {
            TimestampUpdates++;
            if (ThrowOnTimestampUpdate)
                throw new InvalidOperationException("Timestamp storage unavailable.");
            return Profile = Require(id) with
            {
                LastSuccessfulConnectionUtc = lastSuccessfulConnectionUtc
            };
        }

        private SyncRemoteProfile Require(Guid id) =>
            Profile?.Id == id
                ? Profile
                : throw new SyncRemoteProfileNotFoundException(id);
    }
}
