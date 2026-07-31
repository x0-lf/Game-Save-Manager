using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveValidationCoordinatorTests
{
    [Fact]
    public void Begin_TracksOperationUntilItIsDisposed()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        Guid profileId = Guid.NewGuid();

        using (GoogleDriveValidationOperation operation =
            coordinator.Begin(profileId))
        {
            Assert.True(coordinator.IsActive(profileId));
            Assert.True(operation.IsCurrent);
            Assert.False(operation.CancellationToken.IsCancellationRequested);
        }

        Assert.False(coordinator.IsActive(profileId));
    }

    [Fact]
    public void NewGeneration_CancelsAndSupersedesPreviousGeneration()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        Guid profileId = Guid.NewGuid();
        using GoogleDriveValidationOperation first = coordinator.Begin(profileId);

        using GoogleDriveValidationOperation second = coordinator.Begin(profileId);

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
        Assert.True(second.Generation > first.Generation);
    }

    [Fact]
    public void ExplicitCancellation_InvalidatesTheCurrentGeneration()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        Guid profileId = Guid.NewGuid();
        using GoogleDriveValidationOperation operation = coordinator.Begin(profileId);

        coordinator.Cancel(profileId);

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.False(coordinator.IsActive(profileId));
    }

    [Fact]
    public void CallerCancellation_RemainsCurrentUntilOperationFinishes()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        using var cancellation = new CancellationTokenSource();
        Guid profileId = Guid.NewGuid();
        using GoogleDriveValidationOperation operation = coordinator.Begin(
            profileId,
            cancellation.Token);

        cancellation.Cancel();

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.True(operation.IsCurrent);
        Assert.True(coordinator.IsActive(profileId));
    }

    [Fact]
    public void DifferentProfiles_AreCoordinatedIndependently()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        Guid firstProfileId = Guid.NewGuid();
        Guid secondProfileId = Guid.NewGuid();
        using GoogleDriveValidationOperation first = coordinator.Begin(firstProfileId);
        using GoogleDriveValidationOperation second = coordinator.Begin(secondProfileId);

        coordinator.Cancel(firstProfileId);

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.True(second.IsCurrent);
        Assert.True(coordinator.IsActive(secondProfileId));
    }

    [Fact]
    public async Task ConcurrentBegins_LeaveExactlyOneCurrentGeneration()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        Guid profileId = Guid.NewGuid();
        var operations = new GoogleDriveValidationOperation[16];

        await Task.WhenAll(Enumerable.Range(0, operations.Length).Select(index =>
            Task.Run(() => operations[index] = coordinator.Begin(profileId))));

        try
        {
            Assert.Single(operations, operation => operation.IsCurrent);
            Assert.Equal(
                operations.Length - 1,
                operations.Count(operation =>
                    operation.CancellationToken.IsCancellationRequested));
        }
        finally
        {
            foreach (GoogleDriveValidationOperation operation in operations)
                operation.Dispose();
        }

        Assert.False(coordinator.IsActive(profileId));
    }

    [Fact]
    public void SafeFormatting_DoesNotExposeProfileIdentity()
    {
        var coordinator = new GoogleDriveValidationCoordinator();
        Guid profileId = Guid.NewGuid();
        using GoogleDriveValidationOperation operation = coordinator.Begin(profileId);

        string diagnostic = operation.ToString();

        Assert.DoesNotContain(profileId.ToString("D"), diagnostic,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generation", diagnostic, StringComparison.Ordinal);
        Assert.Contains("IsCurrent", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileDisconnectAndDeletion_CancelPendingValidation()
    {
        Guid profileId = Guid.NewGuid();
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(new SyncRemoteProfile(
            profileId,
            "Google Drive profile",
            SyncProviderKind.GoogleDrive,
            null,
            "GameSave Manager Backups",
            new GoogleDriveSyncRemoteSettings(
                null,
                GoogleDriveAuthorizationScopes.DriveFile),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            "root-id"));
        var coordinator = new GoogleDriveValidationCoordinator();
        var service = new SyncRemoteProfileService(
            repository,
            new InMemorySecretStore(),
            coordinator);
        using GoogleDriveValidationOperation beforeDisconnect =
            coordinator.Begin(profileId);

        await service.DisconnectAuthenticationAsync(profileId);

        Assert.True(beforeDisconnect.CancellationToken.IsCancellationRequested);
        Assert.False(beforeDisconnect.IsCurrent);

        using GoogleDriveValidationOperation beforeDelete =
            coordinator.Begin(profileId);
        await service.DeleteAsync(profileId);

        Assert.True(beforeDelete.CancellationToken.IsCancellationRequested);
        Assert.False(beforeDelete.IsCurrent);
        Assert.Null(repository.GetById(profileId));
    }

    [Fact]
    public void DependencyInjection_SharesOneCoordinatorWithoutStartingValidation()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        IGoogleDriveValidationCoordinator coordinator =
            provider.GetRequiredService<IGoogleDriveValidationCoordinator>();
        IGoogleDriveRemoteValidationService validation =
            provider.GetRequiredService<IGoogleDriveRemoteValidationService>();
        IGoogleDriveOAuthService oauth =
            provider.GetRequiredService<IGoogleDriveOAuthService>();
        ISyncRemoteProfileService profiles =
            provider.GetRequiredService<ISyncRemoteProfileService>();

        Assert.Same(
            coordinator,
            ReadCoordinator(validation));
        Assert.Same(
            coordinator,
            ReadCoordinator(oauth));
        Assert.Same(
            coordinator,
            ReadCoordinator(profiles));
        Assert.False(coordinator.IsActive(Guid.NewGuid()));
    }

    private static object? ReadCoordinator(object instance) =>
        instance.GetType()
            .GetField(
                "_validationCoordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);
}
