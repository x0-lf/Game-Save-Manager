using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Mega;
using GameSaves.Infrastructure.Sync;
using Xunit;

namespace GameSaves.Tests
{
    public class MegaSpikeTests
    {
        private static readonly Guid TestProfileId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

        #region 1. MegaApiClient Unit Tests with Mock HttpMessageHandler

        [Fact]
        public async Task MegaApiClient_LoginAsync_SuccessfulResponse_ReturnsConnected()
        {
            var handler = new MockMegaHttpHandler((url, body) =>
            {
                // Successful login response
                var response = new JsonArray
                {
                    new JsonObject
                    {
                        ["tsid"] = "session_token_12345",
                        ["k"] = Convert.ToBase64String(new byte[16]) // 16 bytes master key
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var client = new MegaApiClient(httpClient);

            MegaAuthenticationResult result = await client.LoginAsync("user@example.com", "Password123!");

            Assert.True(result.Succeeded);
            Assert.Equal(MegaAuthenticationStatus.Connected, result.Status);
            Assert.Equal("user@example.com", result.UserEmail);
            Assert.Equal("session_token_12345", result.SessionId);
            Assert.False(result.RequiresTwoFactor);
        }

        [Fact]
        public async Task MegaApiClient_LoginAsync_ReturnsCodeMinus26_DetectsTwoFactorRequired()
        {
            var handler = new MockMegaHttpHandler((url, body) =>
            {
                // Error -26 means 2FA is required
                var response = new JsonArray { -26 };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var client = new MegaApiClient(httpClient);

            MegaAuthenticationResult result = await client.LoginAsync("user@example.com", "Password123!");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresTwoFactor);
            Assert.Equal(MegaAuthenticationStatus.TwoFactorRequired, result.Status);
            Assert.NotNull(result.TwoFactorChallenge);
            Assert.True(result.TwoFactorChallenge.IsRequired);
        }

        [Fact]
        public async Task MegaApiClient_LoginAsync_WithTwoFactorPin_SendsMfaInPayload()
        {
            string? capturedBody = null;
            var handler = new MockMegaHttpHandler((url, body) =>
            {
                capturedBody = body;
                var response = new JsonArray
                {
                    new JsonObject
                    {
                        ["tsid"] = "session_token_after_2fa",
                        ["k"] = Convert.ToBase64String(new byte[16])
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var client = new MegaApiClient(httpClient);

            MegaAuthenticationResult result = await client.LoginAsync("user@example.com", "Password123!", "654321");

            Assert.True(result.Succeeded);
            Assert.Equal("session_token_after_2fa", result.SessionId);
            Assert.NotNull(capturedBody);
            Assert.Contains("\"mfa\":\"654321\"", capturedBody);
        }

        [Fact]
        public async Task MegaApiClient_LoginAsync_ReturnsCodeMinus9_DetectsInvalidCredentials()
        {
            var handler = new MockMegaHttpHandler((url, body) =>
            {
                // Error -9 means invalid user or password
                var response = new JsonArray { -9 };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var client = new MegaApiClient(httpClient);

            MegaAuthenticationResult result = await client.LoginAsync("user@example.com", "WrongPassword");

            Assert.False(result.Succeeded);
            Assert.Equal(MegaAuthenticationStatus.InvalidCredentials, result.Status);
            Assert.Equal(MegaErrorCodes.InvalidCredentials, result.ErrorCode);
        }

        [Fact]
        public async Task MegaApiClient_GetQuotaAsync_ParsesStorageAndCalculatesRemaining()
        {
            var handler = new MockMegaHttpHandler((url, body) =>
            {
                // Quota response with 50 GB total and 15 GB used
                var response = new JsonArray
                {
                    new JsonObject
                    {
                        ["mpos"] = 15L * 1024 * 1024 * 1024,
                        ["msto"] = 50L * 1024 * 1024 * 1024
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var client = new MegaApiClient(httpClient);

            var session = new MegaSessionToken("test_session", new byte[16], "user@example.com", DateTimeOffset.UtcNow);
            MegaQuotaInfo quota = await client.GetQuotaAsync(session);

            Assert.Equal(50L * 1024 * 1024 * 1024, quota.TotalBytes);
            Assert.Equal(15L * 1024 * 1024 * 1024, quota.UsedBytes);
            Assert.Equal(35L * 1024 * 1024 * 1024, quota.RemainingBytes);
            Assert.Equal(30.0, quota.UsagePercentage);
            Assert.False(quota.IsLowStorage);
        }

        [Fact]
        public void MegaQuotaInfo_DetectsLowStorage_WhenRemainingBelowTenPercent()
        {
            long total = 100L * 1024 * 1024 * 1024;
            long used = 92L * 1024 * 1024 * 1024; // 92% used, 8% remaining
            long remaining = total - used;

            var quota = new MegaQuotaInfo(total, used, remaining);

            Assert.True(quota.IsLowStorage);
            Assert.Equal(92.0, quota.UsagePercentage);
        }

        #endregion

        #region 2. MegaSessionService & DPAPI Secret Store Tests

        [Fact]
        public async Task MegaSessionService_AuthenticateAsync_StoresSessionInSecretStore()
        {
            var fakeClient = new FakeMegaApiClient
            {
                LoginResult = MegaAuthenticationResult.Success("gamer@example.com", "mega_sid_998877")
            };
            var secretStore = new InMemorySecretStore();
            var service = new MegaSessionService(fakeClient, secretStore);

            MegaAuthenticationResult result = await service.AuthenticateAsync(TestProfileId, "gamer@example.com", "Password123!");

            Assert.True(result.Succeeded);
            Assert.Equal("mega_sid_998877", result.SessionId);

            var key = new SecretKey(TestProfileId, SecretNames.MegaSessionData);
            Assert.True(await secretStore.ExistsAsync(key));

            // Verify session retrieval
            MegaSessionToken? retrieved = await service.GetSessionAsync(TestProfileId);
            Assert.NotNull(retrieved);
            Assert.Equal("mega_sid_998877", retrieved.SessionId);
            Assert.Equal("gamer@example.com", retrieved.UserEmail);
            Assert.NotNull(retrieved.MasterKey);
            Assert.Equal(16, retrieved.MasterKey.Length);
        }

        [Fact]
        public async Task MegaSessionService_DisconnectAsync_RemovesSessionFromSecretStore()
        {
            var fakeClient = new FakeMegaApiClient
            {
                LoginResult = MegaAuthenticationResult.Success("gamer@example.com", "mega_sid_123")
            };
            var secretStore = new InMemorySecretStore();
            var service = new MegaSessionService(fakeClient, secretStore);

            await service.AuthenticateAsync(TestProfileId, "gamer@example.com", "Password123!");
            Assert.True(await service.HasValidSessionAsync(TestProfileId));

            MegaDisconnectionResult disconnectResult = await service.DisconnectAsync(TestProfileId);

            Assert.True(disconnectResult.Succeeded);
            Assert.True(disconnectResult.LocalSessionRemoved);
            Assert.False(await service.HasValidSessionAsync(TestProfileId));

            MegaSessionToken? session = await service.GetSessionAsync(TestProfileId);
            Assert.Null(session);
        }

        [Fact]
        public async Task MegaSessionService_GetSessionAsync_ReturnsNull_WhenNotAuthenticated()
        {
            var fakeClient = new FakeMegaApiClient();
            var secretStore = new InMemorySecretStore();
            var service = new MegaSessionService(fakeClient, secretStore);

            MegaSessionToken? session = await service.GetSessionAsync(TestProfileId);
            Assert.Null(session);
            Assert.False(await service.HasValidSessionAsync(TestProfileId));
        }

        #endregion

        #region 3. MegaRemoteFileSystemSpike & GSM Safety Invariants

        [Fact]
        public async Task MegaRemoteFileSystemSpike_UploadRunAsync_EnforcesCreateOnly_ThrowsIfTargetExists()
        {
            var fakeClient = new FakeMegaApiClient();
            var secretStore = new InMemorySecretStore();
            var sessionService = new MegaSessionService(fakeClient, secretStore);
            await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "SecretPass");

            var spike = new MegaRemoteFileSystemSpike(fakeClient, sessionService, TestProfileId);

            var files = new Dictionary<string, byte[]>
            {
                ["files/save1.dat"] = Encoding.UTF8.GetBytes("save content 1"),
                ["manifest.json"] = Encoding.UTF8.GetBytes("{\"run_id\":\"run1\"}")
            };

            // First upload succeeds
            MegaNode runNode = await spike.UploadRunAsync("Run_2026_09_23_01", files);
            Assert.NotNull(runNode);

            // Second upload of the same run name MUST fail with InvalidOperationException (create-only guard)
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => spike.UploadRunAsync("Run_2026_09_23_01", files));

            Assert.Contains("already exists", ex.Message);
            Assert.Contains("Overwriting existing runs is prohibited", ex.Message);
        }

        [Fact]
        public async Task MegaRemoteFileSystemSpike_UploadRunAsync_UploadsManifestLast()
        {
            var fakeClient = new FakeMegaApiClient();
            var secretStore = new InMemorySecretStore();
            var sessionService = new MegaSessionService(fakeClient, secretStore);
            await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "SecretPass");

            var spike = new MegaRemoteFileSystemSpike(fakeClient, sessionService, TestProfileId);

            var files = new Dictionary<string, byte[]>
            {
                ["manifest.json"] = Encoding.UTF8.GetBytes("{\"manifest\":true}"),
                ["files/level.dat"] = Encoding.UTF8.GetBytes("level data"),
                ["files/settings.cfg"] = Encoding.UTF8.GetBytes("settings data")
            };

            await spike.UploadRunAsync("Run_2026_09_23_02", files);

            // Verify order of uploaded files: manifest.json MUST be the very last file uploaded!
            Assert.Equal(3, fakeClient.UploadedFileNames.Count);
            Assert.Equal("manifest.json", fakeClient.UploadedFileNames.Last());
            Assert.NotEqual("manifest.json", fakeClient.UploadedFileNames.First());
        }

        [Fact]
        public async Task MegaRemoteFileSystemSpike_SyntheticRun_UploadAndDownloadRoundTrip()
        {
            var fakeClient = new FakeMegaApiClient();
            var secretStore = new InMemorySecretStore();
            var sessionService = new MegaSessionService(fakeClient, secretStore);
            await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "SecretPass");

            var spike = new MegaRemoteFileSystemSpike(fakeClient, sessionService, TestProfileId);

            byte[] saveBytes = Encoding.UTF8.GetBytes("GameState=100;Player=Hero;");
            byte[] manifestBytes = Encoding.UTF8.GetBytes("{\"schema_version\":2,\"files_count\":1}");

            var files = new Dictionary<string, byte[]>
            {
                ["files/gamestate.sav"] = saveBytes,
                ["manifest.json"] = manifestBytes
            };

            // 1. Upload run
            await spike.UploadRunAsync("Run_Hero_001", files);

            // 2. Discover run
            IReadOnlyList<MegaNode> runs = await spike.ListRunsAsync();
            Assert.Single(runs);
            Assert.Equal("Run_Hero_001", runs[0].Name);

            // 3. Download run
            Dictionary<string, byte[]> downloaded = await spike.DownloadRunAsync("Run_Hero_001");
            Assert.Equal(2, downloaded.Count);
            Assert.True(downloaded.ContainsKey("files/gamestate.sav"));
            Assert.True(downloaded.ContainsKey("manifest.json"));
            Assert.Equal(saveBytes, downloaded["files/gamestate.sav"]);
            Assert.Equal(manifestBytes, downloaded["manifest.json"]);
        }

        [Fact]
        public async Task MegaRemoteFileSystemSpike_SupportsArchiveContainers_IsTrue()
        {
            var fakeClient = new FakeMegaApiClient();
            var secretStore = new InMemorySecretStore();
            var sessionService = new MegaSessionService(fakeClient, secretStore);

            var spike = new MegaRemoteFileSystemSpike(fakeClient, sessionService, TestProfileId);

            Assert.True(spike.SupportsArchiveContainers);
            Assert.Equal("GameSave Manager Backups", spike.RootFolderName);
        }

        [Fact]
        public async Task MegaRemoteFileSystemSpike_ZeroDeletion_NeverDeletesRunsDuringSync()
        {
            var fakeClient = new FakeMegaApiClient();
            var secretStore = new InMemorySecretStore();
            var sessionService = new MegaSessionService(fakeClient, secretStore);
            await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "SecretPass");

            var spike = new MegaRemoteFileSystemSpike(fakeClient, sessionService, TestProfileId);

            var files = new Dictionary<string, byte[]>
            {
                ["manifest.json"] = Encoding.UTF8.GetBytes("{}")
            };

            await spike.UploadRunAsync("Run_Immutable", files);

            // Ensure delete was never invoked
            Assert.Equal(0, fakeClient.DeleteCallCount);
        }

        #endregion

        #region 4. Privacy, Security & Catalog Invariants

        [Fact]
        public void MegaSessionToken_ToString_DoesNotExposeMasterKeyOrSessionId()
        {
            byte[] sensitiveKey = Encoding.UTF8.GetBytes("SuperSecretKey12");
            var token = new MegaSessionToken("sensitive_session_id_abcdef", sensitiveKey, "gamer@example.com", DateTimeOffset.UtcNow);

            string str = token.ToString();

            Assert.DoesNotContain("sensitive_session_id_abcdef", str);
            Assert.DoesNotContain("SuperSecretKey", str);
            Assert.Contains("***", str);
            Assert.Contains("gamer@example.com", str);
        }

        [Fact]
        public void SyncProviderCatalog_ExposesMegaAsImplementedAndAvailable()
        {
            var catalog = new SyncProviderCatalog();
            SyncProviderDescriptor descriptor = catalog.GetDescriptor(SyncProviderKind.Mega);

            Assert.NotNull(descriptor);
            Assert.True(descriptor.IsImplemented);
            Assert.True(descriptor.IsConfigurationAvailable);
            Assert.Equal("MEGA", descriptor.DisplayName);
            Assert.Equal(SyncProviderConfigurationSurface.ServerCredentials, descriptor.ConfigurationSurface);
            Assert.Null(descriptor.UnavailableMessage);
            Assert.True(descriptor.Capabilities.SupportsResumableUpload);
            Assert.True(descriptor.Capabilities.SupportsRemoteQuota);
        }

        #endregion

        #region Mock Doubles

        private sealed class MockMegaHttpHandler : HttpMessageHandler
        {
            private readonly Func<string, string, HttpResponseMessage> _responseFactory;

            public MockMegaHttpHandler(Func<string, string, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri?.ToString() ?? string.Empty;
                string body = string.Empty;
                if (request.Content != null)
                {
                    body = await request.Content.ReadAsStringAsync(cancellationToken);
                }
                return _responseFactory(url, body);
            }
        }

        private sealed class FakeMegaApiClient : IMegaApiClient
        {
            public MegaAuthenticationResult LoginResult { get; set; } =
                MegaAuthenticationResult.Success("test@example.com", "fake_session_123");

            public List<string> UploadedFileNames { get; } = new();
            public int DeleteCallCount { get; private set; }

            private readonly List<MegaNode> _nodes = new()
            {
                new MegaNode("root_handle", null, MegaNodeType.Root, "Cloud Drive", 0, DateTimeOffset.UtcNow)
            };
            private readonly Dictionary<string, byte[]> _fileData = new();
            private long _nextId = 1;

            public Task<MegaAuthenticationResult> LoginAsync(string email, string password, string? twoFactorCode = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(LoginResult);

            public Task<MegaQuotaInfo> GetQuotaAsync(MegaSessionToken session, CancellationToken cancellationToken = default) =>
                Task.FromResult(new MegaQuotaInfo(20L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 15L * 1024 * 1024 * 1024));

            public Task<IReadOnlyList<MegaNode>> GetNodesAsync(MegaSessionToken session, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<MegaNode>>(_nodes.ToList());

            public Task<MegaNode> CreateFolderAsync(MegaSessionToken session, string parentNodeId, string folderName, CancellationToken cancellationToken = default)
            {
                string id = $"folder_{Interlocked.Increment(ref _nextId)}";
                var node = new MegaNode(id, parentNodeId, MegaNodeType.Folder, folderName, 0, DateTimeOffset.UtcNow);
                _nodes.Add(node);
                return Task.FromResult(node);
            }

            public Task<MegaNode> UploadFileChunkedAsync(
                MegaSessionToken session,
                string parentFolderNodeId,
                string fileName,
                Stream contentStream,
                IProgress<long>? progress = null,
                CancellationToken cancellationToken = default)
            {
                string id = $"file_{Interlocked.Increment(ref _nextId)}";
                using var ms = new MemoryStream();
                contentStream.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                _fileData[id] = bytes;

                UploadedFileNames.Add(fileName);
                var node = new MegaNode(id, parentFolderNodeId, MegaNodeType.File, fileName, bytes.Length, DateTimeOffset.UtcNow);
                _nodes.Add(node);
                return Task.FromResult(node);
            }

            public Task<Stream> DownloadFileAsync(MegaSessionToken session, string fileNodeId, CancellationToken cancellationToken = default)
            {
                if (_fileData.TryGetValue(fileNodeId, out byte[]? bytes))
                {
                    return Task.FromResult<Stream>(new MemoryStream(bytes));
                }
                throw new FileNotFoundException($"Node '{fileNodeId}' not found.");
            }

            public Task<string?> ReadTextFileAsync(MegaSessionToken session, string fileNodeId, CancellationToken cancellationToken = default)
            {
                if (_fileData.TryGetValue(fileNodeId, out byte[]? bytes))
                {
                    return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
                }
                return Task.FromResult<string?>(null);
            }

            public Task<bool> DeleteNodeAsync(MegaSessionToken session, string nodeId, CancellationToken cancellationToken = default)
            {
                DeleteCallCount++;
                _nodes.RemoveAll(n => n.Id == nodeId);
                return Task.FromResult(true);
            }
        }

        #endregion
    }
}
