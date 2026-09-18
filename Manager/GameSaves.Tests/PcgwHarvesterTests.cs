using GameSaves.Core.Catalog;
using GameSaves.Core.Save;
using GameSaves.External;
using GameSaves.External.Http;
using GameSaves.Infrastructure.Save;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class PcgwHarvesterTests : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly string _databasePath;

        public PcgwHarvesterTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "gsm-pcgw-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            _databasePath = Path.Combine(_tempDirectory, "test.db");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDirectory))
                    Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup in test temp
            }
        }

        [Fact]
        public void ResolveHarvestTargets_FromTracklistObject_ResolvesAppIdsAndKnownTitles()
        {
            var tracklist = new MissingTitlesTracklist(
                GeneratedUtc: DateTimeOffset.UtcNow,
                TotalReconciled: 2,
                TotalCovered: 0,
                TotalMissing: 2,
                UnresearchedCount: 2,
                InReviewCount: 0,
                NoSaveLocationCount: 0,
                Items: new List<MissingTitleEntry>
                {
                    new MissingTitleEntry("400", "Portal", "https://store.steampowered.com/app/400/", MissingTitleResearchStatus.Unresearched, "High", false, 0, DateTimeOffset.UtcNow),
                    new MissingTitleEntry("620", "Portal 2", "https://store.steampowered.com/app/620/", MissingTitleResearchStatus.Unresearched, "High", false, 0, DateTimeOffset.UtcNow)
                });

            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0",
                Tracklist = tracklist
            };

            var (appIds, knownTitles) = PcgwHarvester.ResolveHarvestTargets(options);

            Assert.Equal(2, appIds.Count);
            Assert.Contains("400", appIds);
            Assert.Contains("620", appIds);
            Assert.Equal("Portal", knownTitles["400"]);
            Assert.Equal("Portal 2", knownTitles["620"]);
        }

        [Fact]
        public void ResolveHarvestTargets_FromTracklistJsonFile_ResolvesAppIdsAndKnownTitles()
        {
            var tracklist = new MissingTitlesTracklist(
                GeneratedUtc: DateTimeOffset.UtcNow,
                TotalReconciled: 1,
                TotalCovered: 0,
                TotalMissing: 1,
                UnresearchedCount: 1,
                InReviewCount: 0,
                NoSaveLocationCount: 0,
                Items: new List<MissingTitleEntry>
                {
                    new MissingTitleEntry("105600", "Terraria", "https://store.steampowered.com/app/105600/", MissingTitleResearchStatus.Unresearched, "Normal", false, 0, DateTimeOffset.UtcNow)
                });

            string jsonPath = Path.Combine(_tempDirectory, "missing-titles.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(tracklist, new JsonSerializerOptions { WriteIndented = true }));

            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0",
                TracklistPath = jsonPath
            };

            var (appIds, knownTitles) = PcgwHarvester.ResolveHarvestTargets(options);

            string appId = Assert.Single(appIds);
            Assert.Equal("105600", appId);
            Assert.Equal("Terraria", knownTitles["105600"]);
        }

        [Fact]
        public void ResolveHarvestTargets_FromTracklistCsvFile_ResolvesAppIdsAndKnownTitles()
        {
            string csvPath = Path.Combine(_tempDirectory, "missing-titles.csv");
            string csvContent = "SteamAppId,Title,StoreUrl,ResearchStatus,Priority,IsInstalled,ExistingCandidateCount,DiscoveredUtc,Notes\r\n" +
                                "413150,Stardew Valley,https://store.steampowered.com/app/413150/,Unresearched,High,false,0,2026-09-18T00:00:00Z,\r\n";
            File.WriteAllText(csvPath, csvContent);

            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0",
                TracklistPath = csvPath
            };

            var (appIds, knownTitles) = PcgwHarvester.ResolveHarvestTargets(options);

            string appId = Assert.Single(appIds);
            Assert.Equal("413150", appId);
            Assert.Equal("Stardew Valley", knownTitles["413150"]);
        }

        [Fact]
        public void ResolveHarvestTargets_WithSkipExistingInDatabase_ExcludesCoveredTitles()
        {
            var db = new SavePathDatabase(_databasePath);
            db.Initialize();

            // Insert mapping for 400
            db.ImportMappings(new[]
            {
                new SavePathImportItem("400", "Portal", "windows", "%LOCALAPPDATA%\\Portal", "Directory", "Test", null, null, null)
            }, enabled: true, reviewStatus: "Approved");

            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0",
                SteamAppIds = new[] { "400", "620" },
                SkipExistingInDatabase = true
            };

            var (appIds, _) = PcgwHarvester.ResolveHarvestTargets(options, db);

            string remaining = Assert.Single(appIds);
            Assert.Equal("620", remaining);
        }

        [Fact]
        public void ResolveHarvestTargets_WithMaxTitlesToProcess_LimitsReturnedCount()
        {
            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0",
                SteamAppIds = new[] { "100", "200", "300", "400", "500" },
                MaxTitlesToProcess = 3
            };

            var (appIds, _) = PcgwHarvester.ResolveHarvestTargets(options);

            Assert.Equal(3, appIds.Count);
            Assert.Equal(new[] { "100", "200", "300" }, appIds);
        }

        [Fact]
        public void ResolveHarvestTargets_NonExistentTracklistPath_ThrowsFileNotFoundException()
        {
            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0",
                TracklistPath = Path.Combine(_tempDirectory, "does-not-exist.json")
            };

            Assert.Throws<FileNotFoundException>(() => PcgwHarvester.ResolveHarvestTargets(options));
        }

        [Fact]
        public async Task HarvestAsync_WithEmptyInputs_ThrowsInvalidOperationException()
        {
            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = Path.Combine(_tempDirectory, "out"),
                UserAgent = "TestAgent/1.0"
            };

            var harvester = new PcgwHarvester(options);

            await Assert.ThrowsAsync<InvalidOperationException>(() => harvester.HarvestAsync());
        }

        [Fact]
        public async Task PcgwApiClient_WithMockHttpMessageHandler_ParsesCargoApiResponse()
        {
            string cargoResponseJson = """
            {
              "cargoquery": [
                {
                  "title": {
                    "PageID": "12345",
                    "Page": "Hollow Knight",
                    "SteamAppID": "367520"
                  }
                }
              ]
            }
            """;

            var handler = new MockHttpMessageHandler(req =>
            {
                Assert.Contains("action=cargoquery", req.RequestUri?.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(cargoResponseJson, Encoding.UTF8, "application/json")
                };
            });

            using var politeHttp = new PoliteHttpClient("TestAgent/1.0", new RateLimitOptions(), handler);
            var apiClient = new PcgwApiClient(politeHttp);

            PcgwTitle? title = await apiClient.ResolveSteamAppIdAsync("367520");

            Assert.NotNull(title);
            Assert.Equal(12345, title.PageId);
            Assert.Equal("Hollow_Knight", title.PageName);
            Assert.Equal("Hollow Knight", title.DisplayTitle);
            Assert.Contains("367520", title.SteamAppIds);
            Assert.Equal("https://www.pcgamingwiki.com/wiki/Hollow_Knight", title.SourceUrl);
        }

        [Fact]
        public async Task PcgwApiClient_WithMockHttpMessageHandler_ParsesWikitext()
        {
            string parseResponseJson = """
            {
              "parse": {
                "title": "Hollow Knight",
                "pageid": 12345,
                "wikitext": {
                  "*": "===Save game data location===\n{{Game data/saves|Windows|{{p|localappdataLow\\Team Cherry\\Hollow Knight}}}}\n"
                }
              }
            }
            """;

            var handler = new MockHttpMessageHandler(req =>
            {
                Assert.Contains("action=parse", req.RequestUri?.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(parseResponseJson, Encoding.UTF8, "application/json")
                };
            });

            using var politeHttp = new PoliteHttpClient("TestAgent/1.0", new RateLimitOptions(), handler);
            var apiClient = new PcgwApiClient(politeHttp);

            string wikitext = await apiClient.GetWikitextByPageIdAsync(12345);

            Assert.Contains("Team Cherry", wikitext);
        }

        [Fact]
        public async Task HarvestAsync_EndToEndWithMockApiClient_PersistsPendingCandidateMappings()
        {
            string outputRoot = Path.Combine(_tempDirectory, "harvest_out");

            var mockApiClient = new MockPcgwApiClient
            {
                ResolveHandler = appId =>
                {
                    if (appId == "367520")
                    {
                        return new PcgwTitle(
                            PageId: 4455,
                            PageName: "Hollow_Knight",
                            DisplayTitle: "Hollow Knight",
                            SteamAppIds: new List<string> { "367520" },
                            SourceUrl: "https://www.pcgamingwiki.com/wiki/Hollow_Knight");
                    }
                    return null;
                },
                WikitextHandler = pageId =>
                {
                    return """
                    ==Availability==
                    Available on Steam.
                    ===Save game data location===
                    {{Game data/saves|Windows|{{p|localappdatalow\Team Cherry\Hollow Knight}}}}
                    ==Video==
                    Settings here.
                    """;
                }
            };

            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = outputRoot,
                UserAgent = "TestAgent/1.0",
                SteamAppIds = new[] { "367520" }
            };

            var db = new SavePathDatabase(_databasePath);
            var harvester = new PcgwHarvester(options, mockApiClient, savePathDatabase: db);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            // Verify telemetry
            Assert.Equal(1, result.TitlesIndexed);
            Assert.Equal(1, result.TitlesProcessed);
            Assert.Equal(0, result.TitlesFailed);
            Assert.Equal(1, result.MappingsExtracted);

            // Verify disk artifacts
            string indexFile = Path.Combine(outputRoot, "index", "steam-appids.input.json");
            Assert.True(File.Exists(indexFile));

            string titleDir = Path.Combine(outputRoot, "4455-Hollow_Knight");
            Assert.True(Directory.Exists(titleDir));
            Assert.True(File.Exists(Path.Combine(titleDir, "metadata.json")));
            Assert.True(File.Exists(Path.Combine(titleDir, "raw.wikitext")));
            Assert.True(File.Exists(Path.Combine(titleDir, "savepaths.extracted.json")));

            // Verify Trust Invariant: Harvested mappings MUST be Pending and Disabled (enabled = 0)
            List<SavePathMapping> mappings = db.GetMappingsForApp("367520", "windows", includeDisabled: true, onlyApproved: false);
            SavePathMapping mapping = Assert.Single(mappings);

            Assert.Equal("Pending", mapping.ReviewStatus);
            Assert.False(mapping.Enabled);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight", mapping.PathTemplate);
            Assert.Equal("PCGamingWiki-AutoExtracted", mapping.SourceName);
        }

        [Fact]
        public async Task HarvestAsync_WithUnresolvableAppId_RecordsFailureAndContinues()
        {
            string outputRoot = Path.Combine(_tempDirectory, "harvest_fail_out");

            var mockApiClient = new MockPcgwApiClient
            {
                ResolveHandler = appId =>
                {
                    if (appId == "999999")
                        return null; // Missing/unindexed title

                    return new PcgwTitle(
                        PageId: 101,
                        PageName: "Valid_Game",
                        DisplayTitle: "Valid Game",
                        SteamAppIds: new List<string> { appId },
                        SourceUrl: "https://www.pcgamingwiki.com/wiki/Valid_Game");
                },
                WikitextHandler = pageId =>
                {
                    return """
                    ===Save game data location===
                    {{Game data/saves|Windows|%LOCALAPPDATA%\ValidGame\Saves}}
                    """;
                }
            };

            var options = new PcgwHarvestOptions
            {
                DatabasePath = _databasePath,
                OutputRoot = outputRoot,
                UserAgent = "TestAgent/1.0",
                SteamAppIds = new[] { "999999", "100" }
            };

            var db = new SavePathDatabase(_databasePath);
            var harvester = new PcgwHarvester(options, mockApiClient, savePathDatabase: db);

            PcgwHarvestResult result = await harvester.HarvestAsync();

            Assert.Equal(2, result.TitlesIndexed);
            Assert.Equal(1, result.TitlesProcessed);
            Assert.Equal(1, result.TitlesFailed);
            Assert.Equal(1, result.MappingsExtracted);
        }

        [Fact]
        public void PcgwSavePathExtractor_InfersPathKindsCorrectly()
        {
            Assert.Equal("Directory", PcgwSavePathExtractor.InferPathKind(@"%LOCALAPPDATA%\Game\Saves"));
            Assert.Equal("File", PcgwSavePathExtractor.InferPathKind(@"%LOCALAPPDATA%\Game\save.dat"));
            Assert.Equal("File", PcgwSavePathExtractor.InferPathKind(@"{Documents}\My Games\Game\config.ini"));
            Assert.Equal("Glob", PcgwSavePathExtractor.InferPathKind(@"{Documents}\My Games\Game\*.sav"));
            Assert.Equal("Directory", PcgwSavePathExtractor.InferPathKind(@"{SteamRoot}\userdata\*\620\remote"));
        }

        [Fact]
        public void PcgwSavePathExtractor_ExpandsModernTokenFormats()
        {
            var extractor = new PcgwSavePathExtractor();
            var title = new PcgwTitle(1, "Modern_Game", "Modern Game", new List<string> { "500" }, "https://example.org");

            string wikitext = """
            ===Save game data location===
            {{Game data/saves|Windows|<LocalAppData>\ModernStudio\Game}}
            {{Game data/saves|Windows|<UserDocuments>\My Games\ModernStudio}}
            {{Game data/saves|Windows|{{p|locallow\ModernStudio\Data}}}}
            {{Game data/saves|Windows|{{p|public}}\ModernStudio}}
            """;

            List<SavePathImportItem> candidates = extractor.ExtractCandidates(title, wikitext);

            Assert.Equal(4, candidates.Count);
            Assert.Contains(candidates, c => c.PathTemplate == @"%LOCALAPPDATA%\ModernStudio\Game");
            Assert.Contains(candidates, c => c.PathTemplate == @"%DOCUMENTS%\My Games\ModernStudio");
            Assert.Contains(candidates, c => c.PathTemplate == @"%USERPROFILE%\AppData\LocalLow\ModernStudio\Data");
            Assert.Contains(candidates, c => c.PathTemplate == @"%PUBLIC%\ModernStudio");
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }

        private sealed class MockPcgwApiClient : IPcgwApiClient
        {
            public Func<string, PcgwTitle?>? ResolveHandler { get; set; }
            public Func<int, string>? WikitextHandler { get; set; }

            public Task<PcgwTitle?> ResolveSteamAppIdAsync(string steamAppId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ResolveHandler?.Invoke(steamAppId));
            }

            public Task<string> GetWikitextByPageIdAsync(int pageId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(WikitextHandler?.Invoke(pageId) ?? string.Empty);
            }
        }
    }
}
