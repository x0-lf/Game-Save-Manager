using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Mega
{
    public class MegaSessionService : IMegaSessionService
    {
        private readonly IMegaApiClient _apiClient;
        private readonly ISecretStore _secretStore;

        public MegaSessionService(IMegaApiClient apiClient, ISecretStore secretStore)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        }

        public async Task<MegaAuthenticationResult> AuthenticateAsync(
            Guid remoteProfileId,
            string email,
            string password,
            string? twoFactorCode = null,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException("Remote profile ID cannot be empty.", nameof(remoteProfileId));

            MegaAuthenticationResult result = await _apiClient.LoginAsync(email, password, twoFactorCode, cancellationToken);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.SessionId))
            {
                return result;
            }

            // Derive or generate session master key
            byte[] masterKey = MegaApiClient.DerivePasswordKey(email, password);
            var session = new MegaSessionToken(result.SessionId, masterKey, email, DateTimeOffset.UtcNow);

            // Persist session token safely in DPAPI secret store
            var dto = new MegaSessionDto
            {
                SchemaVersion = 1,
                SessionId = session.SessionId,
                MasterKeyBase64 = Convert.ToBase64String(session.MasterKey),
                UserEmail = session.UserEmail,
                CreatedUtc = session.CreatedUtc
            };

            string json = JsonSerializer.Serialize(dto);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            var secretKey = new SecretKey(remoteProfileId, SecretNames.MegaSessionData);
            SecretOperationResult storeResult = await _secretStore.StoreAsync(secretKey, jsonBytes, cancellationToken);
            if (!storeResult.Succeeded)
            {
                return MegaAuthenticationResult.Failure(
                    MegaAuthenticationStatus.StorageFailure,
                    MegaErrorCodes.SecretStoreUnavailable,
                    "Failed to store MEGA session token in protected secret store.",
                    email);
            }

            return result;
        }

        public async Task<MegaSessionToken?> GetSessionAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                return null;

            var secretKey = new SecretKey(remoteProfileId, SecretNames.MegaSessionData);
            SecretReadResult readResult = await _secretStore.ReadAsync(secretKey, cancellationToken);
            if (readResult.Status != SecretReadStatus.Found || readResult.Value is null)
                return null;

            try
            {
                string json = Encoding.UTF8.GetString(readResult.Value);
                MegaSessionDto? dto = JsonSerializer.Deserialize<MegaSessionDto>(json);
                if (dto is null || string.IsNullOrWhiteSpace(dto.SessionId) || string.IsNullOrWhiteSpace(dto.MasterKeyBase64))
                    return null;

                byte[] masterKey = Convert.FromBase64String(dto.MasterKeyBase64);
                return new MegaSessionToken(dto.SessionId, masterKey, dto.UserEmail ?? string.Empty, dto.CreatedUtc);
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> HasValidSessionAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                return false;

            var secretKey = new SecretKey(remoteProfileId, SecretNames.MegaSessionData);
            return await _secretStore.ExistsAsync(secretKey, cancellationToken);
        }

        public async Task<MegaDisconnectionResult> DisconnectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                return new MegaDisconnectionResult(MegaDisconnectionStatus.AlreadyDisconnected, false, "Profile ID was empty.");

            var secretKey = new SecretKey(remoteProfileId, SecretNames.MegaSessionData);
            bool exists = await _secretStore.ExistsAsync(secretKey, cancellationToken);
            if (!exists)
            {
                return new MegaDisconnectionResult(MegaDisconnectionStatus.AlreadyDisconnected, false, "No stored session found.");
            }

            SecretOperationResult deleteResult = await _secretStore.DeleteAsync(secretKey, cancellationToken);
            return new MegaDisconnectionResult(
                deleteResult.Succeeded ? MegaDisconnectionStatus.Disconnected : MegaDisconnectionStatus.Failed,
                deleteResult.Succeeded,
                deleteResult.Succeeded ? "Session removed from protected store." : "Failed to remove session from protected store.");
        }

        private sealed class MegaSessionDto
        {
            [JsonPropertyName("schema_version")]
            public int SchemaVersion { get; set; } = 1;

            [JsonPropertyName("session_id")]
            public string SessionId { get; set; } = string.Empty;

            [JsonPropertyName("master_key_base64")]
            public string MasterKeyBase64 { get; set; } = string.Empty;

            [JsonPropertyName("user_email")]
            public string? UserEmail { get; set; }

            [JsonPropertyName("created_utc")]
            public DateTimeOffset CreatedUtc { get; set; }
        }
    }
}
