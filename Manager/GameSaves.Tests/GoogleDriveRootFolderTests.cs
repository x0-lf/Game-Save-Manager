using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRootFolderTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-24T12:00:00Z");

    [Theory]
    [InlineData(GoogleDriveRootFolderStatus.Unconfigured, 0)]
    [InlineData(GoogleDriveRootFolderStatus.Checking, 1)]
    [InlineData(GoogleDriveRootFolderStatus.Ready, 2)]
    [InlineData(GoogleDriveRootFolderStatus.Moved, 3)]
    [InlineData(GoogleDriveRootFolderStatus.Missing, 4)]
    [InlineData(GoogleDriveRootFolderStatus.Trashed, 5)]
    [InlineData(GoogleDriveRootFolderStatus.WrongType, 6)]
    [InlineData(GoogleDriveRootFolderStatus.UnsupportedLocation, 7)]
    [InlineData(GoogleDriveRootFolderStatus.Ambiguous, 8)]
    [InlineData(GoogleDriveRootFolderStatus.RecreationConfirmationRequired, 9)]
    [InlineData(GoogleDriveRootFolderStatus.Creating, 10)]
    [InlineData(GoogleDriveRootFolderStatus.ReauthenticationRequired, 11)]
    [InlineData(GoogleDriveRootFolderStatus.Unavailable, 12)]
    [InlineData(GoogleDriveRootFolderStatus.Failed, 13)]
    public void RootFolderStatus_HasStableValues(
        GoogleDriveRootFolderStatus status,
        int value) =>
        Assert.Equal(value, (int)status);

    [Fact]
    public void CoreResultAndContract_AreSafeAndGoogleSdkFree()
    {
        const string folderId = "folder-id-must-not-appear";
        var result = new GoogleDriveRootFolderResult(
            GoogleDriveRootFolderStatus.Missing,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            folderId,
            GoogleDriveApplicationRoot.DisplayName,
            ErrorCode: GoogleDriveRootFolderErrorCodes.Missing,
            Message: "The folder is missing.");

        Assert.DoesNotContain(folderId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("GameSave Manager Backups", GoogleDriveApplicationRoot.DisplayName);
        Assert.Equal(
            "application/vnd.google-apps.folder",
            GoogleDriveApplicationRoot.FolderMimeType);

        IEnumerable<Type> exposed = typeof(IGoogleDriveRootFolderService)
            .GetMethods()
            .SelectMany(method =>
                method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType));

        Assert.DoesNotContain(
            exposed.SelectMany(FlattenTypes),
            type => type.Namespace?.StartsWith("Google.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task StoredId_IsValidatedBeforeSearchAndPreventsCreation()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "authoritative-id",
            rootName: GoogleDriveApplicationRoot.DisplayName));
        var api = new FakeRootFolderApi
        {
            GetResult = Folder("authoritative-id")
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Ready, result.Status);
        Assert.True(result.WasValidatedById);
        Assert.Equal(
            new[] { "get:authoritative-id", "membership:authoritative-id" },
            api.Calls);
        Assert.Equal(0, api.FindCalls);
        Assert.Equal(0, api.CreateCalls);
        Assert.Equal("authoritative-id", repository.GetById(profile.Id)!.RemoteFolderId);
    }

    [Fact]
    public async Task RenamedStoredFolder_KeepsIdAndUpdatesDisplayName()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "same-id",
            rootName: GoogleDriveApplicationRoot.DisplayName));
        var api = new FakeRootFolderApi
        {
            GetResult = Folder("same-id", "Renamed Backups")
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Ready, result.Status);
        Assert.Equal("same-id", result.FolderId);
        Assert.Equal("Renamed Backups", result.DisplayName);
        Assert.Contains("renamed", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("same-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("Renamed Backups", repository.GetById(profile.Id)!.RemoteRootDisplayName);
    }

    [Fact]
    public async Task MovedMyDriveFolder_RemainsLinkedByIdAndCreatesNothing()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "moved-id",
            rootName: "Old display"));
        var api = new FakeRootFolderApi
        {
            GetResult = Folder("moved-id", "Moved display"),
            IsTopLevel = false
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Moved, result.Status);
        Assert.True(result.WasMoved);
        Assert.Equal("moved-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("Moved display", repository.GetById(profile.Id)!.RemoteRootDisplayName);
        Assert.Equal(0, api.FindCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Theory]
    [InlineData("trashed", GoogleDriveRootFolderStatus.Trashed)]
    [InlineData("wrong-type", GoogleDriveRootFolderStatus.WrongType)]
    [InlineData("shared-drive", GoogleDriveRootFolderStatus.UnsupportedLocation)]
    public async Task InvalidStoredFolder_RequiresRecreationAndPreservesMetadata(
        string scenario,
        GoogleDriveRootFolderStatus expected)
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "stale-id",
            rootName: "Previous name"));
        var api = new FakeRootFolderApi
        {
            GetResult = scenario switch
            {
                "trashed" => Folder("stale-id") with { Trashed = true },
                "wrong-type" => Folder("stale-id") with
                {
                    MimeType = "application/octet-stream"
                },
                _ => Folder("stale-id") with { DriveId = "shared-drive-id" }
            }
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.Equal(expected, result.Status);
        Assert.True(result.RequiresRecreationConfirmation);
        Assert.Equal("stale-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("Previous name", repository.GetById(profile.Id)!.RemoteRootDisplayName);
        Assert.Equal(0, api.FindCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task MissingStoredFolder_IsNotSilentlyReplaced()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "missing-id",
            rootName: "Previous name"));
        var api = new FakeRootFolderApi
        {
            GetFailure = GoogleDriveRootFolderApiFailure.NotFound
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Missing, result.Status);
        Assert.True(result.RequiresRecreationConfirmation);
        Assert.Equal("missing-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(0, api.FindCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task InspectWithoutStoredId_DiscoversAndPersistsOneUniqueCandidate()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi();
        api.DiscoveryResults.Enqueue(new[] { Folder("existing-id") });
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Ready, result.Status);
        Assert.True(result.WasDiscovered);
        Assert.False(result.WasCreated);
        Assert.Equal("existing-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task InspectWithoutStoredIdAndNoCandidate_RemainsUnconfigured()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi();
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Unconfigured, result.Status);
        Assert.Null(repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task DuplicateDiscovery_IsAmbiguousAndNeverSelectsOrCreates()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi();
        api.DiscoveryResults.Enqueue(new[]
        {
            Folder("first-id"),
            Folder("second-id")
        });
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Ambiguous, result.Status);
        Assert.Null(result.FolderId);
        Assert.Null(repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task InitialEnsure_SearchesTwiceThenCreatesOneTopLevelFolder()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi
        {
            CreateResult = Folder("created-id")
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Ready, result.Status);
        Assert.True(result.WasCreated);
        Assert.Equal(2, api.FindCalls);
        Assert.Equal(1, api.CreateCalls);
        Assert.Equal("created-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(
            GoogleDriveApplicationRoot.DisplayName,
            repository.GetById(profile.Id)!.RemoteRootDisplayName);
        Assert.DoesNotContain("root", api.Calls);
    }

    [Fact]
    public async Task CandidateAppearingBeforeCreation_IsReused()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi();
        api.DiscoveryResults.Enqueue(Array.Empty<GoogleDriveFolderMetadata>());
        api.DiscoveryResults.Enqueue(new[] { Folder("late-candidate") });
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.True(result.WasDiscovered);
        Assert.False(result.WasCreated);
        Assert.Equal("late-candidate", result.FolderId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task ConcurrentEnsure_CreatesAtMostOneFolder()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeRootFolderApi
        {
            DiscoveryEntered = entered,
            ReleaseDiscovery = release.Task,
            CreateResult = Folder("created-once")
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        Task<GoogleDriveRootFolderResult> first = service.EnsureAsync(profile.Id);
        await entered.Task;
        GoogleDriveRootFolderResult second = await service.EnsureAsync(profile.Id);
        release.SetResult();
        GoogleDriveRootFolderResult completed = await first;

        Assert.Equal(
            GoogleDriveRootFolderErrorCodes.OperationInProgress,
            second.ErrorCode);
        Assert.True(completed.Succeeded);
        Assert.Equal(1, api.CreateCalls);
    }

    [Fact]
    public async Task RecreateWithoutExplicitConfirmation_PerformsNoRemoteCall()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "stale-id",
            rootName: "Old name"));
        var api = new FakeRootFolderApi();
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.RecreateAsync(
            profile.Id,
            GoogleDriveRootFolderRecreationConfirmation.NotConfirmed);

        Assert.Equal(
            GoogleDriveRootFolderStatus.RecreationConfirmationRequired,
            result.Status);
        Assert.Empty(api.Calls);
        Assert.Equal("stale-id", repository.GetById(profile.Id)!.RemoteFolderId);
    }

    [Fact]
    public async Task ConfirmedRecreate_SearchesAndReusesCandidateBeforeCreating()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "stale-id",
            rootName: "Old name"));
        var api = new FakeRootFolderApi
        {
            GetFailure = GoogleDriveRootFolderApiFailure.NotFound
        };
        api.DiscoveryResults.Enqueue(new[] { Folder("replacement-id") });
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.RecreateAsync(
            profile.Id,
            GoogleDriveRootFolderRecreationConfirmation.Confirmed);

        Assert.True(result.WasDiscovered);
        Assert.False(result.WasCreated);
        Assert.Equal("replacement-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task FailedRecreation_PreservesStaleIdentity()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "stale-id",
            rootName: "Old name"));
        var api = new FakeRootFolderApi
        {
            GetFailure = GoogleDriveRootFolderApiFailure.NotFound,
            CreateFailure = GoogleDriveRootFolderApiFailure.Failed
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.RecreateAsync(
            profile.Id,
            GoogleDriveRootFolderRecreationConfirmation.Confirmed);

        Assert.False(result.Succeeded);
        Assert.Equal("stale-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("Old name", repository.GetById(profile.Id)!.RemoteRootDisplayName);
    }

    [Fact]
    public async Task InvalidCreateResponse_IsRejectedWithoutProfileMutation()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi
        {
            CreateResult = Folder("new-id") with { DriveId = "shared-drive-id" }
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderErrorCodes.CreationFailed, result.ErrorCode);
        Assert.Null(repository.GetById(profile.Id)!.RemoteFolderId);
    }

    [Theory]
    [InlineData(
        (int)GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication,
        GoogleDriveRootFolderStatus.ReauthenticationRequired)]
    [InlineData(
        (int)GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked,
        GoogleDriveRootFolderStatus.ReauthenticationRequired)]
    [InlineData(
        (int)GoogleDriveAuthorizedSessionFailure.TokenCorrupted,
        GoogleDriveRootFolderStatus.ReauthenticationRequired)]
    [InlineData(
        (int)GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable,
        GoogleDriveRootFolderStatus.Unavailable)]
    [InlineData(
        (int)GoogleDriveAuthorizedSessionFailure.Unavailable,
        GoogleDriveRootFolderStatus.Unavailable)]
    public async Task AuthenticationFailure_IsMappedWithoutOpeningInteractiveFlow(
        int failureValue,
        GoogleDriveRootFolderStatus expected)
    {
        var failure = (GoogleDriveAuthorizedSessionFailure)failureValue;
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var session = new FakeAuthorizedSessionFactory { Failure = failure };
        var api = new FakeRootFolderApi();
        GoogleDriveRootFolderService service = CreateService(
            repository,
            api,
            sessionFactory: session);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.Equal(expected, result.Status);
        Assert.Empty(api.Calls);
        Assert.Equal(1, session.RestoreCalls);
    }

    [Fact]
    public async Task ConfirmedUnauthorizedFolderResponse_RemovesOnlyGoogleToken()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "saved-id",
            rootName: "Saved name"));
        var secretStore = new InMemorySecretStore();
        byte[] google = { 1, 2, 3 };
        byte[] sftp = { 4, 5, 6 };
        await secretStore.StoreAsync(
            new SecretKey(profile.Id, SecretNames.OAuthTokenData),
            google);
        await secretStore.StoreAsync(
            new SecretKey(profile.Id, SecretNames.SftpPassword),
            sftp);
        var api = new FakeRootFolderApi
        {
            GetFailure = GoogleDriveRootFolderApiFailure.AuthorizationRevoked
        };
        GoogleDriveRootFolderService service = CreateService(
            repository,
            api,
            secretStore: secretStore);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.Equal(
            GoogleDriveRootFolderStatus.ReauthenticationRequired,
            result.Status);
        Assert.False(await secretStore.ExistsAsync(
            new SecretKey(profile.Id, SecretNames.OAuthTokenData)));
        Assert.True(await secretStore.ExistsAsync(
            new SecretKey(profile.Id, SecretNames.SftpPassword)));
        Assert.Equal("saved-id", repository.GetById(profile.Id)!.RemoteFolderId);
    }

    [Fact]
    public async Task TemporaryDriveFailure_PreservesFolderMetadata()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            rootId: "saved-id",
            rootName: "Saved name"));
        var api = new FakeRootFolderApi
        {
            GetFailure = GoogleDriveRootFolderApiFailure.Unavailable
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Unavailable, result.Status);
        Assert.Equal("saved-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("Saved name", repository.GetById(profile.Id)!.RemoteRootDisplayName);
    }

    [Fact]
    public async Task SuccessfulRootUpdate_PreservesAccountSettingsAndOperationalState()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            accountName: "Example User",
            accountEmail: "user@example.invalid"));
        var api = new FakeRootFolderApi
        {
            CreateResult = Folder("created-id")
        };
        var clock = new FixedUtcClock(Now.AddHours(1));
        GoogleDriveRootFolderService service = CreateService(
            repository,
            api,
            clock: clock);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);
        SyncRemoteProfile updated = repository.GetById(profile.Id)!;

        Assert.True(result.Succeeded);
        Assert.Equal("Example User", updated.AccountDisplayName);
        Assert.Equal(
            "user@example.invalid",
            Assert.IsType<GoogleDriveSyncRemoteSettings>(
                updated.ProviderSettings).AccountEmail);
        Assert.Equal(GoogleDriveAuthorizationScopes.DriveFile,
            ((GoogleDriveSyncRemoteSettings)updated.ProviderSettings).RequestedScope);
        Assert.Equal(clock.UtcNow, updated.UpdatedUtc);
        Assert.Equal(clock.UtcNow, updated.LastUsedUtc);
        Assert.Equal(clock.UtcNow, updated.LastSuccessfulConnectionUtc);
    }

    [Fact]
    public void RootApi_UsesExactNarrowDiscoveryContractAndPagination()
    {
        Assert.Equal(
            "name = 'GameSave Manager Backups' and " +
            "mimeType = 'application/vnd.google-apps.folder' and " +
            "trashed = false and 'root' in parents",
            GoogleDriveRootFolderApi.DiscoveryQuery);
        Assert.Contains("nextPageToken", GoogleDriveRootFolderApi.DiscoveryFields);
        Assert.Contains("driveId", GoogleDriveRootFolderApi.DiscoveryFields);
        Assert.Equal(
            "trashed = false and 'root' in parents",
            GoogleDriveRootFolderApi.MembershipQuery);
        Assert.Equal(
            "nextPageToken,incompleteSearch,files(id)",
            GoogleDriveRootFolderApi.MembershipFields);

        string source = ReadRepositoryFile(
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveRootFolderApi.cs");
        Assert.Contains("request.PageToken = pageToken", source, StringComparison.Ordinal);
        Assert.Contains("while (pageToken is not null)", source, StringComparison.Ordinal);
        Assert.Contains("request.Spaces = \"drive\"", source, StringComparison.Ordinal);
        Assert.Contains("request.Corpora = \"user\"", source, StringComparison.Ordinal);
        Assert.Contains("request.IncludeItemsFromAllDrives = false", source, StringComparison.Ordinal);
        Assert.Contains("Parents = new[] { \"root\" }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Files.Get(\"root\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialDiscovery_DoesNotDependOnRootMetadataLookup()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi();
        api.DiscoveryResults.Enqueue(new[] { Folder("app-visible-id") });
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.InspectAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.True(result.WasDiscovered);
        Assert.Equal(new[] { "find" }, api.Calls);
    }

    [Fact]
    public async Task InitialCreation_PersistsImmediatelyWithoutRootMetadataLookup()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi
        {
            CreateResult = Folder("created-id", parents: Array.Empty<string>())
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult result = await service.EnsureAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.True(result.WasCreated);
        Assert.Equal("created-id", repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(new[] { "find", "find", "create" }, api.Calls);
    }

    [Fact]
    public async Task CreationPersistenceFailure_IsExplicitAndRetryReusesFolder()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        repository.ThrowOnUpdate = true;
        var api = new FakeRootFolderApi
        {
            CreateResult = Folder("created-but-not-linked")
        };
        GoogleDriveRootFolderService service = CreateService(repository, api);

        GoogleDriveRootFolderResult failed = await service.EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderErrorCodes.PersistenceFailed, failed.ErrorCode);
        Assert.Contains("might have been created", failed.Message!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(repository.GetById(profile.Id)!.RemoteFolderId);

        repository.ThrowOnUpdate = false;
        api.DiscoveryResults.Enqueue(new[] { Folder("created-but-not-linked") });
        GoogleDriveRootFolderResult retried = await service.EnsureAsync(profile.Id);

        Assert.True(retried.WasDiscovered);
        Assert.Equal("created-but-not-linked",
            repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal(1, api.CreateCalls);
    }

    [Fact]
    public async Task StoredFolderMembership_DeterminesReadyOrMoved()
    {
        var readyRepository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile readyProfile = readyRepository.Create(Profile(rootId: "ready-id"));
        var readyApi = new FakeRootFolderApi
        {
            GetResult = Folder("ready-id"),
            IsTopLevel = true
        };
        var movedRepository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile movedProfile = movedRepository.Create(Profile(rootId: "moved-id"));
        var movedApi = new FakeRootFolderApi
        {
            GetResult = Folder("moved-id"),
            IsTopLevel = false
        };

        GoogleDriveRootFolderResult ready = await CreateService(
            readyRepository,
            readyApi).InspectAsync(readyProfile.Id);
        GoogleDriveRootFolderResult moved = await CreateService(
            movedRepository,
            movedApi).InspectAsync(movedProfile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Ready, ready.Status);
        Assert.Equal(GoogleDriveRootFolderStatus.Moved, moved.Status);
        Assert.Contains("membership:ready-id", readyApi.Calls);
        Assert.Contains("membership:moved-id", movedApi.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, null,
        (int)GoogleDriveRootFolderApiFailure.InvalidRequest,
        GoogleDriveRootFolderErrorCodes.InvalidRequest)]
    [InlineData(HttpStatusCode.BadRequest, "invalidQuery",
        (int)GoogleDriveRootFolderApiFailure.InvalidQuery,
        GoogleDriveRootFolderErrorCodes.InvalidQuery)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions",
        (int)GoogleDriveRootFolderApiFailure.InsufficientScope,
        GoogleDriveRootFolderErrorCodes.InsufficientScope)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientFilePermissions",
        (int)GoogleDriveRootFolderApiFailure.AccessDenied,
        GoogleDriveRootFolderErrorCodes.AccessDenied)]
    [InlineData(HttpStatusCode.Forbidden, "accessNotConfigured",
        (int)GoogleDriveRootFolderApiFailure.ApiNotEnabled,
        GoogleDriveRootFolderErrorCodes.ApiNotEnabled)]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded",
        (int)GoogleDriveRootFolderApiFailure.RateLimited,
        GoogleDriveRootFolderErrorCodes.RateLimited)]
    public void ProviderErrors_MapToSanitizedOperationDiagnostics(
        HttpStatusCode status,
        string? reason,
        int expectedFailure,
        string expectedCode)
    {
        var providerError = new GoogleApiException("Drive", "raw-private-value")
        {
            HttpStatusCode = status,
            Error = reason is null
                ? null
                : new RequestError
                {
                    Errors = new List<SingleError> { new() { Reason = reason } }
                }
        };

        GoogleDriveRootFolderApiException mapped =
            GoogleDriveRootFolderApi.MapException(
                providerError,
                GoogleDriveRootFolderApiOperation.RootFolderDiscovery);

        Assert.Equal((GoogleDriveRootFolderApiFailure)expectedFailure, mapped.Failure);
        Assert.Equal(expectedCode, mapped.Details.SafeErrorCode);
        Assert.Equal(GoogleDriveRootFolderApiOperation.RootFolderDiscovery,
            mapped.Details.Operation);
        Assert.DoesNotContain("raw-private-value", mapped.Details.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_ExcludeUnknownReasonsAndSensitiveValues()
    {
        var providerError = new GoogleApiException(
            "Drive",
            "access_token=secret account=user@example.invalid folder-id-123")
        {
            HttpStatusCode = HttpStatusCode.BadRequest,
            Error = new RequestError
            {
                Errors = new List<SingleError>
                {
                    new() { Reason = "private-folder-id-123" }
                }
            }
        };

        GoogleDriveRootFolderApiException mapped =
            GoogleDriveRootFolderApi.MapException(
                providerError,
                GoogleDriveRootFolderApiOperation.RootFolderCreation);
        string diagnostic = mapped.Details.ToString();

        Assert.DoesNotContain("secret", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folder-id", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Null(mapped.Details.Reason);
    }

    [Fact]
    public async Task ApiNotEnabled_HasDeveloperConfigurationMessage()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile());
        var api = new FakeRootFolderApi
        {
            FindFailure = GoogleDriveRootFolderApiFailure.ApiNotEnabled
        };

        GoogleDriveRootFolderResult result = await CreateService(
            repository,
            api).EnsureAsync(profile.Id);

        Assert.Equal(GoogleDriveRootFolderStatus.Unavailable, result.Status);
        Assert.Equal(GoogleDriveRootFolderErrorCodes.ApiNotEnabled, result.ErrorCode);
        Assert.Equal(
            "The Google Drive API is not enabled for the configured OAuth project.",
            result.Message);
    }

    [Fact]
    public void ProductionBoundary_ForbidsHiddenStorageAndKeepsSyncUnavailable()
    {
        string googleSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    GetProjectDirectory("GameSaves.Infrastructure", "GoogleDrive"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));
        string coreSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    GetProjectDirectory("GameSaves.Core", "Sync"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("appData" + "Folder", googleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("drive." + "appdata", googleSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("using Google.", coreSource, StringComparison.Ordinal);

        SyncProviderDescriptor descriptor =
            new SyncProviderCatalog().GetDescriptor(SyncProviderKind.GoogleDrive);
        Assert.False(descriptor.IsImplemented);
        string factorySource = ReadRepositoryFile(
            "GameSaves.Infrastructure",
            "Sync",
            "SyncProviderFactory.cs");
        Assert.DoesNotContain("GoogleDrive", factorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureRegistration_ProvidesRootServiceWithoutCallingGoogle()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveRootFolderService>(
            provider.GetRequiredService<IGoogleDriveRootFolderService>());
    }

    [Fact]
    public void ExistingSqliteSchema_UsesOnlyExistingRootMetadataColumns()
    {
        string source = ReadRepositoryFile(
            "GameSaves.Infrastructure",
            "Sync",
            "SqliteSyncRemoteProfileRepository.cs");

        Assert.Contains("remote_folder_id", source, StringComparison.Ordinal);
        Assert.Contains("remote_root_display_name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("google_drive_folder_id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("google_drive_root_status", source, StringComparison.Ordinal);
    }

    private static GoogleDriveRootFolderService CreateService(
        InMemorySyncRemoteProfileRepository repository,
        FakeRootFolderApi api,
        FakeAuthorizedSessionFactory? sessionFactory = null,
        InMemorySecretStore? secretStore = null,
        FixedUtcClock? clock = null) =>
        new(
            repository,
            secretStore ?? new InMemorySecretStore(),
            sessionFactory ?? new FakeAuthorizedSessionFactory(),
            api,
            clock ?? new FixedUtcClock(Now));

    private static SyncRemoteProfile Profile(
        string? rootId = null,
        string? rootName = null,
        string? accountName = "Example User",
        string? accountEmail = "user@example.invalid") =>
        new(
            Guid.NewGuid(),
            "Google profile",
            SyncProviderKind.GoogleDrive,
            accountName,
            rootName,
            new GoogleDriveSyncRemoteSettings(
                accountEmail,
                GoogleDriveAuthorizationScopes.DriveFile),
            Now,
            Now,
            Now.AddMinutes(-10),
            Now.AddMinutes(-5),
            rootId);

    private static GoogleDriveFolderMetadata Folder(
        string id,
        string name = GoogleDriveApplicationRoot.DisplayName,
        IReadOnlyList<string>? parents = null) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            Trashed: false,
            parents ?? new[] { "root-id" },
            DriveId: null);

    private static IEnumerable<Type> FlattenTypes(Type type)
    {
        yield return type;

        if (type.IsArray)
        {
            foreach (Type nested in FlattenTypes(type.GetElementType()!))
                yield return nested;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type nested in FlattenTypes(argument))
                yield return nested;
        }
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(GetManagerDirectory(), Path.Combine(segments)));

    private static string GetProjectDirectory(
        string project,
        params string[] segments) =>
        Path.Combine(GetManagerDirectory(), project, Path.Combine(segments));

    private static string GetManagerDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string solution = Path.Combine(directory.FullName, "Manager.sln");

            if (File.Exists(solution))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }

    private sealed class FakeAuthorizedSessionFactory
        : IGoogleDriveAuthorizedSessionFactory
    {
        public GoogleDriveAuthorizedSessionFailure? Failure { get; set; }
        public int RestoreCalls { get; private set; }

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
            var credential = new GoogleAuthorizedCredential(
                new UserCredential(flow, profile.Id.ToString("D"), token));
            return Task.FromResult(new GoogleDriveAuthorizedSession(
                credential,
                new GoogleDriveAccountInfo(
                    profile.AccountDisplayName,
                    (profile.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail)));
        }
    }

    private sealed class FakeRootFolderApi : IGoogleDriveRootFolderApi
    {
        public List<string> Calls { get; } = new();
        public Queue<IReadOnlyList<GoogleDriveFolderMetadata>> DiscoveryResults { get; } =
            new();
        public GoogleDriveFolderMetadata GetResult { get; set; } =
            Folder("stored-id");
        public GoogleDriveFolderMetadata CreateResult { get; set; } =
            Folder("created-id");
        public GoogleDriveRootFolderApiFailure? GetFailure { get; set; }
        public GoogleDriveRootFolderApiFailure? MembershipFailure { get; set; }
        public GoogleDriveRootFolderApiFailure? FindFailure { get; set; }
        public GoogleDriveRootFolderApiFailure? CreateFailure { get; set; }
        public bool IsTopLevel { get; set; } = true;
        public TaskCompletionSource? DiscoveryEntered { get; set; }
        public Task? ReleaseDiscovery { get; set; }
        public int FindCalls { get; private set; }
        public int CreateCalls { get; private set; }

        public Task<bool> IsDirectChildOfMyDriveRootAsync(
            GoogleAuthorizedCredential credential,
            string folderId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"membership:{folderId}");

            if (MembershipFailure is { } failure)
                throw new GoogleDriveRootFolderApiException(failure);

            return Task.FromResult(IsTopLevel);
        }

        public Task<GoogleDriveFolderMetadata> GetFolderByIdAsync(
            GoogleAuthorizedCredential credential,
            string folderId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"get:{folderId}");

            if (GetFailure is { } failure)
                throw new GoogleDriveRootFolderApiException(failure);

            return Task.FromResult(GetResult);
        }

        public async Task<IReadOnlyList<GoogleDriveFolderMetadata>>
            FindTopLevelFoldersByNameAsync(
                GoogleAuthorizedCredential credential,
                CancellationToken cancellationToken)
        {
            FindCalls++;
            Calls.Add("find");
            DiscoveryEntered?.TrySetResult();

            if (ReleaseDiscovery is not null)
                await ReleaseDiscovery.WaitAsync(cancellationToken);

            if (FindFailure is { } failure)
                throw new GoogleDriveRootFolderApiException(failure);

            return DiscoveryResults.Count > 0
                ? DiscoveryResults.Dequeue()
                : Array.Empty<GoogleDriveFolderMetadata>();
        }

        public Task<GoogleDriveFolderMetadata> CreateTopLevelFolderAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            Calls.Add("create");

            if (CreateFailure is { } failure)
                throw new GoogleDriveRootFolderApiException(failure);

            return Task.FromResult(CreateResult);
        }
    }
}
