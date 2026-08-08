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
    private const string RootFolderId = "authoritative-application-root-id";

    public static TheoryData<string> UnsupportedDriveMimeTypes => new()
    {
        "application/vnd.google-apps.shortcut",
        "application/vnd.google-apps.document",
        "application/vnd.google-apps.spreadsheet",
        "application/vnd.google-apps.presentation",
        "application/vnd.google-apps.form",
        "application/vnd.google-apps.drawing",
        "application/vnd.google-apps.site",
        "application/vnd.google-apps.future-object"
    };

    [Fact]
    public async Task EmptyFolder_ReturnsCompletedImmutableEmptyFileList()
    {
        var enumeration = new RecordingChildEnumerationService();
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();
        GoogleDriveRemoteOperationContext operationContext =
            resolved.OperationContext;

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(GoogleDriveRecursiveFileListingStatus.Completed, result.Status);
        Assert.Empty(result.Entries);
        Assert.False(result.Retryable);
        Assert.Null(result.SafeErrorCode);
        Assert.Null(result.SafeUserMessage);
        Assert.Equal(1, enumeration.CallCount);
        Assert.Equal(RunFolderId, enumeration.ParentFolderId);
        Assert.Same(operationContext, enumeration.OperationContext);
        Assert.True(resolved.IsDisposed);
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
                Blob("upper-id", "B.dat")
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Equal(
            new[] { "B.dat", "a.dat", "manifest.json", "z.dat" },
            result.Entries.Select(entry => entry.CanonicalRelativePath));
        Assert.Equal(
            new[] { "upper-id", "lower-id", "manifest-id", "z-id" },
            result.Entries.Select(entry => entry.FileId));
    }

    [Fact]
    public void FinalValidation_OrdersValidNestedPathsOrdinally()
    {
        GoogleDriveRecursiveFileEntry[] files =
        {
            ListedFile("z-id", "z-parent-id", "save.dat", "z/save.dat"),
            ListedFile("a-id", "a-parent-id", "Save.dat", "a/Save.dat"),
            ListedFile("root-id", RunFolderId, "manifest.json", "manifest.json")
        };

        GoogleDriveRecursiveFileEntry[] ordered =
            GoogleDriveOneLevelFileListingService.ValidateAndOrderFiles(
                files,
                CancellationToken.None);

        Assert.Equal(
            new[] { "a/Save.dat", "manifest.json", "z/save.dat" },
            ordered.Select(file => file.CanonicalRelativePath));
    }

    [Fact]
    public void FinalValidation_RejectsExactFullPathCollision()
    {
        GoogleDriveRecursiveFileEntry[] files =
        {
            ListedFile(
                "private-first-id",
                "private-first-parent-id",
                "save.dat",
                "private-folder/save.dat"),
            ListedFile(
                "private-second-id",
                "private-second-parent-id",
                "save.dat",
                "private-folder/save.dat")
        };

        GoogleDriveRecursiveFileListingException exception = Assert.Throws<
            GoogleDriveRecursiveFileListingException>(() =>
                GoogleDriveOneLevelFileListingService.ValidateAndOrderFiles(
                    files,
                    CancellationToken.None));

        AssertAmbiguous(
            exception,
            "private-first-id",
            "private-second-id",
            "private-first-parent-id",
            "private-second-parent-id",
            "private-folder/save.dat");
    }

    [Fact]
    public void FinalValidation_RejectsCaseInsensitiveFullPathCollision()
    {
        GoogleDriveRecursiveFileEntry[] files =
        {
            ListedFile(
                "private-first-id",
                "private-first-parent-id",
                "save.dat",
                "private-folder/save.dat"),
            ListedFile(
                "private-second-id",
                "private-second-parent-id",
                "SAVE.DAT",
                "PRIVATE-FOLDER/SAVE.DAT")
        };

        GoogleDriveRecursiveFileListingException exception = Assert.Throws<
            GoogleDriveRecursiveFileListingException>(() =>
                GoogleDriveOneLevelFileListingService.ValidateAndOrderFiles(
                    files,
                    CancellationToken.None));

        AssertCaseCollision(
            exception,
            "private-first-id",
            "private-second-id",
            "private-first-parent-id",
            "private-second-parent-id",
            "private-folder/save.dat",
            "PRIVATE-FOLDER/SAVE.DAT");
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
        var resolver = Assert.IsType<NeverCalledResolver>(
            resolved.OperationContext.Resolver);

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        GoogleDriveRecursiveFileEntry file = Assert.Single(result.Entries);
        Assert.Equal("save.dat", file.CanonicalRelativePath);
        Assert.Equal(3, enumeration.CallCount);
        Assert.Equal(0, resolver.CallCount);
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
    [MemberData(nameof(UnsupportedDriveMimeTypes))]
    public async Task UnsupportedObjectAtRunRoot_FailsClosedWithoutPartialFiles(
        string mimeType)
    {
        GoogleDriveRecursiveObjectKind kind =
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType);
        Assert.True(kind is
            GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument or
            GoogleDriveRecursiveObjectKind.Shortcut);
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

        AssertUnsupportedObject(
            exception,
            mimeType,
            "partial-file-id",
            "partial.dat",
            "unsupported-object-id",
            "unsupported-object");
        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
        Assert.DoesNotContain(
            mimeType,
            GoogleDriveRecursiveObjectClassificationPolicy
                .ToSafeDiagnosticString(kind),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedObjectDeepInTree_FailsWithoutPartialListing()
    {
        const string unsupportedMimeType =
            "application/vnd.google-apps.future-object";
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("root-file-id", "root.dat"),
                Folder("nested-folder-id", "nested"),
                Folder("untouched-folder-id", "untouched")
            }
        };
        enumeration.SetChildren(
            "nested-folder-id",
            Blob("nested-file-id", "nested.dat", "nested-folder-id"),
            Child(
                "unsupported-object-id",
                "unsupported-object",
                unsupportedMimeType,
                GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument,
                "nested-folder-id"));
        enumeration.SetChildren(
            "untouched-folder-id",
            Blob("untouched-file-id", "untouched.dat", "untouched-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertUnsupportedObject(
            exception,
            unsupportedMimeType,
            "root-file-id",
            "root.dat",
            "nested-file-id",
            "nested.dat",
            "unsupported-object-id",
            "unsupported-object",
            "nested-folder-id");
        Assert.Equal(
            new[] { RunFolderId, "nested-folder-id" },
            enumeration.ParentFolderIds);
        Assert.DoesNotContain(
            "untouched-folder-id",
            enumeration.ParentFolderIds,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ExactDuplicateFileNames_FailWithoutSelectingEitherFile()
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

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertAmbiguous(
            exception,
            "save.dat",
            "first-id",
            "second-id",
            RunFolderId);
        Assert.Equal(1, enumeration.CallCount);
    }

    [Fact]
    public async Task ExactDuplicateFolderNames_FailBeforeEitherFolderIsTraversed()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Folder("first-folder-id", "files"),
                Folder("second-folder-id", "files")
            }
        };
        enumeration.SetChildren(
            "first-folder-id",
            Blob("first-save-id", "first.dat", "first-folder-id"));
        enumeration.SetChildren(
            "second-folder-id",
            Blob("second-save-id", "second.dat", "second-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertAmbiguous(
            exception,
            "files",
            "first-folder-id",
            "second-folder-id",
            RunFolderId);
        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
    }

    [Fact]
    public async Task ExactFileFolderNameCollision_FailsWithoutChoosingAType()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("file-id", "save"),
                Folder("folder-id", "save")
            }
        };
        enumeration.SetChildren(
            "folder-id",
            Blob("nested-id", "nested.dat", "folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertAmbiguous(
            exception,
            "save",
            "file-id",
            "folder-id",
            RunFolderId);
        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
    }

    [Theory]
    [InlineData("save.dat", "SAVE.DAT")]
    [InlineData("\u00c9lan.dat", "\u00e9lan.dat")]
    public async Task CaseInsensitiveFileNameCollision_FailsWithoutSelectingASpelling(
        string firstName,
        string secondName)
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("first-id", firstName),
                Blob("second-id", secondName)
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertCaseCollision(
            exception,
            firstName,
            secondName,
            "first-id",
            "second-id",
            RunFolderId);
        Assert.Equal(1, enumeration.CallCount);
    }

    [Fact]
    public async Task CaseInsensitiveFolderNameCollision_FailsBeforeTraversal()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Folder("first-folder-id", "files"),
                Folder("second-folder-id", "FILES")
            }
        };
        enumeration.SetChildren(
            "first-folder-id",
            Blob("first-save-id", "first.dat", "first-folder-id"));
        enumeration.SetChildren(
            "second-folder-id",
            Blob("second-save-id", "second.dat", "second-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertCaseCollision(
            exception,
            "files",
            "FILES",
            "first-folder-id",
            "second-folder-id",
            RunFolderId);
        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
    }

    [Fact]
    public async Task CaseInsensitiveFileFolderCollision_FailsWithoutChoosingAType()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("file-id", "save"),
                Folder("folder-id", "SAVE")
            }
        };
        enumeration.SetChildren(
            "folder-id",
            Blob("nested-id", "nested.dat", "folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertCaseCollision(
            exception,
            "save",
            "SAVE",
            "file-id",
            "folder-id",
            RunFolderId);
        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
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
        Assert.True(resolved.IsDisposed);
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
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task CancellationBeforeChildQueueing_StopsWithoutNestedWorkOrCache()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new CancelAfterEnumerationCountList<
                GoogleDriveFolderChildEntry>(
                    new[] { Folder("late-folder-id", "late") },
                    cancellation,
                    cancelAfterEnumerationCount: 2)
        };
        var cache = new GoogleDriveObjectIdCache();
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ListAsync(resolved, cancellation.Token));

        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootFolderId),
            RunFolderId,
            "late",
            GoogleDriveObjectKind.Folder,
            out _));
        Assert.True(resolved.IsDisposed);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RepeatedIdentityAtRunRoot_FailsBeforeFurtherTraversal(
        int scenario)
    {
        const string repeatedId = "private-repeated-id";
        var enumeration = new RecordingChildEnumerationService
        {
            Children = scenario switch
            {
                0 => new[] { Folder(RunFolderId, "private-cycle") },
                1 => new[]
                {
                    Folder(repeatedId, "private-first-folder"),
                    Folder(repeatedId, "private-second-folder")
                },
                2 => new[]
                {
                    Blob(repeatedId, "private-first.dat"),
                    Blob(repeatedId, "private-second.dat")
                },
                3 => new[]
                {
                    Folder(repeatedId, "private-folder"),
                    Blob(repeatedId, "private-file.dat")
                },
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            }
        };
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertInvalidMetadata(
            exception,
            RunFolderId,
            repeatedId,
            "private-cycle",
            "private-first-folder",
            "private-second-folder",
            "private-first.dat",
            "private-second.dat",
            "private-folder",
            "private-file.dat");
        Assert.Equal(new[] { RunFolderId }, enumeration.ParentFolderIds);
    }

    [Fact]
    public async Task DeepFolderCycle_FailsWithoutRepeatingTraversal()
    {
        const string firstFolderId = "private-first-folder-id";
        const string secondFolderId = "private-second-folder-id";
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Folder(firstFolderId, "private-first") }
        };
        enumeration.SetChildren(
            firstFolderId,
            Folder(secondFolderId, "private-second", firstFolderId));
        enumeration.SetChildren(
            secondFolderId,
            Folder(firstFolderId, "private-cycle", secondFolderId));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertInvalidMetadata(
            exception,
            firstFolderId,
            secondFolderId,
            "private-first",
            "private-second",
            "private-cycle");
        Assert.Equal(
            new[] { RunFolderId, firstFolderId, secondFolderId },
            enumeration.ParentFolderIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedIdentityAcrossBranches_FailsWithoutPartialListing(
        bool repeatedObjectIsFolder)
    {
        const string leftFolderId = "private-left-folder-id";
        const string rightFolderId = "private-right-folder-id";
        const string repeatedId = "private-repeated-child-id";
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Folder(leftFolderId, "left"),
                Folder(rightFolderId, "right")
            }
        };
        enumeration.SetChildren(
            leftFolderId,
            repeatedObjectIsFolder
                ? Folder(repeatedId, "private-first-folder", leftFolderId)
                : Blob(repeatedId, "private-first.dat", leftFolderId));
        enumeration.SetChildren(
            rightFolderId,
            repeatedObjectIsFolder
                ? Folder(repeatedId, "private-second-folder", rightFolderId)
                : Blob(repeatedId, "private-second.dat", rightFolderId));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertInvalidMetadata(
            exception,
            leftFolderId,
            rightFolderId,
            repeatedId,
            "private-first-folder",
            "private-second-folder",
            "private-first.dat",
            "private-second.dat");
        Assert.Equal(
            new[] { RunFolderId, leftFolderId, rightFolderId },
            enumeration.ParentFolderIds);
        Assert.DoesNotContain(
            repeatedId,
            enumeration.ParentFolderIds,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task IdenticalFileNamesUnderDistinctParents_RemainValid()
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
        var caseInsensitivePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.All(
            result.Entries,
            entry => Assert.True(
                caseInsensitivePaths.Add(entry.CanonicalRelativePath)));
    }

    [Fact]
    public async Task ExactDuplicateInLaterBranch_ReturnsNoPartialListing()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("root-file-id", "root.dat"),
                Folder("unique-folder-id", "unique"),
                Folder("ambiguous-folder-id", "ambiguous"),
                Folder("untouched-folder-id", "untouched")
            }
        };
        enumeration.SetChildren(
            "unique-folder-id",
            Blob("unique-file-id", "save.dat", "unique-folder-id"));
        enumeration.SetChildren(
            "ambiguous-folder-id",
            Blob("first-duplicate-id", "duplicate.dat", "ambiguous-folder-id"),
            Blob("second-duplicate-id", "duplicate.dat", "ambiguous-folder-id"));
        enumeration.SetChildren(
            "untouched-folder-id",
            Blob("untouched-file-id", "untouched.dat", "untouched-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertAmbiguous(
            exception,
            "duplicate.dat",
            "first-duplicate-id",
            "second-duplicate-id",
            "ambiguous-folder-id");
        Assert.Equal(
            new[] { RunFolderId, "unique-folder-id", "ambiguous-folder-id" },
            enumeration.ParentFolderIds);
        Assert.DoesNotContain(
            "untouched-folder-id",
            enumeration.ParentFolderIds,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task CaseInsensitiveCollisionInNestedParent_ReturnsNoPartialListing()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("root-file-id", "root.dat"),
                Folder("unique-folder-id", "unique"),
                Folder("collision-folder-id", "collision"),
                Folder("untouched-folder-id", "untouched")
            }
        };
        enumeration.SetChildren(
            "unique-folder-id",
            Blob("unique-file-id", "save.dat", "unique-folder-id"));
        enumeration.SetChildren(
            "collision-folder-id",
            Blob("first-collision-id", "profile.sav", "collision-folder-id"),
            Blob("second-collision-id", "PROFILE.SAV", "collision-folder-id"));
        enumeration.SetChildren(
            "untouched-folder-id",
            Blob("untouched-file-id", "untouched.dat", "untouched-folder-id"));
        var service = new GoogleDriveOneLevelFileListingService(enumeration);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.ListAsync(resolved));

        AssertCaseCollision(
            exception,
            "profile.sav",
            "PROFILE.SAV",
            "first-collision-id",
            "second-collision-id",
            "collision-folder-id");
        Assert.Equal(
            new[] { RunFolderId, "unique-folder-id", "collision-folder-id" },
            enumeration.ParentFolderIds);
        Assert.DoesNotContain(
            "untouched-folder-id",
            enumeration.ParentFolderIds,
            StringComparer.Ordinal);
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
        GoogleDriveRemoteOperationContext operationContext =
            resolved.OperationContext;
        var resolver = Assert.IsType<NeverCalledResolver>(
            operationContext.Resolver);

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Single(result.Entries);
        Assert.Equal(3, enumeration.OperationContexts.Count);
        Assert.All(
            enumeration.OperationContexts,
            context => Assert.Same(operationContext, context));
        Assert.Equal(0, resolver.CallCount);
        Assert.True(resolved.IsDisposed);
    }

    [Fact]
    public async Task SuccessfulTraversal_CachesOnlyValidatedFolderAndFileIdentities()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Folder("files-folder-id", "files") }
        };
        enumeration.SetChildren(
            "files-folder-id",
            Blob("save-id", "save.dat", "files-folder-id"));
        var cache = new GoogleDriveObjectIdCache();
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        GoogleDriveRecursiveFileListingResult result =
            await service.ListAsync(resolved);

        Assert.Single(result.Entries);
        var scope = new GoogleDriveObjectCacheScope(ProfileId, RootFolderId);
        Assert.True(cache.TryGet(
            scope,
            RunFolderId,
            "files",
            GoogleDriveObjectKind.Folder,
            out GoogleDriveObjectIdCacheEntry? folder));
        Assert.Equal("files-folder-id", folder!.ObjectId);
        Assert.True(cache.TryGet(
            scope,
            "files-folder-id",
            "save.dat",
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? file));
        Assert.Equal("save-id", file!.ObjectId);
        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(Guid.NewGuid(), RootFolderId),
            RunFolderId,
            "files",
            GoogleDriveObjectKind.Folder,
            out _));
        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, "different-root-id"),
            RunFolderId,
            "files",
            GoogleDriveObjectKind.Folder,
            out _));
    }

    [Fact]
    public async Task FailedTraversal_DoesNotCommitStagedCacheEntries()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[]
            {
                Blob("valid-id", "valid.dat"),
                Child(
                    "workspace-id",
                    "notes",
                    "application/vnd.google-apps.document",
                    GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument)
            }
        };
        var cache = new GoogleDriveObjectIdCache();
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
            service.ListAsync(resolved));

        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootFolderId),
            RunFolderId,
            "valid.dat",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Fact]
    public async Task ConfirmedMissingNestedFolder_EvictsOnlyItsPreciseCacheEntry()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new[] { Folder("missing-folder-id", "missing") }
        };
        enumeration.SetFailure(
            "missing-folder-id",
            ListingFailure(GoogleDriveRecursiveFileListingStatus.FolderNotFound));
        var cache = new GoogleDriveObjectIdCache();
        var scope = new GoogleDriveObjectCacheScope(ProfileId, RootFolderId);
        Assert.True(cache.TryStoreUniqueValidated(
            scope,
            RunFolderId,
            "missing",
            GoogleDriveObjectKind.Folder,
            Metadata(
                "missing-folder-id",
                "missing",
                GoogleDriveApplicationRoot.FolderMimeType,
                RunFolderId)));
        Assert.True(cache.TryStoreUniqueValidated(
            scope,
            RunFolderId,
            "keep.dat",
            GoogleDriveObjectKind.File,
            Metadata("keep-id", "keep.dat", "application/octet-stream", RunFolderId)));
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
            service.ListAsync(resolved));

        Assert.False(cache.TryGet(
            scope,
            RunFolderId,
            "missing",
            GoogleDriveObjectKind.Folder,
            out _));
        Assert.True(cache.TryGet(
            scope,
            RunFolderId,
            "keep.dat",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Fact]
    public async Task ReauthenticationFailure_InvalidatesOnlyAffectedProfile()
    {
        var enumeration = new RecordingChildEnumerationService();
        enumeration.SetFailure(
            RunFolderId,
            ListingFailure(
                GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired));
        var cache = new GoogleDriveObjectIdCache();
        var affectedScope = new GoogleDriveObjectCacheScope(ProfileId, RootFolderId);
        Guid otherProfileId = Guid.NewGuid();
        var otherScope = new GoogleDriveObjectCacheScope(
            otherProfileId,
            RootFolderId);
        Assert.True(cache.TryStoreUniqueValidated(
            affectedScope,
            RunFolderId,
            "affected.dat",
            GoogleDriveObjectKind.File,
            Metadata(
                "affected-id",
                "affected.dat",
                "application/octet-stream",
                RunFolderId)));
        Assert.True(cache.TryStoreUniqueValidated(
            otherScope,
            RunFolderId,
            "other.dat",
            GoogleDriveObjectKind.File,
            Metadata(
                "other-id",
                "other.dat",
                "application/octet-stream",
                RunFolderId)));
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
            service.ListAsync(resolved));

        Assert.False(cache.TryGet(
            affectedScope,
            RunFolderId,
            "affected.dat",
            GoogleDriveObjectKind.File,
            out _));
        Assert.True(cache.TryGet(
            otherScope,
            RunFolderId,
            "other.dat",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Theory]
    [InlineData(nameof(GoogleDriveRecursiveFileListingStatus.RateLimited))]
    [InlineData(nameof(GoogleDriveRecursiveFileListingStatus.Unavailable))]
    public async Task TemporaryFailure_PreservesSafeCacheState(
        string statusName)
    {
        GoogleDriveRecursiveFileListingStatus status =
            Enum.Parse<GoogleDriveRecursiveFileListingStatus>(statusName);
        var enumeration = new RecordingChildEnumerationService();
        enumeration.SetFailure(RunFolderId, ListingFailure(status, retryable: true));
        var cache = new GoogleDriveObjectIdCache();
        var scope = new GoogleDriveObjectCacheScope(ProfileId, RootFolderId);
        Assert.True(cache.TryStoreUniqueValidated(
            scope,
            RunFolderId,
            "safe.dat",
            GoogleDriveObjectKind.File,
            Metadata(
                "safe-id",
                "safe.dat",
                "application/octet-stream",
                RunFolderId)));
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
            service.ListAsync(resolved));

        Assert.True(cache.TryGet(
            scope,
            RunFolderId,
            "safe.dat",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Fact]
    public async Task CancellationBeforeCacheCommit_WritesNoCacheState()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService
        {
            Children = new CancelAfterEnumerationCountList<
                GoogleDriveFolderChildEntry>(
                    new[] { Blob("late-id", "late.dat") },
                    cancellation,
                    cancelAfterEnumerationCount: 3)
        };
        var cache = new GoogleDriveObjectIdCache();
        var service = new GoogleDriveOneLevelFileListingService(enumeration, cache);
        using GoogleDriveResolvedRunFolder resolved = ResolvedRunFolder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ListAsync(resolved, cancellation.Token));

        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootFolderId),
            RunFolderId,
            "late.dat",
            GoogleDriveObjectKind.File,
            out _));
    }

    [Fact]
    public void ServiceBoundary_IsInfrastructureInternalReadOnlyAndRegistered()
    {
        Assert.False(typeof(IGoogleDriveOneLevelFileListingService).IsPublic);
        Assert.False(typeof(GoogleDriveOneLevelFileListingService).IsPublic);

        ConstructorInfo constructor = Assert.Single(
            typeof(GoogleDriveOneLevelFileListingService).GetConstructors());
        Assert.Equal(
            new[]
            {
                typeof(IGoogleDriveFolderChildEnumerationService),
                typeof(IGoogleDriveObjectIdCache)
            },
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

    private static void AssertAmbiguous(
        GoogleDriveRecursiveFileListingException exception,
        params string[] privateValues)
    {
        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.Ambiguous,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.Ambiguous,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            "The Google Drive backup folder contains ambiguous duplicate names.",
            exception.Result.SafeUserMessage);

        foreach (string privateValue in privateValues)
        {
            Assert.DoesNotContain(
                privateValue,
                exception.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.SafeUserMessage,
                StringComparison.Ordinal);
        }
    }

    private static void AssertCaseCollision(
        GoogleDriveRecursiveFileListingException exception,
        params string[] privateValues)
    {
        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.CaseCollision,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.CaseCollision,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            "The Google Drive backup folder contains names that differ only by case.",
            exception.Result.SafeUserMessage);

        foreach (string privateValue in privateValues)
        {
            Assert.DoesNotContain(
                privateValue,
                exception.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.SafeUserMessage,
                StringComparison.Ordinal);
        }
    }

    private static void AssertUnsupportedObject(
        GoogleDriveRecursiveFileListingException exception,
        params string[] privateValues)
    {
        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.UnsupportedObject,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.UnsupportedObject,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            "The Google Drive backup folder contains an unsupported object.",
            exception.Result.SafeUserMessage);

        foreach (string privateValue in privateValues)
        {
            Assert.DoesNotContain(
                privateValue,
                exception.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.SafeUserMessage,
                StringComparison.Ordinal);
        }
    }

    private static void AssertInvalidMetadata(
        GoogleDriveRecursiveFileListingException exception,
        params string[] privateValues)
    {
        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.InvalidMetadata,
            exception.Result.SafeErrorCode);
        Assert.False(exception.Result.Retryable);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            "Google Drive returned invalid file metadata.",
            exception.Result.SafeUserMessage);

        foreach (string privateValue in privateValues)
        {
            Assert.DoesNotContain(
                privateValue,
                exception.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                privateValue,
                exception.Result.SafeUserMessage,
                StringComparison.Ordinal);
        }
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

    private static GoogleDriveRecursiveFileEntry ListedFile(
        string fileId,
        string parentFolderId,
        string exactName,
        string canonicalPath) =>
        new(
            fileId,
            parentFolderId,
            exactName,
            canonicalPath,
            "application/octet-stream");

    private static GoogleDriveObjectMetadata Metadata(
        string objectId,
        string exactName,
        string mimeType,
        string parentFolderId) =>
        new(
            objectId,
            exactName,
            mimeType,
            trashed: false,
            new[] { parentFolderId },
            driveId: null);

    private static GoogleDriveRecursiveFileListingException ListingFailure(
        GoogleDriveRecursiveFileListingStatus status,
        bool retryable = false) =>
        new(new GoogleDriveRecursiveFileListingResult(
            status,
            Array.Empty<GoogleDriveRecursiveFileEntry>(),
            retryable,
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
            "The Google Drive backup folder could not be listed."));

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
            RootFolderId,
            credential,
            new NeverCalledResolver());
        return new GoogleDriveResolvedRunFolder(RunFolderId, context);
    }

    private sealed class RecordingChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        private readonly Dictionary<string, IReadOnlyList<GoogleDriveFolderChildEntry>>
            _childrenByParent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _failuresByParent =
            new(StringComparer.Ordinal);

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

        public void SetFailure(string parentFolderId, Exception exception) =>
            _failuresByParent[parentFolderId] = exception;

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
            if (_failuresByParent.TryGetValue(
                    parentFolderId,
                    out Exception? exception))
            {
                throw exception;
            }
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

    private sealed class CancelAfterEnumerationCountList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly CancellationTokenSource _cancellation;
        private readonly int _cancelAfterEnumerationCount;
        private int _enumerationCount;

        public CancelAfterEnumerationCountList(
            IReadOnlyList<T> items,
            CancellationTokenSource cancellation,
            int cancelAfterEnumerationCount)
        {
            _items = items;
            _cancellation = cancellation;
            _cancelAfterEnumerationCount = cancelAfterEnumerationCount;
        }

        public int Count => _items.Count;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            int enumeration = Interlocked.Increment(ref _enumerationCount);
            foreach (T item in _items)
                yield return item;

            if (enumeration == _cancelAfterEnumerationCount)
                _cancellation.Cancel();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
