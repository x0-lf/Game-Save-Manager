using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRemoteFileSystemTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("24b45199-8c22-45b8-a2f2-f80b1d63385c");

    public static IEnumerable<object[]> InvalidValidationStatuses() =>
        Enum.GetValues<GoogleDriveRemoteValidationStatus>()
            .Where(status => status != GoogleDriveRemoteValidationStatus.Valid)
            .Select(status => new object[] { (int)status });

    [Fact]
    public void Factory_CreatesDistinctProfileScopedFileSystems()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile());
        var validation = new RecordingValidationService();
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            validation,
            new RecordingRootExistenceService(),
            new RecordingFolderExistenceService(),
            new RecordingRunFolderNameService(),
            new RecordingTextFileReadService(),
            new RecordingProviderMetadataReadService(),
            new RecordingProviderMetadataReplacementService(),
            new RecordingCreateOnlyTextFileService(),
            new RecordingRecursiveFileListingService(),
            new FakeGoogleDriveBinaryUploadService(),
            new FakeGoogleDriveBinaryDownloadService());

        IRemoteFileSystem first = factory.Create(ProfileId);
        IRemoteFileSystem second = factory.Create(ProfileId);

        Assert.IsType<GoogleDriveRemoteFileSystem>(first);
        Assert.IsType<GoogleDriveRemoteFileSystem>(second);
        Assert.NotSame(first, second);
        Assert.Equal("GameSave Manager Backups", first.DisplayRoot);
        Assert.Equal("GameSave Manager Backups/nested/run", first.GetDisplayPath("nested/run"));
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public async Task Factory_PreservesTheSelectedProfileIdentity()
    {
        Guid secondProfileId =
            Guid.Parse("b9a241af-e03c-4e80-a9c1-078642fd54c6");
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile());
        repository.Create(Profile() with
        {
            Id = secondProfileId,
            DisplayName = "Second Google Drive profile"
        });
        var validation = new RecordingValidationService();
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            validation,
            new RecordingRootExistenceService(),
            new RecordingFolderExistenceService(),
            new RecordingRunFolderNameService(),
            new RecordingTextFileReadService(),
            new RecordingProviderMetadataReadService(),
            new RecordingProviderMetadataReplacementService(),
            new RecordingCreateOnlyTextFileService(),
            new RecordingRecursiveFileListingService(),
            new FakeGoogleDriveBinaryUploadService(),
            new FakeGoogleDriveBinaryDownloadService());

        await factory.Create(ProfileId).ValidateAsync();
        await factory.Create(secondProfileId).ValidateAsync();

        Assert.Equal(new[] { ProfileId, secondProfileId }, validation.ProfileIds);
    }

    [Fact]
    public void Factory_FallsBackWhenDisplayMetadataCouldExposeAnIdOrEmail()
    {
        const string rootId = "private-root-id-marker";
        const string accountEmail = "user@example.invalid";
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile() with
        {
            RemoteFolderId = rootId,
            RemoteRootDisplayName =
                $"Backups {rootId} for {accountEmail}",
            ProviderSettings = new GoogleDriveSyncRemoteSettings(
                accountEmail,
                GoogleDriveAuthorizationScopes.DriveFile)
        });
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            new RecordingValidationService(),
            new RecordingRootExistenceService(),
            new RecordingFolderExistenceService(),
            new RecordingRunFolderNameService(),
            new RecordingTextFileReadService(),
            new RecordingProviderMetadataReadService(),
            new RecordingProviderMetadataReplacementService(),
            new RecordingCreateOnlyTextFileService(),
            new RecordingRecursiveFileListingService(),
            new FakeGoogleDriveBinaryUploadService(),
            new FakeGoogleDriveBinaryDownloadService());

        IRemoteFileSystem remote = factory.Create(ProfileId);

        Assert.Equal("Google Drive", remote.DisplayRoot);
        Assert.DoesNotContain(rootId, remote.DisplayRoot, StringComparison.Ordinal);
        Assert.DoesNotContain(accountEmail, remote.DisplayRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_UsesGenericDisplayForMissingOrNonGoogleProfiles()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var validation = new RecordingValidationService();
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            validation,
            new RecordingRootExistenceService(),
            new RecordingFolderExistenceService(),
            new RecordingRunFolderNameService(),
            new RecordingTextFileReadService(),
            new RecordingProviderMetadataReadService(),
            new RecordingProviderMetadataReplacementService(),
            new RecordingCreateOnlyTextFileService(),
            new RecordingRecursiveFileListingService(),
            new FakeGoogleDriveBinaryUploadService(),
            new FakeGoogleDriveBinaryDownloadService());

        IRemoteFileSystem missing = factory.Create(ProfileId);
        repository.Create(Profile() with
        {
            ProviderKind = SyncProviderKind.LocalFolder,
            RemoteRootDisplayName = @"C:\private\backups",
            ProviderSettings = new LocalFolderSyncRemoteSettings(
                @"C:\private\backups")
        });
        IRemoteFileSystem wrongProvider = factory.Create(ProfileId);

        Assert.Equal("Google Drive", missing.DisplayRoot);
        Assert.Equal("Google Drive", wrongProvider.DisplayRoot);
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public void DependencyInjection_ResolvesFactoryWithoutRemoteWork()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile());
        var validation = new RecordingValidationService();
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<ISyncRemoteProfileRepository>();
        services.RemoveAll<IGoogleDriveRemoteValidationService>();
        services.AddSingleton<ISyncRemoteProfileRepository>(repository);
        services.AddSingleton<IGoogleDriveRemoteValidationService>(validation);

        using ServiceProvider provider = services.BuildServiceProvider();
        IGoogleDriveRemoteFileSystemFactory factory =
            provider.GetRequiredService<IGoogleDriveRemoteFileSystemFactory>();
        Assert.IsType<GoogleDriveRootExistenceService>(
            provider.GetRequiredService<IGoogleDriveRootExistenceService>());
        Assert.IsType<GoogleDriveFolderExistenceService>(
            provider.GetRequiredService<IGoogleDriveFolderExistenceService>());
        Assert.IsType<GoogleDriveRunFolderNameService>(
            provider.GetRequiredService<IGoogleDriveRunFolderNameService>());
        Assert.IsType<GoogleDriveTextFileReadService>(
            provider.GetRequiredService<IGoogleDriveTextFileReadService>());
        Assert.IsType<GoogleDriveProviderMetadataReadService>(
            provider.GetRequiredService<IGoogleDriveProviderMetadataReadService>());
        Assert.IsType<GoogleDriveProviderMetadataReplacementService>(
            provider.GetRequiredService<
                IGoogleDriveProviderMetadataReplacementService>());
        Assert.IsType<GoogleDriveCreateOnlyTextFileService>(
            provider.GetRequiredService<IGoogleDriveCreateOnlyTextFileService>());
        Assert.IsType<GoogleDriveRecursiveFileListingService>(
            provider.GetRequiredService<
                IGoogleDriveRecursiveFileListingService>());
        IRemoteFileSystem remote = factory.Create(ProfileId);

        Assert.IsType<GoogleDriveRemoteFileSystemFactory>(factory);
        Assert.IsType<GoogleDriveRemoteFileSystem>(remote);
        Assert.Equal(0, validation.Calls);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                          typeof(GoogleDriveRemoteFileSystem));
    }

    [Fact]
    public void DependencyInjection_ResolvesUploadServicesWithoutRemoteWork()
    {
        var validation = new RecordingValidationService();
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<IGoogleDriveRemoteValidationService>();
        services.AddSingleton<IGoogleDriveRemoteValidationService>(validation);

        using ServiceProvider provider = services.BuildServiceProvider();
        var uploadService = provider.GetRequiredService<
            IGoogleDriveBinaryUploadService>();

        Assert.IsType<GoogleDriveBinaryUploadService>(uploadService);
        Assert.IsType<GoogleDriveMediaUploadClientFactory>(
            provider.GetRequiredService<IGoogleDriveMediaUploadClientFactory>());
        Assert.IsType<GoogleDriveBinaryDownloadService>(
            provider.GetRequiredService<IGoogleDriveBinaryDownloadService>());
        Assert.IsType<GoogleDriveMediaDownloadClientFactory>(
            provider.GetRequiredService<IGoogleDriveMediaDownloadClientFactory>());
        Assert.NotNull(provider.GetRequiredService<
            GoogleDriveDownloadSourceResolver>());
        Assert.NotNull(provider.GetRequiredService<
            GoogleDriveLocalDownloadDestinationOpener>());
        Assert.NotNull(provider.GetRequiredService<
            GoogleDriveUploadParentPreparationService>());
        Assert.NotNull(provider.GetRequiredService<
            GoogleDriveCreateOnlyUploadTargetGuard>());
        Assert.NotNull(provider.GetRequiredService<
            GoogleDriveLocalUploadSourceOpener>());
        Assert.Same(
            uploadService,
            provider.GetRequiredService<IGoogleDriveBinaryUploadService>());
        Assert.Equal(0, validation.Calls);
        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        // Milestone T registers a provider construction boundary, so the guard
        // is the provider wrapper itself, not every name that starts with it.
        Assert.DoesNotContain(
            services,
            descriptor => string.Equals(
                descriptor.ImplementationType?.Name,
                "GoogleDriveSyncProvider",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNullOnlyForValidResult()
    {
        var validation = new RecordingValidationService
        {
            Result = GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid)
        };
        IRemoteFileSystem remote = Remote(validation);

        TransferPreviewWarning? warning = await remote.ValidateAsync();

        Assert.Null(warning);
        Assert.Equal(1, validation.Calls);
        Assert.Equal(ProfileId, validation.ProfileIds.Single());
    }

    [Theory]
    [MemberData(nameof(InvalidValidationStatuses))]
    public async Task ValidateAsync_ReturnsCentralSafeWarningForEveryInvalidState(
        int statusValue)
    {
        var status = (GoogleDriveRemoteValidationStatus)statusValue;
        var validation = new RecordingValidationService
        {
            Result = GoogleDriveRemoteValidationMapper.FromStatus(
                status,
                "private-root-id-marker")
        };
        IRemoteFileSystem remote = Remote(validation);

        TransferPreviewWarning warning = Assert.IsType<TransferPreviewWarning>(
            await remote.ValidateAsync());
        TransferPreviewWarning expected = Assert.IsType<TransferPreviewWarning>(
            GoogleDriveRemoteValidationMapper.ToTransferPreviewWarning(
                validation.Result));

        Assert.Equal(expected, warning);
        Assert.Equal(1, validation.Calls);
        Assert.DoesNotContain("private-root-id-marker", warning.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", warning.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_ForwardsCancellationWithoutDoingOtherWork()
    {
        var validation = new RecordingValidationService
        {
            Handler = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.Valid));
            }
        };
        IRemoteFileSystem remote = Remote(validation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => remote.ValidateAsync(cancellation.Token));

        Assert.Equal(1, validation.Calls);
        Assert.Equal(cancellation.Token, validation.CancellationTokens.Single());
    }

    [Fact]
    public async Task RootExistsAsync_DelegatesTheSelectedProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var existence = new RecordingRootExistenceService { Result = true };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            existence);

        bool exists = await remote.RootExistsAsync(cancellation.Token);

        Assert.True(exists);
        Assert.Equal(new[] { ProfileId }, existence.ProfileIds);
        Assert.Equal(cancellation.Token, existence.CancellationTokens.Single());
    }

    [Fact]
    public async Task FolderExistsAsync_DelegatesPathProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var existence = new RecordingFolderExistenceService { Result = true };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            folderExistence: existence);

        bool exists = await remote.FolderExistsAsync(
            "nested/run",
            cancellation.Token);

        Assert.True(exists);
        Assert.Equal(new[] { ProfileId }, existence.ProfileIds);
        Assert.Equal(new[] { "nested/run" }, existence.RelativeFolders);
        Assert.Equal(cancellation.Token, existence.CancellationTokens.Single());
    }

    [Fact]
    public async Task ListRunFolderNamesAsync_DelegatesProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var listing = new RecordingRunFolderNameService
        {
            Result = new[] { "Run One", "Run Two" }
        };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            runFolderNames: listing);

        IReadOnlyList<string> names =
            await remote.ListRunFolderNamesAsync(cancellation.Token);

        Assert.Equal(listing.Result, names);
        Assert.Equal(new[] { ProfileId }, listing.ProfileIds);
        Assert.Equal(cancellation.Token, listing.CancellationTokens.Single());
    }

    [Fact]
    public async Task ReadTextFileAsync_DelegatesPathProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new RecordingTextFileReadService { Result = "{}" };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            textFileReads: reader);

        string? content = await remote.ReadTextFileAsync(
            "run/manifest.json",
            cancellation.Token);

        Assert.Equal("{}", content);
        Assert.Equal(new[] { ProfileId }, reader.ProfileIds);
        Assert.Equal(new[] { "run/manifest.json" }, reader.RelativePaths);
        Assert.Equal(cancellation.Token, reader.CancellationTokens.Single());
    }

    [Fact]
    public async Task ReadProviderMetadataAsync_DelegatesPathProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new RecordingProviderMetadataReadService { Result = "[]" };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            providerMetadataReads: reader);

        string? content = await remote.ReadProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog,
            cancellation.Token);

        Assert.Equal("[]", content);
        Assert.Equal(new[] { ProfileId }, reader.ProfileIds);
        Assert.Equal(
            new[] { RemoteProviderMetadataPath.SyncLog },
            reader.RelativePaths);
        Assert.Equal(cancellation.Token, reader.CancellationTokens.Single());
    }

    [Fact]
    public async Task CreateTextFileIfMissingAsync_DelegatesPathContentProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var creator = new RecordingCreateOnlyTextFileService();
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            createOnlyTextFiles: creator);

        await remote.CreateTextFileIfMissingAsync(
            "run/manifest.json",
            "{\"version\":1}",
            cancellation.Token);

        Assert.Equal(new[] { ProfileId }, creator.ProfileIds);
        Assert.Equal(new[] { "run/manifest.json" }, creator.RelativePaths);
        Assert.Equal(new[] { "{\"version\":1}" }, creator.Contents);
        Assert.Equal(cancellation.Token, creator.CancellationTokens.Single());
    }

    [Fact]
    public async Task ReplaceProviderMetadataAsync_DelegatesPathContentProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var replacement = new RecordingProviderMetadataReplacementService();
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            providerMetadataReplacements: replacement);

        await remote.ReplaceProviderMetadataAsync(
            RemoteProviderMetadataPath.SyncLog,
            "{\"runs\":[]}",
            cancellation.Token);

        Assert.Equal(new[] { ProfileId }, replacement.ProfileIds);
        Assert.Equal(
            new[] { RemoteProviderMetadataPath.SyncLog },
            replacement.RelativePaths);
        Assert.Equal(new[] { "{\"runs\":[]}" }, replacement.Contents);
        Assert.Equal(
            cancellation.Token,
            Assert.Single(replacement.CancellationTokens));
    }

    [Fact]
    public async Task ListFilesAsync_DelegatesPathProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var listing = new RecordingRecursiveFileListingService
        {
            Result = new[] { "nested/a.dat", "z.dat" }
        };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            recursiveFileListing: listing);

        IReadOnlyList<string> paths = await remote.ListFilesAsync(
            "Run 42",
            cancellation.Token);

        Assert.Equal(listing.Result, paths);
        Assert.Equal(new[] { ProfileId }, listing.ProfileIds);
        Assert.Equal(new[] { "Run 42" }, listing.RelativeFolders);
        Assert.Equal(cancellation.Token, listing.CancellationTokens.Single());
    }

    [Fact]
    public async Task DownloadFileAsync_DelegatesOneFileAndReturnsCompletedBytes()
    {
        using var cancellation = new CancellationTokenSource();
        var validation = new RecordingValidationService();
        var downloads = new FakeGoogleDriveBinaryDownloadService
        {
            Result = new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Completed,
                4096)
        };
        IRemoteFileSystem remote = Remote(validation, binaryDownloads: downloads);

        long bytes = await remote.DownloadFileAsync(
            "Run 42/nested/save.sav",
            "local-save.sav",
            cancellation.Token);

        Assert.Equal(4096, bytes);
        FakeGoogleDriveBinaryDownloadCall call = Assert.Single(downloads.Calls);
        Assert.Equal(ProfileId, call.Request.RemoteProfileId);
        Assert.Equal("Run 42/nested/save.sav", call.Request.CanonicalRemotePath);
        Assert.Equal("save.sav", call.Request.ExactFileName);
        Assert.Equal("local-save.sav", call.LocalFilePath);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public async Task DownloadFileAsync_NeverReportsBytesForAnIncompleteResult()
    {
        var downloads = new FakeGoogleDriveBinaryDownloadService
        {
            Result = new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Failed,
                0,
                GoogleDriveBinaryDownloadErrorCodes.Failed)
        };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            binaryDownloads: downloads);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                remote.DownloadFileAsync("run/save.sav", "local.sav"));

        Assert.Equal(
            GoogleDriveBinaryDownloadErrorCodes.Failed,
            exception.Result.ErrorCode);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/run/save.sav")]
    [InlineData("run/save.sav/")]
    [InlineData("run//save.sav")]
    [InlineData("run/../save.sav")]
    public async Task DownloadFileAsync_RejectsAnUnsafeSourceBeforeAnyDriveWork(
        string relativeRemotePath)
    {
        var validation = new RecordingValidationService();
        var downloads = new FakeGoogleDriveBinaryDownloadService();
        IRemoteFileSystem remote = Remote(validation, binaryDownloads: downloads);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                remote.DownloadFileAsync(relativeRemotePath, "local.sav"));

        Assert.Equal(
            "GoogleDriveDownloadInvalidSourcePath",
            exception.Result.ErrorCode);
        Assert.DoesNotContain(
            "save.sav",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(downloads.Calls);
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public async Task UploadFileAsync_DelegatesOneFileAndReturnsCompletedBytes()
    {
        using var cancellation = new CancellationTokenSource();
        using var temporary = new TemporaryUploadFile(new byte[] { 1, 2, 3, 4 });
        var validation = new RecordingValidationService();
        var uploads = new FakeGoogleDriveBinaryUploadService
        {
            Result = new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Completed,
                4)
        };
        IRemoteFileSystem remote = Remote(validation, binaryUploads: uploads);

        long bytes = await remote.UploadFileAsync(
            temporary.Path,
            "Run 42/nested/save.sav",
            cancellation.Token);

        Assert.Equal(4, bytes);
        FakeGoogleDriveBinaryUploadCall call = Assert.Single(uploads.Calls);
        Assert.Equal(temporary.Path, call.LocalFilePath);
        Assert.Equal(ProfileId, call.Request.RemoteProfileId);
        Assert.Equal("Run 42/nested/save.sav", call.Request.CanonicalRemotePath);
        Assert.Equal(
            ["Run 42", "nested", "save.sav"],
            call.Request.RemotePath.Segments);
        Assert.Equal(4, call.Request.ExpectedLength);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public async Task UploadFileAsync_ReturnsValidatedBytesNotThePlannedLength()
    {
        using var temporary = new TemporaryUploadFile(new byte[] { 1, 2, 3, 4 });
        var uploads = new FakeGoogleDriveBinaryUploadService
        {
            Result = new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Completed,
                9)
        };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            binaryUploads: uploads);

        long bytes = await remote.UploadFileAsync(temporary.Path, "run/save.sav");

        Assert.Equal(9, bytes);
        Assert.Equal(4, uploads.Calls.Single().Request.ExpectedLength);
    }

    [Theory]
    [InlineData((int)GoogleDriveBinaryUploadStatus.Failed,
        "GoogleDriveBinaryUploadFailed")]
    [InlineData((int)GoogleDriveBinaryUploadStatus.Indeterminate,
        "GoogleDriveUploadCompletionIndeterminate")]
    public async Task UploadFileAsync_NeverReportsBytesForAnIncompleteResult(
        int statusValue,
        string expectedErrorCode)
    {
        using var temporary = new TemporaryUploadFile(new byte[] { 1 });
        var status = (GoogleDriveBinaryUploadStatus)statusValue;
        var uploads = new FakeGoogleDriveBinaryUploadService
        {
            Result = new GoogleDriveBinaryUploadResult(
                status,
                0,
                status == GoogleDriveBinaryUploadStatus.Failed
                    ? GoogleDriveBinaryUploadErrorCodes.Failed
                    : GoogleDriveBinaryUploadErrorCodes.CompletionIndeterminate)
        };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            binaryUploads: uploads);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                remote.UploadFileAsync(temporary.Path, "run/save.sav"));

        Assert.Equal(expectedErrorCode, exception.Result.ErrorCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "save.sav",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            temporary.Path,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/run/save.sav")]
    [InlineData("run/save.sav/")]
    [InlineData("run//save.sav")]
    [InlineData("run/../save.sav")]
    [InlineData("run/./save.sav")]
    public async Task UploadFileAsync_RejectsAnUnsafeTargetBeforeAnyDriveWork(
        string relativeRemotePath)
    {
        var validation = new RecordingValidationService();
        var uploads = new FakeGoogleDriveBinaryUploadService();
        IRemoteFileSystem remote = Remote(validation, binaryUploads: uploads);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                remote.UploadFileAsync("local.sav", relativeRemotePath));

        Assert.Equal(
            "GoogleDriveUploadInvalidTargetPath",
            exception.Result.ErrorCode);
        Assert.DoesNotContain(
            "save.sav",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(uploads.Calls);
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public async Task UploadFileAsync_PropagatesCancellationWithoutBytes()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var uploads = new FakeGoogleDriveBinaryUploadService();
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            binaryUploads: uploads);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            remote.UploadFileAsync(
                "local.sav",
                "run/save.sav",
                cancellation.Token));

        Assert.Empty(uploads.Calls);
    }

    [Fact]
    public async Task UploadFileAsync_KeepsMissingSourcesInsideTheUploadService()
    {
        var uploads = new FakeGoogleDriveBinaryUploadService
        {
            Failure = new GoogleDriveLocalUploadSourceException(
                GoogleDriveLocalUploadSourceFailure.NotFound)
        };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            binaryUploads: uploads);

        GoogleDriveLocalUploadSourceException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalUploadSourceException>(() =>
                remote.UploadFileAsync(
                    @"C:\private\missing save.sav",
                    "run/save.sav"));

        Assert.Equal(
            "GoogleDriveUploadSourceNotFound",
            exception.SafeErrorCode);
        Assert.Equal(0, uploads.Calls.Single().Request.ExpectedLength);
        Assert.DoesNotContain(
            "private",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryUploadFile : IDisposable
    {
        public TemporaryUploadFile(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gamesaves-r17-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }

    [Fact]
    public void FileSystem_HasOnlyNarrowRemotePrimitiveDependencies()
    {
        Type[] fieldTypes = typeof(GoogleDriveRemoteFileSystem)
            .GetFields(BindingFlags.Instance |
                       BindingFlags.NonPublic |
                       BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(typeof(IGoogleDriveRemoteValidationService), fieldTypes);
        Assert.Contains(typeof(IGoogleDriveRootExistenceService), fieldTypes);
        Assert.Contains(typeof(IGoogleDriveFolderExistenceService), fieldTypes);
        Assert.Contains(typeof(IGoogleDriveRunFolderNameService), fieldTypes);
        Assert.Contains(typeof(IGoogleDriveTextFileReadService), fieldTypes);
        Assert.Contains(
            typeof(IGoogleDriveProviderMetadataReadService),
            fieldTypes);
        Assert.Contains(
            typeof(IGoogleDriveProviderMetadataReplacementService),
            fieldTypes);
        Assert.Contains(
            typeof(IGoogleDriveCreateOnlyTextFileService),
            fieldTypes);
        Assert.Contains(
            typeof(IGoogleDriveRecursiveFileListingService),
            fieldTypes);
        Assert.Contains(typeof(IGoogleDriveBinaryUploadService), fieldTypes);
        Assert.Contains(typeof(IGoogleDriveBinaryDownloadService), fieldTypes);
        Assert.DoesNotContain(
            typeof(IGoogleDriveMediaUploadClientFactory),
            fieldTypes);
        Assert.DoesNotContain(
            typeof(IGoogleDriveMediaDownloadClientFactory),
            fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveObjectPathResolver), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveObjectApi), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveRootFolderApi), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveRootValidationApi), fieldTypes);
        Assert.DoesNotContain(
            fieldTypes,
            type => type.Namespace?.StartsWith("Google.", StringComparison.Ordinal)
                    == true);
    }

    [Fact]
    public void ActivatedProvider_KeepsItsWrapperInternal()
    {
        var catalog = new SyncProviderCatalog();
        SyncProviderDescriptor google =
            catalog.GetDescriptor(SyncProviderKind.GoogleDrive);
        Type[] googleTypes = typeof(GoogleDriveRemoteFileSystem).Assembly
            .GetTypes()
            .Where(type => string.Equals(
                type.Namespace,
                "GameSaves.Infrastructure.GoogleDrive",
                StringComparison.Ordinal))
            .ToArray();

        Assert.True(google.IsImplemented);
        // Milestone U added the factory case itself, so the surviving
        // invariant is that there is exactly one, of the agreed shape, and
        // that having it activates nothing.
        MethodInfo driveCase = Assert.Single(
            typeof(SyncProviderFactory).GetMethods(),
            method => method.Name.Contains("Google", StringComparison.Ordinal));
        Assert.Equal("CreateGoogleDriveProvider", driveCase.Name);
        Assert.Equal(typeof(Guid), Assert.Single(
            driveCase.GetParameters()).ParameterType);
        // Milestone T added the wrapper itself, so the surviving invariant is
        // that it stays internal and unactivated, not that it is absent.
        Type wrapper = Assert.Single(
            googleTypes,
            type => type.Name == "GoogleDriveSyncProvider");
        Assert.False(wrapper.IsPublic);
        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
    }

    // Milestone U required that secrets and identifiers never reach a display
    // root. GetSafeDisplayRoot enforces it by falling back to a fixed label
    // whenever the saved display name would carry the account address or the
    // remote folder ID. Milestone V puts these roots on screen, so the scrub
    // is pinned here before that happens.
    [Theory]
    [InlineData("private-root-id-marker")]
    [InlineData("Backups (private-root-id-marker)")]
    [InlineData("user@example.invalid")]
    [InlineData("Backups for USER@EXAMPLE.INVALID")]
    public void DisplayRoot_FallsBackWhenTheSavedNameWouldCarryASecretOrIdentifier(
        string hostileDisplayName)
    {
        const string rootId = "private-root-id-marker";
        const string accountEmail = "user@example.invalid";

        var repository = new InMemorySyncRemoteProfileRepository();
        Guid profileId = Guid.NewGuid();
        repository.Create(DisplayRootProfile(
            profileId, rootId, accountEmail, hostileDisplayName));

        IRemoteFileSystem fileSystem = DisplayRootFactory(repository).Create(profileId);

        Assert.Equal("Google Drive", fileSystem.DisplayRoot);
        Assert.DoesNotContain(rootId, fileSystem.DisplayRoot, StringComparison.Ordinal);
        Assert.DoesNotContain(
            accountEmail, fileSystem.DisplayRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplayRoot_KeepsAnOrdinaryFolderNameSoTheScrubIsNotBlanket()
    {
        // Non-vacuity: a name carrying no identifier survives, so the theory
        // above is proving a scrub rather than a constant.
        var repository = new InMemorySyncRemoteProfileRepository();
        Guid profileId = Guid.NewGuid();
        repository.Create(DisplayRootProfile(
            profileId,
            "private-root-id-marker",
            "user@example.invalid",
            "GameSave Manager Backups"));

        IRemoteFileSystem fileSystem = DisplayRootFactory(repository).Create(profileId);

        Assert.Equal("GameSave Manager Backups", fileSystem.DisplayRoot);
    }

    private static GoogleDriveRemoteFileSystemFactory DisplayRootFactory(
        ISyncRemoteProfileRepository repository) =>
        new(
            repository,
            new RecordingValidationService(),
            new RecordingRootExistenceService(),
            new RecordingFolderExistenceService(),
            new RecordingRunFolderNameService(),
            new RecordingTextFileReadService(),
            new RecordingProviderMetadataReadService(),
            new RecordingProviderMetadataReplacementService(),
            new RecordingCreateOnlyTextFileService(),
            new RecordingRecursiveFileListingService(),
            new FakeGoogleDriveBinaryUploadService(),
            new FakeGoogleDriveBinaryDownloadService());

    private static SyncRemoteProfile DisplayRootProfile(
        Guid profileId,
        string rootId,
        string accountEmail,
        string displayName) =>
        new(
            profileId,
            "Profile name",
            SyncProviderKind.GoogleDrive,
            AccountDisplayName: accountEmail,
            RemoteRootDisplayName: displayName,
            ProviderSettings: new GoogleDriveSyncRemoteSettings(
                accountEmail,
                GoogleDriveAuthorizationScopes.DriveFile),
            CreatedUtc: DateTimeOffset.Parse("2026-08-18T10:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-18T10:00:00Z"),
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: rootId);

    private static IRemoteFileSystem Remote(
        RecordingValidationService validation,
        RecordingRootExistenceService? rootExistence = null,
        RecordingFolderExistenceService? folderExistence = null,
        RecordingRunFolderNameService? runFolderNames = null,
        RecordingTextFileReadService? textFileReads = null,
        RecordingProviderMetadataReadService? providerMetadataReads = null,
        RecordingProviderMetadataReplacementService?
            providerMetadataReplacements = null,
        RecordingCreateOnlyTextFileService? createOnlyTextFiles = null,
        RecordingRecursiveFileListingService? recursiveFileListing = null,
        FakeGoogleDriveBinaryUploadService? binaryUploads = null,
        FakeGoogleDriveBinaryDownloadService? binaryDownloads = null) =>
        new GoogleDriveRemoteFileSystem(
            ProfileId,
            "GameSave Manager Backups",
            validation,
            rootExistence ?? new RecordingRootExistenceService(),
            folderExistence ?? new RecordingFolderExistenceService(),
            runFolderNames ?? new RecordingRunFolderNameService(),
            textFileReads ?? new RecordingTextFileReadService(),
            providerMetadataReads ?? new RecordingProviderMetadataReadService(),
            providerMetadataReplacements ??
                new RecordingProviderMetadataReplacementService(),
            createOnlyTextFiles ?? new RecordingCreateOnlyTextFileService(),
            recursiveFileListing ??
                new RecordingRecursiveFileListingService(),
            binaryUploads ?? new FakeGoogleDriveBinaryUploadService(),
            binaryDownloads ?? new FakeGoogleDriveBinaryDownloadService());

    private static SyncRemoteProfile Profile() =>
        new(
            ProfileId,
            "Google Drive profile",
            SyncProviderKind.GoogleDrive,
            AccountDisplayName: "Example User",
            RemoteRootDisplayName: "GameSave Manager Backups",
            ProviderSettings: new GoogleDriveSyncRemoteSettings(
                "user@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile),
            CreatedUtc: DateTimeOffset.Parse("2026-07-31T10:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-07-31T10:00:00Z"),
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: "private-root-id-marker");

    private sealed class RecordingValidationService
        : IGoogleDriveRemoteValidationService
    {
        public GoogleDriveRemoteValidationResult Result { get; set; } =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid);

        public Func<
            Guid,
            CancellationToken,
            Task<GoogleDriveRemoteValidationResult>>? Handler { get; set; }

        public int Calls { get; private set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            ProfileIds.Add(remoteProfileId);
            CancellationTokens.Add(cancellationToken);
            return Handler is null
                ? Task.FromResult(Result)
                : Handler(remoteProfileId, cancellationToken);
        }
    }

    private sealed class RecordingRootExistenceService
        : IGoogleDriveRootExistenceService
    {
        public bool Result { get; set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<bool> ExistsAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingFolderExistenceService
        : IGoogleDriveFolderExistenceService
    {
        public bool Result { get; set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<string> RelativeFolders { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            RelativeFolders.Add(relativeFolder);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingRunFolderNameService
        : IGoogleDriveRunFolderNameService
    {
        public IReadOnlyList<string> Result { get; set; } =
            Array.Empty<string>();

        public List<Guid> ProfileIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingTextFileReadService
        : IGoogleDriveTextFileReadService
    {
        public string? Result { get; set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<string> RelativePaths { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            RelativePaths.Add(relativePath);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingProviderMetadataReadService
        : IGoogleDriveProviderMetadataReadService
    {
        public string? Result { get; set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<string> RelativePaths { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<string?> ReadAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            RelativePaths.Add(relativePath);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCreateOnlyTextFileService
        : IGoogleDriveCreateOnlyTextFileService
    {
        public List<Guid> ProfileIds { get; } = new();

        public List<string> RelativePaths { get; } = new();

        public List<string> Contents { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task CreateAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            RelativePaths.Add(relativePath);
            Contents.Add(content);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderMetadataReplacementService
        : IGoogleDriveProviderMetadataReplacementService
    {
        public List<Guid> ProfileIds { get; } = new();

        public List<string> RelativePaths { get; } = new();

        public List<string> Contents { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task ReplaceAsync(
            Guid remoteProfileId,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            RelativePaths.Add(relativePath);
            Contents.Add(content);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRecursiveFileListingService
        : IGoogleDriveRecursiveFileListingService
    {
        public IReadOnlyList<string> Result { get; set; } =
            Array.Empty<string>();

        public List<Guid> ProfileIds { get; } = new();

        public List<string> RelativeFolders { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            RelativeFolders.Add(relativeFolder);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }
}

internal sealed class FakeGoogleDriveBinaryUploadCall
{
    public FakeGoogleDriveBinaryUploadCall(
        string localFilePath,
        GoogleDriveBinaryUploadRequest request,
        CancellationToken cancellationToken)
    {
        LocalFilePath = localFilePath;
        Request = request;
        CancellationToken = cancellationToken;
    }

    public string LocalFilePath { get; }

    public GoogleDriveBinaryUploadRequest Request { get; }

    public CancellationToken CancellationToken { get; }
}

internal sealed class FakeGoogleDriveBinaryUploadService
    : IGoogleDriveBinaryUploadService
{
    public GoogleDriveBinaryUploadResult Result { get; set; } =
        new(GoogleDriveBinaryUploadStatus.Completed, 0);

    public Exception? Failure { get; set; }

    public List<FakeGoogleDriveBinaryUploadCall> Calls { get; } = [];

    public Task<GoogleDriveBinaryUploadResult> UploadAsync(
        string localFilePath,
        GoogleDriveBinaryUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeGoogleDriveBinaryUploadCall(
            localFilePath,
            request,
            cancellationToken));

        return Failure is null
            ? Task.FromResult(Result)
            : Task.FromException<GoogleDriveBinaryUploadResult>(Failure);
    }
}

internal sealed class FakeGoogleDriveBinaryDownloadCall
{
    public FakeGoogleDriveBinaryDownloadCall(
        GoogleDriveBinaryDownloadRequest request,
        string localFilePath,
        CancellationToken cancellationToken)
    {
        Request = request;
        LocalFilePath = localFilePath;
        CancellationToken = cancellationToken;
    }

    public GoogleDriveBinaryDownloadRequest Request { get; }

    public string LocalFilePath { get; }

    public CancellationToken CancellationToken { get; }
}

internal sealed class FakeGoogleDriveBinaryDownloadService
    : IGoogleDriveBinaryDownloadService
{
    public GoogleDriveBinaryDownloadResult Result { get; set; } =
        new(GoogleDriveBinaryDownloadStatus.Completed, 0);

    public Exception? Failure { get; set; }

    public List<FakeGoogleDriveBinaryDownloadCall> Calls { get; } = [];

    public Task<GoogleDriveBinaryDownloadResult> DownloadAsync(
        GoogleDriveBinaryDownloadRequest request,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeGoogleDriveBinaryDownloadCall(
            request,
            localFilePath,
            cancellationToken));

        return Failure is null
            ? Task.FromResult(Result)
            : Task.FromException<GoogleDriveBinaryDownloadResult>(Failure);
    }
}
