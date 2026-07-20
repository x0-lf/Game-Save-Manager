using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;
using System.Text;

namespace GameSaves.Tests;

public sealed class SecretStoreAbstractionTests
{
    [Fact]
    public async Task StoreAndRead_RoundTripsArbitraryPasswordAndStructuredBytes()
    {
        var store = new InMemorySecretStore();
        Guid owner = Guid.NewGuid();
        var binaryKey = new SecretKey(owner, SecretNames.OAuthTokenData);
        var passwordKey = new SecretKey(owner, SecretNames.WebDavPassword);
        byte[] binary = { 0, 1, 2, 127, 128, 255 };
        byte[] password = Encoding.UTF8.GetBytes("unique-password-value");
        byte[] json = Encoding.UTF8.GetBytes(
            """{"access_token":"value","refresh_token":"other"}""");

        Assert.True((await store.StoreAsync(binaryKey, binary)).Succeeded);
        Assert.Equal(binary, (await store.ReadAsync(binaryKey)).Value);

        Assert.True((await store.StoreAsync(passwordKey, password)).Succeeded);
        Assert.Equal(password, (await store.ReadAsync(passwordKey)).Value);

        await store.StoreAsync(binaryKey, json);
        Assert.Equal(json, (await store.ReadAsync(binaryKey)).Value);
    }

    [Fact]
    public async Task MissingAndUnavailableSecrets_ReturnExplicitStatuses()
    {
        var store = new InMemorySecretStore();
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);

        SecretReadResult missing = await store.ReadAsync(key);
        Assert.Equal(SecretReadStatus.NotFound, missing.Status);
        Assert.False(await store.ExistsAsync(key));

        store.SimulateUnavailable = true;
        SecretReadResult unavailable = await store.ReadAsync(key);
        Assert.Equal(SecretReadStatus.Unavailable, unavailable.Status);
        Assert.Equal("SecretStoreUnavailable", unavailable.ErrorCode);
    }

    [Fact]
    public async Task DeleteOneSecret_LeavesOtherSecretsIntact()
    {
        var store = new InMemorySecretStore();
        Guid owner = Guid.NewGuid();
        var first = new SecretKey(owner, SecretNames.SftpPassword);
        var second = new SecretKey(owner, SecretNames.SftpPrivateKeyPassphrase);
        await store.StoreAsync(first, new byte[] { 1 });
        await store.StoreAsync(second, new byte[] { 2 });

        await store.DeleteAsync(first);

        Assert.False(await store.ExistsAsync(first));
        Assert.True(await store.ExistsAsync(second));
    }

    [Fact]
    public async Task DeleteAll_RemovesOnlyOneOwnersSecrets()
    {
        var store = new InMemorySecretStore();
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        var firstPassword = new SecretKey(firstOwner, SecretNames.SftpPassword);
        var firstToken = new SecretKey(firstOwner, SecretNames.OAuthTokenData);
        var secondPassword = new SecretKey(secondOwner, SecretNames.SftpPassword);
        await store.StoreAsync(firstPassword, new byte[] { 1 });
        await store.StoreAsync(firstToken, new byte[] { 2 });
        await store.StoreAsync(secondPassword, new byte[] { 3 });

        SecretOperationResult result =
            await store.DeleteAllForOwnerAsync(firstOwner);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AffectedCount);
        Assert.False(await store.ExistsAsync(firstPassword));
        Assert.False(await store.ExistsAsync(firstToken));
        Assert.True(await store.ExistsAsync(secondPassword));
    }

    [Fact]
    public async Task ReadBuffers_CannotMutateStoredContents()
    {
        var store = new InMemorySecretStore();
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);
        byte[] original = { 10, 20, 30 };
        await store.StoreAsync(key, original);
        original[0] = 99;

        byte[] firstRead = (await store.ReadAsync(key)).Value!;
        firstRead[1] = 88;
        byte[] secondRead = (await store.ReadAsync(key)).Value!;

        Assert.Equal(new byte[] { 10, 20, 30 }, secondRead);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Uppercase")]
    [InlineData("contains_underscore")]
    [InlineData("contains/account")]
    [InlineData("account@example.test")]
    public void InvalidSecretKeys_AreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new SecretKey(Guid.NewGuid(), name));
        Assert.Throws<ArgumentException>(() =>
            new SecretKey(Guid.Empty, SecretNames.OAuthTokenData));
    }

    [Fact]
    public async Task ResultFormatting_NeverContainsSecretValues()
    {
        const string marker = "do-not-format-this-secret";
        SecretReadResult result =
            SecretReadResult.Found(Encoding.UTF8.GetBytes(marker));

        Assert.DoesNotContain(marker, result.ToString());
        Assert.DoesNotContain(marker, $"{result}");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProfileDeletion_DeletesOwnerSecretsBeforeProfile()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var store = new InMemorySecretStore();
        var service = new SyncRemoteProfileService(repository, store);
        SyncRemoteProfile profile = repository.Create(CreateProfile());
        var key = new SecretKey(profile.Id, SecretNames.OAuthTokenData);
        await store.StoreAsync(key, new byte[] { 42 });

        SyncRemoteProfileDeleteResult result =
            await service.DeleteAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(profile.Id, store.LastDeleteAllOwnerId);
        Assert.False(await store.ExistsAsync(key));
        Assert.Null(repository.GetById(profile.Id));
    }

    [Fact]
    public async Task FailedSecretCleanup_PreservesProfileAndReportsWarning()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var store = new InMemorySecretStore { SimulateUnavailable = true };
        var service = new SyncRemoteProfileService(repository, store);
        SyncRemoteProfile profile = repository.Create(CreateProfile());

        SyncRemoteProfileDeleteResult result =
            await service.DeleteAsync(profile.Id);

        Assert.False(result.Succeeded);
        Assert.False(result.ProfileDeleted);
        Assert.NotNull(result.CleanupWarning);
        Assert.NotNull(repository.GetById(profile.Id));
    }

    [Fact]
    public async Task Disconnect_RemovesAuthenticationAndPreservesProfile()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var store = new InMemorySecretStore();
        var service = new SyncRemoteProfileService(repository, store);
        SyncRemoteProfile profile = repository.Create(CreateProfile());
        var key = new SecretKey(profile.Id, SecretNames.OneDriveTokenData);
        await store.StoreAsync(key, new byte[] { 42 });

        Assert.True(await service.HasStoredAuthenticationAsync(profile.Id));
        SyncRemoteProfileAuthenticationResult result =
            await service.DisconnectAuthenticationAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.False(await store.ExistsAsync(key));
        Assert.NotNull(repository.GetById(profile.Id));
    }

    private static SyncRemoteProfile CreateProfile()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        return new SyncRemoteProfile(
            Guid.NewGuid(),
            "Test Profile",
            SyncProviderKind.LocalFolder,
            AccountDisplayName: null,
            RemoteRootDisplayName: @"D:\Backups",
            ProviderSettings: new LocalFolderSyncRemoteSettings(@"D:\Backups"),
            CreatedUtc: now,
            UpdatedUtc: now,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null);
    }
}
