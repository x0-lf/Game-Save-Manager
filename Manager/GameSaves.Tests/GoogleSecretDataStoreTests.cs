using GameSaves.Core.Secrets;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2.Responses;
using System.Text;

namespace GameSaves.Tests;

public sealed class GoogleSecretDataStoreTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");

    [Fact]
    public async Task TokenPayload_RoundTripsThroughExactProfileSecretIdentity()
    {
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        TokenResponse token = CreateToken("access-marker", "refresh-marker");

        await store.StoreAsync(ProfileId.ToString("D"), token);
        TokenResponse? restored =
            await store.GetAsync<TokenResponse>(ProfileId.ToString("D"));

        Assert.NotNull(restored);
        Assert.Equal(token.AccessToken, restored!.AccessToken);
        Assert.Equal(token.RefreshToken, restored.RefreshToken);
        Assert.Equal(token.Scope, restored.Scope);
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData)));
    }

    [Fact]
    public async Task TwoProfiles_NeverShareTokenPayloads()
    {
        var secrets = new InMemorySecretStore();
        Guid otherId = Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd");
        var first = new GoogleSecretDataStore(ProfileId, secrets);
        var second = new GoogleSecretDataStore(otherId, secrets);

        await first.StoreAsync(ProfileId.ToString("D"), CreateToken("first", "first-refresh"));
        await second.StoreAsync(otherId.ToString("D"), CreateToken("second", "second-refresh"));

        Assert.Equal("first", (await first.GetAsync<TokenResponse>(ProfileId.ToString("D")))!.AccessToken);
        Assert.Equal("second", (await second.GetAsync<TokenResponse>(otherId.ToString("D")))!.AccessToken);
    }

    [Fact]
    public async Task ClearAndDelete_RemoveOnlyThisProfilesOAuthToken()
    {
        var secrets = new InMemorySecretStore();
        Guid otherId = Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd");
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        var other = new GoogleSecretDataStore(otherId, secrets);
        await store.StoreAsync(ProfileId.ToString("D"), CreateToken("first", "first-refresh"));
        await other.StoreAsync(otherId.ToString("D"), CreateToken("second", "second-refresh"));
        await secrets.StoreAsync(
            new SecretKey(ProfileId, SecretNames.SftpPassword),
            Encoding.UTF8.GetBytes("sftp-session-marker"));

        await store.ClearAsync();

        Assert.Null(await store.GetAsync<TokenResponse>(ProfileId.ToString("D")));
        Assert.NotNull(await other.GetAsync<TokenResponse>(otherId.ToString("D")));
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.SftpPassword)));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheExactProfilesOAuthToken()
    {
        var secrets = new InMemorySecretStore();
        Guid otherId = Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd");
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        var other = new GoogleSecretDataStore(otherId, secrets);
        string key = ProfileId.ToString("D");
        await store.StoreAsync(key, CreateToken("first", "first-refresh"));
        await other.StoreAsync(
            otherId.ToString("D"),
            CreateToken("second", "second-refresh"));
        await secrets.StoreAsync(
            new SecretKey(ProfileId, SecretNames.WebDavPassword),
            Encoding.UTF8.GetBytes("webdav-marker"));

        await store.DeleteAsync<TokenResponse>(key);

        Assert.Null(await store.GetAsync<TokenResponse>(key));
        Assert.NotNull(await other.GetAsync<TokenResponse>(otherId.ToString("D")));
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.WebDavPassword)));
    }

    [Fact]
    public async Task MissingCorruptedAndUnsupportedPayloads_AreHandledSafely()
    {
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        string key = ProfileId.ToString("D");

        Assert.Null(await store.GetAsync<TokenResponse>(key));

        SecretKey secretKey = new(ProfileId, SecretNames.OAuthTokenData);
        secrets.MarkCorrupted(secretKey);
        GoogleSecretDataStoreException corrupted = await Assert.ThrowsAsync<GoogleSecretDataStoreException>(
            () => store.GetAsync<TokenResponse>(key));
        Assert.Equal(GoogleSecretDataStoreFailure.Corrupted, corrupted.Failure);

        await secrets.StoreAsync(secretKey, Encoding.UTF8.GetBytes("{\"version\":99}"));
        await Assert.ThrowsAsync<GoogleSecretDataStoreException>(
            () => store.GetAsync<TokenResponse>(key));
    }

    [Fact]
    public async Task AdapterRejectsArbitraryTypesAndWrongUserKeys()
    {
        var store = new GoogleSecretDataStore(ProfileId, new InMemorySecretStore());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => store.StoreAsync(ProfileId.ToString("D"), "not-a-token"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync("user", CreateToken("a", "r")));
    }

    [Fact]
    public async Task ResultAndExceptionFormatting_NeverRevealTokenValues()
    {
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        await store.StoreAsync(
            ProfileId.ToString("D"),
            CreateToken("unique-access-secret", "unique-refresh-secret"));
        var exception = new GoogleSecretDataStoreException(GoogleSecretDataStoreFailure.Corrupted);

        Assert.DoesNotContain("unique-access-secret", exception.ToString());
        Assert.DoesNotContain("unique-refresh-secret", exception.ToString());
    }

    private static TokenResponse CreateToken(string access, string refresh) => new()
    {
        AccessToken = access,
        RefreshToken = refresh,
        TokenType = "Bearer",
        Scope = GameSaves.Core.Sync.GoogleDriveAuthorizationScopes.DriveFile,
        ExpiresInSeconds = 3600,
        IssuedUtc = DateTime.SpecifyKind(new DateTime(2026, 7, 22, 10, 0, 0), DateTimeKind.Utc)
    };
}
