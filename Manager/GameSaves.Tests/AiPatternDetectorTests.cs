using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Catalog;
using GameSaves.Core.Save;
using GameSaves.Infrastructure.Catalog;
using GameSaves.Infrastructure.Save;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class AiPatternDetectorTests
    {
        private sealed class MockAiCompletionClient : IAiCompletionClient
        {
            private readonly Func<string, string, string> _handler;

            public string LastPrompt { get; private set; } = string.Empty;
            public string LastSystemPrompt { get; private set; } = string.Empty;

            public MockAiCompletionClient(Func<string, string, string> handler)
            {
                _handler = handler;
            }

            public MockAiCompletionClient(string cannedResponse)
            {
                _handler = (_, _) => cannedResponse;
            }

            public Task<string> GenerateCompletionAsync(
                string prompt,
                string? systemPrompt = null,
                CancellationToken cancellationToken = default)
            {
                LastPrompt = prompt;
                LastSystemPrompt = systemPrompt ?? string.Empty;
                return Task.FromResult(_handler(prompt, systemPrompt ?? string.Empty));
            }
        }

        #region Engine Fingerprinting Tests

        [Fact]
        public void Fingerprinter_DetectsUnrealEngine_FromStandardMarkers()
        {
            var fingerprinter = new GameEngineFingerprinter();
            var relativePaths = new List<string>
            {
                "Engine/Binaries/Win64/UnrealPak.exe",
                "ShooterGame/Config/DefaultEngine.ini",
                "ShooterGame.uproject"
            };

            EngineFingerprintResult result = fingerprinter.Detect(relativePaths);

            Assert.Equal(GameEngineKind.Unreal, result.Engine);
            Assert.True(result.Confidence >= DetectionConfidence.High);
            Assert.Contains(result.Evidence, e => e.Contains("uproject", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Fingerprinter_DetectsUnityEngine_AndParsesAppInfo()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_unity_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "TestGame_Data"));
                File.WriteAllText(Path.Combine(tempDir, "UnityPlayer.dll"), "mock dll");
                File.WriteAllText(Path.Combine(tempDir, "TestGame_Data", "app.info"), "AwesomeStudios\r\nSuperGame\r\n");

                var fingerprinter = new GameEngineFingerprinter();
                var relativePaths = new List<string>
                {
                    "UnityPlayer.dll",
                    "TestGame_Data/app.info"
                };

                EngineFingerprintResult result = fingerprinter.Detect(relativePaths, tempDir);

                Assert.Equal(GameEngineKind.Unity, result.Engine);
                Assert.Equal(DetectionConfidence.Certain, result.Confidence);
                Assert.Equal("AwesomeStudios", result.CompanyName);
                Assert.Equal("SuperGame", result.ProductName);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Fingerprinter_DetectsGodotEngine_FromPckAndProjectFile()
        {
            var fingerprinter = new GameEngineFingerprinter();
            var relativePaths = new List<string>
            {
                "game.pck",
                "project.godot"
            };

            EngineFingerprintResult result = fingerprinter.Detect(relativePaths);

            Assert.Equal(GameEngineKind.Godot, result.Engine);
            Assert.True(result.Confidence >= DetectionConfidence.High);
        }

        [Fact]
        public void Fingerprinter_DetectsRenPyEngine()
        {
            var fingerprinter = new GameEngineFingerprinter();
            var relativePaths = new List<string>
            {
                "renpy/common/00action_file.rpy",
                "game/saves/navigation.json"
            };

            EngineFingerprintResult result = fingerprinter.Detect(relativePaths);

            Assert.Equal(GameEngineKind.RenPy, result.Engine);
            Assert.True(result.Confidence >= DetectionConfidence.High);
        }

        [Fact]
        public void Fingerprinter_DetectsSourceEngine()
        {
            var fingerprinter = new GameEngineFingerprinter();
            var relativePaths = new List<string>
            {
                "hl2.exe",
                "hl2/gameinfo.txt"
            };

            EngineFingerprintResult result = fingerprinter.Detect(relativePaths);

            Assert.Equal(GameEngineKind.Source, result.Engine);
            Assert.True(result.Confidence >= DetectionConfidence.High);
        }

        [Fact]
        public void Fingerprinter_DetectsRpgMakerEngine()
        {
            var fingerprinter = new GameEngineFingerprinter();
            var relativePaths = new List<string>
            {
                "Game.rgss3a",
                "System.json"
            };

            EngineFingerprintResult result = fingerprinter.Detect(relativePaths);

            Assert.Equal(GameEngineKind.RpgMaker, result.Engine);
            Assert.True(result.Confidence >= DetectionConfidence.High);
        }

        [Fact]
        public void Fingerprinter_ReturnsCustom_WhenNoKnownSignaturesExist()
        {
            var fingerprinter = new GameEngineFingerprinter();
            var relativePaths = new List<string>
            {
                "data/config.bin",
                "launcher.exe"
            };

            EngineFingerprintResult result = fingerprinter.Detect(relativePaths);

            Assert.Equal(GameEngineKind.Custom, result.Engine);
            Assert.Equal(DetectionConfidence.Low, result.Confidence);
        }

        #endregion

        #region Tree Sanitizer Tests

        [Fact]
        public void Sanitizer_ScrubsCurrentUserAndRedactsSensitiveFiles()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_sanitizer_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "config"));
                Directory.CreateDirectory(Path.Combine(tempDir, "user_data"));

                File.WriteAllText(Path.Combine(tempDir, "game.exe"), "exe");
                File.WriteAllText(Path.Combine(tempDir, ".env"), "SECRET=12345");
                File.WriteAllText(Path.Combine(tempDir, "credentials.json"), "{\"token\":\"abc\"}");
                File.WriteAllText(Path.Combine(tempDir, "id_rsa"), "private key");
                File.WriteAllText(Path.Combine(tempDir, "config", "settings.ini"), "volume=100");

                var sanitizer = new DirectoryTreeSanitizer();
                SanitizedTreeResult result = sanitizer.Sanitize(tempDir);

                // Sensitive files must be scrubbed / excluded
                Assert.DoesNotContain(".env", result.FormattedTree);
                Assert.DoesNotContain("credentials.json", result.FormattedTree);
                Assert.DoesNotContain("id_rsa", result.FormattedTree);

                // Normal game files must be present
                Assert.Contains("game.exe", result.FormattedTree);
                Assert.Contains("settings.ini", result.FormattedTree);
                Assert.True(result.TotalFilesFound > 0);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Sanitizer_EnforcesMaxDepthAndFileCount()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_limits_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Create deep hierarchy: level1/level2/level3/level4/level5/deep.txt
                string deepDir = Path.Combine(tempDir, "l1", "l2", "l3", "l4", "l5");
                Directory.CreateDirectory(deepDir);
                File.WriteAllText(Path.Combine(deepDir, "deep.txt"), "content");
                File.WriteAllText(Path.Combine(tempDir, "root.txt"), "root");

                var sanitizer = new DirectoryTreeSanitizer();
                SanitizedTreeResult result = sanitizer.Sanitize(tempDir, maxDepth: 2, maxFiles: 10);

                Assert.Contains("root.txt", result.FormattedTree);
                Assert.DoesNotContain("deep.txt", result.FormattedTree);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        #endregion

        #region Heuristic & AI Proposals Tests

        [Fact]
        public async Task DetectSavePathsAsync_UnityWithAppInfo_YieldsCompanyAndProductPattern()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_unity_prop_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "MyGame_Data"));
                File.WriteAllText(Path.Combine(tempDir, "UnityPlayer.dll"), "unity");
                File.WriteAllText(Path.Combine(tempDir, "MyGame_Data", "app.info"), "IndieDev\r\nPixelAdventure\r\n");

                var service = new AiPatternDetectorService();
                var request = new AiDetectionRequest(tempDir, SteamAppId: "123456", OfflineOnly: true);

                AiDetectionResult result = await service.DetectSavePathsAsync(request);

                Assert.Equal(GameEngineKind.Unity, result.DetectedEngine);
                Assert.Equal("PixelAdventure", result.GameName);
                Assert.True(result.IsOfflineHeuristic);

                // Check LocalLow candidate was generated
                Assert.Contains(result.Proposals, p =>
                    p.PathTemplate.Contains(@"LocalLow\IndieDev\PixelAdventure", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task DetectSavePathsAsync_UnrealEngine_YieldsSavedGamesPatterns()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_unreal_prop_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "Engine", "Binaries", "Win64"));
                File.WriteAllText(Path.Combine(tempDir, "Engine", "Binaries", "Win64", "UE4.dll"), "ue");
                File.WriteAllText(Path.Combine(tempDir, "ShadowGame.uproject"), "{}");

                var service = new AiPatternDetectorService();
                var request = new AiDetectionRequest(tempDir, SteamAppId: "999888", GameName: "ShadowGame", OfflineOnly: true);

                AiDetectionResult result = await service.DetectSavePathsAsync(request);

                Assert.Equal(GameEngineKind.Unreal, result.DetectedEngine);
                Assert.Contains(result.Proposals, p =>
                    p.PathTemplate.Contains(@"%LOCALAPPDATA%\ShadowGame\Saved\SaveGames", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(result.Proposals, p =>
                    p.PathTemplate.Contains(@"{Documents}\My Games\ShadowGame\Saved\SaveGames", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task DetectSavePathsAsync_Godot_YieldsAppUserDataPattern()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_godot_prop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "project.godot"), "config_version=5");
                File.WriteAllText(Path.Combine(tempDir, "game.pck"), "pck");

                var service = new AiPatternDetectorService();
                var request = new AiDetectionRequest(tempDir, GameName: "GodotQuest", OfflineOnly: true);

                AiDetectionResult result = await service.DetectSavePathsAsync(request);

                Assert.Equal(GameEngineKind.Godot, result.DetectedEngine);
                Assert.Contains(result.Proposals, p =>
                    p.PathTemplate.Contains(@"%APPDATA%\Godot\app_userdata\GodotQuest", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task DetectSavePathsAsync_WithMockAiClient_MergesProposalsAndSetsPromptHash()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_ai_merge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "game.exe"), "bin");

                string mockResponse = @"[
                    {
                        ""pathTemplate"": ""%APPDATA%\\CustomGame\\Saves"",
                        ""pathKind"": ""SaveDirectory"",
                        ""platform"": ""windows"",
                        ""confidence"": ""High"",
                        ""rationale"": ""Config files point to roaming profile saves"",
                        ""priority"": 70
                    }
                ]";

                var mockAi = new MockAiCompletionClient(mockResponse);
                var service = new AiPatternDetectorService(mockAi);
                var request = new AiDetectionRequest(tempDir, GameName: "CustomGame", OfflineOnly: false);

                AiDetectionResult result = await service.DetectSavePathsAsync(request);

                Assert.False(result.IsOfflineHeuristic);
                Assert.NotEmpty(result.PromptHash);
                Assert.Contains(result.Proposals, p => p.PathTemplate == @"%APPDATA%\CustomGame\Saves");
                Assert.Contains("CustomGame", mockAi.LastPrompt);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task DetectSavePathsAsync_FallbackOnMalformedAiResponse_GracefullyUsesHeuristics()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gsm_test_ai_fallback_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "renpy"));
                File.WriteAllText(Path.Combine(tempDir, "renpy", "main.py"), "import renpy");

                var mockAi = new MockAiCompletionClient("I am an AI and I cannot output valid JSON right now: ```error```");
                var service = new AiPatternDetectorService(mockAi);
                var request = new AiDetectionRequest(tempDir, GameName: "VisualNovel", OfflineOnly: false);

                AiDetectionResult result = await service.DetectSavePathsAsync(request);

                // Must cleanly fall back to offline heuristics without throwing
                Assert.True(result.IsOfflineHeuristic);
                Assert.Equal(GameEngineKind.RenPy, result.DetectedEngine);
                Assert.Contains(result.Proposals, p => p.PathTemplate.Contains(@"%APPDATA%\RenPy\VisualNovel"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        #endregion

        #region Trust & Safety Invariants Tests

        [Fact]
        public void ToImportDocument_StrictlyEnforcesPendingReviewStatus()
        {
            var result = new AiDetectionResult(
                GameDirectory: @"C:\Games\Test",
                SteamAppId: "12345",
                GameName: "TestGame",
                DetectedEngine: GameEngineKind.Unity,
                EngineConfidence: DetectionConfidence.High,
                EngineEvidence: new List<string> { "UnityPlayer.dll" },
                Proposals: new List<AiCandidateProposal>
                {
                    new(
                        PathTemplate: @"%USERPROFILE%\AppData\LocalLow\Test\TestGame",
                        Platform: "windows",
                        PathKind: "SaveDirectory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.Unity,
                        Rationale: "Standard Unity path",
                        Priority: 80)
                },
                SanitizedDirectoryTree: "game.exe",
                ModelVersion: "gpt-mock",
                PromptHash: "abcdef1234567890",
                GeneratedUtc: DateTimeOffset.UtcNow,
                IsOfflineHeuristic: false);

            var service = new AiPatternDetectorService();
            MappingImportDocument doc = service.ToImportDocument(result);

            Assert.Equal(1, doc.SchemaVersion);
            Assert.NotNull(doc.Titles);
            var title = Assert.Single(doc.Titles);
            Assert.Equal("12345", title.SteamAppId);

            Assert.NotNull(doc.Mappings);
            var mapping = Assert.Single(doc.Mappings);
            Assert.Equal("12345", mapping.SteamAppId);
            // CRITICAL TRUST INVARIANT: ReviewStatus MUST be Pending
            Assert.Equal("Pending", mapping.ReviewStatus);
            Assert.Contains("AI-assisted", mapping.Notes);
            Assert.Contains("abcdef12", mapping.Notes);
        }

        [Fact]
        public void ToImportItems_SetsSourceNameAndPendingNotes()
        {
            var result = new AiDetectionResult(
                GameDirectory: @"C:\Games\Test",
                SteamAppId: "54321",
                GameName: "TestUnreal",
                DetectedEngine: GameEngineKind.Unreal,
                EngineConfidence: DetectionConfidence.High,
                EngineEvidence: new List<string> { "uproject" },
                Proposals: new List<AiCandidateProposal>
                {
                    new(
                        PathTemplate: @"%LOCALAPPDATA%\TestUnreal\Saved\SaveGames",
                        Platform: "windows",
                        PathKind: "SaveDirectory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.Unreal,
                        Rationale: "Unreal save folder",
                        Priority: 75)
                },
                SanitizedDirectoryTree: "TestUnreal.uproject",
                ModelVersion: "offline-heuristic",
                PromptHash: "fedcba9876543210",
                GeneratedUtc: DateTimeOffset.UtcNow,
                IsOfflineHeuristic: true);

            var service = new AiPatternDetectorService();
            List<SavePathImportItem> items = service.ToImportItems(result);

            var item = Assert.Single(items);
            Assert.Equal("54321", item.SteamAppId);
            Assert.Equal("AI-PatternDetector", item.SourceName);
            Assert.Contains("Pending human review", item.Notes);
        }

        [Fact]
        public async Task Database_DetectAndImportSavePathsAsync_InsertsAsPendingAndDisabled()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), "gsm_test_ai_db_" + Guid.NewGuid().ToString("N") + ".db");
            string gameDir = Path.Combine(Path.GetTempPath(), "gsm_test_ai_game_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(Path.Combine(gameDir, "renpy"));
                File.WriteAllText(Path.Combine(gameDir, "renpy", "common.rpy"), "renpy code");

                var database = new SavePathDatabase(dbPath);
                database.Initialize();

                var service = new AiPatternDetectorService();
                var request = new AiDetectionRequest(
                    GameDirectory: gameDir,
                    SteamAppId: "777888",
                    GameName: "RenPyNovels",
                    OfflineOnly: true);

                var (result, importedCount) = await database.DetectAndImportSavePathsAsync(request, service);

                Assert.True(importedCount > 0);

                // Verify through approved mappings query: Should return ZERO approved mappings because all are Pending
                List<SavePathMapping> approved = database.GetApprovedMappingsForApp("777888", "windows");
                Assert.Empty(approved);

                // Verify through raw pending/disabled mappings query
                List<SavePathMapping> allMappings = database.GetMappingsForApp("777888", "windows", includeDisabled: true, onlyApproved: false);
                var matchingPending = allMappings.Where(m => m.SteamAppId == "777888").ToList();
                Assert.NotEmpty(matchingPending);

                foreach (var m in matchingPending)
                {
                    Assert.Equal("Pending", m.ReviewStatus);
                    Assert.False(m.Enabled);
                    Assert.Equal("AI-PatternDetector", m.SourceName);
                }

                // Now approve one mapping and verify it becomes active
                var first = matchingPending.First();
                database.ApproveMapping(first.Id, "Reviewer verified save location");

                List<SavePathMapping> afterApproval = database.GetApprovedMappingsForApp("777888", "windows");
                Assert.Single(afterApproval);
                Assert.Equal("Approved", afterApproval[0].ReviewStatus);
                Assert.True(afterApproval[0].Enabled);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
                if (Directory.Exists(gameDir))
                {
                    try { Directory.Delete(gameDir, recursive: true); } catch { }
                }
            }
        }

        #endregion
    }
}
