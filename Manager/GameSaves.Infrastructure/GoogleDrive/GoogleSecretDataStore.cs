using GameSaves.Core.Secrets;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleSecretDataStoreFailure
    {
        Unavailable,
        Corrupted,
        Failed
    }

    internal sealed class GoogleSecretDataStoreException : Exception
    {
        public GoogleSecretDataStoreException(GoogleSecretDataStoreFailure failure)
            : base(failure switch
            {
                GoogleSecretDataStoreFailure.Unavailable =>
                    "Protected Google authentication storage is unavailable.",
                GoogleSecretDataStoreFailure.Corrupted =>
                    "Stored Google authentication is unreadable.",
                _ => "Protected Google authentication storage failed."
            })
        {
            Failure = failure;
        }

        public GoogleSecretDataStoreFailure Failure { get; }
    }

    /// <summary>
    /// Profile-scoped Google token store. Only TokenResponse is allowlisted;
    /// serialized token bytes are persisted exclusively through ISecretStore.
    /// </summary>
    internal sealed class GoogleSecretDataStore : IDataStore
    {
        internal const int CurrentPayloadVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly Guid _profileId;
        private readonly string _userKey;
        private readonly ISecretStore _secretStore;
        private readonly SecretKey _secretKey;

        public GoogleSecretDataStore(Guid profileId, ISecretStore secretStore)
        {
            if (profileId == Guid.Empty)
                throw new ArgumentException("A saved remote profile is required.", nameof(profileId));

            _profileId = profileId;
            _userKey = profileId.ToString("D");
            _secretStore = secretStore;
            _secretKey = new SecretKey(profileId, SecretNames.OAuthTokenData);
        }

        public Task ClearAsync() => DeleteSecretAsync();

        public Task DeleteAsync<T>(string key)
        {
            ValidateTypeAndKey<T>(key);
            return DeleteSecretAsync();
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            ValidateTypeAndKey<T>(key);
            SecretReadResult result = await _secretStore.ReadAsync(_secretKey);

            if (result.Status == SecretReadStatus.NotFound)
                return default;

            if (result.Status != SecretReadStatus.Found || result.Value is null)
            {
                throw new GoogleSecretDataStoreException(result.Status switch
                {
                    SecretReadStatus.Unavailable => GoogleSecretDataStoreFailure.Unavailable,
                    SecretReadStatus.Corrupted => GoogleSecretDataStoreFailure.Corrupted,
                    _ => GoogleSecretDataStoreFailure.Failed
                });
            }

            byte[] bytes = result.Value;

            try
            {
                TokenPayload? payload = JsonSerializer.Deserialize<TokenPayload>(bytes, JsonOptions);

                if (payload is null || payload.Version != CurrentPayloadVersion)
                    throw new GoogleSecretDataStoreException(GoogleSecretDataStoreFailure.Corrupted);

                object token = new TokenResponse
                {
                    AccessToken = payload.AccessToken,
                    RefreshToken = payload.RefreshToken,
                    TokenType = payload.TokenType,
                    Scope = payload.Scope,
                    ExpiresInSeconds = payload.ExpiresInSeconds,
                    IssuedUtc = payload.IssuedUtc
                };

                return (T)token;
            }
            catch (GoogleSecretDataStoreException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new GoogleSecretDataStoreException(GoogleSecretDataStoreFailure.Corrupted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public async Task StoreAsync<T>(string key, T value)
        {
            ValidateTypeAndKey<T>(key);

            if (value is not TokenResponse token)
                throw new NotSupportedException("Only Google OAuth token data can be stored.");

            var payload = new TokenPayload(
                CurrentPayloadVersion,
                token.AccessToken,
                token.RefreshToken,
                token.TokenType,
                token.Scope,
                token.ExpiresInSeconds,
                token.IssuedUtc);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

            try
            {
                SecretOperationResult result = await _secretStore.StoreAsync(_secretKey, bytes);

                if (!result.Succeeded)
                {
                    throw new GoogleSecretDataStoreException(
                        result.Status == SecretOperationStatus.Unavailable
                            ? GoogleSecretDataStoreFailure.Unavailable
                            : GoogleSecretDataStoreFailure.Failed);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private async Task DeleteSecretAsync()
        {
            SecretOperationResult result = await _secretStore.DeleteAsync(_secretKey);

            if (!result.Succeeded)
            {
                throw new GoogleSecretDataStoreException(
                    result.Status == SecretOperationStatus.Unavailable
                        ? GoogleSecretDataStoreFailure.Unavailable
                        : GoogleSecretDataStoreFailure.Failed);
            }
        }

        private void ValidateTypeAndKey<T>(string key)
        {
            if (typeof(T) != typeof(TokenResponse))
                throw new NotSupportedException("Only Google OAuth token data can be stored.");

            if (!string.Equals(key, _userKey, StringComparison.Ordinal))
                throw new ArgumentException("The Google token-store key is invalid.", nameof(key));
        }

        private sealed record TokenPayload(
            [property: JsonPropertyName("version")] int Version,
            [property: JsonPropertyName("accessToken")] string? AccessToken,
            [property: JsonPropertyName("refreshToken")] string? RefreshToken,
            [property: JsonPropertyName("tokenType")] string? TokenType,
            [property: JsonPropertyName("scope")] string? Scope,
            [property: JsonPropertyName("expiresInSeconds")] long? ExpiresInSeconds,
            [property: JsonPropertyName("issuedUtc")] DateTime IssuedUtc);
    }

    internal interface IGoogleSecretDataStoreFactory
    {
        GoogleSecretDataStore Create(Guid profileId);
    }

    internal sealed class GoogleSecretDataStoreFactory : IGoogleSecretDataStoreFactory
    {
        private readonly ISecretStore _secretStore;

        public GoogleSecretDataStoreFactory(ISecretStore secretStore) =>
            _secretStore = secretStore;

        public GoogleSecretDataStore Create(Guid profileId) =>
            new(profileId, _secretStore);
    }
}
