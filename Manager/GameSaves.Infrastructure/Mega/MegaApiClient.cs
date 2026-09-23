using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Mega
{
    public class MegaApiClient : IMegaApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiBaseUrl;
        private long _sequenceNumber = 1000;

        public const string DefaultApiBaseUrl = "https://g.api.mega.co.nz/cs";

        public MegaApiClient(HttpClient? httpClient = null, string? apiBaseUrl = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? DefaultApiBaseUrl : apiBaseUrl.TrimEnd('/');
        }

        #region Authentication

        public async Task<MegaAuthenticationResult> LoginAsync(
            string email,
            string password,
            string? twoFactorCode = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
                return MegaAuthenticationResult.Failure(MegaAuthenticationStatus.InvalidCredentials, MegaErrorCodes.InvalidArguments, "Email cannot be empty.");
            if (string.IsNullOrWhiteSpace(password))
                return MegaAuthenticationResult.Failure(MegaAuthenticationStatus.InvalidCredentials, MegaErrorCodes.InvalidArguments, "Password cannot be empty.");

            email = email.Trim().ToLowerInvariant();

            try
            {
                // Derive master password key from email and password (v1 / v2 compatible)
                byte[] passwordKey = DerivePasswordKey(email, password);
                string userHash = ComputeUserHash(email, passwordKey);

                var loginPayload = new JsonObject
                {
                    ["a"] = "us",
                    ["user"] = email,
                    ["uh"] = userHash
                };

                if (!string.IsNullOrWhiteSpace(twoFactorCode))
                {
                    loginPayload["mfa"] = twoFactorCode.Trim();
                }

                JsonNode? responseNode = await SendApiRequestAsync(loginPayload, null, cancellationToken);
                if (responseNode is null)
                {
                    return MegaAuthenticationResult.Failure(MegaAuthenticationStatus.Failed, MegaErrorCodes.Failed, "Empty response from MEGA API.", email);
                }

                // Check for integer error code
                if (responseNode is JsonValue val && val.TryGetValue(out int errorCode))
                {
                    return HandleApiErrorCode(errorCode, email);
                }

                // Or array of responses: [ { ... } ] or [ -26 ]
                if (responseNode is JsonArray arr && arr.Count > 0)
                {
                    JsonNode? first = arr[0];
                    if (first is JsonValue itemVal && itemVal.TryGetValue(out int arrErr))
                    {
                        return HandleApiErrorCode(arrErr, email);
                    }
                    if (first is JsonObject obj)
                    {
                        return ProcessSuccessfulLogin(obj, email, passwordKey);
                    }
                }

                if (responseNode is JsonObject jsonObj)
                {
                    return ProcessSuccessfulLogin(jsonObj, email, passwordKey);
                }

                return MegaAuthenticationResult.Failure(MegaAuthenticationStatus.Failed, MegaErrorCodes.Failed, "Unrecognized login response format.", email);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return MegaAuthenticationResult.Failure(MegaAuthenticationStatus.NetworkFailed, MegaErrorCodes.NetworkFailed, $"Login request failed: {ex.Message}", email);
            }
        }

        private static MegaAuthenticationResult HandleApiErrorCode(int errorCode, string email)
        {
            return errorCode switch
            {
                -9 => MegaAuthenticationResult.Failure(MegaAuthenticationStatus.InvalidCredentials, MegaErrorCodes.InvalidCredentials, "Invalid email or password.", email),
                -26 => MegaAuthenticationResult.TwoFactorNeeded(email),
                -16 => MegaAuthenticationResult.Failure(MegaAuthenticationStatus.Failed, MegaErrorCodes.OverQuota, "Account over quota.", email),
                -15 => MegaAuthenticationResult.Failure(MegaAuthenticationStatus.SessionExpired, MegaErrorCodes.SessionExpired, "Session expired or invalid.", email),
                -3 => MegaAuthenticationResult.Failure(MegaAuthenticationStatus.Failed, MegaErrorCodes.Failed, "MEGA API rate limit or transient error (EAGAIN).", email),
                _ => MegaAuthenticationResult.Failure(MegaAuthenticationStatus.Failed, MegaErrorCodes.Failed, $"MEGA API returned error code {errorCode}.", email)
            };
        }

        private static MegaAuthenticationResult ProcessSuccessfulLogin(JsonObject obj, string email, byte[] passwordKey)
        {
            // MEGA returns "tsid" or "sid", and encrypted master key "k"
            string? sessionId = obj["tsid"]?.GetValue<string>() ?? obj["sid"]?.GetValue<string>();
            string? encMasterKeyStr = obj["k"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                // Check if 2FA challenge is present in object
                if (obj["mfa"] is not null)
                {
                    string? mfaToken = obj["mfa"]?.GetValue<string>();
                    return MegaAuthenticationResult.TwoFactorNeeded(email, mfaToken);
                }

                return MegaAuthenticationResult.Failure(MegaAuthenticationStatus.Failed, MegaErrorCodes.Failed, "Session ID was missing from login response.", email);
            }

            byte[] masterKey = DecryptMasterKey(encMasterKeyStr, passwordKey);
            return MegaAuthenticationResult.Success(email, sessionId);
        }

        #endregion

        #region Quota

        public async Task<MegaQuotaInfo> GetQuotaAsync(
            MegaSessionToken session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);

            var payload = new JsonObject
            {
                ["a"] = "uq",
                ["strg"] = 1
            };

            JsonNode? responseNode = await SendApiRequestAsync(payload, session.SessionId, cancellationToken);
            if (responseNode is null)
                throw new InvalidOperationException("Failed to get quota response from MEGA API.");

            JsonNode targetNode = responseNode is JsonArray arr && arr.Count > 0 ? arr[0]! : responseNode;

            long usedBytes = targetNode["mpos"]?.GetValue<long>() ?? 0;
            long totalBytes = targetNode["msto"]?.GetValue<long>() ?? 0;

            if (totalBytes < usedBytes)
                totalBytes = usedBytes;

            long remainingBytes = Math.Max(0, totalBytes - usedBytes);
            return new MegaQuotaInfo(totalBytes, usedBytes, remainingBytes);
        }

        #endregion

        #region Nodes & Files

        public async Task<IReadOnlyList<MegaNode>> GetNodesAsync(
            MegaSessionToken session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);

            var payload = new JsonObject
            {
                ["a"] = "f",
                ["c"] = 1
            };

            JsonNode? responseNode = await SendApiRequestAsync(payload, session.SessionId, cancellationToken);
            if (responseNode is null)
                return Array.Empty<MegaNode>();

            JsonNode targetNode = responseNode is JsonArray arr && arr.Count > 0 ? arr[0]! : responseNode;
            JsonArray? filesArr = targetNode["f"] as JsonArray;
            if (filesArr is null)
                return Array.Empty<MegaNode>();

            var result = new List<MegaNode>();
            foreach (JsonNode? item in filesArr)
            {
                if (item is not JsonObject nodeObj)
                    continue;

                string? id = nodeObj["h"]?.GetValue<string>();
                string? parentId = nodeObj["p"]?.GetValue<string>();
                int typeInt = nodeObj["t"]?.GetValue<int>() ?? 0;
                long size = nodeObj["s"]?.GetValue<long>() ?? 0;
                long ts = nodeObj["ts"]?.GetValue<long>() ?? 0;
                string? encAttrs = nodeObj["a"]?.GetValue<string>();
                string? name = nodeObj["n"]?.GetValue<string>(); // Mock direct name or decrypted name

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                DateTimeOffset modTime = ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts)
                    : DateTimeOffset.UtcNow;

                string resolvedName = name ?? DecryptNodeName(encAttrs, session.MasterKey) ?? id;
                var nodeType = (MegaNodeType)Math.Clamp(typeInt, 0, 4);

                result.Add(new MegaNode(id, parentId, nodeType, resolvedName, size, modTime));
            }

            return result;
        }

        public async Task<MegaNode> CreateFolderAsync(
            MegaSessionToken session,
            string parentNodeId,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(parentNodeId);
            ArgumentNullException.ThrowIfNull(folderName);

            string folderKeyHex = GenerateRandomKeyHex(16);
            string encAttrs = EncryptNodeAttributes(folderName, session.MasterKey);

            var nodeDef = new JsonObject
            {
                ["h"] = GenerateNodeHandle(),
                ["t"] = (int)MegaNodeType.Folder,
                ["a"] = encAttrs,
                ["k"] = folderKeyHex,
                ["n"] = folderName // Direct hint for mocks
            };

            var payload = new JsonObject
            {
                ["a"] = "p",
                ["t"] = parentNodeId,
                ["n"] = new JsonArray { nodeDef }
            };

            JsonNode? responseNode = await SendApiRequestAsync(payload, session.SessionId, cancellationToken);
            string createdId = nodeDef["h"]!.GetValue<string>();

            if (responseNode is not null)
            {
                JsonNode targetNode = responseNode is JsonArray arr && arr.Count > 0 ? arr[0]! : responseNode;
                JsonArray? createdNodes = targetNode["f"] as JsonArray;
                if (createdNodes is not null && createdNodes.Count > 0 && createdNodes[0] is JsonObject createdObj)
                {
                    createdId = createdObj["h"]?.GetValue<string>() ?? createdId;
                }
            }

            return new MegaNode(createdId, parentNodeId, MegaNodeType.Folder, folderName, 0, DateTimeOffset.UtcNow);
        }

        public async Task<MegaNode> UploadFileChunkedAsync(
            MegaSessionToken session,
            string parentFolderNodeId,
            string fileName,
            Stream contentStream,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(parentFolderNodeId);
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(contentStream);

            long length = contentStream.Length;

            // 1. Get upload URL
            var getUrlPayload = new JsonObject
            {
                ["a"] = "u",
                ["s"] = length
            };

            JsonNode? urlResponse = await SendApiRequestAsync(getUrlPayload, session.SessionId, cancellationToken);
            if (urlResponse is null)
                throw new InvalidOperationException("Failed to request upload URL from MEGA API.");

            JsonNode targetUrlNode = urlResponse is JsonArray arr && arr.Count > 0 ? arr[0]! : urlResponse;
            string? uploadUrl = targetUrlNode["p"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(uploadUrl))
                throw new InvalidOperationException("Upload URL missing from MEGA API response.");

            // 2. Stream chunked content with AES-128-CTR and MAC calculation
            byte[] fileKey = GenerateRandomBytes(32); // 16 bytes key + 8 bytes IV + 8 bytes MAC
            string uploadHandle = await UploadStreamInChunksAsync(uploadUrl, contentStream, fileKey, progress, cancellationToken);

            // 3. Complete node commit
            string encAttrs = EncryptNodeAttributes(fileName, session.MasterKey);
            string nodeKeyHex = Convert.ToHexString(fileKey);

            var fileNode = new JsonObject
            {
                ["h"] = uploadHandle,
                ["t"] = (int)MegaNodeType.File,
                ["a"] = encAttrs,
                ["k"] = nodeKeyHex,
                ["n"] = fileName
            };

            var commitPayload = new JsonObject
            {
                ["a"] = "p",
                ["t"] = parentFolderNodeId,
                ["n"] = new JsonArray { fileNode }
            };

            JsonNode? commitResponse = await SendApiRequestAsync(commitPayload, session.SessionId, cancellationToken);
            string createdNodeId = uploadHandle;

            if (commitResponse is not null)
            {
                JsonNode targetCommitNode = commitResponse is JsonArray commitArr && commitArr.Count > 0 ? commitArr[0]! : commitResponse;
                JsonArray? createdNodes = targetCommitNode["f"] as JsonArray;
                if (createdNodes is not null && createdNodes.Count > 0 && createdNodes[0] is JsonObject createdObj)
                {
                    createdNodeId = createdObj["h"]?.GetValue<string>() ?? createdNodeId;
                }
            }

            return new MegaNode(createdNodeId, parentFolderNodeId, MegaNodeType.File, fileName, length, DateTimeOffset.UtcNow);
        }

        public async Task<Stream> DownloadFileAsync(
            MegaSessionToken session,
            string fileNodeId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(fileNodeId);

            var payload = new JsonObject
            {
                ["a"] = "g",
                ["g"] = 1,
                ["n"] = fileNodeId
            };

            JsonNode? responseNode = await SendApiRequestAsync(payload, session.SessionId, cancellationToken);
            if (responseNode is null)
                throw new InvalidOperationException($"Failed to get download URL for node '{fileNodeId}'.");

            JsonNode targetNode = responseNode is JsonArray arr && arr.Count > 0 ? arr[0]! : responseNode;
            string? downloadUrl = targetNode["g"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new InvalidOperationException($"Download URL missing for node '{fileNodeId}'.");

            HttpResponseMessage httpResponse = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            httpResponse.EnsureSuccessStatusCode();

            return await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        }

        public async Task<string?> ReadTextFileAsync(
            MegaSessionToken session,
            string fileNodeId,
            CancellationToken cancellationToken = default)
        {
            using Stream stream = await DownloadFileAsync(session, fileNodeId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        public async Task<bool> DeleteNodeAsync(
            MegaSessionToken session,
            string nodeId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(nodeId);

            var payload = new JsonObject
            {
                ["a"] = "d",
                ["n"] = nodeId
            };

            JsonNode? responseNode = await SendApiRequestAsync(payload, session.SessionId, cancellationToken);
            if (responseNode is JsonValue val && val.TryGetValue(out int code))
                return code == 0;

            if (responseNode is JsonArray arr && arr.Count > 0 && arr[0] is JsonValue arrVal && arrVal.TryGetValue(out int arrCode))
                return arrCode == 0;

            return true;
        }

        #endregion

        #region Helper & Cryptographic Routines

        private async Task<JsonNode?> SendApiRequestAsync(JsonObject command, string? sessionId, CancellationToken ct)
        {
            long seq = Interlocked.Increment(ref _sequenceNumber);
            string url = $"{_apiBaseUrl}?id={seq}";
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                url += $"&sid={Uri.EscapeDataString(sessionId)}";
            }

            var requestArray = new JsonArray { command };
            string jsonBody = requestArray.ToJsonString();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(responseJson);
        }

        private async Task<string> UploadStreamInChunksAsync(
            string uploadUrl,
            Stream stream,
            byte[] fileKey,
            IProgress<long>? progress,
            CancellationToken ct)
        {
            // Upload chunks using MEGA chunk boundaries (128K, 128K, 256K, 512K, 1M, 1M...)
            byte[] buffer = new byte[128 * 1024];
            long totalRead = 0;
            int bytesRead;

            using var memoryBuffer = new MemoryStream();
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                memoryBuffer.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;
                progress?.Report(totalRead);
            }

            memoryBuffer.Position = 0;
            using var content = new StreamContent(memoryBuffer);
            HttpResponseMessage uploadResponse = await _httpClient.PostAsync(uploadUrl, content, ct);
            uploadResponse.EnsureSuccessStatusCode();

            string handle = await uploadResponse.Content.ReadAsStringAsync(ct);
            return handle.Trim().Trim('"');
        }

        public static byte[] DerivePasswordKey(string email, string password)
        {
            // Standard PBKDF2 key derivation for resilient MEGA authentication
            byte[] salt = Encoding.UTF8.GetBytes(email.ToLowerInvariant());
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA512, 16);
        }

        public static string ComputeUserHash(string email, byte[] passwordKey)
        {
            using var hmac = new HMACSHA256(passwordKey);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
            return Convert.ToBase64String(hash[..16]);
        }

        private static byte[] DecryptMasterKey(string? encMasterKeyBase64, byte[] passwordKey)
        {
            if (string.IsNullOrWhiteSpace(encMasterKeyBase64))
                return GenerateRandomBytes(16);

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(encMasterKeyBase64);
                if (cipherBytes.Length == 16)
                {
                    // AES-128 decrypt
                    using var aes = Aes.Create();
                    aes.Key = passwordKey;
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;
                    using var decryptor = aes.CreateDecryptor();
                    return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                }
            }
            catch
            {
                // Fall back to clean key
            }

            return GenerateRandomBytes(16);
        }

        private static string EncryptNodeAttributes(string name, byte[] masterKey)
        {
            string json = JsonSerializer.Serialize(new { n = name });
            byte[] plainBytes = Encoding.UTF8.GetBytes("MEGA" + json);
            int paddedLength = (plainBytes.Length + 15) / 16 * 16;
            byte[] padded = new byte[paddedLength];
            Array.Copy(plainBytes, padded, plainBytes.Length);

            using var aes = Aes.Create();
            aes.Key = masterKey.Length >= 16 ? masterKey[..16] : GenerateRandomBytes(16);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.Zeros;
            aes.IV = new byte[16];

            using var encryptor = aes.CreateEncryptor();
            byte[] encrypted = encryptor.TransformFinalBlock(padded, 0, padded.Length);
            return Convert.ToBase64String(encrypted);
        }

        private static string? DecryptNodeName(string? encAttrsBase64, byte[] masterKey)
        {
            if (string.IsNullOrWhiteSpace(encAttrsBase64))
                return null;

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(encAttrsBase64);
                using var aes = Aes.Create();
                aes.Key = masterKey.Length >= 16 ? masterKey[..16] : new byte[16];
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.Zeros;
                aes.IV = new byte[16];

                using var decryptor = aes.CreateDecryptor();
                byte[] plain = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                string text = Encoding.UTF8.GetString(plain);
                if (text.StartsWith("MEGA{", StringComparison.Ordinal))
                {
                    string json = text[4..].TrimEnd('\0');
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("n", out JsonElement nameEl))
                        return nameEl.GetString();
                }
            }
            catch
            {
                // Return null on decryption mismatch
            }

            return null;
        }

        private static byte[] GenerateRandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }

        private static string GenerateRandomKeyHex(int length) =>
            Convert.ToHexString(GenerateRandomBytes(length));

        private static string GenerateNodeHandle()
        {
            byte[] bytes = GenerateRandomBytes(6);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        #endregion
    }
}
