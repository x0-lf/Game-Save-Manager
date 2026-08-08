using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveOneLevelFileListingServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("621832c6-7ec0-46de-8a5b-d62f5a9856e8");

    private const string RunFolderId = "authoritative-run-folder-id";

    [Fact]
    public async Task EmptyFolder_ReturnsCompletedImmutableEmptyFileList()
    {
        var enumeration = new RecordingChildEnumerationService();
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(GoogleDriveRecursiveFileListingStatus.Completed, result.Status);
        Assert.Empty(result.Entries);
        Assert.False(result.Retryable);
        Assert.Null(result.SafeErrorCode);
        Assert.Null(result.SafeUserMessage);
        Assert.Equal(1, enumeration.CallCount);
        Assert.Equal(RunFolderId, enumeration.ParentFolderId);
        Assert.Same(resolved.OperationContext, enumeration.OperationContext);
        Assert.False(resolved.IsDisposed);
        IList entries = Assert.IsAssignableFrom<IList>(result.Entries);
        Assert.True(entries.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => entries.Clear());
    }

    [Fact]
    public async Task DirectManifest_ReturnsRunRelativePathAndAuthoritativeIdentity()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Blob("manifest-id", "manifest.json") }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        GoogleDriveRecursiveFileEntry entry = Assert.Single(result.Entries);
        Assert.Equal("manifest-id", entry.FileId);
        Assert.Equal(RunFolderId, entry.ParentFolderId);
        Assert.Equal("manifest.json", entry.ExactFileName);
        Assert.Equal("manifest.json", entry.CanonicalRelativePath);
        Assert.Equal("application/octet-stream", entry.MimeType);
        Assert.DoesNotContain("run-2026-08-03", entry.CanonicalRelativePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyFiles_AreOrderedByCanonicalPathUsingOrdinalComparison()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("z-id", "z.dat"),
                Blob("lower-id", "a.dat"),
                Blob("manifest-id", "manifest.json"),
                Blob("upper-id", "A.dat")
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(
            new[] { "A.dat", "a.dat", "manifest.json", "z.dat" },
            result.Entries.Select(entry => entry.CanonicalRelativePath));
        Assert.Equal(
            new[] { "upper-id", "lower-id", "manifest-id", "z-id" },
            result.Entries.Select(entry => entry.FileId));
    }

    [Fact]
    public async Task MixedFilesAndFolders_TraversesFoldersWithoutReturningThem()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Folder("folder-id", "files"),
                Blob("save-id", "save.dat"),
                Folder("empty-folder-id", "empty")
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        GoogleDriveRecursiveFileEntry file = Assert.Single(result.Entries);
        Assert.Equal("save.dat", file.CanonicalRelativePath);
        Assert.Equal(3, enumeration.CallCount);
        Assert.Equal(0, resolved.OperationContext.Resolver is NeverCalledResolver resolver
            ? resolver.CallCount
            : -1);
    }

    [Fact]
    public async Task ExactNames_PreserveUnicodeCaseApostrophesAndBackslashes()
    {
        const string exactName = "Pokémon O'Brien\\Save.DAT";
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Blob("unicode-id", exactName) }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        GoogleDriveRecursiveFileEntry entry = Assert.Single(result.Entries);
        Assert.Equal(exactName, entry.ExactFileName);
        Assert.Equal(exactName, entry.CanonicalRelativePath);
        Assert.DoesNotContain('/', entry.CanonicalRelativePath);
    }

    [Theory]
    [InlineData(GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument)]
    [InlineData(GoogleDriveRecursiveObjectKind.Shortcut)]
    public async Task UnsupportedDriveObject_FailsClosedWithoutPartialFiles(
        object kindValue)
    {
        GoogleDriveRecursiveObjectKind kind =
            Assert.IsType<GoogleDriveRecursiveObjectKind>(kindValue);
        string mimeType = kind switch
        {
            GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument =>
                "application/vnd.google-apps.document",
            GoogleDriveRecursiveObjectKind.Shortcut =>
                "application/vnd.google-apps.shortcut",
            _ => throw new ArgumentOutOfRangeException(nameof(kindValue))
        };
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("partial-file-id", "partial.dat"),
                Child("unsupported-object-id", "unsupported-object", mimeType, kind)
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.UnsupportedObject,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.UnsupportedObject,
            exception.Result.SafeErrorCode);
        Assert.Empty(exception.Result.Entries);
        Assert.False(exception.Result.Retryable);
        foreach (string privateValue in new[]
                 {
                     "partial-file-id",
                     "partial.dat",
                     "unsupported-object-id",
                     "unsupported-object"
                 })
        {
            Assert.DoesNotContain(
                privateValue,
                exception.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DuplicateNames_ArePreservedForLaterCollisionValidation()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("first-id", "save.dat"),
                Blob("second-id", "save.dat")
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries,
            entry => Assert.Equal("save.dat", entry.CanonicalRelativePath));
        Assert.Equal(
            new[] { "first-id", "second-id" },
            result.Entries.Select(entry => entry.FileId));
    }

    [Fact]
    public async Task CancellationBeforeEnumeration_StopsWithoutRemoteWork()
    {
        var enumeration = new RecordingChildEnumerationService();
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ListAsync(resolved, cancellation.Token));

        Assert.Equal(0, enumeration.CallCount);
        Assert.False(resolved.IsDisposed);
    }

    [Fact]
    public async Task CancellationAfterEnumeration_ReturnsNoPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Blob("late-id", "late.dat") },
            AfterEnumeration = cancellation.Cancel
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ListAsync(resolved, cancellation.Token));

        Assert.Equal(1, enumeration.CallCount);
        Assert.False(resolved.IsDisposed);
    }

    [Fact]
    public async Task OneNestedLevel_ReturnsFileRelativeToRunRoot()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Folder("files-folder-id", "files") }
        };
        enumeration.SetChildren(
            "files-folder-id",
            Blob("save-id", "save.dat", "files-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        GoogleDriveRecursiveFileEntry entry = Assert.Single(result.Entries);
        Assert.Equal("files/save.dat", entry.CanonicalRelativePath);
        Assert.Equal("files-folder-id", entry.ParentFolderId);
        Assert.DoesNotContain("run-2026-08-03", entry.CanonicalRelativePath,
            StringComparison.Ordinal);
        Assert.Equal(
            new[] { RunFolderId, "files-folder-id" },
            enumeration.ParentFolderIds);
    }

    [Fact]
    public async Task ManyNestedLevels_UsesIterativeFolderQueue()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Folder("files-folder-id", "files") }
        };
        enumeration.SetChildren(
            "files-folder-id",
            Folder("drive-folder-id", "C", "files-folder-id"));
        enumeration.SetChildren(
            "drive-folder-id",
            Folder("users-folder-id", "Users", "drive-folder-id"));
        enumeration.SetChildren(
            "users-folder-id",
            Folder("player-folder-id", "Player", "users-folder-id"));
        enumeration.SetChildren(
            "player-folder-id",
            Blob("save-id", "game-save.bin", "player-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        GoogleDriveRecursiveFileEntry entry = Assert.Single(result.Entries);
        Assert.Equal(
            "files/C/Users/Player/game-save.bin",
            entry.CanonicalRelativePath);
        Assert.Equal("player-folder-id", entry.ParentFolderId);
        Assert.Equal(5, enumeration.CallCount);
    }

    [Fact]
    public async Task SiblingDirectories_ReturnFilesFromEveryBranch()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Folder("slot-two-id", "slot2"),
                Folder("slot-one-id", "slot1")
            }
        };
        enumeration.SetChildren(
            "slot-two-id",
            Blob("slot-two-save-id", "save.dat", "slot-two-id"));
        enumeration.SetChildren(
            "slot-one-id",
            Blob("slot-one-save-id", "save.dat", "slot-one-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(
            new[] { "slot1/save.dat", "slot2/save.dat" },
            result.Entries.Select(entry => entry.CanonicalRelativePath));
        Assert.Equal(
            new[] { "slot-one-save-id", "slot-two-save-id" },
            result.Entries.Select(entry => entry.FileId));
    }

    [Fact]
    public async Task EmptyDirectories_ProduceNoFileEntries()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Folder("empty-one-id", "empty-one"),
                Folder("empty-two-id", "empty-two")
            }
        };
        enumeration.SetChildren("empty-one-id");
        enumeration.SetChildren("empty-two-id");
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(GoogleDriveRecursiveFileListingStatus.Completed, result.Status);
        Assert.Empty(result.Entries);
        Assert.Equal(3, enumeration.CallCount);
    }

    [Fact]
    public async Task MixedFilesAtEveryDepth_ReturnsOnlyCanonicalFilePaths()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("manifest-id", "manifest.json"),
                Folder("files-folder-id", "files")
            }
        };
        enumeration.SetChildren(
            "files-folder-id",
            Blob("save-id", "save.dat", "files-folder-id"),
            Folder("nested-folder-id", "nested", "files-folder-id"));
        enumeration.SetChildren(
            "nested-folder-id",
            Blob("game-id", "game.bin", "nested-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(
            new[]
            {
                "files/nested/game.bin",
                "files/save.dat",
                "manifest.json"
            },
            result.Entries.Select(entry => entry.CanonicalRelativePath));
        Assert.All(
            result.Entries,
            entry => Assert.False(entry.CanonicalRelativePath.StartsWith(
                "run-2026-08-03/",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NestedResults_AreDeterministicAcrossProviderOrdering()
    {
        var firstEnumeration = NestedOrderingTree(reverseProviderOrder: false);
        var secondEnumeration = NestedOrderingTree(reverseProviderOrder: true);
        var firstService =
            new GoogleDriveOneLevelFileListingService(firstEnumeration);
        var secondService =
            new GoogleDriveOneLevelFileListingService(secondEnumeration);
        using GoogleDriveResolvedRunFolder firstResolved = ResolvedRunFolder();
        using GoogleDriveResolvedRunFolder secondResolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult first =
            await firstService.ListAsync(firstResolved);
        GoogleDriveRecursiveFileListingResult second =
            await secondService.ListAsync(secondResolved);

        Assert.Equal(
            first.Entries.Select(entry => entry.CanonicalRelativePath),
            second.Entries.Select(entry => entry.CanonicalRelativePath));
        Assert.Equal(
            new[] { "a/root.dat", "z/child.dat" },
            first.Entries.Select(entry => entry.CanonicalRelativePath));
    }

    [Fact]
    public async Task CompleteTraversal_ReusesSingleOperationContext()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Folder("first-folder-id", "first") }
        };
        enumeration.SetChildren(
            "first-folder-id",
            Folder("second-folder-id", "second", "first-folder-id"));
        enumeration.SetChildren(
            "second-folder-id",
            Blob("save-id", "save.dat", "second-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Single(result.Entries);
        Assert.Equal(3, enumeration.OperationContexts.Count);
        Assert.All(
            enumeration.OperationContexts,
            context => Assert.Same(resolved.OperationContext, context));
        Assert.Equal(0, resolved.OperationContext.Resolver is NeverCalledResolver resolver
            ? resolver.CallCount
            : -1);
        Assert.False(resolved.IsDisposed);
    }

    [Fact]
    public void ServiceBoundary_IsInfrastructureInternalReadOnlyAndRegistered()
    {
        Assert.False(typeof(IGoogleDriveOneLevelFileListingService).IsPublic);
        Assert.False(typeof(GoogleDriveOneLevelFileListingService).IsPublic);

        ConstructorInfo constructor = Assert.Single(
            typeof(GoogleDriveOneLevelFileListingService).GetConstructors());
        Assert.Equal(
            new[] { typeof(IGoogleDriveFolderChildEnumerationService) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            new[] { "EnumerateAsync" },
            typeof(IGoogleDriveFolderChildEnumerationService)
                .GetMethods()
                .Select(method => method.Name));

        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType ==
                typeof(IGoogleDriveOneLevelFileListingService));
        Assert.Equal(
            typeof(GoogleDriveOneLevelFileListingService),
            descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static RecordingChildEnumerationService NestedOrderingTree(
        bool reverseProviderOrder)
    {
        GoogleDriveFolderChildEntry aFolder = Folder("a-folder-id", "a");
        GoogleDriveFolderChildEntry zFolder = Folder("z-folder-id", "z");
        var enumeration = new RecordingChildEnumerationService
        {
            Children = reverseProviderOrder
                ? new[] { zFolder, aFolder }
                : new[] { aFolder, zFolder }
        };
        GoogleDriveFolderChildEntry aFile =
            Blob("a-file-id", "root.dat", "a-folder-id");
        GoogleDriveFolderChildEntry zFile =
            Blob("z-file-id", "child.dat", "z-folder-id");
        enumeration.SetChildren("a-folder-id", aFile);
        enumeration.SetChildren("z-folder-id", zFile);
        return enumeration;
    }

    private static GoogleDriveFolderChildEntry Blob(
        string objectId,
        string exactName,
        string parentFolderId = RunFolderId) =>
        Child(
            objectId,
            exactName,
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            parentFolderId);

    private static GoogleDriveFolderChildEntry Folder(
        string objectId,
        string exactName,
        string parentFolderId = RunFolderId) =>
        Child(
            objectId,
            exactName,
            GoogleDriveApplicationRoot.FolderMimeType,
            GoogleDriveRecursiveObjectKind.Folder,
            parentFolderId);

    private static GoogleDriveFolderChildEntry Child(
        string objectId,
        string exactName,
        string mimeType,
        GoogleDriveRecursiveObjectKind kind,
        string parentFolderId = RunFolderId) =>
        new(
            objectId,
            exactName,
            mimeType,
            kind,
            new[] { parentFolderId },
            trashed: false,
            driveId: null);

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
            IssuedUtc = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc)
        };
        var credential = new GoogleAuthorizedCredential(
            new UserCredential(flow, ProfileId.ToString("D"), token),
            wasAuthenticationRefreshed: false);
        var context = new GoogleDriveRemoteOperationContext(
            ProfileId,
            "authoritative-application-root-id",
            credential,
            new NeverCalledResolver());
        return new GoogleDriveResolvedRunFolder(RunFolderId, context);
    }

    private sealed class RecordingChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        private readonly Dictionary<string, IReadOnlyList<GoogleDriveFolderChildEntry>>
            _childrenByParent = new(StringComparer.Ordinal);

        public IReadOnlyList<GoogleDriveFolderChildEntry> Children { get; set; } =
            Array.Empty<GoogleDriveFolderChildEntry>();

        public Action? AfterEnumeration { get; set; }

        public int CallCount { get; private set; }

        public GoogleDriveRemoteOperationContext? OperationContext { get; private set; }

        public string? ParentFolderId { get; private set; }

        public List<GoogleDriveRemoteOperationContext> OperationContexts { get; } =
            new();

        public List<string> ParentFolderIds { get; } = new();

        public void SetChildren(
            string parentFolderId,
            params GoogleDriveFolderChildEntry[] children) =>
            _childrenByParent[parentFolderId] = children;

        public Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            OperationContext = context;
            ParentFolderId = parentFolderId;
            OperationContexts.Add(context);
            ParentFolderIds.Add(parentFolderId);
            AfterEnumeration?.Invoke();
            IReadOnlyList<GoogleDriveFolderChildEntry> result = _childrenByParent
                .TryGetValue(parentFolderId, out IReadOnlyList<
                    GoogleDriveFolderChildEntry>? configured)
                ? configured
                : string.Equals(parentFolderId, RunFolderId, StringComparison.Ordinal)
                    ? Children
                    : Array.Empty<GoogleDriveFolderChildEntry>();
            return Task.FromResult(result);
        }
    }

    private sealed class NeverCalledResolver : IGoogleDriveObjectPathResolver
    {
        public int CallCount { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) => Called();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) => Called();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) => Called();

        private Task<GoogleDriveObjectResolutionResult> Called()
        {
            CallCount++;
            throw new InvalidOperationException(
                "No resolver call is permitted during one-level listing.");
        }
    }
}
